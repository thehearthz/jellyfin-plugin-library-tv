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
    private PublishedSchedule published = new([], [], new Dictionary<string, Airing[]>(), new Dictionary<string, string[]>());

    public async Task<bool> SkipFailed(Airing failed, DateTimeOffset now, CancellationToken ct)
    {
        startup?.RequireReady();
        await gate.WaitAsync(ct);
        try
        {
            if (states is null || !states.TryGetValue(failed.ChannelId, out var saved)) return false;
            var current = Current(failed.ChannelId, now);
            if (current is null) return false;
            if (current.ItemId != failed.ItemId || current.Start != failed.Start) return true;
            var state = CopyState(saved);
            // A failed program also loses its later parts and the commercial breaks between them.
            var skipUntil = failed.End;
            if (!failed.Commercial)
                foreach (var later in state.Guide.Where(x => x.Start >= failed.End).OrderBy(x => x.Start))
                {
                    if (later.Commercial) continue;
                    if (later.ItemId != failed.ItemId || later.Offset <= failed.Offset) break;
                    skipUntil = later.End;
                }
            var next = state.Guide.FirstOrDefault(x => x.Start >= skipUntil);
            if (next is null) return false;
            var delta = next.Start - now;
            var zone = TimeZoneInfo.FindSystemTimeZoneById(Plugin.Instance.Configuration.TimeZoneId);
            state.Guide = state.Guide.Where(x => x.Start < failed.End || x.Start >= skipUntil).Select(Shift).Where(x => x.End > x.Start).ToList();
            var working = new Dictionary<string, ChannelState>(states) { [failed.ChannelId] = state };
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            await File.WriteAllTextAsync(StatePath + ".tmp", JsonSerializer.Serialize(working), ct);
            File.Move(StatePath + ".tmp", StatePath, true);
            states = working;
            var previous = Volatile.Read(ref published);
            var index = new Dictionary<string, Airing[]>(previous.ByChannel) { [failed.ChannelId] = state.Guide.ToArray() };
            Volatile.Write(ref published, new PublishedSchedule(previous.Channels, index.Values.SelectMany(x => x).OrderBy(x => x.Start).ToArray(), index, previous.Warnings));
            return true;
            Airing Shift(Airing item)
            {
                if (item.Start == failed.Start && item.ItemId == failed.ItemId) return item with { End = now };
                if (item.Start < next.Start) return item;
                var shifted = item with { Start = item.Start - delta, End = item.End - delta };
                if (item.Undo?.QuotaKey is { } oldKey)
                {
                    var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(shifted.Start, zone).DateTime);
                    var definition = Plugin.Instance.Configuration.Channels.FirstOrDefault(x => x.Id == item.ChannelId)?.Blocks.FirstOrDefault(x => x.Id == item.Undo.PoolKey);
                    if (definition is not null)
                    {
                        var onlyBlock = new ChannelDefinition { Blocks = [definition] };
                        date = Planner.ActiveBlock(onlyBlock, shifted.Start, zone)?.Date ?? date;
                    }
                    var newKey = date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + ":" + item.Undo.PoolKey;
                    if (newKey != oldKey)
                    {
                        var previousCount = state.DailyCounts.GetValueOrDefault(oldKey);
                        if (previousCount <= 1) state.DailyCounts.Remove(oldKey); else state.DailyCounts[oldKey] = previousCount - 1;
                        state.DailyCounts[newKey] = state.DailyCounts.GetValueOrDefault(newKey) + 1;
                        shifted = shifted with { Undo = item.Undo with { QuotaKey = newKey } };
                    }
                }
                return shifted;
            }
        }
        finally { gate.Release(); }
    }
    public string? LastError { get; private set; }
    public double? LastRefreshMilliseconds { get; private set; }
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
            if (channel.EpisodeBreaks is < 0 or > 5 || channel.MovieBreaks is < 0 or > 5) throw new ArgumentException("Breaks during a program must be 0–5.");
            if (channel.PictureShape is not ("16:9" or "4:3" or "off" or "restart")) throw new ArgumentException("Picture shape must be 16:9, 4:3, off or restart.");
            if (channel.Bumpers is null) throw new ArgumentException("Channel content and schedule lists cannot be null.");
            if (channel.CommercialNetwork is null || channel.CommercialNetwork.Length > 100 || channel.Blocks.Any(x => x is not null && (x.CommercialNetwork is null || x.CommercialNetwork.Length > 100))) throw new ArgumentException("Commercial network names must be 100 characters or fewer.");
            if (channel.Blocks.Select(x => x.Id).Distinct().Count() != channel.Blocks.Length) throw new ArgumentException("Block IDs must be unique within each channel.");
            foreach (var block in channel.Blocks)
            {
                if (block.Id is "default" || block.Id.StartsWith("commercials", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(block.Id)) throw new ArgumentException("Block ID is empty or reserved.");
                if (!TimeOnly.TryParseExact(block.Start, "HH:mm", out _) || !TimeOnly.TryParseExact(block.End, "HH:mm", out _) || block.Start == block.End) throw new ArgumentException("Time blocks require different start/end times in HH:mm format.");
                if (block.MaxItemsPerDay < 0) throw new ArgumentException("Daily episode limits cannot be negative.");
                if (block.Days is null || block.Days.Length == 0 || block.Days.Any(x => !Enum.IsDefined(x))) throw new ArgumentException("Choose at least one valid day for every time block.");
            }
            foreach (var pool in channel.Blocks.Select(x => x.Source).Append(channel.Source).Append(channel.Commercials).Append(channel.Bumpers))
            {
                if (pool is null || pool.LibraryIds is null || pool.ItemIds is null || pool.FolderIds is null || pool.Genres is null || pool.Tags is null) throw new ArgumentException("Content selections cannot be null.");
                if (pool.EpisodesInARow is < 0 or > 100) throw new ArgumentException("Episodes in a row must be 0–100.");
                foreach (var id in pool.LibraryIds.Concat(pool.ItemIds).Concat(pool.FolderIds)) _ = Guid.Parse(id);
            }
        }
        foreach (var pool in new[] { c.SharedCommercials, c.SharedBumpers })
        {
            if (pool is null || pool.LibraryIds is null || pool.ItemIds is null || pool.FolderIds is null || pool.Genres is null || pool.Tags is null) throw new ArgumentException("Content selections cannot be null.");
            foreach (var id in pool.LibraryIds.Concat(pool.ItemIds).Concat(pool.FolderIds)) _ = Guid.Parse(id);
        }
        if (c.Restrictions is null || c.Restrictions.Length > 50 || c.Restrictions.Any(x => x is null)) throw new ArgumentException("Use at most 50 content restrictions.");
        foreach (var rule in c.Restrictions)
        {
            if (!TimeOnly.TryParseExact(rule.Start, "HH:mm", out _) || !TimeOnly.TryParseExact(rule.End, "HH:mm", out _) || rule.Start == rule.End) throw new ArgumentException("Content restrictions require different start/end times in HH:mm format.");
            if (rule.Days is null || rule.Days.Length == 0 || rule.Days.Any(x => !Enum.IsDefined(x))) throw new ArgumentException("Choose at least one valid day for every content restriction.");
            if (rule.Genres is null || rule.Tags is null || rule.Genres.Concat(rule.Tags).All(string.IsNullOrWhiteSpace)) throw new ArgumentException("Each content restriction needs at least one genre or tag.");
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

    public async Task Refresh(CancellationToken ct) => await RefreshInternal(ct, null);

    public async Task<DateTimeOffset?> RebuildFuture(string channelId, CancellationToken ct) => await RefreshInternal(ct, channelId);

    private async Task<DateTimeOffset?> RefreshInternal(CancellationToken ct, string? rebuildChannel)
    {
        startup?.RequireReady();
        await gate.WaitAsync(ct);
        try
        {
            var began = System.Diagnostics.Stopwatch.GetTimestamp();
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
            if (rebuildChannel is not null && !definitions.Any(x => x.Id == rebuildChannel)) throw new ArgumentException("Choose an enabled channel to rebuild.");
            if (assignedNumbers || LastRefresh is null) Plugin.Instance.SaveConfiguration();
            var now = DateTimeOffset.UtcNow;
            var scan = new Catalog.RefreshScan();
            var read = catalog.CreateRefreshReader(false, scan);
            var readCommercials = catalog.CreateRefreshReader(true, scan);
            var chapters = catalog.CreateChapterReader();
            static bool Picked(Pool pool) => pool.LibraryIds.Length + pool.FolderIds.Length + pool.ItemIds.Length > 0;
            // One network index for every channel: common networks, every program's tags and studios, and the overrides.
            var networks = new Planner.NetworkIndex(definitions.SelectMany(x => x.Blocks.Select(b => b.Source).Append(x.Source)).SelectMany(read)
                .SelectMany(x => (x.Studios ?? []).Concat(x.Tags ?? []))
                .Concat(definitions.SelectMany(x => x.Blocks.Select(b => b.CommercialNetwork).Append(x.CommercialNetwork))));
            DateTimeOffset? rebuiltFrom = null;
            var warnings = new Dictionary<string, string[]>();
            foreach (var defined in definitions)
            {
                ct.ThrowIfCancellationRequested();
                // A channel without clips of its own uses the shared commercial and bumper libraries.
                var channel = defined.WithClips(Picked(defined.Commercials) ? defined.Commercials : config.SharedCommercials, Picked(defined.Bumpers) ? defined.Bumpers : config.SharedBumpers);
                if (!working.TryGetValue(channel.Id, out var state)) working[channel.Id] = state = new();
                var pools = channel.Blocks.ToDictionary(x => x.Id, x => read(x.Source));
                pools["default"] = read(channel.Source);
                pools["commercials"] = readCommercials(channel.Commercials);
                pools["bumpers"] = readCommercials(channel.Bumpers);
                var clipIds = pools["commercials"].Concat(pools["bumpers"]).Select(x => x.Id).ToHashSet();
                foreach (var key in pools.Keys.Where(x => x is not ("commercials" or "bumpers")).ToArray()) pools[key].RemoveAll(x => clipIds.Contains(x.Id));
                if (channel.Id == rebuildChannel) rebuiltFrom = Planner.RewindFuture(state, now);
                Planner.Extend(channel, state, pools, now, now.AddDays(config.GuideDays), TimeZoneInfo.FindSystemTimeZoneById(config.TimeZoneId), channel.Id == rebuildChannel ? rebuiltFrom : null, config.Restrictions, chapters, networks);
                var notes = new List<string>();
                if (pools["default"].Count == 0) notes.Add("The default lineup has no eligible local videos. Time outside scheduled blocks may be empty.");
                foreach (var block in channel.Blocks.Where(x => pools[x.Id].Count == 0)) notes.Add($"The {block.Start}–{block.End} block has no eligible local videos and will use the default lineup.");
                if (channel.CommercialCount > 0 && pools["commercials"].Count == 0) notes.Add("Commercial breaks are enabled but no readable commercial clips were found.");
                if (channel.Bumpers.LibraryIds.Length + channel.Bumpers.FolderIds.Length + channel.Bumpers.ItemIds.Length > 0 && pools["bumpers"].Count == 0) notes.Add("Bumpers are selected but no readable bumper clips were found.");
                var upcoming = state.Guide.Where(x => x.End > now).ToArray();
                if (upcoming.Zip(upcoming.Skip(1)).Any(x => x.Second.Start - x.First.End > TimeSpan.FromMinutes(1)))
                    notes.Add("Some times have nothing allowed to air, so the channel goes off the air until the next program. Add content or narrow the content restrictions.");
                if (!state.Guide.Any(x => x.End > now)) notes.Add("No programs are queued. Check content selections, missing files, time blocks, and repeat settings.");
                if (state.Guide.Count >= Planner.MaxGuideEntries) notes.Add("This channel reached the 10,000-program guide limit. Use longer clips or a shorter guide horizon to keep memory and refresh costs low.");
                warnings[channel.Id] = notes.ToArray();
            }
            var retained = config.Channels.Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var selectedLibraries = config.AutoLibraryIds.Select(Guid.Parse).ToHashSet();
            foreach (var id in working.Keys.ToArray())
            {
                var keepAutomatic = id.StartsWith("auto-", StringComparison.OrdinalIgnoreCase) && Guid.TryParse(id[5..], out var libraryId) && selectedLibraries.Contains(libraryId);
                if (!retained.Contains(id) && !keepAutomatic) working.Remove(id);
            }
            var library = Summarize("Commercials", readCommercials(config.SharedCommercials)).Concat(Summarize("Bumpers", readCommercials(config.SharedBumpers))).ToArray();
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            await File.WriteAllTextAsync(StatePath + ".tmp", JsonSerializer.Serialize(working), ct);
            File.Move(StatePath + ".tmp", StatePath, true);
            states = working;
            ClipLibrary = library;
            var byChannel = definitions.ToDictionary(x => x.Id, x => working[x.Id].Guide.OrderBy(p => p.Start).ToArray());
            var guide = byChannel.Values.SelectMany(x => x).OrderBy(x => x.Start).ToArray();
            Volatile.Write(ref published, new PublishedSchedule(definitions, guide, byChannel, warnings));
            LastRefresh = now;
            LastRefreshMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(began).TotalMilliseconds;
            LastError = null;
            return rebuiltFrom;
            // How the shared clips were sorted, for the builder: counts per network and decade.
            IEnumerable<ClipGroup> Summarize(string kind, List<Media> clips) => clips
                .GroupBy(x => (Network: networks.For(x).Select(networks.Name).DefaultIfEmpty("General").First(), Decade: x.Year / 10 * 10))
                .OrderBy(x => x.Key.Network == "General").ThenBy(x => x.Key.Network, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Key.Decade)
                .Select(x => new ClipGroup(kind, x.Key.Network, x.Key.Decade, x.Count()));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LastError = ex.Message;
            log.LogError(ex, "Library TV schedule refresh failed; keeping previous schedule");
            throw;
        }
        finally { gate.Release(); }
    }

    public ClipGroup[] ClipLibrary { get; private set; } = [];
    public string[] WarningsFor(string id) => Volatile.Read(ref published).Warnings.GetValueOrDefault(id) ?? [];
    private sealed record PublishedSchedule(ChannelDefinition[] Channels, Airing[] Guide, Dictionary<string, Airing[]> ByChannel, Dictionary<string, string[]> Warnings);

    private static ChannelState CopyState(ChannelState state) => new()
    {
        Guide = new List<Airing>(state.Guide), // Airing is immutable; share records, not mutable lists.
        DailyCounts = new Dictionary<string, int>(state.DailyCounts),
        Series = new Dictionary<string, SeriesCursor>(state.Series), // SeriesCursor is immutable.
        Rotations = state.Rotations.ToDictionary(x => x.Key, x => new Rotation
        {
            Last = x.Value.Last,
            EpisodesInARow = x.Value.EpisodesInARow,
            Remaining = new List<Guid>(x.Value.Remaining),
            Seen = new List<Guid>(x.Value.Seen)
        })
    };
}

// Decade 0 means no year was found.
public sealed record ClipGroup(string Kind, string Network, int Decade, int Count);

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
