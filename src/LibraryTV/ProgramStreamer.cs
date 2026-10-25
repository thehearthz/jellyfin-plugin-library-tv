using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.LibraryTV;

public interface IProgramStreamer
{
    // Returns how the program was played, for Channel health. A shape, when given, overrides the
    // channel's picture shape: clips are fitted to the program around them.
    // Report receives how the program is playing as soon as it starts, and problems such as slow encoding (true).
    Task<string> Stream(Airing program, ChannelDefinition channel, double? shape, Func<byte[], Task> output, CancellationToken ct, Action<string, bool>? report = null);
    // The program's picture shape (width ÷ height as displayed), or null when unknown.
    Task<double?> Shape(Airing program, CancellationToken ct);
}

// What the file holds, from Jellyfin's library metadata or FFprobe. Null means unknown.
// PixelAspect is the sample aspect ratio (1 for square pixels); DisplayAspect is the picture's shape.
public sealed record SourceFormat(string? Video, string? PixelFormat, int? BitDepth, bool Interlaced, string? Audio, int? Channels, int? SampleRate,
    int? Width = null, int? Height = null, double? PixelAspect = null, double? DisplayAspect = null)
{
    public double? Shape => DisplayAspect ?? (Width > 0 && Height > 0 ? Width.Value * (PixelAspect ?? 1) / Height.Value : null);
}

// Normalize is the always-encode 720p mode; otherwise each stream is copied when compatible.
// FrameWidth/FrameHeight, when set, is the picture the video is encoded into, with black bars.
public sealed record StreamPlan(bool CopyVideo, bool CopyAudio, bool HasAudio = true, bool Deinterlace = false, bool Normalize = false, int FrameWidth = 0, int FrameHeight = 0)
{
    public static readonly StreamPlan Copy = new(true, true);
    public static readonly StreamPlan Encode = new(false, false, Normalize: true, FrameWidth: 1280, FrameHeight: 720);

    // 8-bit H.264 video and stereo AAC audio play on almost every client without Jellyfin re-encoding
    // them, so they are copied. Anything else is encoded to those formats, so every program on a channel
    // reaches the client in the same codecs. Changing codecs mid-stream is a common cause of freezes.
    // Jellyfin sizes its own output from the first program a viewer sees, so a later program of another
    // shape would be stretched. Video in a visibly different shape is therefore encoded
    // into the channel's picture shape with black bars; programs already in that shape are still copied.
    public static StreamPlan For(SourceFormat? format, ChannelDefinition channel, double? shapeOverride = null)
    {
        var hasAudio = format is null || format.Audio is not null;
        if (channel.AlwaysEncode) return Encode with { HasAudio = hasAudio, Deinterlace = format?.Interlaced == true };
        var shape = shapeOverride ?? channel.PictureShape switch { "off" or "restart" => null, "4:3" => 4.0 / 3, _ => (double?)(16.0 / 9) };
        if (format is null) return new(false, false, FrameWidth: shape is null ? 0 : Even(720 * shape.Value), FrameHeight: shape is null ? 0 : 720);
        var pixel = format.PixelAspect ?? 1;
        var display = format.Shape;
        // Only shapes that would visibly stretch are corrected: 1.85:1 films stay as they are on a 16:9 channel.
        // Jellyfin scales by the displayed shape, so non-square pixels in the right shape are fine too.
        var fits = shape is null || display is null || SameShape(display.Value, shape.Value);
        var video = format.Video == "h264" && format.PixelFormat is null or "yuv420p" or "yuvj420p" && format.BitDepth is null or 8 && !format.Interlaced && fits;
        // Audio is cheap to encode, so anything but stereo 48 kHz AAC is converted to keep one audio layout.
        var audio = format.Audio == "aac" && format.Channels is null or 2 && format.SampleRate is null or 48000;
        var plan = new StreamPlan(video, audio, hasAudio, format.Interlaced);
        if (video || shape is not { } target) return plan;
        // Keep the source's size up to 720p, which keeps the encoding light: wider pictures fit the width,
        // narrower ones the height.
        var width = (format.Width ?? 1280) * pixel;
        var height = (double)(format.Height ?? 720);
        var wide = (display ?? target) >= target;
        var frameHeight = wide ? Math.Min(width, 720 * target) / target : Math.Min(height, 720);
        return plan with { FrameWidth = Even(frameHeight * target), FrameHeight = Even(frameHeight) };
    }

    private static int Even(double x) => (int)Math.Round(x / 2) * 2;

    // Within 8%: 1.85:1 counts as 16:9, while 4:3 and 2.39:1 do not.
    public static bool SameShape(double a, double b) => Math.Abs(a - b) / b <= 0.08;

    public string Describe()
    {
        if (Normalize) return "Encoding to 720p H.264/AAC (always encode)";
        if (CopyVideo && CopyAudio && HasAudio) return "Direct: video and audio copied without encoding";
        var video = CopyVideo ? "Video copied" : FrameWidth > 0 ? $"Video encoded to H.264 in a {FrameWidth}×{FrameHeight} picture" : "Video encoded to H.264";
        var audio = !HasAudio ? "silent audio track added" : CopyAudio ? "audio copied" : "audio encoded to AAC stereo";
        return video + "; " + audio;
    }
}

public sealed class ProgramStreamer(IMediaEncoder encoder, IMediaSourceManager? sources = null) : IProgramStreamer
{
    internal Action<int>? ProcessStarted { get; set; }
    // A live FFmpeg process that sends nothing for this long is stalled, for example on a hung network mount.
    internal TimeSpan FirstOutputTimeout { get; set; } = TimeSpan.FromSeconds(20);
    internal TimeSpan StallTimeout { get; set; } = TimeSpan.FromSeconds(12);

    // Square the pixels, fit the picture inside the frame, and add black bars to fill it.
    private static string Fit(int width, int height) =>
        $"scale=trunc(iw*sar/2)*2:ih,setsar=1,scale={width}:{height}:force_original_aspect_ratio=decrease:force_divisible_by=2,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2,setsar=1";

    // Programs run this far ahead of the clock: video is sent up to this long before its time, so the next
    // program's start-up happens while the player still has a few seconds buffered instead of freezing it.
    internal static TimeSpan Lead { get; set; } = TimeSpan.FromSeconds(3);

    // A program that starts before its scheduled time (because the one before ran ahead) starts at its
    // own beginning, with timestamps from its scheduled start. FFmpeg runs unpaced; Stream paces its output
    // by these timestamps, which works the same on every FFmpeg version.
    // Seek, when given, replaces the computed start position (a keyframe for copied video, see Stream).
    public static ProcessStartInfo Command(string executable, Airing item, StreamPlan plan, DateTimeOffset now, double? seek = null)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        void Args(params string[] values) { foreach (var value in values) start.ArgumentList.Add(value); }
        static string Num(double x) => x.ToString("0.000", CultureInfo.InvariantCulture);
        var from = item.Start > now ? item.Start : now;
        var seconds = Num(Math.Max(0.001, (item.End - from).TotalSeconds));
        Args("-hide_banner", "-loglevel", "error", "-nostdin", "-progress", "pipe:2", "-stats_period", "2");
        Args("-ss", Num(seek ?? item.Offset + (from - item.Start).TotalSeconds), "-i", item.Path);
        // Programs without audio get a silent track, so the stream layout never changes between programs.
        if (!plan.HasAudio) Args("-f", "lavfi", "-i", "anullsrc=channel_layout=stereo:sample_rate=48000");
        Args("-t", seconds, "-map", "0:v:0", "-map", plan.HasAudio ? "0:a:0?" : "1:a:0", "-sn", "-dn");
        var deinterlace = plan.Deinterlace ? "yadif," : "";
        if (plan.Normalize) Args("-vf", deinterlace + Fit(1280, 720) + ",fps=30,format=yuv420p", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "23", "-g", "60");
        else if (plan.CopyVideo) Args("-c:v", "copy");
        else Args("-vf", deinterlace + (plan.FrameWidth > 0 ? Fit(plan.FrameWidth, plan.FrameHeight) : "scale=w='min(iw,1920)':h='min(ih,1080)':force_original_aspect_ratio=decrease:force_divisible_by=2") + ",format=yuv420p",
            "-c:v", "libx264", "-preset", "ultrafast", "-crf", "23", "-force_key_frames", "expr:gte(t,n_forced*2)");
        if (plan.CopyAudio && plan.HasAudio && !plan.Normalize) Args("-c:a", "copy");
        else Args("-c:a", "aac", "-b:a", "192k", "-ar", "48000", "-ac", "2");
        Args("-output_ts_offset", Num(from.ToUnixTimeMilliseconds() / 1000.0), "-mpegts_flags", "+initial_discontinuity+resend_headers", "-mpegts_start_pid", "256", "-mpegts_pmt_start_pid", "4096", "-f", "mpegts", "pipe:1");
        return start;
    }

    public async Task<double?> Shape(Airing program, CancellationToken ct) => (await Format(program, ct))?.Shape;

    public async Task<string> Stream(Airing program, ChannelDefinition channel, double? shape, Func<byte[], Task> output, CancellationToken ct, Action<string, bool>? report = null)
    {
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(program.Path)) throw new FileNotFoundException("Scheduled media is missing.");
        var plan = StreamPlan.For(await Format(program, ct), channel, shape);
        var now = DateTimeOffset.UtcNow;
        report?.Invoke(plan.Describe(), false);
        Process started;
        try { started = Process.Start(Command(encoder.EncoderPath, program, plan, now, await ResumePoint(program, plan, now, ct))) ?? throw new EncoderUnavailableException(); }
        catch (System.ComponentModel.Win32Exception) { throw new EncoderUnavailableException(); }
        using var process = started;
        void Kill() { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { } }
        using var canceled = ct.Register(Kill);
        var errors = DrainErrors();
        long bytes = 0;
        var lastOutput = Environment.TickCount64;
        var stalled = false;
        using var watching = new CancellationTokenSource();
        var watchdog = Watch();
        try
        {
            ProcessStarted?.Invoke(process.Id);
            var buffer = new byte[188 * 64];
            int count;
            var clock = new TransportClock(program.Start > now ? program.Start : now);
            while (true)
            {
                // The watchdog times each read, not the pauses for pacing below.
                Volatile.Write(ref lastOutput, Environment.TickCount64);
                if ((count = await process.StandardOutput.BaseStream.ReadAsync(buffer, ct)) <= 0) break;
                // Hold video until it is due, less the lead. FFmpeg waits meanwhile, as its pipe fills.
                if (clock.Read(buffer.AsSpan(0, count)) is { } due && due - Lead - DateTimeOffset.UtcNow is { Ticks: > 0 } wait)
                {
                    Volatile.Write(ref lastOutput, Environment.TickCount64 + (long)wait.TotalMilliseconds);
                    await Task.Delay(wait, ct);
                }
                bytes += count;
                Volatile.Write(ref lastOutput, Environment.TickCount64);
                await output(buffer.AsSpan(0, count).ToArray());
            }
            await process.WaitForExitAsync(ct);
            await errors;
            if (stalled || process.ExitCode != 0 || bytes == 0)
                throw bytes == 0 && !stalled ? new IOException("The scheduled item could not produce a playable transport stream.") : new PlaybackInterruptedException(stalled, bytes > 0);
            return plan.Describe();
        }
        finally
        {
            watching.Cancel();
            try { await watchdog; } catch (OperationCanceledException) { }
            Kill();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch (TimeoutException) { }
            // Stderr is drained concurrently and never accumulated in memory.
            try { await errors.WaitAsync(TimeSpan.FromSeconds(5)); } catch (TimeoutException) { } catch (IOException) { }
        }
        // Stderr carries FFmpeg's progress. Encoding that produces less than 95% of real time over two
        // ten-second windows cannot keep up, and the stream will freeze: say so in Channel health.
        async Task DrainErrors()
        {
            long windowStart = Environment.TickCount64, windowOutput = -1;
            var slowWindows = 0;
            while (await process.StandardError.ReadLineAsync() is { } line)
            {
                if (slowWindows < 0 || !line.StartsWith("out_time_us=", StringComparison.Ordinal) || !long.TryParse(line.AsSpan(12), out var output)) continue;
                var elapsed = Environment.TickCount64 - windowStart;
                if (windowOutput < 0) (windowStart, windowOutput) = (Environment.TickCount64, output);
                else if (elapsed >= 10000)
                {
                    var speed = (output - windowOutput) / 1000.0 / elapsed;
                    slowWindows = speed < 0.95 ? slowWindows + 1 : 0;
                    (windowStart, windowOutput) = (Environment.TickCount64, output);
                    if (slowWindows < 2) continue;
                    report?.Invoke($"Encoding runs at {speed:0.00}× real time, too slow to keep up, so the stream freezes. Choose the 4:3 or own-shape picture, or turn off Always encode, so fewer programs are encoded.", true);
                    slowWindows = -1; // Report once per program.
                }
            }
        }
        async Task Watch()
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
            while (await timer.WaitForNextTickAsync(watching.Token))
            {
                var limit = Interlocked.Read(ref bytes) == 0 ? FirstOutputTimeout : StallTimeout;
                if (Environment.TickCount64 - Volatile.Read(ref lastOutput) < limit.TotalMilliseconds) continue;
                stalled = true;
                Kill();
                return;
            }
        }
    }

    // "16:9", "4:3" or "32:27" as a number; "0:1" and other unknown values as null.
    internal static double? Ratio(string? text)
    {
        var parts = text?.Split(':');
        return parts is [var a, var b] && double.TryParse(a, CultureInfo.InvariantCulture, out var x) && double.TryParse(b, CultureInfo.InvariantCulture, out var y) && x > 0 && y > 0 ? x / y : null;
    }

    // Jellyfin already stores each file's streams; FFprobe is only the fallback for unscanned files.
    // Copied video can only start on a keyframe. Started at a later cut, FFmpeg would begin at the keyframe
    // before it, stamped seconds before the break that just played, and players stutter or freeze on
    // timestamps that go backwards. So a later part starts exactly on that keyframe instead: it repeats
    // those few seconds after the break, with timestamps in order.
    private async Task<double?> ResumePoint(Airing program, StreamPlan plan, DateTimeOffset now, CancellationToken ct)
    {
        if (!plan.CopyVideo || plan.Normalize || program.Offset <= 0 || now > program.Start) return null;
        var keyframes = await RunProbe(["-v", "error", "-select_streams", "v:0", "-skip_frame", "nokey", "-show_entries", "frame=pts_time", "-of", "csv=p=0",
            "-read_intervals", $"{Math.Max(0, program.Offset - 15).ToString("0.###", CultureInfo.InvariantCulture)}%{(program.Offset + 0.05).ToString("0.###", CultureInfo.InvariantCulture)}", program.Path], ct);
        var before = keyframes?.Split('\n').Select(x => double.TryParse(x.Trim().TrimEnd(','), CultureInfo.InvariantCulture, out var t) ? t : -1).Where(x => x >= 0 && x <= program.Offset + 0.05).ToArray();
        return before is { Length: > 0 } ? before.Max() : null;
    }

    // Shape and Stream both need the format; keep the last few so FFprobe runs once per file.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SourceFormat?> formats = new();

    private async Task<SourceFormat?> Format(Airing program, CancellationToken ct)
    {
        if (formats.TryGetValue(program.Path, out var known)) return known;
        var format = await ReadFormat(program, ct);
        if (formats.Count >= 32) formats.Clear();
        return formats[program.Path] = format;
    }

    private async Task<SourceFormat?> ReadFormat(Airing program, CancellationToken ct)
    {
        try
        {
            var streams = sources?.GetMediaStreams(program.ItemId);
            if (streams is { Count: > 0 })
            {
                var video = streams.Where(x => x.Type == MediaStreamType.Video).OrderBy(x => x.Index).FirstOrDefault();
                var audio = streams.Where(x => x.Type == MediaStreamType.Audio).OrderBy(x => x.Index).FirstOrDefault();
                if (video is not null)
                {
                    var display = Ratio(video.AspectRatio);
                    var pixel = display is not null && video.Width > 0 && video.Height > 0 ? display / ((double)video.Width.Value / video.Height.Value) : null;
                    return new(video.Codec?.ToLowerInvariant(), video.PixelFormat, video.BitDepth, video.IsInterlaced, audio?.Codec?.ToLowerInvariant(), audio?.Channels, audio?.SampleRate,
                        video.Width, video.Height, pixel, display);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { }
        return await Probe(program.Path, ct);
    }

    // Runs FFprobe with a ten-second limit; null when it is unavailable or fails.
    private async Task<string?> RunProbe(string[] args, CancellationToken ct)
    {
        string probe;
        try { probe = encoder.ProbePath; }
        catch (NotSupportedException) { return null; }
        if (string.IsNullOrEmpty(probe)) return null;
        var start = new ProcessStartInfo(probe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        try
        {
            using var process = Process.Start(start);
            if (process is null) return null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                var errors = process.StandardError.ReadToEndAsync(timeout.Token);
                var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                await errors;
                return process.ExitCode == 0 ? output : null;
            }
            finally { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { return null; }
    }

    private async Task<SourceFormat?> Probe(string path, CancellationToken ct)
    {
        var json = await RunProbe(["-v", "error", "-show_entries", "stream=index,codec_type,codec_name,pix_fmt,bits_per_raw_sample,field_order,channels,sample_rate,width,height,sample_aspect_ratio,display_aspect_ratio", "-of", "json", path], ct);
        if (json is null) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var all = document.RootElement.GetProperty("streams").EnumerateArray().ToArray();
            var video = all.FirstOrDefault(x => Text(x, "codec_type") == "video");
            var audio = all.FirstOrDefault(x => Text(x, "codec_type") == "audio");
            if (video.ValueKind != JsonValueKind.Object) return null;
            var hasAudio = audio.ValueKind == JsonValueKind.Object;
            return new(Text(video, "codec_name"), Text(video, "pix_fmt"), Number(video, "bits_per_raw_sample"), Text(video, "field_order") is { } order && order != "progressive" && order != "unknown",
                hasAudio ? Text(audio, "codec_name") : null, hasAudio ? Number(audio, "channels") : null, hasAudio ? Number(audio, "sample_rate") : null,
                Number(video, "width"), Number(video, "height"), Ratio(Text(video, "sample_aspect_ratio")), Ratio(Text(video, "display_aspect_ratio")));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
        static string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        static int? Number(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.Number ? v.GetInt32() : int.TryParse(v.GetString(), CultureInfo.InvariantCulture, out var n) ? n : null : null;
    }
}

// Reads MPEG-TS timestamps (PTS) as the stream passes, to tell when each piece of video is due on the
// wall clock. Timestamps wrap every 26.5 hours, so they are placed nearest the program's scheduled time.
public sealed class TransportClock(DateTimeOffset around)
{
    private const double Wrap = 8589934592 / 90000.0;
    // FFmpeg's MPEG-TS muxer starts timestamps this far after the requested offset.
    private const double MuxDelay = 0.7;
    private readonly byte[] partial = new byte[188];
    private int filled;
    private double expected = around.ToUnixTimeMilliseconds() / 1000.0;

    // The latest due time in this data, or null when it carries no timestamp.
    public DateTimeOffset? Read(ReadOnlySpan<byte> data)
    {
        double? latest = null;
        while (data.Length > 0)
        {
            var take = Math.Min(188 - filled, data.Length);
            data[..take].CopyTo(partial.AsSpan(filled));
            filled += take;
            data = data[take..];
            if (filled < 188) break;
            filled = 0;
            if (Pts(partial) is { } pts)
            {
                var seconds = pts / 90000.0;
                seconds += Math.Round((expected - seconds) / Wrap) * Wrap;
                expected = seconds;
                latest = Math.Max(latest ?? seconds, seconds);
            }
        }
        return latest is { } at ? DateTimeOffset.FromUnixTimeMilliseconds((long)((at - MuxDelay) * 1000)) : null;
    }

    // The PTS of a packet that starts a PES packet, in 90 kHz units.
    internal static long? Pts(ReadOnlySpan<byte> packet)
    {
        if (packet[0] != 0x47 || (packet[1] & 0x40) == 0) return null;
        var payload = (packet[3] & 0x30) switch { 0x10 => 4, 0x30 => 5 + packet[4], _ => -1 };
        if (payload < 0 || payload + 14 > 188) return null;
        var pes = packet[payload..];
        if (pes[0] != 0 || pes[1] != 0 || pes[2] != 1 || (pes[7] & 0x80) == 0) return null;
        return ((long)(pes[9] >> 1) & 7) << 30 | (long)pes[10] << 22 | (long)(pes[11] >> 1) << 15 | (long)pes[12] << 7 | (long)pes[13] >> 1;
    }
}

public sealed class EncoderUnavailableException : InvalidOperationException
{
    public EncoderUnavailableException() : base("Jellyfin FFmpeg could not start. Check the server's FFmpeg configuration.") { }
}

// Playback started, then stalled or stopped early. The same program can be restarted from the current position.
public sealed class PlaybackInterruptedException(bool stalled, bool producedOutput) : IOException(stalled ? "Playback stalled." : "Playback stopped unexpectedly.")
{
    public bool Stalled { get; } = stalled;
    public bool ProducedOutput { get; } = producedOutput;
}
