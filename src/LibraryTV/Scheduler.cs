using System.Text.Json;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.LibraryTV;

public sealed partial class Scheduler(Catalog catalog, IApplicationPaths paths, ILogger<Scheduler> log, StartupGate? startup = null, CreditsStore? detections = null)
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
            if (channel.Captions is not ("off" or "forced" or "on") || channel.CaptionLanguage is null || channel.CaptionLanguage.Length > 40) throw new ArgumentException("Captions must be off, forced or on, with a caption language of 40 characters or fewer.");
            if (channel.AudioLanguage is null || channel.AudioLanguage.Length > 40) throw new ArgumentException("The audio language must be 40 characters or fewer.");
            if (channel.CreditsSpeed is < 1 or > 8) throw new ArgumentException("Credits speed must be 1 (normal) to 8.");
            if (channel.PadMinutes is not (0 or 5 or 10 or 15 or 20 or 30 or 60)) throw new ArgumentException("Padding must be off or 5, 10, 15, 20, 30 or 60 minutes.");
            foreach (var image in new[] { channel.Watermark, channel.Logo, channel.OfflineCard })
                if (image is null || image.Length > 500 || image.Length > 0 && (!Path.IsPathFullyQualified(image) || !ProgramStreamer.IsImage(image) || image.Any(char.IsControl))) throw new ArgumentException("Logo, watermark and offline-card images must be full paths to PNG, JPG or WebP files.");
            if (channel.AudioTitle is null || channel.AudioTitle.Length > 60) throw new ArgumentException("The preferred audio title must be 60 characters or fewer.");
            if (channel.WatermarkPosition is not ("top-left" or "top-right" or "bottom-left" or "bottom-right") || channel.WatermarkOpacity is < 10 or > 100 || channel.WatermarkSize is < 3 or > 50) throw new ArgumentException("Watermark position: a corner; opacity 10–100; size 3–50 percent.");
            if (channel.BreakMode is not ("even" or "interval")) throw new ArgumentException("Break placement must be even or interval.");
            if (channel.BreakIntervalMinutes is < 3 or > 120 || channel.MaxBreaksPerProgram is < 0 or > 20 || channel.MinBreakProgramMinutes is < 1 or > 600 || channel.BreakFreeEndMinutes is < 0 or > 60)
                throw new ArgumentException("Break interval: 3–120 minutes; breaks per program: 0–20; shortest program to split: 1–600 minutes; break-free ending: 0–60 minutes.");
            if (channel.CommercialCountMax is < 0 or > 20 || channel.CommercialCountMax != 0 && channel.CommercialCountMax < channel.CommercialCount) throw new ArgumentException("The longest break must be 0 (fixed) or between the commercials per break and 20.");
            if (channel.CommercialNetwork is null || channel.CommercialNetwork.Length > 100 || channel.Blocks.Any(x => x is not null && (x.CommercialNetwork is null || x.CommercialNetwork.Length > 100))) throw new ArgumentException("Commercial network names must be 100 characters or fewer.");
            if (channel.Blocks.Select(x => x.Id).Distinct().Count() != channel.Blocks.Length) throw new ArgumentException("Block IDs must be unique within each channel.");
            foreach (var block in channel.Blocks)
            {
                if (block.Id is "default" || block.Id.StartsWith("commercials", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(block.Id)) throw new ArgumentException("Block ID is empty or reserved.");
                if (!TimeOnly.TryParseExact(block.Start, "HH:mm", out _) || !TimeOnly.TryParseExact(block.End, "HH:mm", out _) || block.Start == block.End) throw new ArgumentException("Time blocks require different start/end times in HH:mm format.");
                if (block.MaxItemsPerDay < 0) throw new ArgumentException("Daily episode limits cannot be negative.");
                if (block.StartMode is not ("soft" or "exact") || block.Fill is not ("count" or "fit")) throw new ArgumentException("A block starts soft or exact and fills by count or fit.");
                if (block.MaxLateMinutes is < 0 or > 1440) throw new ArgumentException("Allowed lateness must be 0–1440 minutes.");
                if (block.GuideTitle is null || block.GuideTitle.Length > 100) throw new ArgumentException("Guide titles must be 100 characters or fewer.");
                if (block.Days is null || block.Days.Length == 0 || block.Days.Any(x => !Enum.IsDefined(x))) throw new ArgumentException("Choose at least one valid day for every time block.");
            }
            foreach (var pool in channel.Blocks.Select(x => x.Source).Append(channel.Source).Append(channel.Commercials).Append(channel.Bumpers))
            {
                if (pool is null || pool.LibraryIds is null || pool.ItemIds is null || pool.FolderIds is null || pool.Genres is null || pool.Tags is null) throw new ArgumentException("Content selections cannot be null.");
                if (pool.EpisodesInARow is < 0 or > 100) throw new ArgumentException("Episodes in a row must be 0–100.");
                ValidatePool(pool);
                foreach (var id in pool.LibraryIds.Concat(pool.ItemIds).Concat(pool.FolderIds)) _ = Guid.Parse(id);
            }
        }
        if (c.SmartChannels is null || c.SmartChannels.Length > 50 || c.SmartChannels.Any(x => x is null)) throw new ArgumentException("Use at most 50 smart channels.");
        if (c.SmartChannels.Select(x => x.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != c.SmartChannels.Length) throw new ArgumentException("Smart channel IDs must be unique.");
        foreach (var smart in c.SmartChannels)
        {
            if (string.IsNullOrWhiteSpace(smart.Name) || smart.Name.Length > 100 || !System.Text.RegularExpressions.Regex.IsMatch(smart.Id, "^[a-zA-Z0-9_-]{1,80}$")) throw new ArgumentException("Give every smart channel a name of 100 characters or fewer.");
            if (smart.Number is null) throw new ArgumentException("Smart channel numbers cannot be null.");
            if (!string.IsNullOrWhiteSpace(smart.Number)) _ = ChannelNumbers.Key(smart.Number);
            if (smart.LibraryIds is null || smart.Genres is null || smart.Years is null || smart.Decades is null || smart.ItemIds is null) throw new ArgumentException("Smart channel selections cannot be null.");
            if (smart.Genres.Length > 100 || smart.Genres.Any(g => g is null || g.Length > 100) || smart.Years.Length > 300 || smart.ItemIds.Length > 500 || smart.LibraryIds.Length > 50)
                throw new ArgumentException("A smart channel has at most 100 genres, 300 years, 500 chosen shows and movies, and 50 libraries.");
            if (smart.Years.Any(y => y is < 1850 or > 2200) || smart.Decades.Any(d => d is < 1850 or > 2200 || d % 10 != 0)) throw new ArgumentException("Smart channel years must be 1850–2200, and decades must start on a multiple of ten.");
            foreach (var id in smart.LibraryIds.Concat(smart.ItemIds)) _ = Guid.Parse(id);
        }
        foreach (var pool in new[] { c.SharedCommercials, c.SharedBumpers })
        {
            if (pool is null || pool.LibraryIds is null || pool.ItemIds is null || pool.FolderIds is null || pool.Genres is null || pool.Tags is null) throw new ArgumentException("Content selections cannot be null.");
            foreach (var id in pool.LibraryIds.Concat(pool.ItemIds).Concat(pool.FolderIds)) _ = Guid.Parse(id);
        }
        if (c.AutomaticCommercialCount is < 0 or > 20) throw new ArgumentException("Commercials per break on automatic channels must be 0–20.");
        if (c.Restrictions is null || c.Restrictions.Length > 50 || c.Restrictions.Any(x => x is null)) throw new ArgumentException("Use at most 50 content restrictions.");
        foreach (var rule in c.Restrictions)
        {
            if (!TimeOnly.TryParseExact(rule.Start, "HH:mm", out _) || !TimeOnly.TryParseExact(rule.End, "HH:mm", out _) || rule.Start == rule.End) throw new ArgumentException("Content restrictions require different start/end times in HH:mm format.");
            if (rule.Days is null || rule.Days.Length == 0 || rule.Days.Any(x => !Enum.IsDefined(x))) throw new ArgumentException("Choose at least one valid day for every content restriction.");
            if (rule.Genres is null || rule.Tags is null || rule.Genres.Concat(rule.Tags).All(string.IsNullOrWhiteSpace)) throw new ArgumentException("Each content restriction needs at least one genre or tag.");
        }
        if (c.ClipRules is null || c.ClipOverrides is null || c.ClipRules.Length > 200 || c.ClipOverrides.Length > 20000 || c.ClipRules.Concat(c.ClipOverrides).Any(x => x is null))
            throw new ArgumentException("Use at most 200 clip rules and 20,000 clip edits.");
        foreach (var edit in c.ClipRules.Concat(c.ClipOverrides))
        {
            if (edit.Network is null || edit.Show is null || edit.Kind is null || edit.Contains is null || edit.ItemId is null || edit.Network.Length > 100 || edit.Show.Length > 100) throw new ArgumentException("Clip network and show names must be 100 characters or fewer.");
            if (edit.Kind is not ("" or "commercial" or "bumper")) throw new ArgumentException("A clip's kind must be commercial or bumper.");
            if (edit.Year != 0 && edit.Year is < 1900 or > 2100) throw new ArgumentException("A clip's year must be between 1900 and 2100.");
        }
        foreach (var rule in c.ClipRules)
            if (string.IsNullOrWhiteSpace(rule.Contains) || rule.Contains.Length > 200) throw new ArgumentException("Each clip rule needs some text for the clip's path to contain, up to 200 characters.");
        foreach (var edit in c.ClipOverrides) _ = Guid.Parse(edit.ItemId);
        ChannelNumbers.ValidateUnique(c.Channels.Where(x => x.Enabled));
        foreach (var id in c.AutoLibraryIds) _ = Guid.Parse(id);
    }

    private static void ValidatePool(Pool pool)
    {
        if (pool.Order is not ("" or "chronological" or "alphabetical")) throw new ArgumentException("Order must be blank, chronological or alphabetical.");
        if (pool.Turns is null || pool.Turns.Length > 200 || pool.Turns.Any(x => x is null || x.Show is null || x.Show.Length > 200 || x.Episodes is < 0 or > 100)) throw new ArgumentException("Use at most 200 shows with 0–100 episodes in a row.");
        if (pool.TitleContains is null || pool.TitleExcludes is null || pool.TitleContains.Concat(pool.TitleExcludes).Any(x => x is null || x.Length > 100) || pool.TitleContains.Length > 50 || pool.TitleExcludes.Length > 50) throw new ArgumentException("Use at most 50 title words of 100 characters or fewer for each filter.");
        if (pool.Years is null || pool.Decades is null || pool.AlwaysItemIds is null || pool.Years.Length > 300 || pool.Years.Any(y => y is < 1850 or > 2200) || pool.Decades.Any(d => d is < 1850 or > 2200 || d % 10 != 0)) throw new ArgumentException("Years must be 1850–2200 and decades must start on a multiple of ten.");
        foreach (var id in pool.AlwaysItemIds) _ = Guid.Parse(id);
        if (pool.ExcludeGenres is null || pool.MaxRating is null || pool.MaxRating.Length > 20) throw new ArgumentException("Content selections cannot be null.");
        if (pool.MinYear is not 0 and (< 1850 or > 2200) || pool.MaxYear is not 0 and (< 1850 or > 2200) || pool.MaxYear != 0 && pool.MaxYear < pool.MinYear) throw new ArgumentException("Years must be between 1850 and 2200, and the last year cannot be before the first.");
        if (pool.MinMinutes < 0 || pool.MaxMinutes < 0 || pool.MaxMinutes != 0 && pool.MaxMinutes < pool.MinMinutes) throw new ArgumentException("Length limits cannot be negative, and the longest cannot be shorter than the shortest.");
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
            var credits = catalog.CreateCreditsReader();
            static bool Picked(Pool pool) => pool.LibraryIds.Length + pool.FolderIds.Length + pool.ItemIds.Length > 0;
            // A channel without clips of its own uses the shared commercial and bumper libraries. The settings'
            // clip rules and edits are applied once per distinct pair of clip lists.
            ChannelDefinition Effective(ChannelDefinition d) => d.WithClips(Picked(d.Commercials) ? d.Commercials : config.SharedCommercials, Picked(d.Bumpers) ? d.Bumpers : config.SharedBumpers);
            var resolvedClips = new Dictionary<string, List<ClipEntry>>(StringComparer.Ordinal);
            List<ClipEntry> ClipsFor(Pool commercials, Pool bumpers)
            {
                var key = JsonSerializer.Serialize(new { commercials, bumpers });
                if (!resolvedClips.TryGetValue(key, out var entries))
                    resolvedClips[key] = entries = ClipEdits.Resolve(readCommercials(commercials).Select(x => (x, "commercials")).Concat(readCommercials(bumpers).Select(x => (x, "bumpers"))), config);
                return entries;
            }
            var channelClips = definitions.ToDictionary(x => x.Id, x => { var e = Effective(x); return ClipsFor(e.Commercials, e.Bumpers); });
            var sharedClips = ClipsFor(config.SharedCommercials, config.SharedBumpers);
            var labels = new Dictionary<Guid, ClipLabel>();
            foreach (var entry in resolvedClips.Values.SelectMany(x => x))
                if (entry.Label is not null) labels[entry.Clip.Id] = entry.Label;
            // One network index for every channel: common networks, every program's tags and studios, and the overrides.
            var allPrograms = definitions.SelectMany(x => x.Blocks.Select(b => b.Source).Append(x.Source)).SelectMany(read).ToArray();
            var networks = new Planner.NetworkIndex(allPrograms.SelectMany(x => (x.Studios ?? []).Concat(x.Tags ?? []))
                .Concat(definitions.SelectMany(x => x.Blocks.Select(b => b.CommercialNetwork).Append(x.CommercialNetwork))), allPrograms.Select(x => x.Show), labels);
            DateTimeOffset? rebuiltFrom = null;
            var warnings = new Dictionary<string, string[]>();
            foreach (var defined in definitions)
            {
                ct.ThrowIfCancellationRequested();
                // A channel without clips of its own uses the shared commercial and bumper libraries.
                var channel = Effective(defined);
                if (!working.TryGetValue(channel.Id, out var state)) working[channel.Id] = state = new();
                var pools = channel.Blocks.ToDictionary(x => x.Id, x => read(x.Source));
                pools["default"] = read(channel.Source);
                pools["commercials"] = ClipEdits.Of(channelClips[channel.Id], "commercials");
                pools["bumpers"] = ClipEdits.Of(channelClips[channel.Id], "bumpers");
                var clipIds = channelClips[channel.Id].Select(x => x.Clip.Id).ToHashSet();
                foreach (var key in pools.Keys.Where(x => x is not ("commercials" or "bumpers")).ToArray()) pools[key].RemoveAll(x => clipIds.Contains(x.Id));
                // Changed schedule settings (content, blocks, breaks, clips, credits, restrictions) apply after the
                // current program, without a manual rebuild; the guide planned days ahead would otherwise keep the old ones.
                var plannedWith = Fingerprint(channel, config, detections?.Generation ?? 0);
                DateTimeOffset? from = null;
                if (channel.Id == rebuildChannel || state.PlannedWith != plannedWith && state.Guide.Any(x => x.Start > now)) from = Planner.RewindFuture(state, now);
                if (channel.Id == rebuildChannel) rebuiltFrom = from;
                Planner.Extend(channel, state, pools, now, now.AddDays(config.GuideDays), TimeZoneInfo.FindSystemTimeZoneById(config.TimeZoneId), from, config.Restrictions, chapters, networks, credits);
                state.PlannedWith = plannedWith;
                var notes = new List<string>();
                if (pools["default"].Count == 0) notes.Add("The default lineup has no eligible local videos. Time outside scheduled blocks may be empty.");
                foreach (var block in channel.Blocks.Where(x => pools[x.Id].Count == 0)) notes.Add($"The {block.Start}–{block.End} block has no eligible local videos and will use the default lineup.");
                if (channel.CommercialCount > 0 && pools["commercials"].Count == 0) notes.Add("Commercial breaks are enabled but no readable commercial clips were found.");
                if (channel.Bumpers.LibraryIds.Length + channel.Bumpers.FolderIds.Length + channel.Bumpers.ItemIds.Length > 0 && pools["bumpers"].Count == 0) notes.Add("Bumpers are selected but no readable bumper clips were found.");
                // Say why breaks are missing, rather than leaving viewers to wonder.
                var ahead = state.Guide.Where(x => x.End > now).ToArray();
                var aired = ahead.Any(x => !x.Commercial);
                bool Planned(string kind) => ahead.Any(x => x.Commercial && x.Undo?.PoolKey.StartsWith(kind, StringComparison.Ordinal) == true);
                if (channel.CommercialCount == 0 && pools["commercials"].Count > 0) notes.Add("Commercials are available, but Clips to play in each break is 0, so no commercials play.");
                if (channel.CommercialCount > 0 && pools["commercials"].Count > 0 && aired && !Planned("commercials"))
                    notes.Add("No commercials fit this channel's programs: every clip is named after a network or show these programs don't match. Add some commercials without a network in their name, or check each program's network in Guide preview.");
                var adsAhead = ahead.Where(x => x.Commercial && x.Undo?.PoolKey.StartsWith("commercials", StringComparison.Ordinal) == true).ToArray();
                var different = adsAhead.Select(x => x.ItemId).Distinct().Count();
                if (different > 0 && different < 8 && adsAhead.Length >= 3 * different)
                    notes.Add($"Only {different} different commercials fit this channel's programs, so they repeat often. Add more commercials, especially general ones without a network in their name.");
                if (pools["bumpers"].Count > 0 && aired && !Planned("bumpers")) notes.Add("No bumpers fit this channel's programs: every bumper is named after a network or show these programs don't match.");
                foreach (var (label, image) in new[] { ("watermark", channel.Watermark), ("logo", channel.Logo), ("offline card", channel.OfflineCard) })
                    if (image.Length > 0 && !File.Exists(image)) notes.Add($"The {label} image was not found at {image}.");
                if (channel.PadMinutes > 0 && pools["commercials"].Count + pools["bumpers"].Count == 0) notes.Add("Padding is on, but there are no commercials or bumpers to fill the time, so the gaps before round times are dead air.");
                foreach (var block in channel.Blocks.Where(x => x.StartMode == "exact" || x.Fill == "fit"))
                    if (pools[block.Id].Count > 0 && pools["default"].Count == 0 && pools["commercials"].Count + pools["bumpers"].Count == 0)
                        notes.Add($"The {block.Start}–{block.End} block waits for its exact start or fills to its end, but the default lineup and clips are empty, so the time before it is dead air.");
                if (channel.CreditsSpeed > 1 && aired && !ahead.Any(x => x.Speed > 1)) notes.Add("None of the upcoming programs have credits markers, so their credits play at normal speed. Credits detection may still be scanning them; otherwise add credits chapters or an Outro media segment (for example with the Intro Skipper plugin).");
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
                var keepSmart = id.StartsWith("smart-", StringComparison.Ordinal) && config.SmartChannels.Any(x => "smart-" + x.Id == id);
                if (!retained.Contains(id) && !keepAutomatic && !keepSmart) working.Remove(id);
            }
            var library = Summarize("Commercials", ClipEdits.Of(sharedClips, "commercials")).Concat(Summarize("Bumpers", ClipEdits.Of(sharedClips, "bumpers"))).ToArray();
            var snapshot = BuildClipSnapshot(resolvedClips.Values.SelectMany(x => x), networks, allPrograms.DistinctBy(x => x.Id).ToArray());
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            await File.WriteAllTextAsync(StatePath + ".tmp", JsonSerializer.Serialize(working), ct);
            File.Move(StatePath + ".tmp", StatePath, true);
            states = working;
            ClipLibrary = library;
            clipSnapshot = snapshot;
            var byChannel = definitions.ToDictionary(x => x.Id, x => working[x.Id].Guide.OrderBy(p => p.Start).ToArray());
            var guide = byChannel.Values.SelectMany(x => x).OrderBy(x => x.Start).ToArray();
            Volatile.Write(ref published, new PublishedSchedule(definitions, guide, byChannel, warnings));
            LastRefresh = now;
            LastRefreshMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(began).TotalMilliseconds;
            LastError = null;
            return rebuiltFrom;
            // How the shared clips were sorted, for the builder: counts per network and decade.
            IEnumerable<ClipGroup> Summarize(string kind, List<Media> clips) => clips
                .GroupBy(x => (Network: networks.ShowsIn(x).Length > 0 ? "Made for a show" : networks.For(x).Select(networks.Name).DefaultIfEmpty("General").First(), Decade: x.Year / 10 * 10))
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

    // Raised when planning rules change, so updating the plugin replans the future with the new rules.
    private const int PlanningRules = 2;

    // The settings that shape a channel's schedule. Names, numbers and playback-only settings are left out,
    // so changing them never rebuilds the guide.
    // Newly detected credits count too: they move breaks before the credits and speed the credits up.
    private static string Fingerprint(ChannelDefinition c, Configuration config, int detectedCredits) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
    {
        PlanningRules, DetectedCredits = config.DetectCredits ? detectedCredits : -1,
        c.Source, c.Blocks, c.Commercials, c.CommercialCount, c.EpisodeBreaks, c.MovieBreaks, c.Bumpers,
        c.MatchCommercialEra, c.MatchCommercialNetwork, c.CommercialNetwork, c.CreditsSpeed, c.PadMinutes, c.BreakMode, c.BreakIntervalMinutes, c.MaxBreaksPerProgram, c.MinBreakProgramMinutes, c.BreakFreeEndMinutes, c.CommercialCountMax, config.Restrictions, config.TimeZoneId, config.ClipRules, config.ClipOverrides
    })));
    public string[] WarningsFor(string id) => Volatile.Read(ref published).Warnings.GetValueOrDefault(id) ?? [];
    private sealed record PublishedSchedule(ChannelDefinition[] Channels, Airing[] Guide, Dictionary<string, Airing[]> ByChannel, Dictionary<string, string[]> Warnings);

    private static ChannelState CopyState(ChannelState state) => new()
    {
        Guide = new List<Airing>(state.Guide), // Airing is immutable; share records, not mutable lists.
        DailyCounts = new Dictionary<string, int>(state.DailyCounts),
        Series = new Dictionary<string, SeriesCursor>(state.Series), // SeriesCursor is immutable.
        PlannedWith = state.PlannedWith,
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
