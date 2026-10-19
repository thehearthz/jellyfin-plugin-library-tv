using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LibraryTV;

public sealed record RuntimeStatus(int Viewers, int Recoveries, string? LastProblem, DateTimeOffset? LastProblemAt);

// One process per watched channel; no timer, media scan, or process while idle.
public sealed class Broadcasts(Scheduler scheduler, IProgramStreamer streamer, ILogger<Broadcasts> log) : IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<string, Broadcast> active = [];
    private readonly Dictionary<string, RuntimeStatus> history = [];
    private bool disposed;

    public RuntimeStatus StatusFor(string id)
    {
        lock (gate)
        {
            var status = history.GetValueOrDefault(id) ?? new RuntimeStatus(0, 0, null, null);
            return status with { Viewers = active.TryGetValue(id, out var live) ? live.Viewers.Count : 0 };
        }
    }

    public async Task Watch(string id, Stream output, CancellationToken ct)
    {
        Broadcast broadcast;
        var subscriber = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(256) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        var key = Guid.NewGuid();
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!active.TryGetValue(id, out broadcast!))
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
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var item = scheduler.Current(id, DateTimeOffset.UtcNow) ?? throw new InvalidOperationException("No program is scheduled. Check channel health.");
                var channel = scheduler.Channels.FirstOrDefault(x => x.Id == id) ?? throw new InvalidOperationException("Channel is disabled.");
                try
                {
                    await streamer.Stream(item, channel.Transcode, chunk =>
                    {
                        foreach (var viewer in broadcast.Viewers)
                            if (!viewer.Value.Writer.TryWrite(chunk))
                            {
                                viewer.Value.Writer.TryComplete(new IOException("Viewer could not keep up with the live stream."));
                                broadcast.Viewers.TryRemove(viewer.Key, out _);
                            }
                        if (broadcast.Viewers.IsEmpty) { broadcast.Stop.Cancel(); ct.ThrowIfCancellationRequested(); }
                        return Task.CompletedTask;
                    }, ct);
                    consecutiveFailures = 0;
                    var delay = item.End - DateTimeOffset.UtcNow;
                    if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
                }
                catch (IOException ex)
                {
                    ct.ThrowIfCancellationRequested();
                    var problem = ex is FileNotFoundException ? "A scheduled file is missing. Check the library or its storage mount." : "A scheduled item could not play. Check file access and media formats; compatibility encoding may help.";
                    Report(id, problem, false);
                    log.LogWarning("Library TV channel {Channel}: {Problem}", id, problem);
                    if (++consecutiveFailures >= 3 || !await scheduler.SkipFailed(item, DateTimeOffset.UtcNow, ct))
                        throw new IOException("Channel stopped after unavailable media. See Channel health for details.");
                    Report(id, problem + " The next scheduled program was brought forward.", true);
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
                if (active.GetValueOrDefault(id) == broadcast) active.Remove(id);
            }
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

    public void Dispose() { lock (gate) { disposed = true; foreach (var b in active.Values) b.Stop.Cancel(); } }
    private sealed class Broadcast
    {
        public CancellationTokenSource Stop { get; } = new();
        public ConcurrentDictionary<Guid, Channel<byte[]>> Viewers { get; } = new();
        public Task? Run { get; set; }
    }
}
