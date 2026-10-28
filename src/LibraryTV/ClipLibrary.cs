using System.Globalization;

namespace Jellyfin.Plugin.LibraryTV;

// One clip as the builder shows it: where it is filed, why, and whether it is switched off.
public sealed record ClipInfo(string Id, string Title, string Path, double Seconds, string Kind, string Network, int Year, string Show, bool Disabled, string[] Sources, string Why);
public sealed record ClipPage(int Total, ClipInfo[] Items, string[] Networks, int[] Decades);
public sealed record ClipMatches(string Summary, int Total, string[] Programs);
// Kind is "commercial" or "bumper"; Problem is "network" (filed as general), "year" (no year), "disabled" or "edited".
public sealed record ClipFilter(string? Search, string? Kind, string? Network, int? Decade, string? Problem);

internal sealed record ClipSnapshot(ClipInfo[] Clips, Dictionary<string, ClipEntry> Entries, Planner.NetworkIndex Index, Media[] Programs, HashSet<string> ClipNetworks);

public sealed partial class Scheduler
{
    private ClipSnapshot clipSnapshot = new([], [], new Planner.NetworkIndex([]), [], []);

    internal static ClipSnapshot BuildClipSnapshot(IEnumerable<ClipEntry> entries, Planner.NetworkIndex index, Media[] programs)
    {
        var all = entries.DistinctBy(x => (x.Clip.Id, x.Kind)).ToArray();
        var infos = all.Select(entry =>
        {
            var clip = entry.Clip;
            var shows = index.ShowsIn(clip);
            var found = index.For(clip).Select(index.Name).ToArray();
            var show = shows.Length > 0 ? CultureInfo.InvariantCulture.TextInfo.ToTitleCase(shows[0]) : "";
            var network = shows.Length > 0 ? "" : string.Join(", ", found);
            var why = entry.Disabled ? "Turned off: never plays."
                : show.Length > 0 ? $"Made for {show}: plays only with that show."
                : network.Length > 0 ? $"Filed under {network}" + (clip.Year > 0 ? $" in the {clip.Year / 10 * 10}s." : "; no year, so it is used only when clips from a program's own era can't fill a break.")
                : "General: plays with programs whose network has no clips of its own" + (clip.Year > 0 ? $", favouring the {clip.Year / 10 * 10}s." : ".");
            return new ClipInfo(clip.Id.ToString("N"), clip.Title, clip.Path, clip.Seconds, entry.Kind == "bumpers" ? "Bumper" : "Commercial", network, clip.Year, show, entry.Disabled, entry.Sources, why);
        }).ToArray();
        var entriesById = new Dictionary<string, ClipEntry>(StringComparer.Ordinal);
        foreach (var entry in all) entriesById.TryAdd(entry.Clip.Id.ToString("N"), entry);
        var networks = all.Where(x => !x.Disabled && index.ShowsIn(x.Clip).Length == 0).SelectMany(x => index.For(x.Clip)).ToHashSet(StringComparer.Ordinal);
        return new ClipSnapshot(infos, entriesById, index, programs, networks);
    }

    private static bool Matches(ClipInfo clip, ClipFilter filter)
    {
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var text = clip.Title + " " + ClipEdits.Normalize(clip.Path);
            if (filter.Search.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(term => !text.Contains(term, StringComparison.OrdinalIgnoreCase))) return false;
        }
        if (filter.Kind is "commercial" or "bumper" && !clip.Kind.Equals(filter.Kind, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrWhiteSpace(filter.Network))
        {
            var general = clip.Network.Length == 0 && clip.Show.Length == 0;
            if (filter.Network.Equals(ClipEdits.General, StringComparison.OrdinalIgnoreCase) ? !general
                : filter.Network.Equals("Made for a show", StringComparison.OrdinalIgnoreCase) ? clip.Show.Length == 0
                : !clip.Network.Equals(filter.Network, StringComparison.OrdinalIgnoreCase)) return false;
        }
        if (filter.Decade is { } decade && clip.Year / 10 * 10 != decade) return false;
        return filter.Problem switch
        {
            "network" => clip.Network.Length == 0 && clip.Show.Length == 0 && !clip.Disabled,
            "year" => clip.Year == 0 && !clip.Disabled,
            "disabled" => clip.Disabled,
            "edited" => clip.Sources.Length > 0,
            _ => true,
        };
    }

    private IEnumerable<ClipInfo> Filtered(ClipFilter filter) => clipSnapshot.Clips.Where(x => Matches(x, filter))
        .OrderBy(x => x.Network.Length == 0 && x.Show.Length == 0).ThenBy(x => x.Network, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Year).ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase);

    public ClipPage BrowseClips(ClipFilter filter, int start, int limit)
    {
        var snapshot = clipSnapshot;
        var matching = Filtered(filter).ToArray();
        return new ClipPage(matching.Length, matching.Skip(Math.Max(0, start)).Take(Math.Clamp(limit, 1, 200)).ToArray(),
            snapshot.Clips.Select(x => x.Network).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            snapshot.Clips.Select(x => x.Year / 10 * 10).Distinct().Order().ToArray());
    }

    // Every clip matching a filter, for selecting them all at once.
    public string[] ClipIds(ClipFilter filter) => Filtered(filter).Take(5000).Select(x => x.Id).ToArray();

    // The programs a clip can play with: those it was made for, those on its network, or, for a general clip,
    // those whose network has no clips of its own.
    public ClipMatches? MatchesFor(string id)
    {
        var snapshot = clipSnapshot;
        if (!snapshot.Entries.TryGetValue(id, out var entry)) return null;
        var index = snapshot.Index;
        var clip = entry.Clip;
        if (entry.Disabled) return new ClipMatches("This clip is turned off, so it plays with nothing.", 0, []);
        string? NetworkOf(Media program) => (program.Tags ?? []).Concat(program.Studios ?? []).Select(x => index.Resolve(x, snapshot.ClipNetworks.Contains)).FirstOrDefault(x => x is not null);
        var shows = index.ShowsIn(clip);
        var networks = index.For(clip);
        Func<Media, bool> fits;
        string summary;
        if (shows.Length > 0)
        {
            fits = p => Planner.NetworkIndex.ShowKey(p.Show) is { } key && shows.Contains(key);
            summary = "Made for a show, so it plays only with its episodes:";
        }
        else if (networks.Length > 0)
        {
            fits = p => NetworkOf(p) is { } network && networks.Contains(network);
            summary = $"Plays with programs from {string.Join(" and ", networks.Select(index.Name))}" + (clip.Year > 0 ? $", favouring the {clip.Year / 10 * 10}s:" : ":");
        }
        else
        {
            fits = p => NetworkOf(p) is null && !(Planner.NetworkIndex.ShowKey(p.Show) is { } key && snapshot.Entries.Values.Any(x => !x.Disabled && index.ShowsIn(x.Clip).Contains(key)));
            summary = "A general clip: it plays with programs whose network has no clips of its own" + (clip.Year > 0 ? $", favouring the {clip.Year / 10 * 10}s:" : ":");
        }
        var programs = snapshot.Programs.Where(fits).GroupBy(x => x.Show ?? x.Title).OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).ToArray();
        string Years(IEnumerable<Media> group) { var years = group.Select(x => x.Year).Where(x => x > 0).ToArray(); return years.Length == 0 ? "" : years.Min() == years.Max() ? $" ({years[0]})" : $" ({years.Min()}–{years.Max()})"; }
        return new ClipMatches(summary, programs.Length, programs.Take(25).Select(x => x.Key + Years(x)).ToArray());
    }
}
