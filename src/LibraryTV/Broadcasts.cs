using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LibraryTV;

// One remuxer per watched channel; bounded queues disconnect slow viewers.
public sealed class Broadcasts(Scheduler scheduler, IMediaEncoder encoder, ILogger<Broadcasts> log) : IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<string, Broadcast> active = [];

    public async Task Watch(string id, Stream output, CancellationToken ct)
    {
        Broadcast broadcast;
        var subscriber = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(256) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        var key = Guid.NewGuid();
        lock (gate)
        {
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
        var token = broadcast.Stop.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var now = DateTimeOffset.UtcNow;
                var item = scheduler.Current(id, now) ?? throw new InvalidOperationException("No program scheduled. Check source folders and refresh the guide.");
                if (!File.Exists(item.Path)) throw new FileNotFoundException("Scheduled media is no longer available.");
                var channel = scheduler.Channels.First(x => x.Id == id);
                var start = new ProcessStartInfo(encoder.EncoderPath) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                void Args(params string[] values) { foreach (var value in values) start.ArgumentList.Add(value); }
                string Num(double x) => x.ToString("0.000", CultureInfo.InvariantCulture);
                Args("-hide_banner", "-loglevel", "error", "-nostdin", "-re", "-ss", Num(Math.Max(0, (now - item.Start).TotalSeconds)), "-i", item.Path,
                    "-t", Num((item.End - now).TotalSeconds), "-map", "0:v:0", "-map", "0:a:0?", "-sn", "-dn");
                if (channel.Transcode) Args("-vf", "scale=1280:720:force_original_aspect_ratio=decrease,pad=1280:720:(ow-iw)/2:(oh-ih)/2,fps=30,format=yuv420p", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "23", "-g", "60", "-c:a", "aac", "-ar", "48000", "-ac", "2");
                else Args("-c", "copy");
                Args("-output_ts_offset", Num(now.ToUnixTimeMilliseconds() / 1000.0), "-mpegts_flags", "+initial_discontinuity+resend_headers", "-mpegts_start_pid", "256", "-mpegts_pmt_start_pid", "4096", "-f", "mpegts", "pipe:1");
                using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not launch Jellyfin FFmpeg.");
                using var registration = token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
                // Drain stderr without retaining media paths or unbounded output in memory.
                var errors = Task.Run(async () => { while (await process.StandardError.ReadLineAsync(token) is not null) { } }, token);
                var buffer = new byte[188 * 64];
                int count;
                while ((count = await process.StandardOutput.BaseStream.ReadAsync(buffer, token)) > 0)
                {
                    var chunk = buffer.AsSpan(0, count).ToArray();
                    foreach (var viewer in broadcast.Viewers)
                        if (!viewer.Value.Writer.TryWrite(chunk))
                        {
                            viewer.Value.Writer.TryComplete(new IOException("Viewer could not keep up with the live stream."));
                            broadcast.Viewers.TryRemove(viewer.Key, out _);
                        }
                    if (broadcast.Viewers.IsEmpty) { broadcast.Stop.Cancel(); token.ThrowIfCancellationRequested(); }
                }
                await process.WaitForExitAsync(token);
                await errors;
                if (process.ExitCode != 0) throw new IOException("FFmpeg could not play this file. Try compatibility transcoding or check codecs.");
                // Fast keyframe seeking/remuxing may finish before the next program's wall-clock start.
                var delay = item.End - DateTimeOffset.UtcNow;
                if (delay > TimeSpan.Zero) await Task.Delay(delay, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { failure = ex; log.LogWarning(ex, "Library TV channel {Channel} stopped", id); }
        finally
        {
            lock (gate)
            {
                foreach (var viewer in broadcast.Viewers.Values) viewer.Writer.TryComplete(failure);
                if (active.GetValueOrDefault(id) == broadcast) active.Remove(id);
            }
        }
    }

    public void Dispose() { lock (gate) foreach (var b in active.Values) b.Stop.Cancel(); }
    private sealed class Broadcast
    {
        public CancellationTokenSource Stop { get; } = new();
        public ConcurrentDictionary<Guid, Channel<byte[]>> Viewers { get; } = new();
        public Task? Run { get; set; }
    }
}
