using System.Globalization;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.LibraryTV;

public static partial class ChannelNumbers
{
    [GeneratedRegex(@"^[0-9]{1,6}(?:[.-][0-9]{1,4})?$")]
    private static partial Regex Format();

    public static (int Major, int Minor) Key(string? value)
    {
        value = value?.Trim();
        if (value is null || !Format().IsMatch(value))
            throw new ArgumentException("Channel numbers must be 1–999999, optionally followed by a subchannel such as 7.2 or 7-10.");
        var parts = value.Split(['.', '-']);
        var major = int.Parse(parts[0], CultureInfo.InvariantCulture);
        if (major == 0) throw new ArgumentException("Channel numbers must start at 1 or higher.");
        return (major, parts.Length == 1 ? -1 : int.Parse(parts[1], CultureInfo.InvariantCulture));
    }

    public static void ValidateUnique(IEnumerable<ChannelDefinition> channels)
    {
        var used = new Dictionary<(int, int), string>();
        foreach (var channel in channels)
        {
            var key = Key(channel.Number);
            if (!used.TryAdd(key, channel.Name))
                throw new ArgumentException($"Channel number {channel.Number} is used by both '{used[key]}' and '{channel.Name}'. Choose a different number.");
        }
    }
}
