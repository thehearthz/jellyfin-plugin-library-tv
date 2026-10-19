using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Jellyfin.Data.Enums;
using MediaBrowser.Model.Querying;
using Jellyfin.Database.Implementations.Enums;
using System.Text.Json;

namespace Jellyfin.Plugin.LibraryTV;

public sealed class Catalog(ILibraryManager library, StartupGate? startup = null)
{
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
                manual.Add(new ChannelDefinition { Id = "auto-" + folder.ItemId, Name = string.IsNullOrWhiteSpace(entry.Name) ? folder.Name : entry.Name.Trim(), Number = entry.Number.Trim(), Source = new Pool { LibraryIds = [folder.ItemId] } });
            }
        if (manual.Select(x => x.Id).Distinct().Count() != manual.Count) throw new ArgumentException("Channel IDs must be unique.");
        ChannelNumbers.ValidateUnique(manual);
        return manual.OrderBy(x => ChannelNumbers.Key(x.Number)).ThenBy(x => x.Name, StringComparer.Ordinal).ToArray();
    }

    // The cache lives for one refresh only, so the next refresh still sees new media.
    public Func<Pool, List<Media>> CreateRefreshReader()
    {
        var cache = new Dictionary<string, List<Media>>(StringComparer.Ordinal);
        return pool =>
        {
            var key = JsonSerializer.Serialize(new { pool.LibraryIds, pool.FolderIds, pool.ItemIds, pool.Genres, pool.Tags, pool.IncludeSpecials });
            if (!cache.TryGetValue(key, out var media)) cache[key] = media = Read(pool);
            return new List<Media>(media); // Commercial exclusion must not modify cached content.
        };
    }

    public List<Media> Read(Pool pool)
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
        return items.Values.Where(x => x.RunTimeTicks > 0 && !string.IsNullOrEmpty(x.Path) && File.Exists(x.Path))
            .Where(x => pool.Genres.Length == 0 || x.Genres.Intersect(pool.Genres, StringComparer.OrdinalIgnoreCase).Any())
            .Where(x => pool.Tags.Length == 0 || x.Tags.Intersect(pool.Tags, StringComparer.OrdinalIgnoreCase).Any())
            .Where(x => pool.IncludeSpecials || x is not Episode ep || ep.ParentIndexNumber > 0)
            .Select(x => new Media(x.Id, x is Episode e ? $"{e.SeriesName} — S{e.ParentIndexNumber:00}E{e.IndexNumber:00} {x.Name}" : x.Name,
                x.Path, TimeSpan.FromTicks(x.RunTimeTicks!.Value).TotalSeconds,
                x is Episode episode ? episode.SeriesId.ToString("N") : "", x.ParentIndexNumber ?? 0, x.IndexNumber ?? 0)).ToList();
    }
}

public sealed record CatalogItem(string Id, string Name, string Type, int? Year, string Overview);
public sealed record CatalogPage(CatalogItem[] Items, int TotalRecordCount);
