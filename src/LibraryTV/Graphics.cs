using System.Globalization;

namespace Jellyfin.Plugin.LibraryTV;

// The lower-third bug: the channel's logo (or number), its name and what's on, every few minutes on the clock.
public sealed record BugInfo(string Channel, string Number, string? Logo, string Title, int Minutes, string Zone);

// A station ident: the channel's logo (or number) and name, animated, with an optional chime.
public sealed record IdentInfo(string Channel, string Number, string? Logo, bool Chime);

// On-screen graphics drawn by FFmpeg over programs that are being encoded: the Up next and Now playing cards
// and the lower-third bug, and station idents. Every panel is drawn once as a single still picture with rounded
// corners, then slid on and off the program at its times, over a soft shadow and (frosted) a blurred copy of
// the picture behind it. Panels never touch frames outside their times.
public static class Graphics
{
    public const string Gold = "0xf5c542";
    private static string N(double x) => x.ToString("0.###", CultureInfo.InvariantCulture);
    private static string I(int x) => x.ToString(CultureInfo.InvariantCulture);
    private static int E(double x) => (int)Math.Round(x / 2) * 2;

    // A panel: Build turns the numbers of its picture inputs into a filter chain ending in [Key], one frame,
    // repeated. Windows are the times it shows, in seconds from the start of this output.
    public sealed record Panel(string Key, int Width, int Height, int X, int Y, (double A, double B)[] Windows, string[] Images, Func<int[], string> Build);

    private sealed record Line(string Text, string Color, int Size, int X, string Y);

    // Shortens text that would not fit a width, with an ellipsis.
    internal static string Fit(string text, int width, int size)
    {
        var most = Math.Max(4, (int)(width / (size * 0.66)));
        return text.Length > most ? text[..(most - 1)].TrimEnd() + "…" : text;
    }

    private static string Text(Line line)
    {
        var font = ProgramStreamer.Font is { } file ? "fontfile=" + ProgramStreamer.Quote(file) + ":" : "";
        return "drawtext=" + ProgramStreamer.FilterValue($"{font}text={ProgramStreamer.Quote(line.Text)}:expansion=none:fontcolor={line.Color}:fontsize={line.Size}:x={line.X}:y={line.Y}");
    }

    // A still panel: a translucent dark background with a gold edge, pictures, then text, then rounded corners.
    private static string Still(string key, int width, int height, bool frosted, IEnumerable<(int Input, string Fit, int X, string Y)> pictures, IEnumerable<Line> lines)
    {
        var steps = new List<string> { $"color=c=0x0d0f14@{(frosted ? "0.5" : "0.88")}:s={width}x{height}:r=1:d=1,format=rgba,drawbox=x=0:y=0:w={Math.Max(3, E(height * 0.03))}:h=ih:color={Gold}@1:t=fill[{key}0]" };
        var label = $"[{key}0]";
        var n = 0;
        foreach (var (input, fit, x, y) in pictures)
        {
            steps.Add($"[{input}:v]{fit},format=rgba[{key}p{n}]");
            steps.Add($"{label}[{key}p{n}]overlay={x}:{y}:format=auto[{key}{++n}]");
            label = $"[{key}{n}]";
        }
        var texts = lines.Select(Text).ToList();
        texts.Add(ProgramStreamer.Rounded(Math.Max(6, height / 8)));
        steps.Add($"{label}{string.Join(",", texts)},loop=loop=-1:size=1,setpts=N/30/TB[{key}]");
        return string.Join(";", steps);
    }

    // The times a card or the bug shows: the latest window that has started, as a piecewise expression.
    private static string Piece((double A, double B)[] windows, Func<(double A, double B), double> pick)
    {
        var expression = N(pick(windows[^1]));
        for (var i = windows.Length - 2; i >= 0; i--) expression = $"if(lt(t,{N(windows[i + 1].A)}),{N(pick(windows[i]))},{expression})";
        return expression;
    }

    internal static string Enable(Panel panel) => string.Join("+", panel.Windows.Select(w => $"between(t,{N(w.A)},{N(w.B)})"));

    // Slides in from the left over half a second and back out at the end, easing at both ends.
    internal static string SlideX(Panel panel, int offset = 0) =>
        $"{panel.X + offset}-{panel.Width + panel.X + 40}*pow(max(0,1-min(t-({Piece(panel.Windows, w => w.A)}),({Piece(panel.Windows, w => w.B)})-t)/0.5),3)";

    // Draws a panel over [label]: its shadow, the frosted picture behind it, then the panel. Returns the new label.
    internal static string Compose(List<string> steps, string label, Panel panel, int[] inputs, bool frosted)
    {
        var k = panel.Key;
        var enable = Enable(panel);
        var x = SlideX(panel);
        var radius = Math.Max(6, panel.Height / 8);
        steps.Add(panel.Build(inputs));
        // A soft shadow, a little below the panel: its alpha falls off over `soft` pixels outside a rounded rectangle.
        var soft = Math.Max(6, E(panel.Height * 0.09));
        var (sw, sh) = (panel.Width + 2 * soft, panel.Height + 2 * soft);
        var distance = $"hypot(max(0,max({soft + radius}-X,X-({soft + panel.Width - 1 - radius}))),max(0,max({soft + radius}-Y,Y-({soft + panel.Height - 1 - radius}))))-{radius}";
        steps.Add($"color=c=black:s={sw}x{sh}:r=1:d=1,format=rgba,geq={ProgramStreamer.FilterValue($"r=0:g=0:b=0:a={ProgramStreamer.Quote($"140*pow(clip(1-max(0,{distance})/{soft},0,1),2)")}")},loop=loop=-1:size=1,setpts=N/30/TB[{k}s]");
        steps.Add($"{label}[{k}s]overlay={ProgramStreamer.FilterValue($"x={ProgramStreamer.Quote(SlideX(panel, -soft))}:y={panel.Y - soft + soft / 2}:eval=frame:format=auto:enable={ProgramStreamer.Quote(enable)}")}[{k}a]");
        label = $"[{k}a]";
        if (frosted)
        {
            // Every frame flows through both branches, so FFmpeg never holds frames back waiting; only the
            // frames in the panel's windows are blurred and rounded.
            var when = ProgramStreamer.Quote(enable);
            steps.Add($"{label}split[{k}m][{k}b]");
            steps.Add($"[{k}b]crop={ProgramStreamer.FilterValue($"w={panel.Width}:h={panel.Height}:x={ProgramStreamer.Quote(x)}:y={panel.Y}")},boxblur={ProgramStreamer.FilterValue($"lr=18:lp=2:cr=9:cp=2:enable={when}")},format=rgba,{ProgramStreamer.Rounded(radius, enable)}[{k}f]");
            steps.Add($"[{k}m][{k}f]overlay={ProgramStreamer.FilterValue($"x={ProgramStreamer.Quote(x)}:y={panel.Y}:eval=frame:format=auto:enable={ProgramStreamer.Quote(enable)}")}[{k}c]");
            label = $"[{k}c]";
        }
        steps.Add($"{label}[{k}]overlay={ProgramStreamer.FilterValue($"x={ProgramStreamer.Quote(x)}:y={panel.Y}:eval=frame:format=auto:enable={ProgramStreamer.Quote(enable)}")},format=yuv420p[{k}d]");
        return $"[{k}d]";
    }

    // The window a card shows in, from scheduled times; null when it is over before this output starts.
    private static (double A, double B)? Window(DateTimeOffset start, double seconds, DateTimeOffset from)
    {
        var a = (start - from).TotalSeconds;
        var b = a + seconds;
        return b <= 1 ? null : (Math.Max(0, a), b);
    }

    // The Up next card: 40 seconds before the part ends (at once in a shorter part, such as sped-up credits), for 12 seconds.
    public static Panel? UpNext(Airing item, StreamPlan plan, DateTimeOffset from, int frameWidth, int frameHeight)
    {
        if (plan.UpNext is not { } next || plan.UpNextStyle == "text") return null;
        var show = item.End - TimeSpan.FromSeconds(40) is var early && early > item.Start ? early : item.Start;
        if (Window(show, 12, from) is not { } window) return null;
        var poster = plan.UpNextStyle is "card" or "poster" ? next.Poster : null;
        var art = plan.UpNextStyle is "card" or "logo" ? next.Logo : null;
        return Card("next", "UP NEXT", next, poster, 2 / 3.0, art, window, frameWidth, frameHeight, plan.Frosted);
    }

    // The Now playing card: from a second into a program's first part, for 8 seconds, with its backdrop.
    public static Panel? Intro(Airing item, StreamPlan plan, DateTimeOffset from, int frameWidth, int frameHeight)
    {
        if (plan.Intro is not { } now || Window(item.Start + TimeSpan.FromSeconds(1), 8, from) is not { } window) return null;
        return Card("intro", "NOW PLAYING", now, now.Backdrop, 16 / 9.0, now.Logo, window, frameWidth, frameHeight, plan.Frosted);
    }

    private static Panel Card(string key, string label, UpNextCard card, string? picture, double shape, string? art, (double A, double B) window, int w, int h, bool frosted)
    {
        var pad = Math.Max(6, E(h * 0.028));
        var accent = Math.Max(3, E(h * 0.3 * 0.03));
        var height = picture is null ? E(h * 0.2) : E(h * 0.3);
        var pictureHeight = height - 2 * pad;
        var pictureWidth = picture is null ? 0 : E(pictureHeight * shape);
        var textX = accent + pad + (picture is null ? 0 : pictureWidth + pad);
        var textWidth = E(Math.Min(w * 0.34, w - textX - pad - w * 0.1));
        var width = textX + textWidth + pad;
        var labelSize = Math.Max(10, h / 34);
        var titleSize = Math.Max(14, h / 19);
        var subtitleSize = Math.Max(10, h / 30);
        var artY = pad + labelSize + pad / 2;
        var artHeight = Math.Max(titleSize, height - artY - pad - (card.Subtitle is null ? 0 : subtitleSize + pad / 2));
        var margin = E(h * 0.06);
        var images = new[] { picture, art }.OfType<string>().ToArray();
        var lines = new List<Line> { new(label, Gold, labelSize, textX, I(pad)) };
        if (art is null) lines.Add(new(Fit(card.Title, textWidth, titleSize), "white", titleSize, textX, $"{artY}+({artHeight}-th)/2"));
        if (card.Subtitle is { } sub) lines.Add(new(Fit(sub, textWidth, subtitleSize), "white@0.8", subtitleSize, textX, $"h-{pad}-th"));
        return new Panel(key, width, height, margin, h - margin - height, [window], images, inputs =>
        {
            var pictures = new List<(int, string, int, string)>();
            var n = 0;
            if (picture is not null)
                pictures.Add((inputs[n++], $"scale={pictureWidth}:{pictureHeight}:force_original_aspect_ratio=increase,crop={pictureWidth}:{pictureHeight},format=rgba,{ProgramStreamer.Rounded(Math.Max(3, pad / 2))}", accent + pad, I(pad)));
            if (art is not null)
                pictures.Add((inputs[n], $"scale=w={textWidth}:h={artHeight}:force_original_aspect_ratio=decrease", textX, $"{artY}+({artHeight}-overlay_h)/2"));
            return Still(key, width, height, frosted, pictures, lines);
        });
    }

    // The bug: every Minutes on the local clock, for 8 seconds, except where a card is already showing.
    public static Panel? Bug(Airing item, StreamPlan plan, DateTimeOffset from, int w, int h, IEnumerable<(double A, double B)> busy)
    {
        if (plan.Bug is not { } bug) return null;
        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(bug.Zone); } catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { zone = TimeZoneInfo.Utc; }
        var period = TimeSpan.FromMinutes(Math.Clamp(bug.Minutes, 5, 240)).Ticks;
        var taken = busy.ToArray();
        var windows = new List<(double A, double B)>();
        var local = from.UtcTicks + zone.GetUtcOffset(from).Ticks;
        var first = new DateTimeOffset(from.UtcTicks - local % period, TimeSpan.Zero);
        for (var at = first; at < item.End && windows.Count < 40; at = at.AddTicks(period))
        {
            // Programs ahead of the clock still mark the hour by the clock.
            var aligned = at.AddTicks(-(at.UtcTicks + zone.GetUtcOffset(at).Ticks) % period);
            if (Window(aligned, 8, from) is not { } window || window.A >= (item.End - from).TotalSeconds - 2) continue;
            if (taken.Any(t => window.A < t.B + 2 && t.A < window.B + 2)) continue;
            windows.Add(window);
        }
        if (windows.Count == 0) return null;
        var pad = Math.Max(4, E(h * 0.018));
        var height = Math.Max(40, E(h * 0.115));
        var accent = Math.Max(3, E(height * 0.04));
        var logoHeight = height - 2 * pad;
        var logoWidth = bug.Logo is null ? logoHeight : E(logoHeight * 1.8);
        var textX = accent + pad + logoWidth + pad;
        var nameSize = Math.Max(11, height * 30 / 100);
        var nowSize = Math.Max(10, height * 22 / 100);
        var textWidth = E(w * 0.26);
        var width = textX + textWidth + pad;
        var margin = E(h * 0.06);
        var lines = new List<Line> { new(Fit(bug.Channel, textWidth, nameSize), "white", nameSize, textX, $"{pad}+{logoHeight / 2}-th-{pad / 4}"), new(Fit("Now · " + bug.Title, textWidth, nowSize), "white@0.8", nowSize, textX, $"{pad}+{logoHeight / 2}+{pad / 3}") };
        if (bug.Logo is null) lines.Insert(0, new(Fit(bug.Number, logoWidth, logoHeight * 6 / 10), Gold, logoHeight * 6 / 10, accent + pad, $"{pad}+({logoHeight}-th)/2"));
        return new Panel("bug", width, height, margin, h - margin - height, windows.ToArray(), bug.Logo is { } logo ? [logo] : [], inputs => Still("bug", width, height, plan.Frosted,
            bug.Logo is null ? [] : [(inputs[0], $"scale=w={logoWidth}:h={logoHeight}:force_original_aspect_ratio=decrease", accent + pad, $"{pad}+({logoHeight}-overlay_h)/2")], lines));
    }

    // The ident's picture, as a chain from [0:v] (a dark background of the ident's length) to [v0]: the logo (or
    // the number) rises and fades in, a gold line draws out beneath it, then the name and number appear, and it
    // all fades to black. Logo is the logo's input number, or -1. Elapsed skips what has already aired.
    public static string Ident(IdentInfo ident, int logoInput, int w, int h, double seconds, double elapsed)
    {
        var logoWidth = E(w * 0.36);
        var logoHeight = E(h * 0.34);
        var lineWidth = E(w * 0.22);
        var lineY = E(h * 0.6);
        var nameSize = Math.Max(16, h / 14);
        var numberSize = Math.Max(10, h / 30);
        var steps = new List<string>();
        var label = "[0:v]";
        var rise = $"{E(h * 0.04)}*pow(max(0,1-t/0.9),3)";
        if (logoInput >= 0)
        {
            steps.Add($"[{logoInput}:v]scale=w={logoWidth}:h={logoHeight}:force_original_aspect_ratio=decrease,format=rgba,loop=loop=-1:size=1,setpts=N/30/TB,fade=t=in:st=0.15:d=0.7:alpha=1[il]");
            steps.Add($"{label}[il]overlay={ProgramStreamer.FilterValue($"x=(W-w)/2:y={ProgramStreamer.Quote($"{lineY}-{E(h * 0.04)}-h+{rise}")}:eval=frame:shortest=1:format=auto")}[ia]");
            label = "[ia]";
        }
        var reveal = "min(1,max(0,(t-0.55)/0.6))";
        var line = $"drawbox={ProgramStreamer.FilterValue($"x={ProgramStreamer.Quote($"(iw-{lineWidth}*{reveal})/2")}:y={lineY}:w={ProgramStreamer.Quote($"max(1,{lineWidth}*{reveal})")}:h={Math.Max(2, h / 180)}:color={Gold}@1:t=fill")}";
        var font = ProgramStreamer.Font is { } file ? "fontfile=" + ProgramStreamer.Quote(file) + ":" : "";
        string Words(string text, string color, int size, string y, double delay) => "drawtext=" + ProgramStreamer.FilterValue(
            $"{font}text={ProgramStreamer.Quote(text)}:expansion=none:fontcolor={color}:fontsize={size}:x=(w-tw)/2:y={y}:alpha={ProgramStreamer.Quote($"min(1,max(0,(t-{N(delay)})/0.5))")}");
        var parts = new List<string> { line };
        if (logoInput < 0) parts.Add(Words(Fit(ident.Number, w / 2, h / 5), Gold, h / 5, $"{lineY}-{E(h * 0.04)}-th", 0.15));
        parts.Add(Words(Fit(ident.Channel, E(w * 0.8), nameSize), "white", nameSize, $"{lineY + E(h * 0.04)}", 0.9));
        if (logoInput >= 0) parts.Add(Words("CHANNEL " + ident.Number, Gold, numberSize, $"{lineY + E(h * 0.04) + nameSize + E(h * 0.025)}", 1.2));
        parts.Add($"fade=t=in:st=0:d=0.25,fade=t=out:st={N(Math.Max(0, seconds - 0.5))}:d=0.5");
        if (elapsed > 0.05) parts.Add($"trim=start={N(elapsed)},setpts=PTS-STARTPTS");
        steps.Add($"{label}{string.Join(",", parts)},format=yuv420p[v0]");
        return string.Join(";", steps);
    }

    // A three-note chime that rings out over the ident.
    public static string Chime(double seconds) =>
        $"aevalsrc={ProgramStreamer.FilterValue(ProgramStreamer.Quote("0.16*(sin(2*PI*523.25*t)*exp(-3.5*t)+gte(t,0.16)*sin(2*PI*659.25*(t-0.16))*exp(-3.5*(t-0.16))+gte(t,0.32)*sin(2*PI*783.99*(t-0.32))*exp(-2.5*(t-0.32)))"))}:s=48000:c=stereo:d={N(seconds)}";
}
