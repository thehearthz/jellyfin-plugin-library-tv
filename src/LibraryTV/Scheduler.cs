using System.Text.Json;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.LibraryTV;

public sealed class Scheduler(Catalog catalog, IApplicationPaths paths, ILogger<Scheduler> log, StartupGate? startup = null)
{
    public bool IsReady => startup?.IsReady ?? true;
    private readonly SemaphoreSlim gate = new(1);
    private Dictionary<string, ChannelState>? states;
    private PublishedSchedule published = new([], [], new Dictionary<string, Airing[]>());
    public string? LastError { get; private set; }
    public DateTimeOffset? LastRefresh { get; private set; }
    public IReadOnlyList<ChannelDefinition> Channels => Volatile.Read(ref published).Channels;
    public Airing[] Guide => Volatile.Read(ref published).Guide;
    public IReadOnlyList<Airing> GuideFor(string id) => Volatile.Read(ref published).ByChannel.GetValueOrDefault(id) ?? [];
    public Airing? Current(string id, DateTimeOffset now)
    {
        var items = GuideFor(id);
        var low = 0;
        var high = items.Count - 1;
        while (low <= high)
        {
            var mid = low + (high - low) / 2;
            if (items[mid].Start > now) high = mid - 1;
            else if (items[mid].End <= now) low = mid + 1;
            else return items[mid];
        }
        return null;
    }
    public string StatePath => Path.Combine(paths.DataPath, "library-tv", "schedule.json");

    public static void Validate(Configuration c)
    {
        if (c.Channels is null || c.AutoLibraryIds is null || c.AutomaticChannels is null) throw new ArgumentException("Channel and library lists cannot be null.");
        if (c.Channels.Any(x => x is null) || c.AutomaticChannels.Any(x => x is null)) throw new ArgumentException("Channel entries cannot be null.");
        if (c.AutoChannelStartNumber is < 1 or > 999999) throw new ArgumentException("Automatic channel numbers must start between 1 and 999999.");
        if (c.GuideDays is < 1 or > 7 || c.RefreshMinutes is < 5 or > 1440 || c.MaxActiveChannels is < 1 or > 16) throw new ArgumentException("Guide days: 1–7; refresh minutes: 5–1440; active channels: 1–16.");
        _ = TimeZoneInfo.FindSystemTimeZoneById(c.TimeZoneId);
        if (!Uri.TryCreate(c.InternalBaseUrl, UriKind.Absolute, out var uri) || !uri.IsLoopback || uri.Scheme is not ("http" or "https") || uri.Query.Length != 0 || uri.Fragment.Length != 0) throw new ArgumentException("Internal URL must be a loopback HTTP(S) Jellyfin URL, including any base path.");
        if (c.StreamToken is null || c.StreamToken.Length < 32) throw new ArgumentException("Stream token must have at least 32 characters.");
        if (c.Channels.Length > 100) throw new ArgumentException("At most 100 manual channels are supported.");
        if (c.Channels.Select(x => x.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != c.Channels.Length) throw new ArgumentException("Channel IDs must be unique, including disabled channels.");
        if (c.AutomaticChannels.Select(x => Guid.Parse(x.LibraryId)).Distinct().Count() != c.AutomaticChannels.Length) throw new ArgumentException("Each automatic library can have only one set of channel settings.");
        foreach (var entry in c.AutomaticChannels)
            if (!string.IsNullOrWhiteSpace(entry.Number)) _ = ChannelNumbers.Key(entry.Number);
        foreach (var channel in c.Channels)
        {
            _ = ChannelNumbers.Key(channel.Number);
            channel.Number = channel.Number.Trim();
            if (channel.Blocks is null || channel.Source is null || channel.Commercials is null) throw new ArgumentException("Channel content and schedule lists cannot be null.");
            if (channel.Blocks.Any(x => x is null)) throw new ArgumentException("Time blocks cannot be null.");
            if (string.IsNullOrWhiteSpace(channel.Name) || !System.Text.RegularExpressions.Regex.IsMatch(channel.Id, "^[a-zA-Z0-9_-]{1,80}$")) throw new ArgumentException("Provide a name and a unique channel ID using letters, numbers, underscores or hyphens.");
            if (channel.CommercialCount is < 0 or > 20) throw new ArgumentException("Commercial count must be 0–20.");
            if (channel.Blocks.Select(x => x.Id).Distinct().Count() != channel.Blocks.Length) throw new ArgumentException("Block IDs must be unique within each channel.");
            foreach (var block in channel.Blocks)
            {
                if (block.Id is "default" or "commercials" || string.IsNullOrWhiteSpace(block.Id)) throw new ArgumentException("Block ID is empty or reserved.");
                if (!TimeOnly.TryParseExact(block.Start, "HH:mm", out _) || !TimeOnly.TryParseExact(block.End, "HH:mm", out _) || block.Start == block.End) throw new ArgumentException("Time blocks require different start/end times in HH:mm format.");
                if (block.MaxItemsPerDay < 0) throw new ArgumentException("Daily episode limits cannot be negative.");
                if (block.Days is null || block.Days.Length == 0 || block.Days.Any(x => !Enum.IsDefined(x))) throw new ArgumentException("Choose at least one valid day for every time block.");
            }
            foreach (var pool in channel.Blocks.Select(x => x.Source).Append(channel.Source).Append(channel.Commercials))
            {
                if (pool is null || pool.LibraryIds is null || pool.ItemIds is null || pool.FolderIds is null || pool.Genres is null || pool.Tags is null) throw new ArgumentException("Content selections cannot be null.");
                foreach (var id in pool.LibraryIds.Concat(pool.ItemIds).Concat(pool.FolderIds)) _ = Guid.Parse(id);
            }
        }
        ChannelNumbers.ValidateUnique(c.Channels.Where(x => x.Enabled));
        foreach (var id in c.AutoLibraryIds) _ = Guid.Parse(id);
    }

    public async Task SaveConfiguration(Configuration config, CancellationToken ct)
    {
        startup?.RequireReady();
        await gate.WaitAsync(ct);
        try
        {
            Validate(config);
            catalog.PrepareAutomaticChannels(config);
            _ = catalog.Channels(config); // Reject manual/automatic conflicts before persisting.
            Plugin.Instance.UpdateConfiguration(config);
        }
        finally { gate.Release(); }
    }

    public async Task Refresh(CancellationToken ct)
    {
        startup?.RequireReady();
        await gate.WaitAsync(ct);
        try
        {
            var config = Plugin.Instance.Configuration;
            Validate(config);
            var assignedNumbers = catalog.PrepareAutomaticChannels(config, preserveLegacyNumbers: File.Exists(StatePath));
            if (states is null)
            {
                states = File.Exists(StatePath) ? JsonSerializer.Deserialize<Dictionary<string, ChannelState>>(await File.ReadAllTextAsync(StatePath, ct)) ?? [] : [];
            }
            // Work on a copy, so failed scans/writes cannot partially advance cursors.
            var working = states.ToDictionary(x => x.Key, x => CopyState(x.Value));
            var definitions = catalog.Channels(config);
            if (assignedNumbers || LastRefresh is null) Plugin.Instance.SaveConfiguration();
            var now = DateTimeOffset.UtcNow;
            var read = catalog.CreateRefreshReader();
            foreach (var channel in definitions)
            {
                ct.ThrowIfCancellationRequested();
                if (!working.TryGetValue(channel.Id, out var state)) working[channel.Id] = state = new();
                var pools = channel.Blocks.ToDictionary(x => x.Id, x => read(x.Source));
                pools["default"] = read(channel.Source);
                pools["commercials"] = read(channel.Commercials);
                var adIds = pools["commercials"].Select(x => x.Id).ToHashSet();
                foreach (var key in pools.Keys.Where(x => x != "commercials").ToArray()) pools[key].RemoveAll(x => adIds.Contains(x.Id));
                Planner.Extend(channel, state, pools, now, now.AddDays(config.GuideDays), TimeZoneInfo.FindSystemTimeZoneById(config.TimeZoneId));
            }
            var retained = config.Channels.Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var selectedLibraries = config.AutoLibraryIds.Select(Guid.Parse).ToHashSet();
            foreach (var id in working.Keys.ToArray())
            {
                var keepAutomatic = id.StartsWith("auto-", StringComparison.OrdinalIgnoreCase) && Guid.TryParse(id[5..], out var libraryId) && selectedLibraries.Contains(libraryId);
                if (!retained.Contains(id) && !keepAutomatic) working.Remove(id);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            await File.WriteAllTextAsync(StatePath + ".tmp", JsonSerializer.Serialize(working), ct);
            File.Move(StatePath + ".tmp", StatePath, true);
            states = working;
            var byChannel = definitions.ToDictionary(x => x.Id, x => working[x.Id].Guide.OrderBy(p => p.Start).ToArray());
            var guide = byChannel.Values.SelectMany(x => x).OrderBy(x => x.Start).ToArray();
            Volatile.Write(ref published, new PublishedSchedule(definitions, guide, byChannel));
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

    private sealed record PublishedSchedule(ChannelDefinition[] Channels, Airing[] Guide, Dictionary<string, Airing[]> ByChannel);

    private static ChannelState CopyState(ChannelState state) => new()
    {
        Guide = new List<Airing>(state.Guide), // Airing is immutable; share records, not mutable lists.
        DailyCounts = new Dictionary<string, int>(state.DailyCounts),
        Rotations = state.Rotations.ToDictionary(x => x.Key, x => new Rotation
        {
            Last = x.Value.Last,
            Remaining = new List<Guid>(x.Value.Remaining),
            Seen = new List<Guid>(x.Value.Seen)
        })
    };
}

public sealed class RefreshService(IServiceProvider services, ILogger<RefreshService> log, StartupGate startup) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await startup.WaitAsync(stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        var scheduler = services.GetRequiredService<Scheduler>();
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await scheduler.Refresh(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogWarning(ex, "Will retry Library TV refresh"); }
            await Task.Delay(TimeSpan.FromMinutes(Math.Clamp(Plugin.Instance.Configuration.RefreshMinutes, 5, 1440)), stoppingToken);
        }
    }
}
