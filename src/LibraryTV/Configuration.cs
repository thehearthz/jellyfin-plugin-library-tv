using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.LibraryTV;

public sealed class Configuration : BasePluginConfiguration
{
    public bool AutoChannels { get; set; }
    public string[] AutoLibraryIds { get; set; } = [];
    public int AutoChannelStartNumber { get; set; } = 100;
    public AutomaticChannel[] AutomaticChannels { get; set; } = [];
    public int GuideDays { get; set; } = 3;
    public int RefreshMinutes { get; set; } = 60;
    public int MaxActiveChannels { get; set; } = 2;
    public string TimeZoneId { get; set; } = "UTC";
    public string InternalBaseUrl { get; set; } = "http://127.0.0.1:8096";
    public string StreamToken { get; set; } = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
    public ChannelDefinition[] Channels { get; set; } = [];
    // Applies to every channel: matching genres or tags never air during these windows.
    public ContentRule[] Restrictions { get; set; } = [];
    // The shared commercial and bumper libraries, used by every channel that has no clips of its own.
    // Each refresh sorts them by the year, decade and network in their folder and file names.
    public Pool SharedCommercials { get; set; } = new();
    public Pool SharedBumpers { get; set; } = new();
}

public sealed class ContentRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Start { get; set; } = "06:00";
    public string End { get; set; } = "21:00";
    public DayOfWeek[] Days { get; set; } = Enum.GetValues<DayOfWeek>();
    public string[] Genres { get; set; } = ["Horror"];
    public string[] Tags { get; set; } = [];
}

public sealed class AutomaticChannel
{
    public string LibraryId { get; set; } = "";
    public string Number { get; set; } = "";
    public string Name { get; set; } = "";
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
    // Commercial breaks inside a program, placed at chapter marks when there are any (needs CommercialCount > 0).
    public int EpisodeBreaks { get; set; } = 1;
    public int MovieBreaks { get; set; } = 2;
    // Short clips played going into and coming out of each break, or between programs without commercials.
    public Pool Bumpers { get; set; } = new();
    // Prefer commercials from the same decade as the program they follow.
    public bool MatchCommercialEra { get; set; } = true;
    // Prefer commercials filed under the network the program aired on (its studios or tags).
    public bool MatchCommercialNetwork { get; set; } = true;
    // Optional network for every break on this channel, such as "Toonami"; a block can override it.
    public string CommercialNetwork { get; set; } = "";
    // Normalizes every program to 720p H.264/AAC. Otherwise only incompatible streams are encoded.
    public bool AlwaysEncode { get; set; }
    // Every program is delivered in this picture shape ("16:9", "4:3" or "off"); other shapes get black bars.
    // "restart" plays programs in their own shape and restarts viewers' players when the shape changes.
    public string PictureShape { get; set; } = "16:9";
    // Captions burned into the picture for everyone watching: "off", "forced" (forced tracks only, such as
    // foreign-language dialogue) or "on" (when the program has a caption track). Language may be a code
    // ("en", "eng") or a name ("English"); blank accepts any language.
    public string Captions { get; set; } = "off";
    public string CaptionLanguage { get; set; } = "";
    // The audio track to play when a program has several, by language name or code; blank uses the
    // file's default track. Programs without that language play their default track.
    public string AudioLanguage { get; set; } = "";

    internal ChannelDefinition WithClips(Pool commercials, Pool bumpers)
    {
        var copy = (ChannelDefinition)MemberwiseClone();
        (copy.Commercials, copy.Bumpers) = (commercials, bumpers);
        return copy;
    }
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
    // Episodes of one series aired before moving to the next; 0 airs each series straight through.
    public int EpisodesInARow { get; set; } = 2;
}

public sealed class TimeBlock
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Start { get; set; } = "18:00";
    public string End { get; set; } = "21:00";
    public DayOfWeek[] Days { get; set; } = Enum.GetValues<DayOfWeek>();
    public int MaxItemsPerDay { get; set; }
    public Pool Source { get; set; } = new();
    public string CommercialNetwork { get; set; } = "";
}

public sealed record Media(Guid Id, string Title, string Path, double Seconds, string Series, int Season, int Episode, int Year = 0, string[]? Genres = null, string[]? Tags = null, string[]? Studios = null, string? Show = null);
// Offset is where in the file this entry starts: the later parts of a program split by a commercial break.
// Match says why a commercial or bumper was chosen, for the guide preview.
public sealed record Airing(string ChannelId, Guid ItemId, string Title, string Path, DateTimeOffset Start, DateTimeOffset End, bool Commercial, PlanningUndo? Undo = null, double Offset = 0, string? Match = null);
// Index is where the rotation entry was taken from (SeenIndex when an allowed item was repeated early);
// Token is that entry when a series cursor aired a different episode. Part marks the later parts of a
// program split by a commercial break: the first part holds the selection, so they have nothing to rewind.
public sealed record PlanningUndo(string PoolKey, Guid PreviousLast, Guid[] AddedIds, bool Swapped, Guid[]? PreviousSeen, string? QuotaKey = null,
    int Index = 0, Guid? Token = null, string? Series = null, SeriesCursor? PreviousCursor = null, int? SeenIndex = null, bool Part = false);
public sealed record SeriesCursor(int Season, int Episode);
public sealed class Rotation
{
    public List<Guid> Remaining { get; set; } = [];
    public List<Guid> Seen { get; set; } = [];
    public Guid Last { get; set; }
    public int EpisodesInARow { get; set; }
}
public sealed class ChannelState
{
    public List<Airing> Guide { get; set; } = [];
    public Dictionary<string, Rotation> Rotations { get; set; } = [];
    public Dictionary<string, int> DailyCounts { get; set; } = [];
    // Last episode aired per series on this channel, shared by every lineup and block.
    public Dictionary<string, SeriesCursor> Series { get; set; } = [];
}
