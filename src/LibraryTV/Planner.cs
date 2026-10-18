using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.LibraryTV;

// Pure scheduling logic: no timers, media processes or library queries.
public static class Planner
{
    public static Media? Next(List<Media> media, Pool pool, Rotation rotation, string seed)
    {
        var byId = media.ToDictionary(x => x.Id);
        rotation.Remaining.RemoveAll(id => !byId.ContainsKey(id));
        var known = rotation.Remaining.Concat(rotation.Seen).ToHashSet();
        var added = media.Where(x => !known.Contains(x.Id)).ToList();
        if (rotation.Remaining.Count == 0 && added.Count == 0 && pool.RepeatWhenExhausted)
        {
            rotation.Seen.Clear();
            added = media.ToList();
        }
        // Order each series by season/episode; shuffle series, never their episodes.
        var groups = added.GroupBy(x => x.Series.Length == 0 ? x.Id.ToString() : x.Series);
        if (pool.Shuffle) groups = groups.OrderBy(g => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed + rotation.Last + g.Key))));
        foreach (var group in groups) rotation.Remaining.AddRange(group.OrderBy(x => x.Season).ThenBy(x => x.Episode).ThenBy(x => x.Id).Select(x => x.Id));
        if (rotation.Remaining.Count == 0) return null;
        if (rotation.Remaining[0] == rotation.Last && rotation.Remaining.Count > 1 && byId[rotation.Last].Series.Length == 0)
            (rotation.Remaining[0], rotation.Remaining[1]) = (rotation.Remaining[1], rotation.Remaining[0]);
        var next = rotation.Remaining[0];
        rotation.Remaining.RemoveAt(0);
        rotation.Seen.Add(next);
        rotation.Last = next;
        return byId[next];
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

    public static void Extend(ChannelDefinition channel, ChannelState state, IReadOnlyDictionary<string, List<Media>> pools, DateTimeOffset now, DateTimeOffset until, TimeZoneInfo zone)
    {
        var at = state.Guide.Count > 0 ? state.Guide[^1].End : now;
        state.Guide.RemoveAll(x => x.End < now.AddHours(-6));
        // Preserve continuity through server downtime, with bounded catch-up work.
        if (at < now.AddDays(-7)) at = now;
        var iterations = 0;
        while (at < until && iterations++ < 100000)
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
            var next = Next(pools[key], pool, rotation, channel.Id + key);
            if (next is null && key != "default")
            {
                key = "default";
                if (!state.Rotations.TryGetValue(key, out rotation)) state.Rotations[key] = rotation = new();
                next = Next(pools[key], channel.Source, rotation, channel.Id + key);
            }
            if (next is null) { at = at.AddMinutes(1); continue; }
            Add(next, false);
            if (key != "default" && countKey is not null) state.DailyCounts[countKey] = state.DailyCounts.GetValueOrDefault(countKey) + 1;
            if (!state.Rotations.TryGetValue("commercials", out var ads)) state.Rotations["commercials"] = ads = new();
            for (var i = 0; i < channel.CommercialCount; i++)
            {
                var ad = Next(pools["commercials"], channel.Commercials, ads, channel.Id + "ads");
                if (ad is null) break;
                Add(ad, true);
            }
        }
        // Daily quota entries only need to survive the current planning horizon.
        var keep = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now.AddDays(-1), zone).DateTime).ToString("yyyy-MM-dd");
        foreach (var key in state.DailyCounts.Keys.Where(k => string.CompareOrdinal(k[..10], keep) < 0).ToArray()) state.DailyCounts.Remove(key);
        void Add(Media item, bool commercial)
        {
            var end = at.AddSeconds(item.Seconds);
            state.Guide.Add(new(channel.Id, item.Id, item.Title, item.Path, at, end, commercial));
            at = end;
        }
    }
}
