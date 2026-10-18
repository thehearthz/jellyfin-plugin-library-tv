using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.LibraryTV;

public sealed class Configuration : BasePluginConfiguration
{
    public bool AutoChannels { get; set; }
    public string[] AutoLibraryIds { get; set; } = [];
    public int GuideDays { get; set; } = 3;
    public int RefreshMinutes { get; set; } = 60;
    public int MaxActiveChannels { get; set; } = 2;
    public string TimeZoneId { get; set; } = "UTC";
    public string InternalBaseUrl { get; set; } = "http://127.0.0.1:8096";
    public string StreamToken { get; set; } = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
    public ChannelDefinition[] Channels { get; set; } = [];
}

public sealed class ChannelDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New channel";
    public string Number { get; set; } = "1";
    public bool Enabled { get; set; } = true;
    public Pool Source { get; set; } = new();
    public TimeBlock[] Blocks { get; set; } = [];
    public Pool Commercials { get; set; } = new();
    public int CommercialCount { get; set; }
    public bool Transcode { get; set; }
}

public sealed class Pool
{
    public string[] LibraryIds { get; set; } = [];
    public string[] ItemIds { get; set; } = [];
    public string[] Genres { get; set; } = [];
    public string[] Tags { get; set; } = [];
    public string[] FolderIds { get; set; } = [];
    public bool Shuffle { get; set; } = true;
    public bool IncludeSpecials { get; set; }
    public bool RepeatWhenExhausted { get; set; } = true;
}

public sealed class TimeBlock
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Start { get; set; } = "18:00";
    public string End { get; set; } = "21:00";
    public DayOfWeek[] Days { get; set; } = Enum.GetValues<DayOfWeek>();
    public int MaxItemsPerDay { get; set; }
    public Pool Source { get; set; } = new();
}

public sealed record Media(Guid Id, string Title, string Path, double Seconds, string Series, int Season, int Episode);
public sealed record Airing(string ChannelId, Guid ItemId, string Title, string Path, DateTimeOffset Start, DateTimeOffset End, bool Commercial);
public sealed class Rotation
{
    public List<Guid> Remaining { get; set; } = [];
    public List<Guid> Seen { get; set; } = [];
    public Guid Last { get; set; }
}
public sealed class ChannelState
{
    public List<Airing> Guide { get; set; } = [];
    public Dictionary<string, Rotation> Rotations { get; set; } = [];
    public Dictionary<string, int> DailyCounts { get; set; } = [];
}
