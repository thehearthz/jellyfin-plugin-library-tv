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
    Task<string> Stream(Airing program, bool alwaysEncode, Func<byte[], Task> output, CancellationToken ct);
}

// What the file holds, from Jellyfin's library metadata or FFprobe. Null means unknown.
public sealed record SourceFormat(string? Video, string? PixelFormat, int? BitDepth, bool Interlaced, string? Audio, int? Channels, int? SampleRate);

// Normalize is the always-encode 720p mode; otherwise each stream is copied when compatible.
public sealed record StreamPlan(bool CopyVideo, bool CopyAudio, bool HasAudio = true, bool Deinterlace = false, bool Normalize = false)
{
    public static readonly StreamPlan Copy = new(true, true);
    public static readonly StreamPlan Encode = new(false, false, Normalize: true);

    // 8-bit H.264 video and stereo AAC audio play on almost every client without Jellyfin re-encoding
    // them, so they are copied. Anything else is encoded to those formats, so every program on a channel
    // reaches the client in the same codecs. Changing codecs mid-stream is a common cause of freezes.
    public static StreamPlan For(SourceFormat? format, bool alwaysEncode)
    {
        if (alwaysEncode) return Encode with { HasAudio = format?.Audio is not null || format is null };
        if (format is null) return new(false, false);
        var video = format.Video == "h264" && format.PixelFormat is null or "yuv420p" or "yuvj420p" && format.BitDepth is null or 8 && !format.Interlaced;
        // Audio is cheap to encode, so anything but stereo 48 kHz AAC is converted to keep one audio layout.
        var audio = format.Audio == "aac" && format.Channels is null or 2 && format.SampleRate is null or 48000;
        return new(video, audio, format.Audio is not null, format.Interlaced);
    }

    public string Describe() => Normalize ? "Encoding to 720p H.264/AAC (always encode)"
        : CopyVideo && CopyAudio ? "Direct: video and audio copied without encoding"
        : CopyVideo ? HasAudio ? "Video copied; audio encoded to AAC stereo" : "Video copied; silent audio track added"
        : CopyAudio ? "Video encoded to H.264; audio copied" : "Video encoded to H.264; audio encoded to AAC stereo";
}

public sealed class ProgramStreamer(IMediaEncoder encoder, IMediaSourceManager? sources = null) : IProgramStreamer
{
    internal Action<int>? ProcessStarted { get; set; }
    // A live FFmpeg process that sends nothing for this long is stalled, for example on a hung network mount.
    internal TimeSpan FirstOutputTimeout { get; set; } = TimeSpan.FromSeconds(20);
    internal TimeSpan StallTimeout { get; set; } = TimeSpan.FromSeconds(12);

    public static ProcessStartInfo Command(string executable, Airing item, StreamPlan plan, DateTimeOffset now)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        void Args(params string[] values) { foreach (var value in values) start.ArgumentList.Add(value); }
        static string Num(double x) => x.ToString("0.000", CultureInfo.InvariantCulture);
        var seconds = Num(Math.Max(0.001, (item.End - now).TotalSeconds));
        Args("-hide_banner", "-loglevel", "error", "-nostdin", "-re", "-ss", Num(Math.Max(0, (now - item.Start).TotalSeconds)), "-i", item.Path);
        // Programs without audio get a silent track, so the stream layout never changes between programs.
        if (!plan.HasAudio) Args("-f", "lavfi", "-i", "anullsrc=channel_layout=stereo:sample_rate=48000");
        Args("-t", seconds, "-map", "0:v:0", "-map", plan.HasAudio ? "0:a:0?" : "1:a:0", "-sn", "-dn");
        if (plan.Normalize) Args("-vf", "scale=1280:720:force_original_aspect_ratio=decrease,pad=1280:720:(ow-iw)/2:(oh-ih)/2,fps=30,format=yuv420p", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "23", "-g", "60");
        else if (plan.CopyVideo) Args("-c:v", "copy");
        else Args("-vf", (plan.Deinterlace ? "yadif," : "") + "scale=w='min(iw,1920)':h='min(ih,1080)':force_original_aspect_ratio=decrease:force_divisible_by=2,format=yuv420p",
            "-c:v", "libx264", "-preset", "ultrafast", "-crf", "23", "-force_key_frames", "expr:gte(t,n_forced*2)");
        if (plan.CopyAudio && plan.HasAudio && !plan.Normalize) Args("-c:a", "copy");
        else Args("-c:a", "aac", "-b:a", "192k", "-ar", "48000", "-ac", "2");
        Args("-output_ts_offset", Num(now.ToUnixTimeMilliseconds() / 1000.0), "-mpegts_flags", "+initial_discontinuity+resend_headers", "-mpegts_start_pid", "256", "-mpegts_pmt_start_pid", "4096", "-f", "mpegts", "pipe:1");
        return start;
    }

    public async Task<string> Stream(Airing program, bool alwaysEncode, Func<byte[], Task> output, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(program.Path)) throw new FileNotFoundException("Scheduled media is missing.");
        var plan = StreamPlan.For(await Format(program, ct), alwaysEncode);
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
                if (video is not null) return new(video.Codec?.ToLowerInvariant(), video.PixelFormat, video.BitDepth, video.IsInterlaced, audio?.Codec?.ToLowerInvariant(), audio?.Channels, audio?.SampleRate);
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
        foreach (var arg in new[] { "-v", "error", "-show_entries", "stream=index,codec_type,codec_name,pix_fmt,bits_per_raw_sample,field_order,channels,sample_rate", "-of", "json", path }) start.ArgumentList.Add(arg);
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
                    hasAudio ? Text(audio, "codec_name") : null, hasAudio ? Number(audio, "channels") : null, hasAudio ? Number(audio, "sample_rate") : null);
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
