using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LibraryTV;

// How loud a file is (EBU R128 integrated loudness, LUFS) and its true peak (dBTP), measured once ahead of time.
// Method is "measured", "silent" (too quiet to level: left alone) or "failed" (retried later).
public sealed record MeasuredLoudness(string Path, long Size, long Modified, double Integrated, double Peak, string Method, DateTimeOffset ScannedAt);

public sealed class LoudnessStore(IApplicationPaths paths)
{
    // Broadcast TV's level (ATSC A/85). Boosts are capped so quiet, noisy clips aren't pumped up, and cuts so
    // nothing is ever made nearly silent.
    public const double Target = -24, MaxBoost = 12, MaxCut = -18;
    private readonly object gate = new();
    private Dictionary<Guid, MeasuredLoudness>? entries;
    public string StorePath => Path.Combine(paths.DataPath, "library-tv", "loudness.json");

    // The fixed gain that brings a measured file to the target, or null when it hasn't been measured (or is silent).
    public double? Gain(Guid id, string path)
    {
        var entry = Entry(id);
        return entry is { Method: "measured" } && entry.Path == path ? GainFor(entry.Integrated) : null;
    }

    internal static double GainFor(double integrated) => Math.Round(Math.Clamp(Target - integrated, MaxCut, MaxBoost), 1);

    public MeasuredLoudness? Entry(Guid id) { Load(); lock (gate) return entries!.GetValueOrDefault(id); }
    public int Count(string method) { Load(); lock (gate) return entries!.Values.Count(x => x.Method == method); }
    public void Put(Guid id, MeasuredLoudness entry) { Load(); lock (gate) entries![id] = entry; }

    public void Save()
    {
        Load();
        string json;
        lock (gate) json = JsonSerializer.Serialize(entries);
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
            Dictionary<Guid, MeasuredLoudness>? stored = null;
            try { if (File.Exists(StorePath)) stored = JsonSerializer.Deserialize<Dictionary<Guid, MeasuredLoudness>>(File.ReadAllText(StorePath)); }
            catch (Exception ex) when (ex is JsonException or IOException) { stored = null; }
            Volatile.Write(ref entries, stored ?? []);
        }
    }
}

public sealed record LoudnessScanStatus(bool Enabled, bool Running, int Files, int Measured, int Remaining, string? Current, DateTimeOffset? LastPass);

// Measures, in the background and one file at a time at low priority, the loudness of everything a channel
// levels: commercials and bumpers, and programs on channels that level everything. Clips are measured whole;
// long programs from up to three ten-minute stretches. Changed files are measured again.
public sealed class LoudnessScanner(IServiceProvider services, LoudnessStore store, StartupGate startup, ILogger<LoudnessScanner> log) : BackgroundService
{
    private static readonly TimeSpan RetryFailed = TimeSpan.FromDays(1);
    private volatile LoudnessScanStatus status = new(false, false, 0, 0, 0, null, null);
    public LoudnessScanStatus Status => status;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await startup.WaitAsync(stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        var scheduler = services.GetRequiredService<Scheduler>();
        var catalog = services.GetRequiredService<Catalog>();
        var encoder = services.GetRequiredService<IMediaEncoder>();
        while (!stoppingToken.IsCancellationRequested)
        {
            try { if (scheduler.LastRefresh is not null) await Pass(scheduler, catalog, encoder, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogWarning(ex, "Library TV loudness measuring failed; will retry"); }
            status = status with { Running = false, Current = null };
            await Task.Delay(scheduler.LastRefresh is null ? TimeSpan.FromMinutes(1) : TimeSpan.FromMinutes(Math.Clamp(Plugin.Instance.Configuration.RefreshMinutes, 5, 1440)), stoppingToken);
        }
    }

    // What needs measuring: soonest to air first.
    internal static List<Media> Wanted(Scheduler scheduler, Catalog catalog, Configuration config)
    {
        var channels = scheduler.Channels.Where(x => x.NormalizeVolume is "commercials" or "all").ToArray();
        if (channels.Length == 0) return [];
        var readClips = catalog.CreateRefreshReader(true);
        var read = catalog.CreateRefreshReader();
        var shared = channels.Any(x => x.Commercials.LibraryIds.Length + x.Commercials.FolderIds.Length + x.Commercials.ItemIds.Length == 0) ? [config.SharedCommercials, config.SharedBumpers] : Array.Empty<Pool>();
        var media = channels.SelectMany(x => new[] { x.Commercials, x.Bumpers }).Concat(shared).SelectMany(readClips)
            .Concat(channels.Where(x => x.NormalizeVolume == "all").SelectMany(x => x.Blocks.Select(b => b.Source).Append(x.Source)).SelectMany(read))
            .DistinctBy(x => x.Id).ToList();
        var now = DateTimeOffset.UtcNow;
        var airs = scheduler.Guide.Where(x => x.End > now).GroupBy(x => x.ItemId).ToDictionary(x => x.Key, x => x.Min(a => a.Start));
        return media.OrderBy(x => airs.GetValueOrDefault(x.Id, DateTimeOffset.MaxValue)).ToList();
    }

    private async Task Pass(Scheduler scheduler, Catalog catalog, IMediaEncoder encoder, CancellationToken ct)
    {
        var wanted = Wanted(scheduler, catalog, Plugin.Instance.Configuration);
        var due = wanted.Where(x => Due(x, DateTimeOffset.UtcNow)).ToList();
        status = new(wanted.Count > 0, due.Count > 0, wanted.Count, wanted.Count - due.Count, due.Count, null, status.LastPass);
        var measured = 0;
        foreach (var media in due)
        {
            ct.ThrowIfCancellationRequested();
            status = status with { Current = media.Title };
            var result = await Measure(encoder, media, ct);
            var file = Stat(media.Path);
            store.Put(media.Id, result is { } r
                ? new MeasuredLoudness(media.Path, file.Size, file.Modified, r.Integrated, r.Peak, r.Integrated < -60 ? "silent" : "measured", DateTimeOffset.UtcNow)
                : new MeasuredLoudness(media.Path, file.Size, file.Modified, 0, 0, "failed", DateTimeOffset.UtcNow));
            if (++measured % 20 == 0) store.Save();
            status = status with { Measured = status.Measured + 1, Remaining = status.Remaining - 1 };
        }
        if (measured > 0) store.Save();
        status = status with { LastPass = DateTimeOffset.UtcNow };
    }

    private bool Due(Media media, DateTimeOffset now)
    {
        if (store.Entry(media.Id) is not { } entry) return true;
        var file = Stat(media.Path);
        if (entry.Path != media.Path || entry.Size != file.Size || entry.Modified != file.Modified) return true;
        return entry.Method == "failed" && now - entry.ScannedAt > RetryFailed;
    }

    private static (long Size, long Modified) Stat(string path)
    {
        try { var info = new FileInfo(path); return (info.Length, info.LastWriteTimeUtc.Ticks); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (0, 0); }
    }

    // FFmpeg's ebur128 filter reads only the sound. Long programs are sampled: three ten-minute stretches.
    internal static async Task<(double Integrated, double Peak)?> Measure(IMediaEncoder encoder, Media media, CancellationToken ct)
    {
        var stretches = media.Seconds > 2400 ? new[] { media.Seconds * 0.15, media.Seconds * 0.45, media.Seconds * 0.7 } : [0.0];
        var levels = new List<(double Integrated, double Peak)>();
        foreach (var start in stretches)
        {
            string[] window = media.Seconds > 2400 ? ["-ss", start.ToString("0", CultureInfo.InvariantCulture), "-t", "600"] : [];
            var result = await CreditsScanner.Run(encoder, [.. window, "-i", media.Path, "-map", "0:a:0", "-vn", "-sn", "-dn", "-af", "ebur128=peak=true:framelog=quiet", "-f", "null", "-"], "info", TimeSpan.FromMinutes(10), ct);
            if (result is null || Parse(result.Value.Log) is not { } level) continue;
            levels.Add(level);
        }
        if (levels.Count == 0) return null;
        // Loudness adds as energy, not decibels.
        var energy = levels.Where(x => x.Integrated > -70).Select(x => Math.Pow(10, x.Integrated / 10)).DefaultIfEmpty(0).Average();
        return (energy > 0 ? Math.Round(10 * Math.Log10(energy), 1) : -70, levels.Max(x => x.Peak));
    }

    private static readonly Regex Integrated = new(@"I:\s+(-?[\d.]+|-inf)\s+LUFS", RegexOptions.Compiled);
    private static readonly Regex TruePeak = new(@"Peak:\s+(-?[\d.]+|-inf)\s+dBFS", RegexOptions.Compiled);

    // The summary ebur128 prints at the end.
    internal static (double Integrated, double Peak)? Parse(string log)
    {
        var summary = log.LastIndexOf("Summary:", StringComparison.Ordinal);
        if (summary < 0) return null;
        var text = log[summary..];
        static double Value(Match m) => m.Groups[1].Value == "-inf" ? -70 : double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var i = Integrated.Match(text);
        if (!i.Success) return null;
        var p = TruePeak.Match(text);
        return (Value(i), p.Success ? Value(p) : 0);
    }
}
