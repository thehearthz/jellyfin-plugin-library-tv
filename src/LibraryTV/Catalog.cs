using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Jellyfin.Data.Enums;
using MediaBrowser.Model.Querying;
using Jellyfin.Database.Implementations.Enums;
using System.Text.Json;

namespace Jellyfin.Plugin.LibraryTV;

public sealed class Catalog(ILibraryManager library, StartupGate? startup = null, MediaBrowser.Controller.Chapters.IChapterManager? chapterManager = null,
    MediaBrowser.Controller.MediaSegments.IMediaSegmentManager? segmentManager = null)
{
    private static readonly System.Text.RegularExpressions.Regex CreditsChapter = new(@"\b(end\s*credits|credits|outro|ending|closing)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    // Where a program's credits run, in seconds: an Outro media segment (added by plugins such as Intro
    // Skipper), else a chapter named like "Credits", "Outro" or "Ending". Only credits in the second half,
    // at least ten seconds long, count. Read only for programs being scheduled, cached for one refresh.
    public Func<Media, (double Start, double End)?> CreateCreditsReader()
    {
        var cache = new Dictionary<Guid, (double Start, double End)?>();
        return media =>
        {
            if (cache.TryGetValue(media.Id, out var known)) return known;
            (double Start, double End)? found = null;
            try
            {
                if (segmentManager is not null && library.GetItemById(media.Id) is { } item
                    && segmentManager.GetSegmentsAsync(item, [MediaSegmentType.Outro], library.GetLibraryOptions(item), true).GetAwaiter().GetResult().OrderBy(x => x.StartTicks).LastOrDefault() is { } outro)
                    found = (Seconds(outro.StartTicks), Seconds(outro.EndTicks));
                if (found is null && chapterManager is not null)
                {
                    var chapters = chapterManager.GetChapters(media.Id).OrderBy(x => x.StartPositionTicks).ToList();
                    var index = chapters.FindLastIndex(x => x.Name is { } name && CreditsChapter.IsMatch(name));
                    if (index >= 0) found = (Seconds(chapters[index].StartPositionTicks), index + 1 < chapters.Count ? Seconds(chapters[index + 1].StartPositionTicks) : media.Seconds);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { found = null; }
            if (found is { } credits)
            {
                var end = Math.Min(credits.End, media.Seconds);
                found = credits.Start >= media.Seconds / 2 && end - credits.Start >= 10 ? (credits.Start, end) : null;
            }
            return cache[media.Id] = found;
        };
        static double Seconds(long ticks) => TimeSpan.FromTicks(ticks).TotalSeconds;
    }

    // Chapter start times in seconds, read only for programs being scheduled and cached for one refresh.
    public Func<Media, double[]> CreateChapterReader()
    {
        var cache = new Dictionary<Guid, double[]>();
        return media =>
        {
            if (cache.TryGetValue(media.Id, out var marks)) return marks;
            try { marks = chapterManager?.GetChapters(media.Id).Select(x => TimeSpan.FromTicks(x.StartPositionTicks).TotalSeconds).Where(x => x > 0).ToArray() ?? []; }
            catch (Exception ex) when (ex is not OperationCanceledException) { marks = []; }
            return cache[media.Id] = marks;
        };
    }

    public CatalogPage Browse(string kind, Guid libraryId, string? search, int start, int limit)
    {
        startup?.RequireReady();
        BaseItemKind[] types = kind switch
        {
            "shows" => [BaseItemKind.Series], "movies" => [BaseItemKind.Movie],
            "folders" => [BaseItemKind.Folder], "videos" => [BaseItemKind.Video],
            _ => throw new ArgumentException("Choose shows, movies, folders, or videos.")
        };
        var result = library.GetItemsResult(new InternalItemsQuery
        {
            ParentId = libraryId, Recursive = true, IsVirtualItem = false, IncludeItemTypes = types,
            SearchTerm = string.IsNullOrWhiteSpace(search) ? null : search.Trim(),
            StartIndex = Math.Max(0, start), Limit = Math.Clamp(limit, 1, 60), EnableTotalRecordCount = true,
            OrderBy = [(ItemSortBy.SortName, SortOrder.Ascending)]
        });
        return new CatalogPage(result.Items.Select(Describe).ToArray(), result.TotalRecordCount);
    }

    public CatalogItem[] Resolve(Guid[] ids)
    {
        startup?.RequireReady();
        if (ids.Length > 500) throw new ArgumentException("Resolve at most 500 selected items at a time.");
        return ids.Distinct().Select(id => library.GetItemById(id)).Where(x => x is not null).Select(x => Describe(x!)).ToArray();
    }

    private static CatalogItem Describe(BaseItem item) => new(item.Id.ToString("N"), item.Name, item.GetBaseItemKind().ToString(), item.ProductionYear,
        item.Overview is { Length: > 240 } overview ? overview[..240] + "…" : item.Overview ?? "");

    public object Libraries()
    {
        startup?.RequireReady();
        return library.GetVirtualFolders().Select(x => new { x.ItemId, x.Name, x.Locations });
    }

    public bool PrepareAutomaticChannels(Configuration config, bool preserveLegacyNumbers = false)
    {
        startup?.RequireReady();
        if (!config.AutoChannels) return false;
        var changed = false;
        var overrides = config.AutomaticChannels.ToList();
        var used = config.Channels.Select(x => ChannelNumbers.Key(x.Number)).ToHashSet();
        foreach (var entry in overrides.Where(x => !string.IsNullOrWhiteSpace(x.Number))) used.Add(ChannelNumbers.Key(entry.Number));
        var next = config.AutoChannelStartNumber;
        var selected = config.AutoLibraryIds.Select(Guid.Parse).ToHashSet();
        var folders = library.GetVirtualFolders().Where(x => selected.Contains(Guid.Parse(x.ItemId))).ToArray();
        if (preserveLegacyNumbers && overrides.Count == 0)
        {
            var legacyNumber = 100 + config.Channels.Count(x => x.Enabled);
            foreach (var folder in folders)
            {
                // v0.2 used these numbers. Retain them on upgrade when available.
                if (!used.Contains((legacyNumber, -1)))
                {
                    overrides.Add(new AutomaticChannel { LibraryId = folder.ItemId, Number = legacyNumber.ToString(System.Globalization.CultureInfo.InvariantCulture) });
                    used.Add((legacyNumber, -1));
                    changed = true;
                }
                legacyNumber++;
            }
        }
        foreach (var folder in folders.OrderBy(x => x.ItemId, StringComparer.Ordinal))
        {
            var entry = overrides.FirstOrDefault(x => Guid.Parse(x.LibraryId) == Guid.Parse(folder.ItemId));
            if (entry is null) { entry = new AutomaticChannel { LibraryId = folder.ItemId }; overrides.Add(entry); changed = true; }
            if (!string.IsNullOrWhiteSpace(entry.Number)) continue;
            while (next <= 999999 && used.Contains((next, -1))) next++;
            if (next > 999999) throw new ArgumentException("No automatic channel numbers remain. Choose a lower starting number.");
            entry.Number = next.ToString(System.Globalization.CultureInfo.InvariantCulture);
            used.Add((next++, -1));
            changed = true;
        }
        if (changed) config.AutomaticChannels = overrides.ToArray();
        return changed;
    }

    public ChannelDefinition[] Channels(Configuration config)
    {
        var manual = config.Channels.Where(x => x.Enabled).ToList();
        if (config.AutoChannels)
            foreach (var folder in library.GetVirtualFolders().Where(x => config.AutoLibraryIds.Any(id => Guid.Parse(id) == Guid.Parse(x.ItemId))))
            {
                var entry = config.AutomaticChannels.FirstOrDefault(x => Guid.Parse(x.LibraryId) == Guid.Parse(folder.ItemId))
                    ?? throw new ArgumentException("Automatic channel numbers must be assigned before building channels.");
                manual.Add(new ChannelDefinition { Id = "auto-" + folder.ItemId, Name = string.IsNullOrWhiteSpace(entry.Name) ? folder.Name : entry.Name.Trim(), Number = entry.Number.Trim(), Source = new Pool { LibraryIds = [folder.ItemId] },
                    CommercialCount = config.AutomaticCommercialCount });
            }
        if (manual.Select(x => x.Id).Distinct().Count() != manual.Count) throw new ArgumentException("Channel IDs must be unique.");
        ChannelNumbers.ValidateUnique(manual);
        return manual.OrderBy(x => ChannelNumbers.Key(x.Number)).ThenBy(x => x.Name, StringComparer.Ordinal).ToArray();
    }

    // The cache lives for one refresh only, so the next refresh still sees new media.
    public Func<Pool, List<Media>> CreateRefreshReader(bool commercials = false, RefreshScan? scan = null)
    {
        var cache = new Dictionary<string, List<Media>>(StringComparer.Ordinal);
        scan ??= new();
        return pool =>
        {
            var key = JsonSerializer.Serialize(new { pool.LibraryIds, pool.FolderIds, pool.ItemIds, pool.Genres, pool.Tags, pool.IncludeSpecials, commercials });
            if (!cache.TryGetValue(key, out var media)) cache[key] = media = Read(pool, scan, commercials);
            return new List<Media>(media); // Commercial exclusion must not modify cached content.
        };
    }

    // Shared by every pool read during one refresh.
    public sealed class RefreshScan
    {
        public Dictionary<string, bool> Folders { get; } = new(StringComparer.Ordinal);
        public Dictionary<Guid, Series?> Series { get; } = [];
    }

    public List<Media> Read(Pool pool, RefreshScan? scan = null, bool commercials = false)
    {
        startup?.RequireReady();
        // An empty selector deliberately means no content, never every library.
        if (pool.LibraryIds.Length + pool.FolderIds.Length + pool.ItemIds.Length == 0) return [];
        var items = new Dictionary<Guid, BaseItem>();
        foreach (var id in pool.LibraryIds.Concat(pool.FolderIds))
            foreach (var item in library.GetItemList(new InternalItemsQuery { ParentId = Guid.Parse(id), Recursive = true, IsVirtualItem = false, IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Video] })) items[item.Id] = item;
        foreach (var id in pool.ItemIds)
        {
            var item = library.GetItemById(Guid.Parse(id));
            if (item is MediaBrowser.Controller.Entities.Folder)
                foreach (var child in library.GetItemList(new InternalItemsQuery { ParentId = item.Id, Recursive = true, IsVirtualItem = false, IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Video] })) items[child.Id] = child;
            else if (item is not null) items[item.Id] = item;
        }
        // Check each media folder once, not every file: this catches an offline mount cheaply.
        // Jellyfin's library scan removes deleted files, and playback recovery skips stragglers.
        scan ??= new();
        bool Available(string path)
        {
            var folder = Path.GetDirectoryName(path) ?? "";
            if (!scan.Folders.TryGetValue(folder, out var exists)) scan.Folders[folder] = exists = Directory.Exists(folder);
            return exists;
        }
        Series? SeriesOf(Episode episode)
        {
            if (episode.SeriesId == Guid.Empty) return null;
            if (!scan.Series.TryGetValue(episode.SeriesId, out var series)) scan.Series[episode.SeriesId] = series = library.GetItemById(episode.SeriesId) as Series;
            return series;
        }
        var media = new List<Media>();
        foreach (var x in items.Values.Where(x => x.RunTimeTicks > 0 && !string.IsNullOrEmpty(x.Path) && Available(x.Path)))
        {
            var episode = x as Episode;
            var series = episode is null ? null : SeriesOf(episode);
            // Episodes usually carry their genres and tags on the series.
            var genres = x.Genres.Concat(series?.Genres ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var tags = x.Tags.Concat(series?.Tags ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            // For shows, studios are the networks they aired on, such as Nickelodeon or Adult Swim.
            var studios = x.Studios.Concat(series?.Studios ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (pool.Genres.Length > 0 && !genres.Intersect(pool.Genres, StringComparer.OrdinalIgnoreCase).Any()) continue;
            if (pool.Tags.Length > 0 && !tags.Intersect(pool.Tags, StringComparer.OrdinalIgnoreCase).Any()) continue;
            if (!pool.IncludeSpecials && episode is not null && !(episode.ParentIndexNumber > 0)) continue;
            var pathYear = YearFromPath(x.Path);
            // Commercial folders are usually organised by year or decade, which beats file dates.
            var year = commercials ? pathYear ?? x.ProductionYear ?? x.PremiereDate?.Year
                : episode is not null ? x.PremiereDate?.Year ?? x.ProductionYear ?? series?.ProductionYear ?? pathYear
                : x.ProductionYear ?? x.PremiereDate?.Year ?? pathYear;
            media.Add(new Media(x.Id, episode is not null ? $"{episode.SeriesName} — S{episode.ParentIndexNumber:00}E{episode.IndexNumber:00} {x.Name}" : x.Name,
                x.Path, TimeSpan.FromTicks(x.RunTimeTicks!.Value).TotalSeconds,
                episode is not null ? SeriesKey(episode, series) : "", x.ParentIndexNumber ?? 0, x.IndexNumber ?? 0, year ?? 0, genres, tags, studios, episode is not null ? episode.SeriesName : x.Name));
        }
        // The same file, or the same episode of the same show in two libraries, airs only once per cycle.
        return media.DistinctBy(x => x.Path, StringComparer.Ordinal)
            .DistinctBy(x => x.Series.Length > 0 && x.Episode > 0 ? $"{x.Series}|{x.Season}|{x.Episode}" : x.Id.ToString())
            .ToList();
    }

    // One key per show, even when two libraries hold separate copies of it.
    private static string SeriesKey(Episode episode, Series? series)
    {
        if (series is not null)
            foreach (var provider in new[] { "Tvdb", "Tmdb", "Imdb" })
                if (series.ProviderIds.TryGetValue(provider, out var value) && !string.IsNullOrWhiteSpace(value)) return provider.ToLowerInvariant() + ":" + value.Trim();
        return episode.SeriesId.ToString("N");
    }

    private static readonly System.Text.RegularExpressions.Regex YearPattern = new(@"(?<![0-9])(?:(?<year>19[2-9][0-9]|20[0-9]{2})(?![0-9])|(?<decade>19[2-9]0|20[0-2]0)'?s\b|(?<short>[2-9]0)'?s\b|'(?<apostrophe>[2-9][0-9])\b)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    // The last year or decade in a path, such as "1980s/Cola (1985).mp4" (1985), "80s ads/clip.mp4" (1980) or "Gak '94.mp4" (1994).
    public static int? YearFromPath(string path)
    {
        int? found = null;
        foreach (System.Text.RegularExpressions.Match match in YearPattern.Matches(path))
        {
            var value = match.Groups["year"].Success ? int.Parse(match.Groups["year"].Value, System.Globalization.CultureInfo.InvariantCulture)
                : match.Groups["decade"].Success ? int.Parse(match.Groups["decade"].Value, System.Globalization.CultureInfo.InvariantCulture)
                : match.Groups["short"].Success ? 1900 + int.Parse(match.Groups["short"].Value, System.Globalization.CultureInfo.InvariantCulture)
                : 1900 + int.Parse(match.Groups["apostrophe"].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (value <= DateTime.UtcNow.Year + 1) found = value;
        }
        return found;
    }
}

public sealed record CatalogItem(string Id, string Name, string Type, int? Year, string Overview);
public sealed record CatalogPage(CatalogItem[] Items, int TotalRecordCount);
