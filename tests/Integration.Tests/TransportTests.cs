using System.Diagnostics;
using System.Text.Json;
using Jellyfin.Plugin.LibraryTV;
using MediaBrowser.Controller.MediaEncoding;

public static class TransportTests
{
    public static async Task Run(Action<bool, string> check, string directory)
    {
        var ffmpeg = Environment.GetEnvironmentVariable("LIBRARY_TV_FFMPEG");
        if (string.IsNullOrEmpty(ffmpeg)) { Console.WriteLine("SKIP: set LIBRARY_TV_FFMPEG to run real transport/process tests."); return; }
        if (!File.Exists(ffmpeg)) throw new FileNotFoundException("The configured transport-test FFmpeg is missing.", ffmpeg);
        var ffprobe = Path.Combine(Path.GetDirectoryName(ffmpeg)!, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
        var root = Path.Combine(directory, "transport"); Directory.CreateDirectory(root);
        var clips = new[] { Path.Combine(root, "episode with spaces.mp4"), Path.Combine(root, "commercial.mp4") };
        foreach (var clip in clips)
            await Run(ffmpeg, ["-hide_banner","-loglevel","error","-y","-f","lavfi","-i","testsrc2=size=320x180:rate=25","-f","lavfi","-i","sine=frequency=440:sample_rate=48000","-t","2","-c:v","libx264","-preset","ultrafast","-g","25","-pix_fmt","yuv420p","-c:a","aac","-ac","2",clip]);
        var encoder = Proxy<IMediaEncoder>.Make((name, _) => name switch { "get_EncoderPath" => ffmpeg, "get_ProbePath" => ffprobe, _ => throw new NotSupportedException(name) });
        var streamer = new ProgramStreamer(encoder);
        Airing Item(string clip, int seconds = 2) { var now = DateTimeOffset.UtcNow; return new("transport", Guid.NewGuid(), "Test", clip, now, now.AddSeconds(seconds), false); }
        var Auto = new ChannelDefinition();
        var later = ProgramStreamer.Command(ffmpeg, Item(clips[0]) with { Offset = 60 }, StreamPlan.Copy, DateTimeOffset.UtcNow);
        check(later.ArgumentList[later.ArgumentList.IndexOf("-ss") + 1].StartsWith("60."), "The second part of a program starts where the first part stopped");
        var clock = DateTimeOffset.UtcNow;
        var early = new Airing("transport", Guid.NewGuid(), "Next", clips[0], clock.AddSeconds(2), clock.AddSeconds(32), false, Offset: 600);
        var ahead = ProgramStreamer.Command(ffmpeg, early, StreamPlan.Copy, clock).ArgumentList;
        string After(IList<string> args, string name) => args[args.IndexOf(name) + 1];
        check(After(ahead, "-ss") == "600.000" && After(ahead, "-t") == "30.000" && After(ahead, "-output_ts_offset") == (early.Start.ToUnixTimeMilliseconds() / 1000.0).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)
            && !ahead.Contains("-re") && !ahead.Contains("-readrate"), "A program started ahead of time plays from its own start, for its full length, stamped with its scheduled time");
        var command = ProgramStreamer.Command(ffmpeg, Item(clips[0]), StreamPlan.Copy, DateTimeOffset.UtcNow);
        check(!command.UseShellExecute && command.ArgumentList.Contains(clips[0]) && command.ArgumentList.Contains("copy") && !command.ArgumentList.Contains("libx264"), "Compatible files are copied, and paths are passed as individual arguments without shell interpretation");
        using var transport = new MemoryStream();
        foreach (var clip in clips) await streamer.Stream(Item(clip), Auto, null, bytes => { transport.Write(bytes); return Task.CompletedTask; }, default);
        var transportPath = Path.Combine(root, "transition.ts"); await File.WriteAllBytesAsync(transportPath, transport.ToArray());
        var probe = JsonDocument.Parse(await Run(ffprobe, ["-v","error","-show_entries","stream=codec_name,codec_type","-of","json",transportPath]));
        var codecs = probe.RootElement.GetProperty("streams").EnumerateArray().Select(x => x.GetProperty("codec_name").GetString()).ToArray();
        check(codecs.Contains("h264") && codecs.Contains("aac"), "Real episode/commercial stream-copy transitions retain H.264 video and AAC audio");
        var frames = await Run(ffprobe, ["-v","error","-select_streams","v:0","-count_frames","-show_entries","stream=nb_read_frames","-of","csv=p=0",transportPath]);
        check(frames.Split(['\r','\n'], StringSplitOptions.RemoveEmptyEntries).Any(x => int.TryParse(x.Trim().TrimEnd(','), out var count) && count > 60), "The joined transport decodes video frames from both sides of a program transition");
        var silent = Path.Combine(root, "silent mpeg4.avi");
        await Run(ffmpeg, ["-hide_banner","-loglevel","error","-y","-f","lavfi","-i","testsrc2=size=320x180:rate=25","-t","2","-c:v","mpeg4","-an",silent]);
        using var converted = new MemoryStream();
        var convertedMethod = await streamer.Stream(Item(silent), Auto, null, bytes => { converted.Write(bytes); return Task.CompletedTask; }, default);
        var convertedPath = Path.Combine(root, "converted.ts"); await File.WriteAllBytesAsync(convertedPath, converted.ToArray());
        var convertedStreams = JsonDocument.Parse(await Run(ffprobe, ["-v","error","-show_entries","stream=codec_name,codec_type,channels,width","-of","json",convertedPath])).RootElement.GetProperty("streams").EnumerateArray().ToArray();
        check(convertedStreams.Any(x => x.GetProperty("codec_name").GetString() == "h264" && x.GetProperty("width").GetInt32() == 320) && convertedStreams.Any(x => x.GetProperty("codec_name").GetString() == "aac" && x.GetProperty("channels").GetInt32() == 2) && convertedMethod.Contains("encoded"),
            "Files in other formats are encoded to H.264 at their own size, and a silent file gets a stereo AAC track, found with FFprobe when Jellyfin has no stream data");
        using var mixed = new MemoryStream();
        foreach (var clip in new[] { clips[0], silent }) await streamer.Stream(Item(clip), Auto, null, bytes => { mixed.Write(bytes); return Task.CompletedTask; }, default);
        var mixedPath = Path.Combine(root, "mixed.ts"); await File.WriteAllBytesAsync(mixedPath, mixed.ToArray());
        var mixedFrames = await Run(ffprobe, ["-v","error","-select_streams","v:0","-count_frames","-show_entries","stream=nb_read_frames","-of","csv=p=0",mixedPath]);
        var mixedAudio = await Run(ffprobe, ["-v","error","-select_streams","a:0","-count_packets","-show_entries","stream=codec_name,nb_read_packets","-of","csv=p=0",mixedPath]);
        check(mixedFrames.Split(['\r','\n'], StringSplitOptions.RemoveEmptyEntries).Any(x => int.TryParse(x.Trim().TrimEnd(','), out var count) && count > 80) && mixedAudio.Contains("aac"),
            "A copied program followed by an encoded one decodes as one continuous H.264/AAC stream");
        // Six seconds of video sent in well under six seconds shows the lead at work, with room for slow machines.
        var sixSeconds = Path.Combine(root, "six seconds.mp4");
        await Run(ffmpeg, ["-hide_banner","-loglevel","error","-y","-f","lavfi","-i","testsrc2=size=320x180:rate=25","-f","lavfi","-i","sine=frequency=440:sample_rate=48000","-t","6","-c:v","libx264","-preset","ultrafast","-g","25","-pix_fmt","yuv420p","-c:a","aac","-ac","2",sixSeconds]);
        ProgramStreamer.Lead = TimeSpan.FromSeconds(6);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        string? reported = null;
        var directMethod = await streamer.Stream(Item(sixSeconds, 6), Auto, null, _ => Task.CompletedTask, default, (text, _) => reported ??= text);
        timer.Stop();
        ProgramStreamer.Lead = TimeSpan.FromSeconds(3);
        check(timer.Elapsed < TimeSpan.FromSeconds(4) && reported == directMethod, "Video is sent up to the lead ahead of its time, so a short clip arrives faster than real time on any FFmpeg version, and Channel health learns how it plays as it starts");
        check(directMethod.StartsWith("Direct"), "Health reports that compatible files played directly without encoding");
        var libraryFormats = Proxy<MediaBrowser.Controller.Library.IMediaSourceManager>.Make((name, args) => name == "GetMediaStreams" && args![0] is Guid
            ? new List<MediaBrowser.Model.Entities.MediaStream> { new() { Index = 0, Type = MediaBrowser.Model.Entities.MediaStreamType.Video, Codec = "h264", PixelFormat = "yuv420p", BitDepth = 8 }, new() { Index = 1, Type = MediaBrowser.Model.Entities.MediaStreamType.Audio, Codec = "ac3", Channels = 6, SampleRate = 48000 } }
            : throw new NotSupportedException(name));
        var known = await new ProgramStreamer(encoder, libraryFormats).Stream(Item(clips[0], 1), Auto, null, _ => Task.CompletedTask, default);
        check(known == "Video copied; audio encoded to AAC stereo", "Jellyfin's stored stream data decides what to encode, so only the incompatible audio is converted");
        if (!OperatingSystem.IsWindows())
        {
            var fifo = Path.Combine(root, "hung mount.ts");
            await Run("mkfifo", [fifo]);
            var hung = new ProgramStreamer(encoder, libraryFormats) { FirstOutputTimeout = TimeSpan.FromSeconds(1) };
            var hungPid = 0;
            hung.ProcessStarted = id => hungPid = id;
            try { await hung.Stream(Item(fifo), Auto, null, _ => Task.CompletedTask, default).WaitAsync(TimeSpan.FromSeconds(10)); throw new Exception("Stalled input was not detected"); }
            catch (PlaybackInterruptedException ex) { check(ex.Stalled && hungPid > 0 && Exited(hungPid), "FFmpeg that stops producing output is detected as stalled and terminated"); }
        }
        var square = Path.Combine(root, "four by three.mp4");
        await Run(ffmpeg, ["-hide_banner","-loglevel","error","-y","-f","lavfi","-i","testsrc2=size=320x240:rate=25","-f","lavfi","-i","sine=frequency=440:sample_rate=48000","-t","2","-c:v","libx264","-preset","ultrafast","-pix_fmt","yuv420p","-c:a","aac","-ac","2",square]);
        using var boxed = new MemoryStream();
        var boxedMethod = await streamer.Stream(Item(square), Auto, null, bytes => { boxed.Write(bytes); return Task.CompletedTask; }, default);
        var boxedPath = Path.Combine(root, "boxed.ts"); await File.WriteAllBytesAsync(boxedPath, boxed.ToArray());
        var boxedVideo = JsonDocument.Parse(await Run(ffprobe, ["-v","error","-select_streams","v:0","-show_entries","stream=width,height,sample_aspect_ratio","-of","json",boxedPath])).RootElement.GetProperty("streams")[0];
        check(boxedVideo.GetProperty("width").GetInt32() == 426 && boxedVideo.GetProperty("height").GetInt32() == 240 && boxedVideo.TryGetProperty("sample_aspect_ratio", out var sar) && sar.GetString() is "1:1" or null && boxedMethod.Contains("426×240"),
            "A 4:3 program on a 16:9 channel is encoded into a 16:9 picture with square pixels and black bars");
        var fourByThree = await streamer.Stream(Item(square, 1), new ChannelDefinition { PictureShape = "4:3" }, null, _ => Task.CompletedTask, default);
        check(fourByThree.StartsWith("Direct"), "A 4:3 program on a 4:3 channel is copied without encoding");
        using var compatibility = new MemoryStream();
        await streamer.Stream(Item(clips[0],1), new ChannelDefinition { AlwaysEncode = true }, null, bytes => { compatibility.Write(bytes); return Task.CompletedTask; }, default);
        var compatiblePath = Path.Combine(root,"compatibility.ts"); await File.WriteAllBytesAsync(compatiblePath,compatibility.ToArray());
        var compatible = JsonDocument.Parse(await Run(ffprobe,["-v","error","-select_streams","v:0","-show_entries","stream=width,height,codec_name","-of","json",compatiblePath]));
        check(compatible.RootElement.GetProperty("streams").EnumerateArray().Any(x=>x.GetProperty("width").GetInt32()==1280&&x.GetProperty("height").GetInt32()==720&&x.GetProperty("codec_name").GetString()=="h264"),"Always encode produces the configured 720p H.264 output in a real FFmpeg run");
        // Captions are burned into the picture, in sync after a seek, from inside the file or a separate file.
        var captionRoot = Path.Combine(root, "captions");
        Directory.CreateDirectory(captionRoot);
        var srt = Path.Combine(captionRoot, "embedded.srt");
        await File.WriteAllTextAsync(srt, "1\n00:00:05,000 --> 00:00:07,500\nHELLO HELLO HELLO\n");
        var captioned = Path.Combine(captionRoot, "captioned show.mkv");
        await Run(ffmpeg, ["-hide_banner","-loglevel","error","-y","-f","lavfi","-i","color=black:size=320x180:rate=25","-f","lavfi","-i","sine=frequency=440:sample_rate=48000","-i",srt,"-t","8",
            "-map","0:v","-map","1:a","-map","2:s","-c:v","libx264","-preset","ultrafast","-g","25","-pix_fmt","yuv420p","-c:a","aac","-ac","2","-c:s","srt","-metadata:s:s:0","language=eng",captioned]);
        async Task<double> Brightest(Airing program, ChannelDefinition channel, ProgramStreamer? with = null)
        {
            using var captionOutput = new MemoryStream();
            await (with ?? streamer).Stream(program, channel, null, bytes => { captionOutput.Write(bytes); return Task.CompletedTask; }, default);
            var path = Path.Combine(captionRoot, Guid.NewGuid().ToString("N") + ".ts");
            await File.WriteAllBytesAsync(path, captionOutput.ToArray());
            var stats = await Run(ffprobe, ["-v","error","-f","lavfi","-i","movie=" + path.Replace("\\", "/") + ",signalstats","-show_entries","frame_tags=lavfi.signalstats.YMAX","-of","csv=p=0","-read_intervals","%+1"]);
            return stats.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => double.Parse(x.Trim().TrimEnd(','), System.Globalization.CultureInfo.InvariantCulture)).Max();
        }
        var captionClock = DateTimeOffset.UtcNow;
        var secondHalf = new Airing("captions", Guid.NewGuid(), "Part 2", captioned, captionClock, captionClock.AddSeconds(3), false, Offset: 5);
        var withCaptions = await Brightest(secondHalf, new ChannelDefinition { Captions = "on", CaptionLanguage = "English" });
        var withoutCaptions = await Brightest(secondHalf, new ChannelDefinition());
        var forcedOnly = await Brightest(secondHalf, new ChannelDefinition { Captions = "forced" });
        check(withCaptions > 150 && withoutCaptions < 40 && forcedOnly < 40,
            "Captions from inside the file are burned in, in sync when a program starts part-way through, and forced-only mode skips ordinary captions");
        var oddName = Path.Combine(captionRoot, "it's [a], test: 1.srt");
        await File.WriteAllTextAsync(oddName, "1\n00:00:00,000 --> 00:00:09,000\nSIDE FILE\n");
        var sideFile = Proxy<MediaBrowser.Controller.Library.IMediaSourceManager>.Make((name, args) => name == "GetMediaStreams" && args![0] is Guid
            ? new List<MediaBrowser.Model.Entities.MediaStream> { new() { Index = 0, Type = MediaBrowser.Model.Entities.MediaStreamType.Video, Codec = "h264", PixelFormat = "yuv420p", BitDepth = 8, Width = 320, Height = 180, AspectRatio = "16:9" },
                new() { Index = 1, Type = MediaBrowser.Model.Entities.MediaStreamType.Audio, Codec = "aac", Channels = 2, SampleRate = 48000 },
                new() { Index = 3, Type = MediaBrowser.Model.Entities.MediaStreamType.Subtitle, Codec = "subrip", Language = "spa", IsExternal = true, Path = oddName } }
            : throw new NotSupportedException(name));
        var plain = Path.Combine(captionRoot, "plain.mp4");
        await Run(ffmpeg, ["-hide_banner","-loglevel","error","-y","-f","lavfi","-i","color=black:size=320x180:rate=25","-f","lavfi","-i","sine=frequency=440:sample_rate=48000","-t","4","-c:v","libx264","-preset","ultrafast","-pix_fmt","yuv420p","-c:a","aac","-ac","2",plain]);
        var sideStreamer = new ProgramStreamer(encoder, sideFile);
        var spanish = await Brightest(new Airing("captions", Guid.NewGuid(), "Plain", plain, captionClock, captionClock.AddSeconds(2), false, Offset: 1), new ChannelDefinition { Captions = "on", CaptionLanguage = "es" }, sideStreamer);
        var english = await Brightest(new Airing("captions", Guid.NewGuid(), "Plain", plain, captionClock, captionClock.AddSeconds(2), false, Offset: 1), new ChannelDefinition { Captions = "on", CaptionLanguage = "eng" }, sideStreamer);
        check(spanish > 150 && english < 40, "A separate caption file is burned in even when its name has quotes, brackets, commas and colons, and other languages are left out");
        // The preferred audio language plays that track: here the default Spanish track is silent and English is a tone.
        var bilingual = Path.Combine(captionRoot, "bilingual.mkv");
        await Run(ffmpeg, ["-hide_banner","-loglevel","error","-y","-f","lavfi","-i","color=black:size=320x180:rate=25","-f","lavfi","-i","anullsrc=channel_layout=stereo:sample_rate=48000","-f","lavfi","-i","sine=frequency=440:sample_rate=48000",
            "-t","3","-map","0:v","-map","1:a","-map","2:a","-c:v","libx264","-preset","ultrafast","-pix_fmt","yuv420p","-c:a","aac","-ac","2",
            "-metadata:s:a:0","language=spa","-metadata:s:a:1","language=eng","-disposition:a:0","default","-disposition:a:1","0",bilingual]);
        async Task<double> Loudness(ChannelDefinition channel)
        {
            using var sound = new MemoryStream();
            var started = DateTimeOffset.UtcNow;
            await streamer.Stream(new Airing("audio", Guid.NewGuid(), "Bilingual", bilingual, started, started.AddSeconds(2), false), channel, null, bytes => { sound.Write(bytes); return Task.CompletedTask; }, default);
            var path = Path.Combine(captionRoot, Guid.NewGuid().ToString("N") + ".ts");
            await File.WriteAllBytesAsync(path, sound.ToArray());
            var levels = await Run(ffprobe, ["-v","error","-f","lavfi","-i","amovie=" + path.Replace("\\", "/") + ",astats=metadata=1:reset=0","-show_entries","frame_tags=lavfi.astats.Overall.RMS_level","-of","csv=p=0"]);
            var last = levels.Split('\n', StringSplitOptions.RemoveEmptyEntries).Last().Trim().TrimEnd(',');
            return last.StartsWith("-inf", StringComparison.OrdinalIgnoreCase) ? double.NegativeInfinity : double.Parse(last, System.Globalization.CultureInfo.InvariantCulture);
        }
        var englishAudio = await Loudness(new ChannelDefinition { AudioLanguage = "English" });
        var defaultAudio = await Loudness(new ChannelDefinition());
        check(englishAudio > -40 && defaultAudio < -80, "The preferred audio language plays that track instead of the file's default");
        // A show resuming after a break starts on its keyframe, so timestamps never run backwards into the break.
        var sparse = Path.Combine(root, "sparse keyframes.mp4");
        await Run(ffmpeg, ["-hide_banner","-loglevel","error","-y","-f","lavfi","-i","testsrc2=size=320x180:rate=25","-f","lavfi","-i","sine=frequency=440:sample_rate=48000","-t","10","-c:v","libx264","-preset","ultrafast","-g","100","-pix_fmt","yuv420p","-c:a","aac","-ac","2",sparse]);
        var resumeAt = DateTimeOffset.UtcNow.AddSeconds(1);
        using var resumed = new MemoryStream();
        await streamer.Stream(new Airing("transport", Guid.NewGuid(), "Ad", clips[1], resumeAt.AddSeconds(-2), resumeAt, true), Auto, null, bytes => { resumed.Write(bytes); return Task.CompletedTask; }, default);
        await streamer.Stream(new Airing("transport", Guid.NewGuid(), "Part 2", sparse, resumeAt, resumeAt.AddSeconds(3), false, Offset: 5), Auto, null, bytes => { resumed.Write(bytes); return Task.CompletedTask; }, default);
        var resumedPath = Path.Combine(root, "resumed.ts"); await File.WriteAllBytesAsync(resumedPath, resumed.ToArray());
        var dts = (await Run(ffprobe, ["-v","error","-select_streams","v:0","-show_entries","packet=dts_time","-of","csv=p=0",resumedPath]))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => double.Parse(x.Trim().TrimEnd(','), System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        var wrap = Math.Pow(2, 33) / 90000;
        var expectedStart = (resumeAt.ToUnixTimeMilliseconds() / 1000.0 + 0.7) % wrap;
        check(dts.Zip(dts.Skip(1)).All(x => x.Second >= x.First) && dts.Length >= 90 && Math.Abs(dts.SkipWhile(x => x < expectedStart - 0.1).First() - expectedStart) < 0.1,
            "A copied show resuming after a break starts on a keyframe at its scheduled time, so timestamps never run backwards");
        // End to end: the real scheduler, broadcast and FFmpeg run each program ahead of its time, once, in order.
        var shows = Path.Combine(root, "lead");
        Directory.CreateDirectory(shows);
        var parts = new List<MediaBrowser.Controller.Entities.Video>();
        for (var n = 0; n < 5; n++)
        {
            var file = Path.Combine(shows, $"show{n}.mp4");
            await Run(ffmpeg, ["-hide_banner","-loglevel","error","-y","-f","lavfi","-i","testsrc2=size=320x180:rate=25","-f","lavfi","-i","sine=frequency=440:sample_rate=48000","-t","4","-c:v","libx264","-preset","ultrafast","-g","25","-pix_fmt","yuv420p","-c:a","aac","-ac","2",file]);
            parts.Add(new() { Id = Guid.NewGuid(), Name = "Show " + n, Path = file, RunTimeTicks = TimeSpan.FromSeconds(4).Ticks });
        }
        var leadCatalog = new Catalog(Proxy<MediaBrowser.Controller.Library.ILibraryManager>.Make((name, args) => name switch
        {
            "GetItemById" => parts.FirstOrDefault(x => x.Id == (Guid)args![0]!),
            "GetVirtualFolders" => new List<MediaBrowser.Model.Entities.VirtualFolderInfo>(),
            _ => throw new NotSupportedException(name)
        }));
        var savedConfiguration = Plugin.Instance.Configuration;
        Plugin.Instance.UpdateConfiguration(new Configuration { GuideDays = 1, Channels = [new ChannelDefinition { Id = "lead", Name = "Lead", Source = new Pool { ItemIds = parts.Select(x => x.Id.ToString()).ToArray(), Shuffle = false } }] });
        try
        {
            var leadScheduler = new Scheduler(leadCatalog, Proxy<MediaBrowser.Common.Configuration.IApplicationPaths>.Make((_, _) => shows), Microsoft.Extensions.Logging.Abstractions.NullLogger<Scheduler>.Instance);
            await leadScheduler.Refresh(default);
            var recorder = new RecordingStreamer(streamer);
            using var live = new Broadcasts(leadScheduler, recorder, Microsoft.Extensions.Logging.Abstractions.NullLogger<Broadcasts>.Instance);
            using var stop = new CancellationTokenSource();
            var watching = live.Watch("lead", new MemoryStream(), stop.Token);
            await Task.Delay(TimeSpan.FromSeconds(11));
            stop.Cancel();
            try { await watching.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }
            var played = recorder.Played.ToArray();
            var guide = leadScheduler.GuideFor("lead");
            var order = guide.SkipWhile(x => x.Start != played[0].Program.Start).Take(played.Length).Select(x => x.Start);
            check(played.Length >= 3 && played.Select(x => x.Program.Start).SequenceEqual(order) && played.Skip(1).All(x => x.Started < x.Program.Start - TimeSpan.FromSeconds(1)),
                "Each program starts seconds ahead of its scheduled time, right after the one before, and plays once in order");
        }
        finally { Plugin.Instance.UpdateConfiguration(savedConfiguration); }
        // End to end with breaks: real clips, matched to each show, all stream through FFmpeg in order.
        var breakRoot = Path.Combine(root, "breaks");
        Directory.CreateDirectory(breakRoot);
        async Task<string> Make(string file, double seconds)
        {
            var path = Path.Combine(breakRoot, file);
            await Run(ffmpeg, ["-hide_banner","-loglevel","error","-y","-f","lavfi","-i","testsrc2=size=320x180:rate=25","-f","lavfi","-i","sine=frequency=440:sample_rate=48000","-t",seconds.ToString(System.Globalization.CultureInfo.InvariantCulture),"-c:v","libx264","-preset","ultrafast","-g","25","-pix_fmt","yuv420p","-c:a","aac","-ac","2",path]);
            return path;
        }
        MediaBrowser.Controller.Entities.BaseItem Clip(string name, string path, double seconds) => new MediaBrowser.Controller.Entities.Video { Id = Guid.NewGuid(), Name = name, Path = path, RunTimeTicks = TimeSpan.FromSeconds(seconds).Ticks };
        var rugrats = new MediaBrowser.Controller.Entities.Movies.Movie { Id = Guid.NewGuid(), Name = "Rugrats", Path = await Make("Rugrats.mp4", 4), RunTimeTicks = TimeSpan.FromSeconds(4).Ticks, ProductionYear = 1993, Studios = ["Nickelodeon"] };
        var aqua = new MediaBrowser.Controller.Entities.Movies.Movie { Id = Guid.NewGuid(), Name = "Aqua Teen", Path = await Make("Aqua Teen.mp4", 4), RunTimeTicks = TimeSpan.FromSeconds(4).Ticks, ProductionYear = 2003, Studios = ["Williams Street"] };
        var adClips = new[] { Clip("Nick Gak", await Make("Nick Gak 1995.mp4", 2), 2), Clip("Swim promo", await Make("Adult Swim promo 2004.mp4", 2), 2), Clip("Cola", await Make("Cola.mp4", 2), 2) };
        var bumperClips = new[] { Clip("Rugrats bumper", await Make("Rugrats bumper.mp4", 1), 1), Clip("Swim bump", await Make("Adult Swim bump.mp4", 1), 1) };
        var everything = adClips.Concat(bumperClips).Append(rugrats).Append(aqua).ToArray();
        var breakCatalog = new Catalog(Proxy<MediaBrowser.Controller.Library.ILibraryManager>.Make((name, args) => name switch
        {
            "GetItemById" => everything.FirstOrDefault(x => x.Id == (Guid)args![0]!),
            "GetVirtualFolders" => new List<MediaBrowser.Model.Entities.VirtualFolderInfo>(),
            _ => throw new NotSupportedException(name)
        }));
        var keptBreakConfig = Plugin.Instance.Configuration;
        Plugin.Instance.UpdateConfiguration(new Configuration { GuideDays = 1, Channels = [new ChannelDefinition { Id = "e2e", Name = "Breaks", CommercialCount = 1,
            Source = new Pool { ItemIds = [rugrats.Id.ToString(), aqua.Id.ToString()], Shuffle = false },
            Commercials = new Pool { ItemIds = adClips.Select(x => x.Id.ToString()).ToArray() }, Bumpers = new Pool { ItemIds = bumperClips.Select(x => x.Id.ToString()).ToArray() } }] });
        try
        {
            var breakScheduler = new Scheduler(breakCatalog, Proxy<MediaBrowser.Common.Configuration.IApplicationPaths>.Make((_, _) => breakRoot), Microsoft.Extensions.Logging.Abstractions.NullLogger<Scheduler>.Instance);
            await breakScheduler.Refresh(default);
            var recorder = new RecordingStreamer(streamer);
            using var live = new Broadcasts(breakScheduler, recorder, Microsoft.Extensions.Logging.Abstractions.NullLogger<Broadcasts>.Instance);
            using var stop = new CancellationTokenSource();
            var output = new MemoryStream();
            var watching = live.Watch("e2e", output, stop.Token);
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (recorder.Finished.Count < 9 && DateTime.UtcNow < deadline) await Task.Delay(100);
            var status = live.StatusFor("e2e");
            stop.Cancel();
            try { await watching.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }
            var titles = recorder.Finished.Take(9).Select(x => x.Program.Title).ToArray();
            var start = Array.IndexOf(titles, "Rugrats");
            var cycle = titles.Skip(start).Take(8).ToArray();
            check(cycle.SequenceEqual(["Rugrats", "Rugrats bumper", "Nick Gak", "Rugrats bumper", "Aqua Teen", "Swim bump", "Swim promo", "Swim bump"]) && recorder.Finished.Take(9).All(x => x.Bytes > 0) && status.LastProblem is null,
                "With real FFmpeg, every break plays bumper, commercial, bumper, each show gets its own bumpers and its network's commercials, and every clip streams video");
        }
        finally { Plugin.Instance.UpdateConfiguration(keptBreakConfig); }
        // Without a lead, so the clip still plays in real time when it is canceled.
        ProgramStreamer.Lead = TimeSpan.Zero;
        var pid = 0;
        streamer.ProcessStarted = id => pid = id;
        using (var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(250)))
        {
            try { await streamer.Stream(Item(clips[0]),Auto, null,_=>Task.CompletedTask,stop.Token).WaitAsync(TimeSpan.FromSeconds(8)); throw new Exception("Canceled FFmpeg did not cancel"); }
            catch(OperationCanceledException) { }
        }
        check(pid > 0 && Exited(pid),"Canceling an actual stream terminates its FFmpeg process without leaving an orphan");
        pid=0;
        try { await streamer.Stream(Item(clips[0]),Auto, null,_=>Task.FromException(new IOException("Disconnected test output")),default).WaitAsync(TimeSpan.FromSeconds(8)); throw new Exception("Output failure was ignored"); }
        catch(IOException) { }
        check(pid>0&&Exited(pid),"A viewer/output failure also terminates its actual FFmpeg process");
        ProgramStreamer.Lead = TimeSpan.FromSeconds(3);
        var invalid=Path.Combine(root,"broken.mp4"); await File.WriteAllTextAsync(invalid,"not a media file");
        try { await streamer.Stream(Item(invalid),Auto, null,_=>Task.CompletedTask,default).WaitAsync(TimeSpan.FromSeconds(8)); throw new Exception("Unreadable media was accepted"); }
        catch(IOException) { check(true,"Invalid media is reported as a recoverable item failure instead of a successful empty stream"); }
    }

    private static bool Exited(int pid) { try { using var process=Process.GetProcessById(pid);return process.HasExited; } catch(ArgumentException){return true;} }
    private static async Task<string> Run(string executable,string[] args)
    {
        var command=new ProcessStartInfo(executable){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true};
        foreach(var arg in args)command.ArgumentList.Add(arg);
        using var process=Process.Start(command)!;
        var output=process.StandardOutput.ReadToEndAsync();var errors=process.StandardError.ReadToEndAsync();
        try{await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));}
        catch{try{process.Kill(true);}catch(InvalidOperationException){}throw;}
        var text=await output;var error=await errors;
        if(process.ExitCode!=0)throw new Exception("Media test tool failed: "+error);
        return text;
    }
}

sealed class RecordingStreamer(IProgramStreamer inner) : IProgramStreamer
{
    public System.Collections.Concurrent.ConcurrentQueue<(Airing Program, DateTimeOffset Started)> Played { get; } = new();
    public System.Collections.Concurrent.ConcurrentQueue<(Airing Program, long Bytes)> Finished { get; } = new();
    public async Task<string> Stream(Airing program, ChannelDefinition channel, double? shape, Func<byte[], Task> output, CancellationToken ct, Action<string, bool>? report = null)
    {
        Played.Enqueue((program, DateTimeOffset.UtcNow));
        long bytes = 0;
        var result = await inner.Stream(program, channel, shape, chunk => { bytes += chunk.Length; return output(chunk); }, ct, report);
        Finished.Enqueue((program, bytes));
        return result;
    }
    public Task<double?> Shape(Airing program, CancellationToken ct) => inner.Shape(program, ct);
}
