using System.Diagnostics;
using System.Text.Json;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LibraryTV;

// Method is "audio" or "picture" when credits were found, "none" when a scan found nothing and "failed"
// when the file could not be read; those two are retried later.
public sealed record DetectedCredits(string Path, long Size, long Modified, double Start, double End, string Method, DateTimeOffset ScannedAt);

public sealed class CreditsStore(IApplicationPaths paths)
{
    private readonly object gate = new();
    private Dictionary<Guid, DetectedCredits>? entries;
    private int generation;
    public string StorePath => Path.Combine(paths.DataPath, "library-tv", "credits.json");

    // Raised after a scan pass finds new credits, so the planned guide is rebuilt once to use them.
    public int Generation { get { Load(); return Volatile.Read(ref generation); } }

    public (double Start, double End)? For(Media media)
    {
        Load();
        lock (gate)
            return entries!.TryGetValue(media.Id, out var found) && found.Method is "audio" or "picture" && found.Path == media.Path ? (found.Start, found.End) : null;
    }

    public DetectedCredits? Entry(Guid id)
    {
        Load();
        lock (gate) return entries!.GetValueOrDefault(id);
    }

    public (int Found, int Scanned) Counts()
    {
        Load();
        lock (gate) return (entries!.Values.Count(x => x.Method is "audio" or "picture"), entries.Count);
    }

    public void Put(Guid id, DetectedCredits credits)
    {
        Load();
        lock (gate) entries![id] = credits;
    }

    public void Advance() { Load(); Interlocked.Increment(ref generation); }

    public void Save()
    {
        Load();
        string json;
        lock (gate) json = JsonSerializer.Serialize(new StoredCredits(generation, entries!));
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        File.WriteAllText(StorePath + ".tmp", json);
        File.Move(StorePath + ".tmp", StorePath, true);
    }

    private void Load()
    {
        if (Volatile.Read(ref entries) is not null) return;
        lock (gate)
        {
            if (entries is not null) return;
            StoredCredits? stored = null;
            try { if (File.Exists(StorePath)) stored = JsonSerializer.Deserialize<StoredCredits>(File.ReadAllText(StorePath)); }
            catch (Exception ex) when (ex is JsonException or IOException) { stored = null; }
            generation = stored?.Generation ?? 0;
            Volatile.Write(ref entries, stored?.Items ?? []);
        }
    }

    private sealed record StoredCredits(int Generation, Dictionary<Guid, DetectedCredits> Items);
}

public sealed record CreditsScanStatus(bool Enabled, bool Running, int Programs, int Scanned, int Found, int Remaining, string? Current, DateTimeOffset? LastPass);

// Scans every program on the enabled channels in the background, one file at a time at low priority, after
// Jellyfin has started and the first schedule exists. Each file is read once; changed files are read again.
public sealed class CreditsScanner(IServiceProvider services, CreditsStore store, StartupGate startup, ILogger<CreditsScanner> log) : BackgroundService
{
    private static readonly TimeSpan RetryNone = TimeSpan.FromDays(14), RetryFailed = TimeSpan.FromDays(1);
    private volatile CreditsScanStatus status = new(true, false, 0, 0, 0, 0, null, null);
    public CreditsScanStatus Status => status;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await startup.WaitAsync(stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        var scheduler = services.GetRequiredService<Scheduler>();
        var catalog = services.GetRequiredService<Catalog>();
        var encoder = services.GetRequiredService<IMediaEncoder>();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (scheduler.LastRefresh is not null) await Pass(scheduler, catalog, encoder, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogWarning(ex, "Library TV credits detection pass failed; will retry"); }
            status = status with { Running = false, Current = null };
            await Task.Delay(scheduler.LastRefresh is null ? TimeSpan.FromMinutes(1) : TimeSpan.FromMinutes(Math.Clamp(Plugin.Instance.Configuration.RefreshMinutes, 5, 1440)), stoppingToken);
        }
    }

    private async Task Pass(Scheduler scheduler, Catalog catalog, IMediaEncoder encoder, CancellationToken ct)
    {
        var config = Plugin.Instance.Configuration;
        var (found, scanned) = store.Counts();
        if (!config.DetectCredits) { status = new(false, false, 0, scanned, found, 0, null, status.LastPass); return; }
        var programs = Programs(scheduler, catalog, config);
        // Programs with a credits chapter or Outro segment already have their credits marked.
        var marked = catalog.CreateCreditsReader(detected: false);
        var now = DateTimeOffset.UtcNow;
        var due = programs.Where(x => Due(x, now) && marked(x) is null).Select(x => x.Id).ToHashSet();
        status = new(true, due.Count > 0, programs.Count, scanned, found, due.Count, null, status.LastPass);
        if (due.Count == 0) return;
        // Whatever airs soonest is scanned first. Episodes are scanned by show, next to their neighbours.
        var airs = scheduler.Guide.Where(x => x.End > now).GroupBy(x => x.ItemId).ToDictionary(x => x.Key, x => x.Min(a => a.Start));
        DateTimeOffset Soonest(IEnumerable<Media> items) => items.Where(x => due.Contains(x.Id)).Select(x => airs.GetValueOrDefault(x.Id, DateTimeOffset.MaxValue)).Min();
        var units = programs.GroupBy(x => x.Series.Length > 0 ? x.Series : x.Id.ToString())
            .Select(x => x.OrderBy(m => m.Season).ThenBy(m => m.Episode).ToList())
            .Where(x => x.Any(m => due.Contains(m.Id))).OrderBy(Soonest).ToList();
        var added = false;
        foreach (var unit in units)
        {
            ct.ThrowIfCancellationRequested();
            if (!Plugin.Instance.Configuration.DetectCredits) break;
            var fingerprints = new Dictionary<Guid, uint[]?>();
            async Task<uint[]?> Fingerprint(Media media) => fingerprints.TryGetValue(media.Id, out var known) ? known : fingerprints[media.Id] = await AudioPrint(encoder, media, ct);
            for (var i = 0; i < unit.Count; i++)
            {
                var media = unit[i];
                if (!due.Contains(media.Id)) continue;
                status = status with { Current = media.Title };
                (double Start, double End)? credits = null;
                var method = "none";
                var readable = true;
                if (media.Series.Length > 0 && unit.Count > 1)
                {
                    var print = await Fingerprint(media);
                    readable = print is not null;
                    // The next episode first, then the previous one, then any other in the show.
                    foreach (var other in unit.Skip(i + 1).Take(1).Concat(unit.Take(i).Reverse().Take(1)).Concat(unit.Where((_, j) => j != i && j != i + 1 && j != i - 1).Take(2)))
                    {
                        if (print is null || await Fingerprint(other) is not { } otherPrint) continue;
                        if (CreditsDetector.Match(print, otherPrint) is not { } match) continue;
                        var (start, end) = CreditsDetector.AudioTimes(media.Seconds, match.StartA, match.EndA);
                        // Theme music near the end of an episode: the credits, unless it is a short sting.
                        if (start < media.Seconds * 0.75) continue;
                        credits = (start, media.Seconds - end <= 5 ? media.Seconds : end);
                        method = "audio";
                        break;
                    }
                }
                if (credits is null)
                {
                    var frames = await PictureFrames(encoder, media, ct);
                    readable = frames is not null;
                    if (frames is not null && CreditsDetector.BlackRun(frames, media.Seconds) is { } run)
                    {
                        credits = (run.Start, media.Seconds - run.End <= 5 ? media.Seconds : run.End);
                        method = "picture";
                    }
                }
                var file = Stat(media.Path);
                store.Put(media.Id, new DetectedCredits(media.Path, file.Size, file.Modified, credits?.Start ?? 0, credits?.End ?? 0, credits is null ? readable ? "none" : "failed" : method, DateTimeOffset.UtcNow));
                added |= credits is not null;
                due.Remove(media.Id);
                (found, scanned) = store.Counts();
                status = status with { Scanned = scanned, Found = found, Remaining = due.Count };
            }
            store.Save();
        }
        if (due.Count > 0) return; // Turned off part way: keep what was found without replanning.
        status = status with { LastPass = DateTimeOffset.UtcNow };
        if (!added) return;
        store.Advance();
        store.Save();
        try { await scheduler.Refresh(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Library TV could not refresh the guide with new credits"); }
    }

    private static List<Media> Programs(Scheduler scheduler, Catalog catalog, Configuration config)
    {
        var read = catalog.CreateRefreshReader();
        var readClips = catalog.CreateRefreshReader(true);
        var clips = scheduler.Channels.SelectMany(x => new[] { x.Commercials, x.Bumpers }).Append(config.SharedCommercials).Append(config.SharedBumpers)
            .SelectMany(readClips).Select(x => x.Id).ToHashSet();
        return scheduler.Channels.SelectMany(x => x.Blocks.Select(b => b.Source).Append(x.Source)).SelectMany(read)
            .Where(x => !clips.Contains(x.Id) && x.Seconds >= 120).DistinctBy(x => x.Id).ToList();
    }

    private bool Due(Media media, DateTimeOffset now)
    {
        if (store.Entry(media.Id) is not { } entry) return true;
        var file = Stat(media.Path);
        if (entry.Path != media.Path || entry.Size != file.Size || entry.Modified != file.Modified) return true;
        return entry.Method switch { "none" => now - entry.ScannedAt > RetryNone, "failed" => now - entry.ScannedAt > RetryFailed, _ => false };
    }

    private static (long Size, long Modified) Stat(string path)
    {
        try { var info = new FileInfo(path); return (info.Length, info.LastWriteTimeUtc.Ticks); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (0, 0); }
    }

    internal static async Task<uint[]?> AudioPrint(IMediaEncoder encoder, Media media, CancellationToken ct)
    {
        var window = CreditsDetector.AudioWindow(media.Seconds);
        var result = await Run(encoder, ["-ss", Seconds(media.Seconds - window), "-i", media.Path, "-t", Seconds(window), "-map", "0:a:0", "-vn", "-sn", "-dn", "-ac", "1",
            "-f", "chromaprint", "-fp_format", "raw", "-"], "error", TimeSpan.FromMinutes(3), ct);
        return result is { Output.Length: >= 4 } ? CreditsDetector.ParseFingerprint(result.Value.Output) : null;
    }

    internal static async Task<List<(double Time, int Black)>?> PictureFrames(IMediaEncoder encoder, Media media, CancellationToken ct)
    {
        var window = CreditsDetector.PictureWindow(media.Seconds);
        var from = Math.Max(0, media.Seconds - window);
        // Only keyframes are decoded, and at a tiny size, so this reads little and costs little.
        var result = await Run(encoder, ["-skip_frame", "nokey", "-ss", Seconds(from), "-i", media.Path, "-t", Seconds(window), "-map", "0:v:0", "-an", "-sn", "-dn",
            "-vf", "scale=160:90,blackframe=amount=0:threshold=32", "-f", "null", "-"], "info", TimeSpan.FromMinutes(5), ct);
        if (result is null) return null;
        var frames = CreditsDetector.ParseBlackFrames(result.Value.Log, from);
        return frames.Count > 0 ? frames : null;
    }

    private static string Seconds(double value) => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    private static async Task<(byte[] Output, string Log)?> Run(IMediaEncoder encoder, string[] args, string level, TimeSpan limit, CancellationToken ct)
    {
        string ffmpeg;
        try { ffmpeg = encoder.EncoderPath; }
        catch (NotSupportedException) { return null; }
        if (string.IsNullOrEmpty(ffmpeg)) return null;
        var start = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in new[] { "-nostdin", "-hide_banner", "-nostats", "-v", level, "-threads", "1" }.Concat(args)) start.ArgumentList.Add(arg);
        try
        {
            using var process = Process.Start(start);
            if (process is null) return null;
            // Never compete with live channels for the processor.
            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or PlatformNotSupportedException) { }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(limit);
            try
            {
                var errors = process.StandardError.ReadToEndAsync(timeout.Token);
                using var output = new MemoryStream();
                await process.StandardOutput.BaseStream.CopyToAsync(output, timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                var log = await errors;
                return process.ExitCode == 0 ? (output.ToArray(), log) : null;
            }
            finally { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { return null; }
    }
}
