using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.LibraryTV;

public interface IProgramStreamer
{
    // Returns how the program was played, for Channel health.
    Task<string> Stream(Airing program, ChannelDefinition channel, Func<byte[], Task> output, CancellationToken ct);
}

// What the file holds, from Jellyfin's library metadata or FFprobe. Null means unknown.
// PixelAspect is the sample aspect ratio (1 for square pixels); DisplayAspect is the picture's shape.
public sealed record SourceFormat(string? Video, string? PixelFormat, int? BitDepth, bool Interlaced, string? Audio, int? Channels, int? SampleRate,
    int? Width = null, int? Height = null, double? PixelAspect = null, double? DisplayAspect = null);

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
    // shape would be stretched. Video in another shape, or with non-square pixels, is therefore encoded
    // into the channel's picture shape with black bars; programs already in that shape are still copied.
    public static StreamPlan For(SourceFormat? format, ChannelDefinition channel)
    {
        var hasAudio = format is null || format.Audio is not null;
        if (channel.AlwaysEncode) return Encode with { HasAudio = hasAudio, Deinterlace = format?.Interlaced == true };
        double? shape = channel.PictureShape switch { "off" => null, "4:3" => 4.0 / 3, _ => 16.0 / 9 };
        if (format is null) return new(false, false, FrameWidth: shape is null ? 0 : Even(720 * shape.Value), FrameHeight: shape is null ? 0 : 720);
        var pixel = format.PixelAspect ?? 1;
        var display = format.DisplayAspect ?? (format.Width > 0 && format.Height > 0 ? format.Width.Value * pixel / format.Height.Value : null);
        var fits = shape is null || display is null || Math.Abs(pixel - 1) <= 0.02 && Math.Abs(display.Value - shape.Value) / shape.Value <= 0.03;
        var video = format.Video == "h264" && format.PixelFormat is null or "yuv420p" or "yuvj420p" && format.BitDepth is null or 8 && !format.Interlaced && fits;
        // Audio is cheap to encode, so anything but stereo 48 kHz AAC is converted to keep one audio layout.
        var audio = format.Audio == "aac" && format.Channels is null or 2 && format.SampleRate is null or 48000;
        var plan = new StreamPlan(video, audio, hasAudio, format.Interlaced);
        if (video || shape is not { } target) return plan;
        // Keep the source's size where possible: wider pictures fit the width, narrower ones the height.
        var width = (format.Width ?? 1280) * pixel;
        var height = (double)(format.Height ?? 720);
        var wide = (display ?? target) >= target;
        var frameHeight = wide ? Math.Min(width, 1920) / target : Math.Min(height, 1080);
        return plan with { FrameWidth = Even(frameHeight * target), FrameHeight = Even(frameHeight) };
    }

    private static int Even(double x) => (int)Math.Round(x / 2) * 2;

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

    public static ProcessStartInfo Command(string executable, Airing item, StreamPlan plan, DateTimeOffset now)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        void Args(params string[] values) { foreach (var value in values) start.ArgumentList.Add(value); }
        static string Num(double x) => x.ToString("0.000", CultureInfo.InvariantCulture);
        var seconds = Num(Math.Max(0.001, (item.End - now).TotalSeconds));
        Args("-hide_banner", "-loglevel", "error", "-nostdin", "-re", "-ss", Num(item.Offset + Math.Max(0, (now - item.Start).TotalSeconds)), "-i", item.Path);
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
        Args("-output_ts_offset", Num(now.ToUnixTimeMilliseconds() / 1000.0), "-mpegts_flags", "+initial_discontinuity+resend_headers", "-mpegts_start_pid", "256", "-mpegts_pmt_start_pid", "4096", "-f", "mpegts", "pipe:1");
        return start;
    }

    public async Task<string> Stream(Airing program, ChannelDefinition channel, Func<byte[], Task> output, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(program.Path)) throw new FileNotFoundException("Scheduled media is missing.");
        var plan = StreamPlan.For(await Format(program, ct), channel);
        Process started;
        try { started = Process.Start(Command(encoder.EncoderPath, program, plan, DateTimeOffset.UtcNow)) ?? throw new EncoderUnavailableException(); }
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
            while ((count = await process.StandardOutput.BaseStream.ReadAsync(buffer, ct)) > 0)
            {
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
        async Task DrainErrors() { while (await process.StandardError.ReadLineAsync() is not null) { } }
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
    private async Task<SourceFormat?> Format(Airing program, CancellationToken ct)
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

    private async Task<SourceFormat?> Probe(string path, CancellationToken ct)
    {
        string probe;
        try { probe = encoder.ProbePath; }
        catch (NotSupportedException) { return null; }
        if (string.IsNullOrEmpty(probe)) return null;
        var start = new ProcessStartInfo(probe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in new[] { "-v", "error", "-show_entries", "stream=index,codec_type,codec_name,pix_fmt,bits_per_raw_sample,field_order,channels,sample_rate,width,height,sample_aspect_ratio,display_aspect_ratio", "-of", "json", path }) start.ArgumentList.Add(arg);
        try
        {
            using var process = Process.Start(start);
            if (process is null) return null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                var json = await process.StandardOutput.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                await errors;
                if (process.ExitCode != 0) return null;
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
            finally { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
        static string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        static int? Number(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.Number ? v.GetInt32() : int.TryParse(v.GetString(), CultureInfo.InvariantCulture, out var n) ? n : null : null;
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
