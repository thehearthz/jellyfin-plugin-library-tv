using System.Text.Json;

namespace Jellyfin.Plugin.LibraryTV;

// Explicit allowlist: no stream token, server URL, filesystem paths, or playback history.
public sealed class SettingsBackup
{
    public int FormatVersion { get; set; } = 1;
    public ChannelDefinition[] Channels { get; set; } = [];
    public bool AutoChannels { get; set; }
    public string[] AutoLibraryIds { get; set; } = [];
    public AutomaticChannel[] AutomaticChannels { get; set; } = [];
    public int AutoChannelStartNumber { get; set; } = 100;
    public string TimeZoneId { get; set; } = "UTC";
    public int GuideDays { get; set; } = 3;
    public int RefreshMinutes { get; set; } = 60;
    public int MaxActiveChannels { get; set; } = 2;
    public ContentRule[] Restrictions { get; set; } = [];
    public Pool SharedCommercials { get; set; } = new();
    public Pool SharedBumpers { get; set; } = new();
    public int AutomaticCommercialCount { get; set; } = 2;
    public bool DetectCredits { get; set; } = true;
    public ClipEdit[] ClipRules { get; set; } = [];
    public ClipEdit[] ClipOverrides { get; set; } = [];

    public static SettingsBackup Export(Configuration config) => JsonSerializer.Deserialize<SettingsBackup>(JsonSerializer.Serialize(config))!;

    public Configuration ApplyTo(Configuration current)
    {
        if (FormatVersion != 1) throw new ArgumentException("This backup format is not supported. Use a Library TV settings backup with FormatVersion 1.");
        var config = JsonSerializer.Deserialize<Configuration>(JsonSerializer.Serialize(current))!;
        config.Channels = Channels;
        config.AutoChannels = AutoChannels;
        config.AutoLibraryIds = AutoLibraryIds;
        config.AutomaticChannels = AutomaticChannels;
        config.AutoChannelStartNumber = AutoChannelStartNumber;
        config.TimeZoneId = TimeZoneId;
        config.GuideDays = GuideDays;
        config.RefreshMinutes = RefreshMinutes;
        config.MaxActiveChannels = MaxActiveChannels;
        config.Restrictions = Restrictions;
        config.SharedCommercials = SharedCommercials;
        config.SharedBumpers = SharedBumpers;
        config.AutomaticCommercialCount = AutomaticCommercialCount;
        config.DetectCredits = DetectCredits;
        config.ClipRules = ClipRules;
        config.ClipOverrides = ClipOverrides;
        Scheduler.Validate(config);
        return config;
    }
}
