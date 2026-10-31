using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.LibraryTV;

public sealed class Configuration : BasePluginConfiguration
{
    // Where programs that must be encoded are encoded: "cpu" (software, the default) or a graphics encoder:
    // "nvenc" (NVIDIA), "vaapi" (Intel/AMD on Linux) or "qsv" (Intel Quick Sync). If it does not work, the CPU is used.
    public string HardwareEncoder { get; set; } = "cpu";
    // The render device VAAPI uses.
    public string HardwareDevice { get; set; } = "/dev/dri/renderD128";
    public bool AutoChannels { get; set; }
    public string[] AutoLibraryIds { get; set; } = [];
    public int AutoChannelStartNumber { get; set; } = 100;
    public AutomaticChannel[] AutomaticChannels { get; set; } = [];
    // Channels built from what is in the library: any of some genres, years or decades, plus chosen shows and movies.
    public SmartChannel[] SmartChannels { get; set; } = [];
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
    // Commercials per break on automatic library channels, which use the shared commercials and bumpers.
    public int AutomaticCommercialCount { get; set; } = 2;
    // How much faster credits play on automatic and smart channels (1 = normal). Hand-built channels set their own.
    public int AutomaticCreditsSpeed { get; set; } = 1;
    // Reads every program's file in the background to find where its credits start, for programs without
    // a credits chapter or Outro segment.
    public bool DetectCredits { get; set; } = true;
    // Changes to how clips are filed, on top of what their folder and file names say: rules for every clip
    // whose path contains some text, and edits to single clips, which win over the rules.
    public ClipEdit[] ClipRules { get; set; } = [];
    public ClipEdit[] ClipOverrides { get; set; } = [];
}

public sealed class ClipEdit
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    // A single clip's item ID (edits), or text its path must contain (rules), such as "bump" or "/Adult Swim/2000s/".
    public string ItemId { get; set; } = "";
    public string Contains { get; set; } = "";
    // Blank leaves the clip as its name says. "General" files it under no network.
    public string Network { get; set; } = "";
    public int Year { get; set; }
    // A show the clip was made for, so it airs only with that show.
    public string Show { get; set; } = "";
    // "commercial" or "bumper"; blank keeps the kind of the list it was chosen in.
    public string Kind { get; set; } = "";
    public bool? Disabled { get; set; }
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

public sealed class SmartChannel
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New smart channel";
    // Blank receives the next free number when saved.
    public string Number { get; set; } = "";
    public bool Enabled { get; set; } = true;
    // The libraries searched for matches (a channel with none airs only its chosen shows and movies).
    public string[] LibraryIds { get; set; } = [];
    // A program matches when it has any of the genres and is from any of the years or decades. A criterion left empty matches everything.
    public string[] Genres { get; set; } = [];
    public int[] Years { get; set; } = [];
    public int[] Decades { get; set; } = [];
    // Shows and movies that always air, whether or not they match.
    public string[] ItemIds { get; set; } = [];
    public bool Shuffle { get; set; } = true;
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
    // Credits play this many times faster (1 = normal), with their music at normal speed until the next
    // program. Needs a credits marker: an Outro media segment, a chapter named like "Credits", or credits
    // detection.
    public int CreditsSpeed { get; set; } = 1;
    // How well programs that have to be encoded look: "fast" (smallest CPU load), "balanced", "high" or "best".
    // Programs that are copied are never touched.
    public string EncodeQuality { get; set; } = "balanced";
    // "" follows the server's hardware encoder setting; "cpu" always encodes this channel on the CPU.
    public string EncodeWith { get; set; } = "";
    // The tallest picture an encoded program is made in: 480, 576, 720 or 1080.
    public int EncodeHeight { get; set; } = 720;
    // Pads each program with commercials or bumpers until the next multiple of this many minutes on the
    // clock (for example 30 starts programs at :00 and :30); 0 starts each program as soon as the last ends.
    public int PadMinutes { get; set; }
    // Where breaks fall inside a program: "even" splits it into equal parts (at chapter marks when there are
    // any); "interval" breaks every BreakIntervalMinutes, up to MaxBreaksPerProgram.
    public string BreakMode { get; set; } = "even";
    public int BreakIntervalMinutes { get; set; } = 15;
    public int MaxBreaksPerProgram { get; set; } = 5;
    // Programs shorter than this are never split, and the last BreakFreeEndMinutes of a program have no breaks.
    public int MinBreakProgramMinutes { get; set; } = 10;
    public int BreakFreeEndMinutes { get; set; }
    // A break plays CommercialCount commercials, or a varying number up to this one when it is higher.
    public int CommercialCountMax { get; set; }
    // A preferred audio track by title (for example "Dolby" or "Original"); with several tracks left, the one
    // with the most channels wins when the file marks no default.
    public string AudioTitle { get; set; } = "";
    // A channel logo "bug" drawn over the picture of everything on the channel, which means encoding it. The
    // path is an image file (PNG, JPG or WebP) readable by Jellyfin.
    public string Watermark { get; set; } = "";
    // top-left, top-right, bottom-left or bottom-right.
    public string WatermarkPosition { get; set; } = "bottom-right";
    // Opacity 10–100 and the logo's width as a percentage of the picture's width, 3–50.
    public int WatermarkOpacity { get; set; } = 70;
    public int WatermarkSize { get; set; } = 12;
    // An image shown in the Live TV guide for the channel.
    public string Logo { get; set; } = "";
    // An image shown during gaps in the schedule instead of a black screen.
    public string OfflineCard { get; set; } = "";
    // The guide lists a program without its commercial breaks, so it appears to run through them.
    public bool GuideHidesBreaks { get; set; }

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
    // How the pool is ordered between cycles: blank follows Shuffle; "chronological" by air or release date;
    // "alphabetical" by title. Episodes within a show always keep their season and episode order.
    public string Order { get; set; } = "";
    // The first cycle starts at a random place instead of the beginning.
    public bool RandomStart { get; set; }
    // Two-part episodes ("Part 1", "(2)") are never split between turns.
    public bool KeepMultiPart { get; set; }
    // Episodes in a row for particular shows, instead of EpisodesInARow.
    public ShowTurn[] Turns { get; set; } = [];
    // Filters on what the pool offers; zero or blank means no limit.
    public int MinYear { get; set; }
    public int MaxYear { get; set; }
    public int MinMinutes { get; set; }
    public int MaxMinutes { get; set; }
    // The highest content rating that may air (for example "TV-PG"); unrated programs are kept.
    public string MaxRating { get; set; } = "";
    public string[] ExcludeGenres { get; set; } = [];
    // Words that a show or movie title must contain (any of them) and must not contain, ignoring case.
    public string[] TitleContains { get; set; } = [];
    public string[] TitleExcludes { get; set; } = [];
    // Programs from any of these years or decades (a decade is its first year, such as 1990). Programs with no known year are left out.
    public int[] Years { get; set; } = [];
    public int[] Decades { get; set; } = [];
    // Shows and movies that always air, whatever the filters say.
    public string[] AlwaysItemIds { get; set; } = [];
}

public sealed class ShowTurn
{
    public string Show { get; set; } = "";
    public int Episodes { get; set; } = 2;
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
    // "soft": a program and its break finish before the block is considered, so a long program can delay it.
    // "exact": nothing is allowed to run past the block's start; the time before it is filled with clips.
    public string StartMode { get; set; } = "soft";
    // A soft block that would start more than this many minutes late is skipped for the day (0 = never skipped).
    public int MaxLateMinutes { get; set; }
    // "count" airs the daily limit; "fit" airs only whole programs that end before the block does.
    public string Fill { get; set; } = "count";
    // The title shown in the Live TV guide for every program of this block, such as "Saturday Morning Cartoons".
    public string GuideTitle { get; set; } = "";
}

public sealed record Media(Guid Id, string Title, string Path, double Seconds, string Series, int Season, int Episode, int Year = 0, string[]? Genres = null, string[]? Tags = null, string[]? Studios = null, string? Show = null);
// Offset is where in the file this entry starts: the later parts of a program split by a commercial break.
// Match says why a commercial or bumper was chosen, for the guide preview.
// Speed above 1 plays the picture that many times faster (credits), keeping the sound at normal speed.
public sealed record Airing(string ChannelId, Guid ItemId, string Title, string Path, DateTimeOffset Start, DateTimeOffset End, bool Commercial, PlanningUndo? Undo = null, double Offset = 0, string? Match = null,
    double Speed = 1, string? Heading = null);
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
    // The schedule settings the future guide was planned with; a change rebuilds the future.
    public string? PlannedWith { get; set; }
}
