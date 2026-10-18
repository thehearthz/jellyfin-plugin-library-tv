using System.Text.Json;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LibraryTV;

public sealed class Scheduler(Catalog catalog, IApplicationPaths paths, ILogger<Scheduler> log)
{
    private readonly SemaphoreSlim gate = new(1);
    private Dictionary<string, ChannelState>? states;
    private Airing[] snapshot = [];
    private ChannelDefinition[] channels = [];
    public string? LastError { get; private set; }
    public DateTimeOffset? LastRefresh { get; private set; }
    public IReadOnlyList<ChannelDefinition> Channels => Volatile.Read(ref channels);
    public Airing[] Guide => Volatile.Read(ref snapshot);
    public Airing? Current(string id, DateTimeOffset now) => Guide.FirstOrDefault(x => x.ChannelId == id && x.Start <= now && x.End > now);
    public string StatePath => Path.Combine(paths.DataPath, "library-tv", "schedule.json");

    public static void Validate(Configuration c)
    {
        if (c.GuideDays is < 1 or > 7 || c.RefreshMinutes is < 5 or > 1440 || c.MaxActiveChannels is < 1 or > 16) throw new ArgumentException("Guide days: 1–7; refresh minutes: 5–1440; active channels: 1–16.");
        _ = TimeZoneInfo.FindSystemTimeZoneById(c.TimeZoneId);
        if (!Uri.TryCreate(c.InternalBaseUrl, UriKind.Absolute, out var uri) || !uri.IsLoopback || uri.Scheme is not ("http" or "https") || uri.Query.Length != 0 || uri.Fragment.Length != 0) throw new ArgumentException("Internal URL must be a loopback HTTP(S) Jellyfin URL, including any base path.");
        if (c.StreamToken.Length < 32) throw new ArgumentException("Stream token must have at least 32 characters.");
        if (c.Channels.Length > 100) throw new ArgumentException("At most 100 manual channels are supported.");
        foreach (var channel in c.Channels)
        {
            if (string.IsNullOrWhiteSpace(channel.Name) || !System.Text.RegularExpressions.Regex.IsMatch(channel.Id, "^[a-zA-Z0-9_-]{1,80}$")) throw new ArgumentException("Provide a name and a unique channel ID using letters, numbers, underscores or hyphens.");
            if (channel.CommercialCount is < 0 or > 20) throw new ArgumentException("Commercial count must be 0–20.");
            if (channel.Blocks.Select(x => x.Id).Distinct().Count() != channel.Blocks.Length) throw new ArgumentException("Block IDs must be unique within each channel.");
            foreach (var block in channel.Blocks)
            {
                if (block.Id is "default" or "commercials" || string.IsNullOrWhiteSpace(block.Id)) throw new ArgumentException("Block ID is empty or reserved.");
                if (!TimeOnly.TryParseExact(block.Start, "HH:mm", out _) || !TimeOnly.TryParseExact(block.End, "HH:mm", out _) || block.Start == block.End) throw new ArgumentException("Time blocks require different start/end times in HH:mm format.");
                if (block.MaxItemsPerDay < 0) throw new ArgumentException("Daily episode limits cannot be negative.");
            }
            foreach (var pool in channel.Blocks.Select(x => x.Source).Append(channel.Source).Append(channel.Commercials))
                foreach (var id in pool.LibraryIds.Concat(pool.ItemIds).Concat(pool.FolderIds)) _ = Guid.Parse(id);
        }
        foreach (var id in c.AutoLibraryIds) _ = Guid.Parse(id);
    }

    public async Task Refresh(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var config = Plugin.Instance.Configuration;
            Validate(config);
            if (states is null)
            {
                states = File.Exists(StatePath) ? JsonSerializer.Deserialize<Dictionary<string, ChannelState>>(await File.ReadAllTextAsync(StatePath, ct)) ?? [] : [];
            }
            // Work on a copy, so failed scans/writes cannot partially advance cursors.
            var working = JsonSerializer.Deserialize<Dictionary<string, ChannelState>>(JsonSerializer.Serialize(states))!;
            var definitions = catalog.Channels(config);
            var now = DateTimeOffset.UtcNow;
            foreach (var channel in definitions)
            {
                ct.ThrowIfCancellationRequested();
                if (!working.TryGetValue(channel.Id, out var state)) working[channel.Id] = state = new();
                var pools = channel.Blocks.ToDictionary(x => x.Id, x => catalog.Read(x.Source));
                pools["default"] = catalog.Read(channel.Source);
                pools["commercials"] = catalog.Read(channel.Commercials);
                var adIds = pools["commercials"].Select(x => x.Id).ToHashSet();
                foreach (var key in pools.Keys.Where(x => x != "commercials").ToArray()) pools[key].RemoveAll(x => adIds.Contains(x.Id));
                Planner.Extend(channel, state, pools, now, now.AddDays(config.GuideDays), TimeZoneInfo.FindSystemTimeZoneById(config.TimeZoneId));
            }
            foreach (var id in working.Keys.Except(definitions.Select(x => x.Id)).ToArray()) working.Remove(id);
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            await File.WriteAllTextAsync(StatePath + ".tmp", JsonSerializer.Serialize(working), ct);
            File.Move(StatePath + ".tmp", StatePath, true);
            states = working;
            Volatile.Write(ref snapshot, working.Values.SelectMany(x => x.Guide).OrderBy(x => x.Start).ToArray());
            Volatile.Write(ref channels, definitions);
            LastRefresh = now;
            LastError = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LastError = ex.Message;
            log.LogError(ex, "Library TV schedule refresh failed; keeping previous schedule");
            throw;
        }
        finally { gate.Release(); }
    }
}

public sealed class RefreshService(Scheduler scheduler, ILogger<RefreshService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Plugin.Instance.SaveConfiguration(); // Persist the private stream token once.
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await scheduler.Refresh(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogWarning(ex, "Will retry Library TV refresh"); }
            await Task.Delay(TimeSpan.FromMinutes(Math.Clamp(Plugin.Instance.Configuration.RefreshMinutes, 5, 1440)), stoppingToken);
        }
    }
}
