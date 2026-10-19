using System.Diagnostics;
using System.Globalization;
using MediaBrowser.Controller.MediaEncoding;

namespace Jellyfin.Plugin.LibraryTV;

public interface IProgramStreamer
{
    Task Stream(Airing program, bool transcode, Func<byte[], Task> output, CancellationToken ct);
}

public sealed class ProgramStreamer(IMediaEncoder encoder) : IProgramStreamer
{
    internal Action<int>? ProcessStarted { get; set; }
    public static ProcessStartInfo Command(string executable, Airing item, bool transcode, DateTimeOffset now)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        void Args(params string[] values) { foreach (var value in values) start.ArgumentList.Add(value); }
        static string Num(double x) => x.ToString("0.000", CultureInfo.InvariantCulture);
        Args("-hide_banner", "-loglevel", "error", "-nostdin", "-re", "-ss", Num(Math.Max(0, (now - item.Start).TotalSeconds)), "-i", item.Path,
            "-t", Num(Math.Max(0.001, (item.End - now).TotalSeconds)), "-map", "0:v:0", "-map", "0:a:0?", "-sn", "-dn");
        if (transcode) Args("-vf", "scale=1280:720:force_original_aspect_ratio=decrease,pad=1280:720:(ow-iw)/2:(oh-ih)/2,fps=30,format=yuv420p", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "23", "-g", "60", "-c:a", "aac", "-ar", "48000", "-ac", "2");
        else Args("-c", "copy");
        Args("-output_ts_offset", Num(now.ToUnixTimeMilliseconds() / 1000.0), "-mpegts_flags", "+initial_discontinuity+resend_headers", "-mpegts_start_pid", "256", "-mpegts_pmt_start_pid", "4096", "-f", "mpegts", "pipe:1");
        return start;
    }

    public async Task Stream(Airing program, bool transcode, Func<byte[], Task> output, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(program.Path)) throw new FileNotFoundException("Scheduled media is missing.");
        Process started;
        try { started = Process.Start(Command(encoder.EncoderPath, program, transcode, DateTimeOffset.UtcNow)) ?? throw new EncoderUnavailableException(); }
        catch (System.ComponentModel.Win32Exception) { throw new EncoderUnavailableException(); }
        using var process = started;
        void Kill() { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { } }
        using var canceled = ct.Register(Kill);
        var errors = DrainErrors();
        long bytes = 0;
        try
        {
            ProcessStarted?.Invoke(process.Id);
            var buffer = new byte[188 * 64];
            int count;
            while ((count = await process.StandardOutput.BaseStream.ReadAsync(buffer, ct)) > 0)
            {
                bytes += count;
                await output(buffer.AsSpan(0, count).ToArray());
            }
            await process.WaitForExitAsync(ct);
            await errors;
            if (process.ExitCode != 0 || bytes == 0) throw new IOException("The scheduled item could not produce a playable transport stream.");
        }
        finally
        {
            Kill();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch (TimeoutException) { }
            // Stderr is drained concurrently and never accumulated in memory.
            try { await errors.WaitAsync(TimeSpan.FromSeconds(5)); } catch (TimeoutException) { } catch (IOException) { }
        }
        async Task DrainErrors() { while (await process.StandardError.ReadLineAsync() is not null) { } }
    }
}

public sealed class EncoderUnavailableException : InvalidOperationException
{
    public EncoderUnavailableException() : base("Jellyfin FFmpeg could not start. Check the server's FFmpeg configuration.") { }
}
