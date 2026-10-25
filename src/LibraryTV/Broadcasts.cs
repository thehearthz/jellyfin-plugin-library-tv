using System.Collections.Concurrent;
using System.Threading.Channels;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LibraryTV;

public sealed record RuntimeStatus(int Viewers, int Recoveries, string? LastProblem, DateTimeOffset? LastProblemAt, string? Playback = null);

// One process per watched channel; no timer, media scan, or process while idle.
public sealed class Broadcasts(Scheduler scheduler, IProgramStreamer streamer, ILogger<Broadcasts> log, ITaskManager? tasks = null,
    ISessionManager? sessions = null, ILibraryManager? library = null) : IDisposable
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
    // With the "restart" picture shape, players are restarted this long after a program of a new shape starts,
    // so Jellyfin sizes the restarted stream from the new program.
    internal static TimeSpan ShapeRestartDelay { get; set; } = TimeSpan.FromSeconds(2);
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
        Airing? finished = null;
        double? programShape = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var now = DateTimeOffset.UtcNow;
                // Programs run up to a few seconds ahead of the clock, so after one finishes, the clock may still
                // say it (or, after short clips, an earlier one) is on: always continue with the entry after it.
                // It starts at once, while the player still has those seconds buffered.
                var item = finished is null ? scheduler.Current(id, now) : scheduler.GuideFor(id).FirstOrDefault(x => x.Start >= finished.End);
                // Running behind instead (slow encoding): skip entries that have already ended.
                if (item is not null && item.End <= now) item = scheduler.Current(id, now);
                if (item is null || item.Start - now > ProgramStreamer.Lead)
                {
                    // Content restrictions or an empty pool can leave a short gap. Stay connected through it.
                    var upcoming = item ?? scheduler.GuideFor(id).FirstOrDefault(x => x.Start > now);
                    if (upcoming is null || upcoming.Start - now > MaxGap) throw new InvalidOperationException("No program is scheduled. Check channel health.");
                    await Hold(broadcast, upcoming.Start - now - ProgramStreamer.Lead, ct);
                    continue;
                }
                finished = null;
                var channel = scheduler.Channels.FirstOrDefault(x => x.Id == id) ?? throw new InvalidOperationException("Channel is disabled.");
                if (restarted != item) (restarted, restarts) = (item, 0);
                double? shape = null;
                var restartPlayers = false;
                if (channel.PictureShape == "restart")
                {
                    // Programs play in their own shape; commercials and bumpers take the shape of the program they follow.
                    if (item.Commercial) shape = programShape;
                    else if (await streamer.Shape(item, ct) is { } current)
                    {
                        restartPlayers = item.Offset == 0 && programShape is { } before && !StreamPlan.SameShape(current, before);
                        programShape = current;
                    }
                }
                try
                {
                    var method = await streamer.Stream(item, channel, shape, chunk =>
                    {
                        if (restartPlayers)
                        {
                            restartPlayers = false;
                            _ = RestartPlayers(id, ct);
                        }
                        return Send(broadcast, chunk, ct);
                    }, ct, (text, problem) =>
                    {
                        if (!problem) { lock (gate) playback[id] = text; return; }
                        Report(id, text, false);
                        log.LogWarning("Library TV channel {Channel}: {Problem}", id, text);
                    });
                    lock (gate) playback[id] = method;
                    consecutiveFailures = 0;
                    // The file was shorter than its listed runtime: start the next program instead of dead air.
                    if (item.End - DateTimeOffset.UtcNow > ProgramStreamer.Lead + EarlyEnd && await scheduler.SkipFailed(item, DateTimeOffset.UtcNow, ct))
                    {
                        RequestGuideImport();
                        continue;
                    }
                    finished = item;
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

    // Ask every app playing this channel to start it again, so Jellyfin sizes the new stream for the new shape.
    // Apps that do not accept remote control keep playing as before.
    private async Task RestartPlayers(string id, CancellationToken ct)
    {
        try
        {
            await Task.Delay(ShapeRestartDelay, ct);
            if (sessions is null) return;
            var watching = sessions.Sessions.Where(x => x.SupportsRemoteControl && x.NowPlayingItem is not null && Watching(x, id)).ToArray();
            foreach (var session in watching)
                await sessions.SendPlayCommand(session.Id, session.Id, new PlayRequest { ItemIds = [session.NowPlayingItem.Id], PlayCommand = PlayCommand.PlayNow, ControllingUserId = session.UserId }, ct);
            if (watching.Length > 0) log.LogInformation("Library TV channel {Channel}: restarted {Count} players for a new picture shape", id, watching.Length);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { log.LogWarning(ex, "Library TV channel {Channel}: could not restart players for a new picture shape", id); }
    }

    // A session is watching the channel through Live TV or the Library TV folder.
    private bool Watching(SessionInfo session, string id)
    {
        if (session.PlayState?.MediaSourceId == LiveTvService.SourceId(id)) return true;
        var item = library?.GetItemById(session.NowPlayingItem.Id);
        return item is not null && item.ExternalId == id
            && (item is MediaBrowser.Controller.LiveTv.LiveTvChannel { ServiceName: "Library TV" } || item.ChannelId != Guid.Empty && library!.GetItemById(item.ChannelId)?.Name == "Library TV");
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
