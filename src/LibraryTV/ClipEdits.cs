namespace Jellyfin.Plugin.LibraryTV;

// How a clip is filed once the settings' rules and edits are applied on top of what its path says.
// Network: null leaves it to the path, "" files the clip as general, anything else is that network.
public sealed record ClipLabel(string? Network, string? Show);

// Kind is "commercials" or "bumpers", the same keys the planner's pools use.
public sealed record ClipEntry(Media Clip, string Kind, bool Disabled, ClipLabel? Label, string[] Sources);

public static class ClipEdits
{
    public const string General = "General";

    // Applies the rules in order, then the per-clip edits (which win), to every clip with the kind it came from.
    // Disabled clips stay in the result, so they can be listed and switched back on.
    public static List<ClipEntry> Resolve(IEnumerable<(Media Clip, string Kind)> clips, Configuration config)
    {
        var rules = (config.ClipRules ?? []).Where(x => x is not null && !string.IsNullOrWhiteSpace(x.Contains)).Select(x => (Rule: x, Text: Normalize(x.Contains.Trim()))).ToArray();
        var edits = new Dictionary<Guid, ClipEdit>();
        foreach (var edit in config.ClipOverrides ?? [])
            if (edit is not null && Guid.TryParse(edit.ItemId, out var id)) edits[id] = edit;
        var result = new List<ClipEntry>();
        foreach (var (clip, baseKind) in clips)
        {
            var path = Normalize(clip.Path);
            string? network = null, show = null;
            var kind = baseKind;
            var year = clip.Year;
            var disabled = false;
            var sources = new List<string>();
            void Apply(ClipEdit edit, string source)
            {
                var changed = false;
                if (!string.IsNullOrWhiteSpace(edit.Network)) { network = edit.Network.Trim().Equals(General, StringComparison.OrdinalIgnoreCase) ? "" : edit.Network.Trim(); changed = true; }
                if (edit.Year > 0) { year = edit.Year; changed = true; }
                if (!string.IsNullOrWhiteSpace(edit.Show)) { show = edit.Show.Trim(); changed = true; }
                if (edit.Kind is "commercial" or "bumper") { kind = edit.Kind + "s"; changed = true; }
                if (edit.Disabled is { } off) { disabled = off; changed = true; }
                if (changed) sources.Add(source);
            }
            foreach (var (rule, text) in rules)
                if (path.Contains(text, StringComparison.OrdinalIgnoreCase)) Apply(rule, $"rule “{rule.Contains.Trim()}”");
            if (edits.TryGetValue(clip.Id, out var own)) Apply(own, "edited");
            result.Add(new ClipEntry(year == clip.Year ? clip : clip with { Year = year }, kind, disabled, network is null && show is null ? null : new ClipLabel(network, show), sources.ToArray()));
        }
        return result.DistinctBy(x => (x.Clip.Id, x.Kind)).ToList();
    }

    public static List<Media> Of(IEnumerable<ClipEntry> entries, string kind) => entries.Where(x => !x.Disabled && x.Kind == kind).Select(x => x.Clip).ToList();

    public static string Normalize(string path) => path.Replace('\\', '/');
}
