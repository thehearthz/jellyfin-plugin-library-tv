using System.Collections.Concurrent;
using System.Threading.Channels;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LibraryTV;

public sealed record RuntimeStatus(int Viewers, int Recoveries, string? LastProblem, DateTimeOffset? LastProblemAt, string? Playback = null);

// One process per watched channel; no timer, media scan, or process while idle.
public sealed class Broadcasts(Scheduler scheduler, IProgramStreamer streamer, ILogger<Broadcasts> log, ITaskManager? tasks = null) : IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<string, Broadcast> active = [];
    private readonly Dictionary<string, RuntimeStatus> history = [];
    private readonly Dictionary<string, string> playback = [];
    private bool disposed;
    // Restarts of one program before it counts as unplayable, and how long a guide gap may be bridged.
    internal const int MaxRestarts = 2;
    internal static TimeSpan RestartDelay { get; set; } = TimeSpan.FromSeconds(1);
    internal static TimeSpan MaxGap { get; set; } = TimeSpan.FromMinutes(15);
    // A program ending this much earlier than the guide says is followed at once by the next one.
    internal static TimeSpan EarlyEnd { get; set; } = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan KeepAlive = TimeSpan.FromMilliseconds(500);
    // MPEG-TS null packets: clients discard them, but they keep the connection busy during short waits.
    private static readonly byte[] Filler = Enumerable.Range(0, 7).SelectMany(_ => new byte[] { 0x47, 0x1F, 0xFF, 0x10 }.Concat(Enumerable.Repeat((byte)0xFF, 184))).ToArray();

    public RuntimeStatus StatusFor(string id)
    {
        lock (gate)
        {
            var status = history.GetValueOrDefault(id) ?? new RuntimeStatus(0, 0, null, null);
            var watched = active.TryGetValue(id, out var live);
            return status with { Viewers = watched ? live!.Viewers.Count : 0, Playback = watched ? playback.GetValueOrDefault(id) : null };
        }
    }

    public async Task Watch(string id, Stream output, CancellationToken ct)
    {
        Broadcast broadcast;
        var subscriber = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1024) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        var key = Guid.NewGuid();
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            // A stopping worker may still be listed briefly; never attach a viewer to it.
            if (!active.TryGetValue(id, out broadcast!) || broadcast.Stop.IsCancellationRequested)
            {
                if (active.Count >= Plugin.Instance.Configuration.MaxActiveChannels) throw new InvalidOperationException("Active channel limit reached.");
                broadcast = new Broadcast();
                active[id] = broadcast;
                broadcast.Viewers[key] = subscriber;
                broadcast.Run = Task.Run(() => Run(id, broadcast));
            }
            else broadcast.Viewers[key] = subscriber;
        }
        try
        {
            await foreach (var bytes in subscriber.Reader.ReadAllAsync(ct))
            {
                await output.WriteAsync(bytes, ct);
                await output.FlushAsync(ct);
            }
        }
        finally
        {
            lock (gate)
            {
                broadcast.Viewers.TryRemove(key, out _);
                if (broadcast.Viewers.IsEmpty)
                {
                    broadcast.Stop.Cancel();
                    if (active.GetValueOrDefault(id) == broadcast) active.Remove(id);
                }
            }
        }
    }

    private async Task Run(string id, Broadcast broadcast)
    {
        Exception? failure = null;
        var ct = broadcast.Stop.Token;
        var consecutiveFailures = 0;
        var restarts = 0;
        Airing? restarted = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var now = DateTimeOffset.UtcNow;
                var item = scheduler.Current(id, now);
                if (item is null)
                {
                    // Content restrictions or an empty pool can leave a short gap. Stay connected through it.
                    var upcoming = scheduler.GuideFor(id).FirstOrDefault(x => x.Start > now);
                    if (upcoming is null || upcoming.Start - now > MaxGap) throw new InvalidOperationException("No program is scheduled. Check channel health.");
                    await Hold(broadcast, upcoming.Start - now, ct);
                    continue;
                }
                var channel = scheduler.Channels.FirstOrDefault(x => x.Id == id) ?? throw new InvalidOperationException("Channel is disabled.");
                if (restarted != item) (restarted, restarts) = (item, 0);
                try
                {
                    var method = await streamer.Stream(item, channel, chunk => Send(broadcast, chunk, ct), ct);
                    lock (gate) playback[id] = method;
                    consecutiveFailures = 0;
                    var remaining = item.End - DateTimeOffset.UtcNow;
                    // The file was shorter than its listed runtime: start the next program instead of dead air.
                    if (remaining > EarlyEnd && await scheduler.SkipFailed(item, DateTimeOffset.UtcNow, ct))
                    {
                        RequestGuideImport();
                        continue;
                    }
                    if (remaining > TimeSpan.Zero) await Hold(broadcast, item.End - DateTimeOffset.UtcNow, ct);
                }
                catch (PlaybackInterruptedException ex) when (restarts < MaxRestarts)
                {
                    // Restart the same program from the current time; viewers stay connected.
                    ct.ThrowIfCancellationRequested();
                    restarts++;
                    var problem = (ex.Stalled ? "Playback stalled" : "Playback stopped unexpectedly") + " and restarted automatically. If this repeats, check the media file and its storage.";
                    Report(id, problem, true);
                    log.LogWarning("Library TV channel {Channel}: {Problem}", id, problem);
                    await Hold(broadcast, RestartDelay, ct);
                }
                catch (IOException ex)
                {
                    ct.ThrowIfCancellationRequested();
                    var problem = ex is FileNotFoundException ? "A scheduled file is missing. Check the library or its storage mount."
                        : ex is PlaybackInterruptedException ? "A scheduled item kept stalling and was skipped. Check the file and its storage."
                        : "A scheduled item could not play. Check file access and media formats; Always encode may help.";
                    Report(id, problem, false);
                    log.LogWarning("Library TV channel {Channel}: {Problem}", id, problem);
                    if (++consecutiveFailures >= 3 || !await scheduler.SkipFailed(item, DateTimeOffset.UtcNow, ct))
                        throw new IOException("Channel stopped after unavailable media. See Channel health for details.");
                    Report(id, problem + " The next scheduled program was brought forward.", true);
                    RequestGuideImport();
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            failure = ex;
            if (ex is EncoderUnavailableException) Report(id, ex.Message, false);
            log.LogWarning(ex, "Library TV channel {Channel} stopped", id);
        }
        finally
        {
            lock (gate)
            {
                foreach (var viewer in broadcast.Viewers.Values) viewer.Writer.TryComplete(failure);
                if (active.GetValueOrDefault(id) == broadcast) { active.Remove(id); playback.Remove(id); }
            }
        }
    }

    private static Task Send(Broadcast broadcast, byte[] chunk, CancellationToken ct)
    {
        foreach (var viewer in broadcast.Viewers)
            if (!viewer.Value.Writer.TryWrite(chunk))
            {
                viewer.Value.Writer.TryComplete(new IOException("Viewer could not keep up with the live stream."));
                broadcast.Viewers.TryRemove(viewer.Key, out _);
            }
        if (broadcast.Viewers.IsEmpty) { broadcast.Stop.Cancel(); ct.ThrowIfCancellationRequested(); }
        return Task.CompletedTask;
    }

    // Wait while keeping the connection fed, so neither Jellyfin nor the client gives up on the stream.
    private static async Task Hold(Broadcast broadcast, TimeSpan duration, CancellationToken ct)
    {
        var until = DateTimeOffset.UtcNow + duration;
        for (var left = duration; left > TimeSpan.Zero; left = until - DateTimeOffset.UtcNow)
        {
            await Task.Delay(left < KeepAlive ? left : KeepAlive, ct);
            if (until > DateTimeOffset.UtcNow) await Send(broadcast, Filler, ct);
        }
    }

    private void Report(string id, string problem, bool recovered)
    {
        lock (gate)
        {
            if (!history.ContainsKey(id) && history.Count >= 100) history.Remove(history.OrderBy(x => x.Value.LastProblemAt).First().Key);
            var old = history.GetValueOrDefault(id);
            history[id] = new RuntimeStatus(0, (old?.Recoveries ?? 0) + (recovered ? 1 : 0), problem, DateTimeOffset.UtcNow);
        }
    }

    // Jellyfin keeps its own copy of the guide. Re-import it once after recovery shifts
    // program times; a refresh that is already running or queued covers this request.
    private void RequestGuideImport()
    {
        try
        {
            var worker = tasks?.ScheduledTasks.FirstOrDefault(x => x.ScheduledTask.Key == "RefreshGuide");
            if (worker is { State: TaskState.Idle }) tasks!.QueueScheduledTask(worker.ScheduledTask, new TaskOptions());
        }
        catch (Exception ex) { log.LogWarning(ex, "Could not queue Jellyfin's guide refresh after Library TV recovery"); }
    }

    public void Dispose() { lock (gate) { disposed = true; foreach (var b in active.Values) b.Stop.Cancel(); } }
    private sealed class Broadcast
    {
        public CancellationTokenSource Stop { get; } = new();
        public ConcurrentDictionary<Guid, Channel<byte[]>> Viewers { get; } = new();
        public Task? Run { get; set; }
    }
}
