using System.Diagnostics;
using Jellyfin.Plugin.LibraryTV;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;

public static class CreditsTests
{
    public static async Task Run(Action<bool, string> check, string directory, IApplicationPaths paths)
    {
        var store = new CreditsStore(paths);
        var stored = new Media(Guid.NewGuid(), "Stored", Path.Combine(directory, "stored.mkv"), 1800, "", 0, 0);
        store.Put(stored.Id, new DetectedCredits(stored.Path, 1, 2, 1700, 1800, "audio", DateTimeOffset.UtcNow));
        store.Advance();
        store.Save();
        var reloaded = new CreditsStore(paths);
        check(reloaded.For(stored) == (1700, 1800) && reloaded.Generation == 1 && reloaded.For(stored with { Path = "moved.mkv" }) is null, "Detected credits are saved, reloaded, and ignored for a different file");
        var catalog = new Catalog(Proxy<ILibraryManager>.Make((name, _) => throw new NotSupportedException(name)), detections: reloaded);
        check(catalog.CreateCreditsReader()(stored) == (1700, 1800) && catalog.CreateCreditsReader(detected: false)(stored) is null, "Scheduling uses detected credits when a program has no credits marker");

        var ffmpeg = Environment.GetEnvironmentVariable("LIBRARY_TV_FFMPEG");
        if (string.IsNullOrEmpty(ffmpeg)) { Console.WriteLine("SKIP: set LIBRARY_TV_FFMPEG to run real credits detection tests."); return; }
        var root = Path.Combine(directory, "credits"); Directory.CreateDirectory(root);
        var encoder = Proxy<IMediaEncoder>.Make((name, _) => name == "get_EncoderPath" ? ffmpeg : throw new NotSupportedException(name));
        // Two episodes: a different 100-second story each, then the same 20 seconds of closing music.
        var episodes = new List<Media>();
        foreach (var seed in new[] { 1, 2 })
        {
            var path = Path.Combine(root, $"episode {seed}.mkv");
            await Run(ffmpeg, ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", $"aevalsrc=0.5*sin(2*PI*{(seed == 1 ? 261.6 : 370)}*t)*(1+0.5*sin({seed}*t)):s=44100:d=100",
                "-f", "lavfi", "-i", "aevalsrc=0.4*sin(2*PI*(440+220*floor(mod(t*2\\,4)))*t)+0.2*sin(2*PI*196*t):s=44100:d=20", "-f", "lavfi", "-i", "testsrc2=size=160x90:rate=10:duration=120",
                "-filter_complex", "[0:a][1:a]concat=n=2:v=0:a=1[a]", "-map", "2:v", "-map", "[a]", "-c:v", "libx264", "-preset", "ultrafast", "-g", "20", "-c:a", "aac", path]);
            episodes.Add(new Media(Guid.NewGuid(), $"Episode {seed}", path, 120, "show", 1, seed));
        }
        var first = await CreditsScanner.AudioPrint(encoder, episodes[0], default);
        var second = await CreditsScanner.AudioPrint(encoder, episodes[1], default);
        var match = first is null || second is null ? null : CreditsDetector.Match(first, second);
        var start = match is { } m ? CreditsDetector.AudioTimes(120, m.StartA, m.EndA).Start : -1;
        check(match is not null && Math.Abs(start - 100) <= 1.5, $"Closing music shared by two episodes is found where the credits start ({start:0.0} s)");
        // A movie: 160 seconds of picture, then 40 seconds of credits on black.
        var movie = Path.Combine(root, "movie.mkv");
        await Run(ffmpeg, ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=10:duration=160",
            "-f", "lavfi", "-i", "color=black:size=320x180:rate=10:duration=40,drawbox=x=120:y=80:w=80:h=6:color=white:t=fill",
            "-filter_complex", "[0:v][1:v]concat=n=2:v=1:a=0[v]", "-map", "[v]", "-c:v", "libx264", "-preset", "ultrafast", "-g", "20", "-pix_fmt", "yuv420p", movie]);
        var frames = await CreditsScanner.PictureFrames(encoder, new Media(Guid.NewGuid(), "Movie", movie, 200, "", 0, 0), default);
        var run = frames is null ? null : CreditsDetector.BlackRun(frames, 200);
        check(run is { } r && Math.Abs(r.Start - 160) <= 3 && r.End >= 195, $"Movie credits on black are found where they start ({run?.Start:0.0} s)");
    }

    private static async Task Run(string file, string[] args)
    {
        var start = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var errors = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new Exception(errors);
    }
}
