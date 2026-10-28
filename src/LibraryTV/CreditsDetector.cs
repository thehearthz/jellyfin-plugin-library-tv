using System.Numerics;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.LibraryTV;

// Finds where credits start from the file itself. Episodes of one show share their closing music, so the
// longest stretch of matching audio near the end of neighbouring episodes is the credits. Movies, and episodes
// whose credits differ, fall back to the picture: credits are a long, mostly black stretch near the end.
public static class CreditsDetector
{
    // One Chromaprint point per 4096 / 3 samples at 11025 Hz.
    public const double PointSeconds = 4096.0 / 3 / 11025;
    // Two points match when at most this many of their 32 bits differ, and a match may skip this much.
    private const int MaxBitErrors = 6;
    private const double MaxSkip = 3.5;
    public const double MinAudioCredits = 15;
    public const double MinBlackCredits = 20;
    // A sampled frame this percent black counts as black, with white text on it.
    public const int BlackPercent = 80;

    // Each point also hears about two seconds of the sound after it, so a run of matching points starts
    // that much before the shared music does.
    public const double PointLead = 2;

    // Where matching points [start, end) of the search window at the end of a file are, in seconds.
    public static (double Start, double End) AudioTimes(double seconds, int start, int end)
    {
        var from = seconds - AudioWindow(seconds) + PointLead;
        return (from + start * PointSeconds, Math.Min(seconds, from + end * PointSeconds));
    }

    // How much of the end of a file is searched.
    public static double AudioWindow(double seconds) => Math.Min(seconds * 0.25, 360);
    public static double PictureWindow(double seconds) => Math.Min(Math.Max(seconds * 0.15, 120), 900);

    // The longest run of matching points between two fingerprints at any shift, as point indexes [start, end) in each.
    public static (int StartA, int EndA, int StartB, int EndB)? Match(uint[] a, uint[] b)
    {
        var skip = (int)Math.Ceiling(MaxSkip / PointSeconds);
        var minimum = (int)Math.Ceiling(MinAudioCredits / PointSeconds);
        (int StartA, int EndA, int StartB, int EndB)? best = null;
        var bestLength = minimum - 1;
        // Shift is the index in b of a[0].
        for (var shift = -(a.Length - 1); shift < b.Length; shift++)
        {
            var from = Math.Max(0, -shift);
            var to = Math.Min(a.Length, b.Length - shift);
            if (to - from <= bestLength) continue;
            int start = -1, last = -1;
            for (var i = from; i < to; i++)
            {
                if (BitOperations.PopCount(a[i] ^ b[i + shift]) > MaxBitErrors) continue;
                if (start < 0 || i - last > skip) start = i;
                last = i;
                if (last + 1 - start > bestLength)
                {
                    bestLength = last + 1 - start;
                    best = (start, last + 1, start + shift, last + 1 + shift);
                }
            }
        }
        return best;
    }

    // Credits from keyframes sampled at (time, percent black): the longest mostly black stretch, at least
    // twenty seconds, allowing single non-black frames (logos). It runs to the first frame after it, or the end.
    public static (double Start, double End)? BlackRun(IReadOnlyList<(double Time, int Black)> frames, double end)
    {
        (double Start, double End)? best = null;
        for (var i = 0; i < frames.Count; i++)
        {
            if (frames[i].Black < BlackPercent || i > 0 && frames[i - 1].Black >= BlackPercent) continue;
            var last = i;
            for (var j = i + 1; j < frames.Count; j++)
                if (frames[j].Black >= BlackPercent) last = j;
                else if (j + 1 >= frames.Count || frames[j + 1].Black < BlackPercent) break;
            var stop = last + 1 < frames.Count ? frames[last + 1].Time : end;
            if (stop - frames[i].Time >= MinBlackCredits && (best is null || stop - frames[i].Time >= best.Value.End - best.Value.Start))
                best = (frames[i].Time, stop);
            i = last;
        }
        return best;
    }

    private static readonly Regex BlackFrame = new(@"pblack:(\d+) pts:\S+ t:([0-9.]+)", RegexOptions.CultureInvariant);

    public static List<(double Time, int Black)> ParseBlackFrames(string log, double offset) => BlackFrame.Matches(log)
        .Select(x => (offset + double.Parse(x.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture), int.Parse(x.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)))
        .OrderBy(x => x.Item1).ToList();

    public static uint[] ParseFingerprint(byte[] raw)
    {
        var points = new uint[raw.Length / 4];
        for (var i = 0; i < points.Length; i++) points[i] = BitConverter.ToUInt32(raw, i * 4);
        return points;
    }
}
