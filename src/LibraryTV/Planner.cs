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
        // The commercial break after the current program belongs to it, so keep it too.
        foreach (var ad in state.Guide.Where(x => x.Start >= boundary).OrderBy(x => x.Start))
        {
            if (!ad.Commercial || ad.Start != boundary) break;
            boundary = ad.End;
        }
        var removed = state.Guide.Where(x => x.Start >= boundary && x.Start > now).ToArray();
        foreach (var item in removed.Reverse())
        {
            var undo = item.Undo ?? throw new InvalidOperationException("Cannot rewind a legacy program.");
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
        DateTimeOffset? startAt = null, IReadOnlyList<ContentRule>? rules = null)
    {
        var at = state.Guide.Count > 0 ? state.Guide[^1].End : now;
        if (startAt > at) at = startAt.Value;
        var cutoff = now.AddHours(-6);
        state.Guide.RemoveAll(x => x.End < cutoff);
        // Preserve continuity through server downtime, with bounded catch-up work.
        if (at < now.AddDays(-7)) at = now;
        var episodes = new Dictionary<string, ILookup<string, Media>>();
        var commercials = pools["commercials"];
        // Network names this channel can ask for: its overrides, and its programs' tags and studios.
        var networks = new Dictionary<string, List<Media>>(StringComparer.Ordinal);
        if (channel.MatchCommercialNetwork && commercials.Count > 0)
            foreach (var name in channel.Blocks.Select(x => x.CommercialNetwork).Append(channel.CommercialNetwork)
                .Concat(pools.Where(x => x.Key != "commercials").SelectMany(x => x.Value).SelectMany(x => (x.Studios ?? []).Concat(x.Tags ?? [])))
                .Select(NetworkKey).Where(x => x.Length > 0).Distinct(StringComparer.Ordinal))
            {
                var filed = commercials.Where(x => FiledUnder(x.Path, name)).ToList();
                if (filed.Count > 0) networks[name] = filed;
            }
        var networkClips = networks.Values.SelectMany(x => x).Select(x => x.Id).ToHashSet();
        var generic = commercials.Where(x => !networkClips.Contains(x.Id)).ToList();
        var eras = new Dictionary<string, Dictionary<int, List<Media>>>(StringComparer.Ordinal);
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
            var next = Next(pools[key], pool, rotation, channel.Id + key, out var undo, x => Continue(key, x), Allowed);
            if (next is null && key != "default")
            {
                key = "default";
                network = channel.CommercialNetwork;
                if (!state.Rotations.TryGetValue(key, out rotation)) state.Rotations[key] = rotation = new();
                next = Next(pools[key], channel.Source, rotation, channel.Id + key, out undo, x => Continue(key, x), Allowed);
            }
            if (next is null) { at = at.AddMinutes(1); continue; }
            SeriesCursor? previousCursor = null;
            if (next.Series.Length > 0)
            {
                previousCursor = state.Series.GetValueOrDefault(next.Series);
                state.Series[next.Series] = new SeriesCursor(next.Season, next.Episode);
            }
            Add(next, false, undo! with { PoolKey = key, QuotaKey = key == "default" ? null : countKey, Series = next.Series.Length > 0 ? next.Series : null, PreviousCursor = previousCursor });
            if (key != "default" && countKey is not null) state.DailyCounts[countKey] = state.DailyCounts.GetValueOrDefault(countKey) + 1;
            var (adKey, adPool) = CommercialsFor(next, network);
            if (!state.Rotations.TryGetValue(adKey, out var ads)) state.Rotations[adKey] = ads = new();
            for (var i = 0; i < channel.CommercialCount; i++)
            {
                if (state.Guide.Count - stale >= MaxGuideEntries) break;
                var ad = Next(adPool, channel.Commercials, ads, channel.Id + (adKey == "commercials" ? "ads" : adKey), out var adUndo, null, Allowed);
                if (ad is null) break;
                Add(ad, true, adUndo! with { PoolKey = adKey });
            }
        }
        if (stale > 0) state.Guide.RemoveAll(x => x.End < cutoff);
        // Daily quota entries only need to survive the current planning horizon.
        var keep = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now.AddDays(-1), zone).DateTime).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        foreach (var key in state.DailyCounts.Keys.Where(k => string.CompareOrdinal(k[..10], keep) < 0).ToArray()) state.DailyCounts.Remove(key);
        void Add(Media item, bool commercial, PlanningUndo undo)
        {
            var end = at.AddSeconds(item.Seconds);
            state.Guide.Add(new(channel.Id, item.Id, item.Title, item.Path, at, end, commercial, undo));
            if (end < cutoff) stale++;
            at = end;
        }
        bool Allowed(Media item) => rules is null || !Restricted(item, at, at.AddSeconds(item.Seconds), rules, zone);
        // A series continues from the last episode this channel aired, whichever lineup or block aired it.
        Media Continue(string poolKey, Media item)
        {
            if (item.Series.Length == 0 || !state.Series.TryGetValue(item.Series, out var cursor)) return item;
            if (!episodes.TryGetValue(poolKey, out var bySeries))
                episodes[poolKey] = bySeries = pools[poolKey].Where(x => x.Series.Length > 0).OrderBy(x => x.Season).ThenBy(x => x.Episode).ThenBy(x => x.Id).ToLookup(x => x.Series);
            var series = bySeries[item.Series];
            return series.FirstOrDefault(x => x.Season > cursor.Season || x.Season == cursor.Season && x.Episode > cursor.Episode) ?? series.FirstOrDefault() ?? item;
        }
        // Pick the network first (the block or channel override, then the program's tags, then its studios),
        // then the decade within that network's clips. Each combination keeps its own rotation.
        (string, List<Media>) CommercialsFor(Media program, string network)
        {
            var key = "commercials";
            var clips = commercials;
            if (networks.Count > 0)
            {
                var wanted = (program.Tags ?? []).Concat(program.Studios ?? []).Prepend(network).Select(NetworkKey).FirstOrDefault(networks.ContainsKey);
                // Without a matching network, use clips not filed under any network, so a kids show never gets another network's ads.
                if (wanted is not null) (key, clips) = ("commercials@" + wanted, networks[wanted]);
                else if (generic.Count > 0) (key, clips) = ("commercials@", generic);
            }
            if (!eras.TryGetValue(key, out var decades))
                eras[key] = decades = clips.Where(x => x.Year > 0).GroupBy(x => x.Year / 10 * 10).ToDictionary(g => g.Key, g => g.ToList());
            if (!channel.MatchCommercialEra || program.Year <= 0 || decades.Count == 0) return (key, clips);
            var decade = program.Year / 10 * 10;
            var nearest = decades.Keys.OrderBy(x => Math.Abs(x - decade)).ThenBy(x => x).First();
            return (key + ":" + nearest.ToString(System.Globalization.CultureInfo.InvariantCulture), decades[nearest]);
        }
    }

    // "Adult Swim", "adult_swim" and "AdultSwim" all become "adult swim" or match it compactly.
    private static string NetworkKey(string? name) => string.Join(' ', System.Text.RegularExpressions.Regex.Split((name ?? "").ToLowerInvariant(), "[^a-z0-9]+").Where(x => x.Length > 0));

    // True when a folder or file name in the path names the network, as whole words ("Adult Swim 2004")
    // or run together ("AdultSwimBumps"); run-together matches need six letters, so "ABC" never matches "abcd".
    public static bool FiledUnder(string path, string network)
    {
        var compactNetwork = network.Replace(" ", "");
        var parts = path.Split('/', '\\');
        parts[^1] = Path.GetFileNameWithoutExtension(parts[^1]);
        foreach (var part in parts)
        {
            var words = NetworkKey(part);
            if ((" " + words + " ").Contains(" " + network + " ", StringComparison.Ordinal)) return true;
            if (compactNetwork.Length >= 6 && words.Replace(" ", "").Contains(compactNetwork, StringComparison.Ordinal)) return true;
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
