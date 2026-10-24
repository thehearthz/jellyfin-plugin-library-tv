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
        var command = ProgramStreamer.Command(ffmpeg, Item(clips[0]), StreamPlan.Copy, DateTimeOffset.UtcNow);
        check(!command.UseShellExecute && command.ArgumentList.Contains(clips[0]) && command.ArgumentList.Contains("copy") && !command.ArgumentList.Contains("libx264"), "Compatible files are copied, and paths are passed as individual arguments without shell interpretation");
        using var transport = new MemoryStream();
        foreach (var clip in clips) await streamer.Stream(Item(clip), Auto, bytes => { transport.Write(bytes); return Task.CompletedTask; }, default);
        var transportPath = Path.Combine(root, "transition.ts"); await File.WriteAllBytesAsync(transportPath, transport.ToArray());
        var probe = JsonDocument.Parse(await Run(ffprobe, ["-v","error","-show_entries","stream=codec_name,codec_type","-of","json",transportPath]));
        var codecs = probe.RootElement.GetProperty("streams").EnumerateArray().Select(x => x.GetProperty("codec_name").GetString()).ToArray();
        check(codecs.Contains("h264") && codecs.Contains("aac"), "Real episode/commercial stream-copy transitions retain H.264 video and AAC audio");
        var frames = await Run(ffprobe, ["-v","error","-select_streams","v:0","-count_frames","-show_entries","stream=nb_read_frames","-of","csv=p=0",transportPath]);
        check(frames.Split(['\r','\n'], StringSplitOptions.RemoveEmptyEntries).Any(x => int.TryParse(x.Trim().TrimEnd(','), out var count) && count > 60), "The joined transport decodes video frames from both sides of a program transition");
        var silent = Path.Combine(root, "silent mpeg4.avi");
        await Run(ffmpeg, ["-hide_banner","-loglevel","error","-y","-f","lavfi","-i","testsrc2=size=320x180:rate=25","-t","2","-c:v","mpeg4","-an",silent]);
        using var converted = new MemoryStream();
        var convertedMethod = await streamer.Stream(Item(silent), Auto, bytes => { converted.Write(bytes); return Task.CompletedTask; }, default);
        var convertedPath = Path.Combine(root, "converted.ts"); await File.WriteAllBytesAsync(convertedPath, converted.ToArray());
        var convertedStreams = JsonDocument.Parse(await Run(ffprobe, ["-v","error","-show_entries","stream=codec_name,codec_type,channels,width","-of","json",convertedPath])).RootElement.GetProperty("streams").EnumerateArray().ToArray();
        check(convertedStreams.Any(x => x.GetProperty("codec_name").GetString() == "h264" && x.GetProperty("width").GetInt32() == 320) && convertedStreams.Any(x => x.GetProperty("codec_name").GetString() == "aac" && x.GetProperty("channels").GetInt32() == 2) && convertedMethod.Contains("encoded"),
            "Files in other formats are encoded to H.264 at their own size, and a silent file gets a stereo AAC track, found with FFprobe when Jellyfin has no stream data");
        using var mixed = new MemoryStream();
        foreach (var clip in new[] { clips[0], silent }) await streamer.Stream(Item(clip), Auto, bytes => { mixed.Write(bytes); return Task.CompletedTask; }, default);
        var mixedPath = Path.Combine(root, "mixed.ts"); await File.WriteAllBytesAsync(mixedPath, mixed.ToArray());
        var mixedFrames = await Run(ffprobe, ["-v","error","-select_streams","v:0","-count_frames","-show_entries","stream=nb_read_frames","-of","csv=p=0",mixedPath]);
        var mixedAudio = await Run(ffprobe, ["-v","error","-select_streams","a:0","-count_packets","-show_entries","stream=codec_name,nb_read_packets","-of","csv=p=0",mixedPath]);
        check(mixedFrames.Split(['\r','\n'], StringSplitOptions.RemoveEmptyEntries).Any(x => int.TryParse(x.Trim().TrimEnd(','), out var count) && count > 80) && mixedAudio.Contains("aac"),
            "A copied program followed by an encoded one decodes as one continuous H.264/AAC stream");
        var directMethod = await streamer.Stream(Item(clips[1], 1), Auto, _ => Task.CompletedTask, default);
        check(directMethod.StartsWith("Direct"), "Health reports that compatible files played directly without encoding");
        var libraryFormats = Proxy<MediaBrowser.Controller.Library.IMediaSourceManager>.Make((name, args) => name == "GetMediaStreams" && args![0] is Guid
            ? new List<MediaBrowser.Model.Entities.MediaStream> { new() { Index = 0, Type = MediaBrowser.Model.Entities.MediaStreamType.Video, Codec = "h264", PixelFormat = "yuv420p", BitDepth = 8 }, new() { Index = 1, Type = MediaBrowser.Model.Entities.MediaStreamType.Audio, Codec = "ac3", Channels = 6, SampleRate = 48000 } }
            : throw new NotSupportedException(name));
        var known = await new ProgramStreamer(encoder, libraryFormats).Stream(Item(clips[0], 1), Auto, _ => Task.CompletedTask, default);
        check(known == "Video copied; audio encoded to AAC stereo", "Jellyfin's stored stream data decides what to encode, so only the incompatible audio is converted");
        if (!OperatingSystem.IsWindows())
        {
            var fifo = Path.Combine(root, "hung mount.ts");
            await Run("mkfifo", [fifo]);
            var hung = new ProgramStreamer(encoder, libraryFormats) { FirstOutputTimeout = TimeSpan.FromSeconds(1) };
            var hungPid = 0;
            hung.ProcessStarted = id => hungPid = id;
            try { await hung.Stream(Item(fifo), Auto, _ => Task.CompletedTask, default).WaitAsync(TimeSpan.FromSeconds(10)); throw new Exception("Stalled input was not detected"); }
            catch (PlaybackInterruptedException ex) { check(ex.Stalled && hungPid > 0 && Exited(hungPid), "FFmpeg that stops producing output is detected as stalled and terminated"); }
        }
        var square = Path.Combine(root, "four by three.mp4");
        await Run(ffmpeg, ["-hide_banner","-loglevel","error","-y","-f","lavfi","-i","testsrc2=size=320x240:rate=25","-f","lavfi","-i","sine=frequency=440:sample_rate=48000","-t","2","-c:v","libx264","-preset","ultrafast","-pix_fmt","yuv420p","-c:a","aac","-ac","2",square]);
        using var boxed = new MemoryStream();
        var boxedMethod = await streamer.Stream(Item(square), Auto, bytes => { boxed.Write(bytes); return Task.CompletedTask; }, default);
        var boxedPath = Path.Combine(root, "boxed.ts"); await File.WriteAllBytesAsync(boxedPath, boxed.ToArray());
        var boxedVideo = JsonDocument.Parse(await Run(ffprobe, ["-v","error","-select_streams","v:0","-show_entries","stream=width,height,sample_aspect_ratio","-of","json",boxedPath])).RootElement.GetProperty("streams")[0];
        check(boxedVideo.GetProperty("width").GetInt32() == 426 && boxedVideo.GetProperty("height").GetInt32() == 240 && boxedVideo.TryGetProperty("sample_aspect_ratio", out var sar) && sar.GetString() is "1:1" or null && boxedMethod.Contains("426×240"),
            "A 4:3 program on a 16:9 channel is encoded into a 16:9 picture with square pixels and black bars");
        var fourByThree = await streamer.Stream(Item(square, 1), new ChannelDefinition { PictureShape = "4:3" }, _ => Task.CompletedTask, default);
        check(fourByThree.StartsWith("Direct"), "A 4:3 program on a 4:3 channel is copied without encoding");
        using var compatibility = new MemoryStream();
        await streamer.Stream(Item(clips[0],1), new ChannelDefinition { AlwaysEncode = true }, bytes => { compatibility.Write(bytes); return Task.CompletedTask; }, default);
        var compatiblePath = Path.Combine(root,"compatibility.ts"); await File.WriteAllBytesAsync(compatiblePath,compatibility.ToArray());
        var compatible = JsonDocument.Parse(await Run(ffprobe,["-v","error","-select_streams","v:0","-show_entries","stream=width,height,codec_name","-of","json",compatiblePath]));
        check(compatible.RootElement.GetProperty("streams").EnumerateArray().Any(x=>x.GetProperty("width").GetInt32()==1280&&x.GetProperty("height").GetInt32()==720&&x.GetProperty("codec_name").GetString()=="h264"),"Always encode produces the configured 720p H.264 output in a real FFmpeg run");
        var pid = 0;
        streamer.ProcessStarted = id => pid = id;
        using (var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(250)))
        {
            try { await streamer.Stream(Item(clips[0]),Auto,_=>Task.CompletedTask,stop.Token).WaitAsync(TimeSpan.FromSeconds(8)); throw new Exception("Canceled FFmpeg did not cancel"); }
            catch(OperationCanceledException) { }
        }
        check(pid > 0 && Exited(pid),"Canceling an actual stream terminates its FFmpeg process without leaving an orphan");
        pid=0;
        try { await streamer.Stream(Item(clips[0]),Auto,_=>Task.FromException(new IOException("Disconnected test output")),default).WaitAsync(TimeSpan.FromSeconds(8)); throw new Exception("Output failure was ignored"); }
        catch(IOException) { }
        check(pid>0&&Exited(pid),"A viewer/output failure also terminates its actual FFmpeg process");
        var invalid=Path.Combine(root,"broken.mp4"); await File.WriteAllTextAsync(invalid,"not a media file");
        try { await streamer.Stream(Item(invalid),Auto,_=>Task.CompletedTask,default).WaitAsync(TimeSpan.FromSeconds(8)); throw new Exception("Unreadable media was accepted"); }
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
