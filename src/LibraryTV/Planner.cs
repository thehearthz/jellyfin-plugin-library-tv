using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.LibraryTV;

// Pure scheduling logic: no timers, media processes or library queries.
public static class Planner
{
    public const int MaxGuideEntries = 10000;
    public static Media? Next(List<Media> media, Pool pool, Rotation rotation, string seed)
        => Next(media, pool, rotation, seed, out _);

    private static Media? Next(List<Media> media, Pool pool, Rotation rotation, string seed, out PlanningUndo? undo)
    {
        undo = null;
        if (media.Count == 0) return null;
        var previousLast = rotation.Last;
        Guid[]? previousSeen = null;
        var swapped = false;
        var byId = media.ToDictionary(x => x.Id);
        rotation.Remaining.RemoveAll(id => !byId.ContainsKey(id));
        var known = rotation.Remaining.Concat(rotation.Seen).ToHashSet();
        var added = media.Where(x => !known.Contains(x.Id)).ToList();
        if (rotation.Remaining.Count == 0 && added.Count == 0 && pool.RepeatWhenExhausted)
        {
            previousSeen = rotation.Seen.ToArray();
            rotation.Seen.Clear();
            added = media.ToList();
        }
        // Order each series by season/episode; shuffle series, never their episodes.
        var groups = added.GroupBy(x => x.Series.Length == 0 ? x.Id.ToString() : x.Series);
        if (pool.Shuffle) groups = groups.OrderBy(g => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed + rotation.Last + g.Key))));
        foreach (var group in groups) rotation.Remaining.AddRange(group.OrderBy(x => x.Season).ThenBy(x => x.Episode).ThenBy(x => x.Id).Select(x => x.Id));
        if (rotation.Remaining.Count == 0) return null;
        if (rotation.Remaining[0] == rotation.Last && rotation.Remaining.Count > 1 && byId[rotation.Last].Series.Length == 0)
        {
            (rotation.Remaining[0], rotation.Remaining[1]) = (rotation.Remaining[1], rotation.Remaining[0]);
            swapped = true;
        }
        var next = rotation.Remaining[0];
        rotation.Remaining.RemoveAt(0);
        rotation.Seen.Add(next);
        rotation.Last = next;
        undo = new PlanningUndo("", previousLast, added.Select(x => x.Id).ToArray(), swapped, previousSeen);
        return byId[next];
    }

    // Rewind only future selections. Legacy guide entries have no undo metadata,
    // so keep them fixed and safely rebuild the reversible tail after them.
    public static DateTimeOffset RewindFuture(ChannelState state, DateTimeOffset now)
    {
        var boundary = state.Guide.Where(x => x.Start <= now && x.End > now).Select(x => x.End).DefaultIfEmpty(now).Max();
        boundary = state.Guide.Where(x => x.Start > now && x.Undo is null).Select(x => x.End).Append(boundary).Max();
        var removed = state.Guide.Where(x => x.Start >= boundary && x.Start > now).ToArray();
        foreach (var item in removed.Reverse())
        {
            var undo = item.Undo ?? throw new InvalidOperationException("Cannot rewind a legacy program.");
            var rotation = state.Rotations[undo.PoolKey];
            rotation.Remaining.Insert(0, item.ItemId);
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
            if (undo.QuotaKey is { } quota)
            {
                var count = state.DailyCounts.GetValueOrDefault(quota);
                if (count <= 1) state.DailyCounts.Remove(quota); else state.DailyCounts[quota] = count - 1;
            }
        }
        state.Guide.RemoveAll(x => x.Start >= boundary && x.Start > now);
        return boundary;
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

    public static void Extend(ChannelDefinition channel, ChannelState state, IReadOnlyDictionary<string, List<Media>> pools, DateTimeOffset now, DateTimeOffset until, TimeZoneInfo zone, DateTimeOffset? startAt = null)
    {
        var at = state.Guide.Count > 0 ? state.Guide[^1].End : now;
        if (startAt > at) at = startAt.Value;
        state.Guide.RemoveAll(x => x.End < now.AddHours(-6));
        // Preserve continuity through server downtime, with bounded catch-up work.
        if (at < now.AddDays(-7)) at = now;
        var iterations = 0;
        while (at < until && state.Guide.Count < MaxGuideEntries && iterations++ < 100000)
        {
            var active = ActiveBlock(channel, at, zone);
            var key = "default";
            var pool = channel.Source;
            string? countKey = null;
            if (active is { } match)
            {
                countKey = match.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + ":" + match.Block.Id;
                state.DailyCounts.TryGetValue(countKey, out var count);
                if (match.Block.MaxItemsPerDay == 0 || count < match.Block.MaxItemsPerDay) { key = match.Block.Id; pool = match.Block.Source; }
            }
            if (!state.Rotations.TryGetValue(key, out var rotation)) state.Rotations[key] = rotation = new();
            var next = Next(pools[key], pool, rotation, channel.Id + key, out var undo);
            if (next is null && key != "default")
            {
                key = "default";
                if (!state.Rotations.TryGetValue(key, out rotation)) state.Rotations[key] = rotation = new();
                next = Next(pools[key], channel.Source, rotation, channel.Id + key, out undo);
            }
            if (next is null) { at = at.AddMinutes(1); continue; }
            Add(next, false, undo! with { PoolKey = key, QuotaKey = key == "default" ? null : countKey });
            if (key != "default" && countKey is not null) state.DailyCounts[countKey] = state.DailyCounts.GetValueOrDefault(countKey) + 1;
            if (!state.Rotations.TryGetValue("commercials", out var ads)) state.Rotations["commercials"] = ads = new();
            for (var i = 0; i < channel.CommercialCount; i++)
            {
                if (state.Guide.Count >= MaxGuideEntries) break;
                var ad = Next(pools["commercials"], channel.Commercials, ads, channel.Id + "ads", out var adUndo);
                if (ad is null) break;
                Add(ad, true, adUndo! with { PoolKey = "commercials" });
            }
        }
        // Daily quota entries only need to survive the current planning horizon.
        var keep = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now.AddDays(-1), zone).DateTime).ToString("yyyy-MM-dd");
        foreach (var key in state.DailyCounts.Keys.Where(k => string.CompareOrdinal(k[..10], keep) < 0).ToArray()) state.DailyCounts.Remove(key);
        void Add(Media item, bool commercial, PlanningUndo undo)
        {
            var end = at.AddSeconds(item.Seconds);
            state.Guide.Add(new(channel.Id, item.Id, item.Title, item.Path, at, end, commercial, undo));
            at = end;
        }
    }
}
