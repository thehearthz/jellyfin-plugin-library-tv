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

    // Programs still on or coming up, listed the way Jellyfin's own guide lists them.
    public static string Xmltv(IEnumerable<ChannelDefinition> channels, Func<string, IReadOnlyList<Airing>> guide, DateTimeOffset now)
    {
        var tv = new XElement("tv", new XAttribute("generator-info-name", "Library TV"));
        var all = channels.ToArray();
        foreach (var channel in all)
            tv.Add(new XElement("channel", new XAttribute("id", channel.Id), new XElement("display-name", channel.Name), new XElement("display-name", channel.Number)));
        foreach (var channel in all)
            foreach (var program in LiveTvService.Listings(guide(channel.Id), channel.GuideHidesBreaks).Where(x => x.End > now))
                tv.Add(new XElement("programme", new XAttribute("start", Time(program.Start)), new XAttribute("stop", Time(program.End)), new XAttribute("channel", channel.Id),
                    new XElement("title", program.Heading ?? program.Title),
                    new XElement("desc", program.Commercial ? "Commercial break" : program.Heading is null ? "From your Jellyfin library" : program.Title)));
        return new XDocument(new XDeclaration("1.0", "utf-8", null), tv).ToString(SaveOptions.DisableFormatting);
        static string Time(DateTimeOffset at) => at.UtcDateTime.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + " +0000";
    }
}
