using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace Jellyfin.Plugin.LibraryTV;

// The channel list (M3U) and guide (XMLTV) for other apps, such as IPTV players. Every link carries the
// export token, because these apps cannot sign in to Jellyfin.
public static class Export
{
    public static bool Allowed(Configuration config, string? token) => config.ExportEnabled && token is not null && config.ExportToken.Length >= 32
        && CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(token)), SHA256.HashData(Encoding.UTF8.GetBytes(config.ExportToken)));

    public static string StreamUrl(string baseUrl, string id, string token) => $"{baseUrl.TrimEnd('/')}/LibraryTV/live/{Uri.EscapeDataString(id)}.ts?token={Uri.EscapeDataString(token)}";

    public static string M3u(IEnumerable<ChannelDefinition> channels, string baseUrl, string token)
    {
        var text = new StringBuilder();
        text.Append("#EXTM3U url-tvg=\"").Append(baseUrl.TrimEnd('/')).Append("/LibraryTV/epg.xml?token=").Append(Uri.EscapeDataString(token)).Append("\"\n");
        foreach (var channel in channels)
        {
            // Quotes and line breaks would end the attribute or the line.
            var name = Clean(channel.Name);
            text.Append("#EXTINF:-1 tvg-id=\"").Append(Clean(channel.Id)).Append("\" tvg-chno=\"").Append(Clean(channel.Number)).Append("\" tvg-name=\"").Append(name)
                .Append("\" group-title=\"Library TV\",").Append(name.Replace(",", " ")).Append('\n')
                .Append(StreamUrl(baseUrl, channel.Id, token)).Append('\n');
        }
        return text.ToString();
        static string Clean(string value) => value.Replace("\"", "'").Replace("\r", " ").Replace("\n", " ").Trim();
    }

    // Programs still on or coming up, listed the way Jellyfin's own guide lists them, with what the library
    // knows about each: episode, genres, rating, year and a picture from Jellyfin.
    public static string Xmltv(IEnumerable<ChannelDefinition> channels, Func<string, IReadOnlyList<Airing>> guide, DateTimeOffset now, Func<Guid, ProgramDetails?>? details = null, string? baseUrl = null)
    {
        var tv = new XElement("tv", new XAttribute("generator-info-name", "Library TV"));
        var all = channels.ToArray();
        var known = new Dictionary<Guid, ProgramDetails?>();
        foreach (var channel in all)
            tv.Add(new XElement("channel", new XAttribute("id", channel.Id), new XElement("display-name", channel.Name), new XElement("display-name", channel.Number)));
        foreach (var channel in all)
            foreach (var entry in LiveTvService.Listings(guide(channel.Id), channel.GuideHidesBreaks).Where(x => x.End > now))
            {
                var about = entry.Commercial || details is null ? null : known.TryGetValue(entry.ItemId, out var found) ? found : known[entry.ItemId] = details(entry.ItemId);
                var info = LiveTvService.Program(entry, about);
                var program = new XElement("programme", new XAttribute("start", Time(entry.Start)), new XAttribute("stop", Time(entry.End)), new XAttribute("channel", channel.Id),
                    new XElement("title", info.Name));
                if (info.EpisodeTitle is { Length: > 0 } episode) program.Add(new XElement("sub-title", episode));
                program.Add(new XElement("desc", info.Overview));
                if (info.ProductionYear is { } year) program.Add(new XElement("date", year.ToString(CultureInfo.InvariantCulture)));
                foreach (var genre in info.Genres ?? []) program.Add(new XElement("category", genre));
                if (info.IsMovie) program.Add(new XElement("category", "Movie"));
                if (about?.Image is not null && baseUrl is not null) program.Add(new XElement("icon", new XAttribute("src", $"{baseUrl.TrimEnd('/')}/Items/{entry.ItemId:N}/Images/Primary")));
                if (info.SeasonNumber is > 0 || info.EpisodeNumber is > 0)
                    program.Add(new XElement("episode-num", new XAttribute("system", "xmltv_ns"), $"{(info.SeasonNumber is > 0 ? (info.SeasonNumber - 1)!.Value.ToString(CultureInfo.InvariantCulture) : "")}.{(info.EpisodeNumber is > 0 ? (info.EpisodeNumber - 1)!.Value.ToString(CultureInfo.InvariantCulture) : "")}."),
                        new XElement("episode-num", new XAttribute("system", "onscreen"), $"S{info.SeasonNumber ?? 0:00}E{info.EpisodeNumber ?? 0:00}"));
                if (info.OfficialRating is { Length: > 0 } rating) program.Add(new XElement("rating", new XElement("value", rating)));
                tv.Add(program);
            }
        return new XDocument(new XDeclaration("1.0", "utf-8", null), tv).ToString(SaveOptions.DisableFormatting);
        static string Time(DateTimeOffset at) => at.UtcDateTime.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + " +0000";
    }
}
