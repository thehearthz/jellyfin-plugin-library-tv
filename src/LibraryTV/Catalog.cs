using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Jellyfin.Data.Enums;

namespace Jellyfin.Plugin.LibraryTV;

public sealed class Catalog(ILibraryManager library)
{
    public object Libraries() => library.GetVirtualFolders().Select(x => new { x.ItemId, x.Name, x.Locations });
    public ChannelDefinition[] Channels(Configuration config)
    {
        var manual = config.Channels.Where(x => x.Enabled).ToList();
        if (config.AutoChannels)
            foreach (var folder in library.GetVirtualFolders().Where(x => config.AutoLibraryIds.Contains(x.ItemId)))
                manual.Add(new ChannelDefinition { Id = "auto-" + folder.ItemId, Name = folder.Name, Number = (100 + manual.Count).ToString(), Source = new Pool { LibraryIds = [folder.ItemId] } });
        if (manual.Select(x => x.Id).Distinct().Count() != manual.Count) throw new ArgumentException("Channel IDs must be unique.");
        return manual.ToArray();
    }

    public List<Media> Read(Pool pool)
    {
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
