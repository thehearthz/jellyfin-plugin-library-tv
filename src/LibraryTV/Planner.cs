using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.LibraryTV;

// Pure scheduling logic: no timers, media processes or library queries.
public static class Planner
{
    public const int MaxGuideEntries = 10000;
    public static Media? Next(List<Media> media, Pool pool, Rotation rotation, string seed)
        => Next(media, pool, rotation, seed, out _);

    // `resolve` may air a different episode of the same series (the channel's series cursor);
    // `allowed` skips entries that cannot air in this slot, leaving them for a later slot.
    private static Media? Next(List<Media> media, Pool pool, Rotation rotation, string seed, out PlanningUndo? undo,
        Func<Media, Media>? resolve = null, Func<Media, bool>? allowed = null)
    {
        undo = null;
        if (media.Count == 0) return null;
        var previousLast = rotation.Last;
        Guid[]? previousSeen = null;
        var swapped = false;
        var byId = media.ToDictionary(x => x.Id);
        rotation.Remaining.RemoveAll(id => !byId.ContainsKey(id));
        // Rotations saved before this setting existed, or under another value, are reordered once.
        if (rotation.EpisodesInARow != pool.EpisodesInARow)
        {
            rotation.Remaining = Interleave(rotation.Remaining, byId, pool.EpisodesInARow);
            rotation.EpisodesInARow = pool.EpisodesInARow;
        }
        var known = rotation.Remaining.Concat(rotation.Seen).ToHashSet();
        var added = media.Where(x => !known.Contains(x.Id)).ToList();
        if (rotation.Remaining.Count == 0 && added.Count == 0 && pool.RepeatWhenExhausted)
        {
            previousSeen = rotation.Seen.ToArray();
            rotation.Seen.Clear();
            added = media.ToList();
        }
        // Order each series by season/episode; shuffle series, never their episodes.
        var groups = added.GroupBy(SeriesKey);
        if (pool.Shuffle) groups = groups.OrderBy(g => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed + rotation.Last + g.Key))));
        var ordered = groups.SelectMany(group => group.OrderBy(x => x.Season).ThenBy(x => x.Episode).ThenBy(x => x.Id).Select(x => x.Id)).ToList();
        var before = rotation.Remaining.Count;
        rotation.Remaining.AddRange(Interleave(ordered, byId, pool.EpisodesInARow));
        if (rotation.Remaining.Count == 0) return null;
        if (rotation.Remaining[0] == rotation.Last && rotation.Remaining.Count > 1 && byId[rotation.Last].Series.Length == 0)
        {
            (rotation.Remaining[0], rotation.Remaining[1]) = (rotation.Remaining[1], rotation.Remaining[0]);
            swapped = true;
        }
        var index = -1;
        Media? chosen = null;
        for (var i = 0; i < rotation.Remaining.Count && chosen is null; i++)
        {
            var candidate = byId[rotation.Remaining[i]];
            candidate = resolve?.Invoke(candidate) ?? candidate;
            if (allowed?.Invoke(candidate) != false) (index, chosen) = (i, candidate);
        }
        if (chosen is null)
        {
            // Nothing left in this cycle can air in this slot. Undo the refill so the next slot sees the same rotation.
            if (swapped) (rotation.Remaining[0], rotation.Remaining[1]) = (rotation.Remaining[1], rotation.Remaining[0]);
            rotation.Remaining.RemoveRange(before, rotation.Remaining.Count - before);
            if (previousSeen is not null) rotation.Seen = new List<Guid>(previousSeen);
            if (!pool.RepeatWhenExhausted) return null;
            // Rather than going off the air, repeat the least recently aired item that is allowed.
            // A series still continues from its cursor, so this does not repeat episodes.
            // The movie that just aired is the last resort.
            foreach (var again in new[] { false, true })
                for (var i = 0; i < rotation.Seen.Count && chosen is null; i++)
                {
                    if (!byId.TryGetValue(rotation.Seen[i], out var candidate) || again != (rotation.Seen[i] == previousLast && candidate.Series.Length == 0)) continue;
                    candidate = resolve?.Invoke(candidate) ?? candidate;
                    if (allowed?.Invoke(candidate) != false) (index, chosen) = (i, candidate);
                }
            if (chosen is null) return null;
            var repeated = rotation.Seen[index];
            rotation.Seen.RemoveAt(index);
            rotation.Seen.Add(repeated);
            rotation.Last = repeated;
            undo = new PlanningUndo("", previousLast, [], false, null, Index: index, Token: repeated == chosen.Id ? null : repeated, SeenIndex: index);
            return chosen;
        }
        var token = rotation.Remaining[index];
        rotation.Remaining.RemoveAt(index);
        rotation.Seen.Add(token);
        rotation.Last = token;
        undo = new PlanningUndo("", previousLast, added.Select(x => x.Id).ToArray(), swapped, previousSeen, Index: index, Token: token == chosen.Id ? null : token);
        return chosen;
    }

    private static string SeriesKey(Media x) => x.Series.Length == 0 ? x.Id.ToString() : x.Series;

    // A turn is up to `run` consecutive episodes of one series, or a single movie. Each
    // group's turns are spread evenly through the cycle, so short and long series stay mixed.
    // Group order and episode order are preserved; zero keeps every series in one turn.
    private static List<Guid> Interleave(List<Guid> ids, Dictionary<Guid, Media> byId, int run)
    {
        var groups = ids.GroupBy(id => SeriesKey(byId[id])).ToArray();
        return groups.SelectMany((group, index) =>
            {
                var turns = group.Chunk(run > 0 ? run : int.MaxValue).ToArray();
                return turns.Select((turn, k) => (Position: (k + (index + 0.5) / groups.Length) / turns.Length, Group: index, Turn: turn));
            })
            .OrderBy(x => x.Position).ThenBy(x => x.Group).SelectMany(x => x.Turn).ToList();
    }

    // Rewind only future selections. Legacy guide entries have no undo metadata,
    // so keep them fixed and safely rebuild the reversible tail after them.
    public static DateTimeOffset RewindFuture(ChannelState state, DateTimeOffset now)
    {
        var boundary = state.Guide.Where(x => x.Start <= now && x.End > now).Select(x => x.End).DefaultIfEmpty(now).Max();
        boundary = state.Guide.Where(x => x.Start > now && x.Undo is null).Select(x => x.End).Append(boundary).Max();
        // The rest of the current program (its breaks and later parts) and the break after it belong to it.
        foreach (var rest in state.Guide.Where(x => x.Start >= boundary).OrderBy(x => x.Start))
        {
            if (!(rest.Commercial || rest.Offset > 0) || rest.Start != boundary) break;
            boundary = rest.End;
        }
        var removed = state.Guide.Where(x => x.Start >= boundary && x.Start > now).ToArray();
        foreach (var item in removed.Reverse())
        {
            var undo = item.Undo ?? throw new InvalidOperationException("Cannot rewind a legacy program.");
            if (undo.Part) continue;
            var rotation = state.Rotations[undo.PoolKey];
            if (undo.SeenIndex is { } seenIndex)
            {
                // An early repeat moved this entry to the end of Seen; move it back.
                if (rotation.Seen.Count > 0) rotation.Seen.RemoveAt(rotation.Seen.Count - 1);
                rotation.Seen.Insert(Math.Min(seenIndex, rotation.Seen.Count), undo.Token ?? item.ItemId);
                rotation.Last = undo.PreviousLast;
                RestoreCursorAndQuota(undo);
                continue;
            }
            rotation.Remaining.Insert(Math.Min(undo.Index, rotation.Remaining.Count), undo.Token ?? item.ItemId);
            if (undo.Swapped)
            {
                var index = rotation.Remaining.IndexOf(undo.PreviousLast, 1);
                if (index > 0) (rotation.Remaining[0], rotation.Remaining[index]) = (rotation.Remaining[index], rotation.Remaining[0]);
            }
            if (undo.AddedIds.Length > 0)
            {
                var added = undo.AddedIds.ToHashSet();
                rotation.Remaining.RemoveAll(added.Contains);
            }
            if (undo.PreviousSeen is not null) rotation.Seen = new List<Guid>(undo.PreviousSeen);
            else if (rotation.Seen.Count > 0) rotation.Seen.RemoveAt(rotation.Seen.Count - 1);
            rotation.Last = undo.PreviousLast;
            RestoreCursorAndQuota(undo);
        }
        state.Guide.RemoveAll(x => x.Start >= boundary && x.Start > now);
        return boundary;
        void RestoreCursorAndQuota(PlanningUndo undo)
        {
            if (undo.Series is { } series)
            {
                if (undo.PreviousCursor is null) state.Series.Remove(series); else state.Series[series] = undo.PreviousCursor;
            }
            if (undo.QuotaKey is { } quota)
            {
                var count = state.DailyCounts.GetValueOrDefault(quota);
                if (count <= 1) state.DailyCounts.Remove(quota); else state.DailyCounts[quota] = count - 1;
            }
        }
    }

    public static (TimeBlock Block, DateOnly Date)? ActiveBlock(ChannelDefinition channel, DateTimeOffset at, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(at, zone);
        var time = TimeOnly.FromDateTime(local.DateTime);
        foreach (var block in channel.Blocks)
        {
            var start = TimeOnly.ParseExact(block.Start, "HH:mm");
            var end = TimeOnly.ParseExact(block.End, "HH:mm");
            var date = DateOnly.FromDateTime(local.DateTime);
            var active = start < end ? time >= start && time < end : time >= start || time < end;
            if (start >= end && time < end) date = date.AddDays(-1);
            if (active && block.Days.Contains(date.DayOfWeek)) return (block, date);
        }
        return null;
    }

    public static void Extend(ChannelDefinition channel, ChannelState state, IReadOnlyDictionary<string, List<Media>> pools, DateTimeOffset now, DateTimeOffset until, TimeZoneInfo zone,
        DateTimeOffset? startAt = null, IReadOnlyList<ContentRule>? rules = null, Func<Media, double[]>? chapters = null, NetworkIndex? networks = null,
        Func<Media, (double Start, double End)?>? credits = null)
    {
        var at = state.Guide.Count > 0 ? state.Guide[^1].End : now;
        if (startAt > at) at = startAt.Value;
        var cutoff = now.AddHours(-6);
        state.Guide.RemoveAll(x => x.End < cutoff);
        // Preserve continuity through server downtime, with bounded catch-up work.
        if (at < now.AddDays(-7)) at = now;
        var episodes = new Dictionary<string, ILookup<string, Media>>();
        var commercials = pools["commercials"];
        var bumpers = pools.GetValueOrDefault("bumpers") ?? [];
        // The scheduler shares one index across channels; on its own, a channel knows its overrides, its programs' tags and studios, and common networks.
        var programs = pools.Where(x => x.Key is not ("commercials" or "bumpers")).SelectMany(x => x.Value).ToArray();
        networks ??= new NetworkIndex(channel.Blocks.Select(x => x.CommercialNetwork).Append(channel.CommercialNetwork)
            .Concat(programs.SelectMany(x => (x.Tags ?? []).Concat(x.Studios ?? []))), programs.Select(x => x.Show));
        var adPicker = new ClipPicker(channel, "commercials", commercials, networks);
        var bumperPicker = new ClipPicker(channel, "bumpers", bumpers, networks);
        // Restrictions must cover a program's own commercial breaks, so estimate their length.
        var breakSeconds = channel.CommercialCount * (commercials.Count > 0 ? commercials.Average(x => x.Seconds) : 0) + 2 * (bumpers.Count > 0 ? bumpers.Average(x => x.Seconds) : 0);
        // Catch-up entries that already ended are pruned below; they must not use the guide cap.
        var stale = 0;
        var iterations = 0;
        while (at < until && state.Guide.Count - stale < MaxGuideEntries && iterations++ < 100000)
        {
            var active = ActiveBlock(channel, at, zone);
            var key = "default";
            var pool = channel.Source;
            string? countKey = null;
            var network = channel.CommercialNetwork;
            if (active is { } match)
            {
                countKey = match.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + ":" + match.Block.Id;
                state.DailyCounts.TryGetValue(countKey, out var count);
                if (match.Block.MaxItemsPerDay == 0 || count < match.Block.MaxItemsPerDay)
                {
                    key = match.Block.Id;
                    pool = match.Block.Source;
                    if (!string.IsNullOrWhiteSpace(match.Block.CommercialNetwork)) network = match.Block.CommercialNetwork;
                }
            }
            if (!state.Rotations.TryGetValue(key, out var rotation)) state.Rotations[key] = rotation = new();
            var next = Next(pools[key], pool, rotation, channel.Id + key, out var undo, x => Continue(key, x), AllowedProgram);
            if (next is null && key != "default")
            {
                key = "default";
                network = channel.CommercialNetwork;
                if (!state.Rotations.TryGetValue(key, out rotation)) state.Rotations[key] = rotation = new();
                next = Next(pools[key], channel.Source, rotation, channel.Id + key, out undo, x => Continue(key, x), AllowedProgram);
            }
            if (next is null) { at = at.AddMinutes(1); continue; }
            SeriesCursor? previousCursor = null;
            if (next.Series.Length > 0)
            {
                previousCursor = state.Series.GetValueOrDefault(next.Series);
                state.Series[next.Series] = new SeriesCursor(next.Season, next.Episode);
            }
            if (key != "default" && countKey is not null) state.DailyCounts[countKey] = state.DailyCounts.GetValueOrDefault(countKey) + 1;
            var first = undo! with { PoolKey = key, QuotaKey = key == "default" ? null : countKey, Series = next.Series.Length > 0 ? next.Series : null, PreviousCursor = previousCursor };
            // The program airs in parts with a commercial break between each, then a break after it. With a
            // credits speed, its credits are a faster part of their own, and anything after them plays normally.
            var run = channel.CreditsSpeed > 1 ? credits?.Invoke(next) : null;
            var content = run?.Start ?? next.Seconds;
            var from = 0.0;
            foreach (var cut in Cuts(next, network, content).Append(content))
            {
                Add(next, false, from == 0 ? first : Part(), from, cut - from);
                if (cut < content) Break(next, network);
                from = cut;
            }
            if (run is { } creditsRun)
            {
                Add(next, false, Part(), creditsRun.Start, (creditsRun.End - creditsRun.Start) / channel.CreditsSpeed, channel.CreditsSpeed);
                if (creditsRun.End < next.Seconds - 1) Add(next, false, Part(), creditsRun.End, next.Seconds - creditsRun.End);
            }
            Break(next, network);
        }
        if (stale > 0) state.Guide.RemoveAll(x => x.End < cutoff);
        // Daily quota entries only need to survive the current planning horizon.
        var keep = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now.AddDays(-1), zone).DateTime).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        foreach (var key in state.DailyCounts.Keys.Where(k => string.CompareOrdinal(k[..10], keep) < 0).ToArray()) state.DailyCounts.Remove(key);
        static PlanningUndo Part() => new("", Guid.Empty, [], false, null, Part: true);
        void Add(Media item, bool commercial, PlanningUndo undo, double offset = 0, double? seconds = null, double speed = 1, string? match = null)
        {
            var end = at.AddSeconds(seconds ?? item.Seconds);
            state.Guide.Add(new(channel.Id, item.Id, item.Title, item.Path, at, end, commercial, undo, offset, match, speed));
            if (end < cutoff) stale++;
            at = end;
        }
        // A break is bumper, commercials, bumper; without commercials it is one bumper between programs.
        void Break(Media program, string network)
        {
            if (channel.CommercialCount == 0) { Clip(bumperPicker, program, network); return; }
            Clip(bumperPicker, program, network);
            for (var i = 0; i < channel.CommercialCount; i++)
                if (!Clip(adPicker, program, network)) break;
            Clip(bumperPicker, program, network);
        }
        bool Clip(ClipPicker picker, Media program, string network)
        {
            if (picker.Empty || state.Guide.Count - stale >= MaxGuideEntries || picker.For(program, network) is not var (clipKey, clips, why)) return false;
            if (!state.Rotations.TryGetValue(clipKey, out var clipRotation)) state.Rotations[clipKey] = clipRotation = new();
            var seed = channel.Id + (clipKey == "commercials" ? "ads" : clipKey);
            var clip = Next(clips, picker.Pool, clipRotation, seed, out var clipUndo, null, Allowed);
            if (clip is null) return false;
            Add(clip, true, clipUndo! with { PoolKey = clipKey }, match: why);
            return true;
        }
        // Where to break inside a program: the chapter mark nearest each even split, or the split itself.
        // Length is the program before its credits.
        IEnumerable<double> Cuts(Media program, string network, double length)
        {
            var count = BreakCount(program);
            // Only split a program for breaks that will have commercials suited to it.
            if (count == 0 || adPicker.For(program, network) is null) return [];
            var marks = chapters?.Invoke(program) ?? [];
            var cuts = new List<double>();
            for (var k = 1; k <= count; k++)
            {
                var target = length * k / (count + 1);
                var previous = cuts.Count > 0 ? cuts[^1] : 0;
                var mark = marks.Where(x => Math.Abs(x - target) <= length * 0.1 && x > previous + 120 && x < length - 120).OrderBy(x => Math.Abs(x - target)).Cast<double?>().FirstOrDefault();
                cuts.Add(mark ?? target);
            }
            return cuts;
        }
        // Breaks need commercials, and a program of at least ten minutes.
        int BreakCount(Media program) => channel.CommercialCount == 0 || commercials.Count == 0 || program.Seconds < 600 ? 0
            : program.Series.Length > 0 ? channel.EpisodeBreaks : channel.MovieBreaks;
        bool Allowed(Media item) => rules is null || !Restricted(item, at, at.AddSeconds(item.Seconds), rules, zone);
        bool AllowedProgram(Media item) => rules is null || !Restricted(item, at, at.AddSeconds(item.Seconds + BreakCount(item) * breakSeconds), rules, zone);
        // A series continues from the last episode this channel aired, whichever lineup or block aired it.
        Media Continue(string poolKey, Media item)
        {
            if (item.Series.Length == 0 || !state.Series.TryGetValue(item.Series, out var cursor)) return item;
            if (!episodes.TryGetValue(poolKey, out var bySeries))
                episodes[poolKey] = bySeries = pools[poolKey].Where(x => x.Series.Length > 0).OrderBy(x => x.Season).ThenBy(x => x.Episode).ThenBy(x => x.Id).ToLookup(x => x.Series);
            var series = bySeries[item.Series];
            return series.FirstOrDefault(x => x.Season > cursor.Season || x.Season == cursor.Season && x.Episode > cursor.Episode) ?? series.FirstOrDefault() ?? item;
        }
    }

    // Chooses commercials or bumpers for a program: the network first (the block or channel override,
    // then the program's tags, then its studios), then the decade within that network's clips.
    // Each combination keeps its own rotation, keyed by the prefix ("commercials" or "bumpers").
    private sealed class ClipPicker
    {
        private readonly ChannelDefinition channel;
        private readonly string prefix;
        private readonly List<Media> clips;
        private readonly NetworkIndex index;
        private readonly Dictionary<string, List<Media>> showClips = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<Media>> networks = new(StringComparer.Ordinal);
        private readonly List<Media> general = [];
        private readonly Dictionary<string, Dictionary<int, List<Media>>> eras = new(StringComparer.Ordinal);

        public ClipPicker(ChannelDefinition channel, string prefix, List<Media> clips, NetworkIndex index)
        {
            (this.channel, this.prefix, this.clips, this.index) = (channel, prefix, clips, index);
            foreach (var clip in clips)
            {
                // A clip made for a show ("Now back to SpongeBob") only plays with that show.
                var made = index.ShowsIn(clip);
                foreach (var show in made) Add(showClips, show, clip);
                if (made.Length > 0) continue;
                var found = channel.MatchCommercialNetwork ? index.For(clip) : [];
                if (found.Length == 0) general.Add(clip);
                foreach (var name in found) Add(networks, name, clip);
            }
            static void Add(Dictionary<string, List<Media>> groups, string key, Media clip)
            {
                if (!groups.TryGetValue(key, out var list)) groups[key] = list = [];
                list.Add(clip);
            }
        }

        public bool Empty => clips.Count == 0;
        public Pool Pool => prefix == "bumpers" ? channel.Bumpers : channel.Commercials;

        // The clips for a program, their rotation key, and why they were chosen (shown in the guide preview).
        public (string Key, List<Media> Clips, string Why)? For(Media program, string network)
        {
            string key, why;
            List<Media> chosen;
            if (NetworkIndex.ShowKey(program.Show) is { } show && showClips.TryGetValue(show, out var own))
                (key, chosen, why) = (prefix + "#" + show, own, $"made for {program.Show}");
            else if (Network() is { } match) (key, chosen, why) = match;
            else if (networks.Count == 0 && general.Count > 0) (key, chosen, why) = (prefix, general, channel.MatchCommercialNetwork ? "no network clips" : "network matching off");
            else if (general.Count > 0) (key, chosen, why) = (prefix + "@", general, "general: no clips for this program's network");
            // Every clip belongs to a network or a show, and none fits: better no clip than another network's.
            else return null;
            if (!eras.TryGetValue(key, out var decades))
                eras[key] = decades = chosen.Where(x => x.Year > 0).GroupBy(x => x.Year / 10 * 10).ToDictionary(g => g.Key, g => g.ToList());
            if (!channel.MatchCommercialEra || decades.Count == 0) return (key, chosen, why);
            if (program.Year <= 0) return (key, chosen, why + " · any year (program year unknown)");
            var decade = program.Year / 10 * 10;
            var nearest = decades.Keys.OrderBy(x => Math.Abs(x - decade)).ThenBy(x => x).First();
            return (key + ":" + nearest.ToString(System.Globalization.CultureInfo.InvariantCulture), decades[nearest],
                why + $" · {nearest}s" + (nearest == decade ? "" : $" (closest to {program.Year})"));

            (string, List<Media>, string)? Network()
            {
                if (networks.Count == 0) return null;
                foreach (var (source, name) in (program.Tags ?? []).Select(x => ("tag", x)).Concat((program.Studios ?? []).Select(x => ("studio", x)))
                    .Prepend(("channel or block setting", network)))
                    if (index.Resolve(name, networks.ContainsKey) is { } found)
                        return (prefix + "@" + found, networks[found], $"{index.Name(found)} ({source} “{name.Trim()}”)");
                return null;
            }
        }
    }

    // Networks recognised in commercial and bumper names even when no program on the server lists them.
    // Names that are also everyday words (BET, Lifetime, Showtime…) are left out; programs' own studios still add them.
    public static readonly string[] Networks =
    [
        "Nickelodeon", "Nick Jr", "Nick at Nite", "Nicktoons", "TeenNick", "Cartoon Network", "Adult Swim", "Toonami", "Boomerang",
        "Disney Channel", "Disney Junior", "Disney XD", "Toon Disney", "Jetix", "Fox Kids", "Kids WB", "4Kids", "Qubo", "PBS Kids", "Discovery Kids",
        "ABC", "NBC", "CBS", "FOX", "PBS", "The WB", "UPN", "CW", "MTV", "VH1", "Comedy Central", "TBS", "TNT", "USA Network", "FX",
        "AMC", "Syfy", "Sci Fi Channel", "HBO", "Cinemax", "Starz", "TLC", "Discovery Channel", "History Channel",
        "ESPN", "Food Network", "HGTV", "TV Land", "Fox Family", "ABC Family", "Freeform", "BBC", "CBBC", "CBeebies", "ITV", "Channel 4",
        "Teletoon", "YTV", "Family Channel",
    ];

    // Sorts commercials and bumpers by the networks and shows their folder and file names mention, from
    // known names plus the common networks above. The most specific network wins: "Fox Kids promo" is
    // Fox Kids, not also FOX. A clip naming no network is a general clip. Results are cached per clip.
    public sealed class NetworkIndex
    {
        // Nicknames and production companies that stand for a network, in clip names and in studios.
        private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
        {
            ["nick"] = "nickelodeon", ["cn"] = "cartoon network", ["williams street"] = "adult swim", ["cartoon network studios"] = "cartoon network",
            ["nickelodeon animation studio"] = "nickelodeon", ["nickelodeon productions"] = "nickelodeon", ["disney television animation"] = "disney channel",
        };
        private readonly string[] keys;
        private readonly string[] shows;
        private readonly Dictionary<Guid, (string[] Networks, string[] Shows)> cache = [];
        private readonly Dictionary<string, string> display = new(StringComparer.Ordinal);

        public NetworkIndex(IEnumerable<string?> names, IEnumerable<string?>? showTitles = null)
        {
            foreach (var name in Networks.Concat(names))
                if (NetworkKey(name) is { Length: > 0 } key && !Aliases.ContainsKey(key)) display.TryAdd(key, name!.Trim());
            keys = display.Keys.Concat(Aliases.Keys).Distinct(StringComparer.Ordinal).ToArray();
            shows = (showTitles ?? []).Select(ShowKey).OfType<string>().Where(x => !keys.Contains(x)).Distinct(StringComparer.Ordinal).ToArray();
        }

        // Display name for a key, such as "Adult Swim" for "adult swim".
        public string Name(string key) => display.GetValueOrDefault(key, key);

        public string[] For(Media clip) => Read(clip).Networks;

        // Shows the clip is made for, such as "spongebob squarepants" for "SpongeBob bumper 2001.mp4".
        public string[] ShowsIn(Media clip) => Read(clip).Shows;

        private (string[] Networks, string[] Shows) Read(Media clip)
        {
            lock (cache)
            {
                if (cache.TryGetValue(clip.Id, out var known)) return known;
                var segments = Segments(clip.Path);
                var found = keys.Where(x => Mentions(segments, x)).ToArray();
                var networks = found.Where(x => !found.Any(y => y != x && (" " + y + " ").Contains(" " + x + " ", StringComparison.Ordinal)))
                    .Select(x => Aliases.GetValueOrDefault(x, x)).Distinct(StringComparer.Ordinal).ToArray();
                return cache[clip.Id] = (networks, shows.Where(x => Mentions(segments, x)).ToArray());
            }
        }

        // The network a program's studio, tag or override stands for: the name itself, a nickname, or a
        // network inside a longer name ("Nickelodeon Animation Studio" is Nickelodeon); the longest match
        // wins. Only networks that have clips count.
        public string? Resolve(string? name, Func<string, bool> hasClips)
        {
            var key = NetworkKey(name);
            if (key.Length == 0) return null;
            foreach (var candidate in keys.Where(x => (" " + key + " ").Contains(" " + x + " ", StringComparison.Ordinal)).OrderByDescending(x => x == key).ThenByDescending(x => x.Length).Prepend(key))
                if (Aliases.GetValueOrDefault(candidate, candidate) is var network && hasClips(network)) return network;
            return null;
        }

        // A show or movie title usable in clip names: without a leading "The", and at least five letters
        // long, so short titles like "Up" never match ordinary words.
        public static string? ShowKey(string? title)
        {
            var key = NetworkKey(title);
            if (key.StartsWith("the ", StringComparison.Ordinal)) key = key[4..];
            return key.Replace(" ", "").Length >= 5 ? key : null;
        }
    }

    // "Adult Swim", "adult_swim" and "AdultSwim" all become "adult swim" or match it compactly.
    public static string NetworkKey(string? name) => string.Join(' ', System.Text.RegularExpressions.Regex.Split((name ?? "").ToLowerInvariant(), "[^a-z0-9]+").Where(x => x.Length > 0));

    // True when a folder or file name in the path names the network, as whole words ("Adult Swim 2004")
    // or run together ("AdultSwimBumps"); run-together matches need six letters, so "ABC" never matches "abcd".
    public static bool FiledUnder(string path, string network) => Mentions(Segments(path), network);

    // Each folder and file name as space-padded words and as one run-together word.
    private static (string Words, string Compact)[] Segments(string path)
    {
        var parts = path.Split('/', '\\');
        parts[^1] = Path.GetFileNameWithoutExtension(parts[^1]);
        return parts.Select(NetworkKey).Where(x => x.Length > 0).Select(x => (" " + x + " ", x.Replace(" ", ""))).ToArray();
    }

    private static bool Mentions((string Words, string Compact)[] segments, string network)
    {
        var compactNetwork = network.Replace(" ", "");
        foreach (var (words, compact) in segments)
        {
            if (words.Contains(" " + network + " ", StringComparison.Ordinal)) return true;
            if (compactNetwork.Length >= 6 && compact.Contains(compactNetwork, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    // True when the item would be on the air during a rule's window and matches its genres or tags.
    public static bool Restricted(Media item, DateTimeOffset start, DateTimeOffset end, IReadOnlyList<ContentRule> rules, TimeZoneInfo zone)
    {
        foreach (var rule in rules)
        {
            var matches = (item.Genres ?? []).Intersect(rule.Genres, StringComparer.OrdinalIgnoreCase).Any()
                || (item.Tags ?? []).Intersect(rule.Tags, StringComparer.OrdinalIgnoreCase).Any();
            if (!matches) continue;
            var from = TimeOnly.ParseExact(rule.Start, "HH:mm");
            var to = TimeOnly.ParseExact(rule.End, "HH:mm");
            var first = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(start, zone).DateTime).AddDays(-1);
            var last = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(end, zone).DateTime);
            for (var day = first; day <= last; day = day.AddDays(1))
            {
                if (!rule.Days.Contains(day.DayOfWeek)) continue;
                var windowStart = Local(day, from);
                var windowEnd = Local(from < to ? day : day.AddDays(1), to);
                if (windowStart < end && start < windowEnd) return true;
            }
        }
        return false;
        DateTimeOffset Local(DateOnly day, TimeOnly time)
        {
            var local = day.ToDateTime(time, DateTimeKind.Unspecified);
            // A time skipped by a daylight-saving change starts at the first valid time after it.
            while (zone.IsInvalidTime(local)) local = local.AddMinutes(30);
            return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero);
        }
    }
}
