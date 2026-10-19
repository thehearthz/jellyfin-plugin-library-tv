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
            await Run(ffmpeg, ["-hide_banner","-loglevel","error","-y","-f","lavfi","-i","testsrc2=size=320x180:rate=25","-f","lavfi","-i","sine=frequency=440:sample_rate=48000","-t","2","-c:v","libx264","-preset","ultrafast","-g","25","-pix_fmt","yuv420p","-c:a","aac",clip]);
        var encoder = Proxy<IMediaEncoder>.Make((name, _) => name == "get_EncoderPath" ? ffmpeg : throw new NotSupportedException(name));
        var streamer = new ProgramStreamer(encoder);
        Airing Item(string clip, int seconds = 2) { var now = DateTimeOffset.UtcNow; return new("transport", Guid.NewGuid(), "Test", clip, now, now.AddSeconds(seconds), false); }
        var command = ProgramStreamer.Command(ffmpeg, Item(clips[0]), false, DateTimeOffset.UtcNow);
        check(!command.UseShellExecute && command.ArgumentList.Contains(clips[0]) && command.ArgumentList.Contains("copy") && !command.ArgumentList.Contains("libx264"), "Default transport copies codecs and passes paths as individual arguments without shell interpretation");
        using var transport = new MemoryStream();
        foreach (var clip in clips) await streamer.Stream(Item(clip), false, bytes => { transport.Write(bytes); return Task.CompletedTask; }, default);
        var transportPath = Path.Combine(root, "transition.ts"); await File.WriteAllBytesAsync(transportPath, transport.ToArray());
        var probe = JsonDocument.Parse(await Run(ffprobe, ["-v","error","-show_entries","stream=codec_name,codec_type","-of","json",transportPath]));
        var codecs = probe.RootElement.GetProperty("streams").EnumerateArray().Select(x => x.GetProperty("codec_name").GetString()).ToArray();
        check(codecs.Contains("h264") && codecs.Contains("aac"), "Real episode/commercial stream-copy transitions retain H.264 video and AAC audio");
        var frames = await Run(ffprobe, ["-v","error","-select_streams","v:0","-count_frames","-show_entries","stream=nb_read_frames","-of","csv=p=0",transportPath]);
        check(frames.Split(['\r','\n'], StringSplitOptions.RemoveEmptyEntries).Any(x => int.TryParse(x.Trim().TrimEnd(','), out var count) && count > 60), "The joined transport decodes video frames from both sides of a program transition");
        using var compatibility = new MemoryStream();
        await streamer.Stream(Item(clips[0],1), true, bytes => { compatibility.Write(bytes); return Task.CompletedTask; }, default);
        var compatiblePath = Path.Combine(root,"compatibility.ts"); await File.WriteAllBytesAsync(compatiblePath,compatibility.ToArray());
        var compatible = JsonDocument.Parse(await Run(ffprobe,["-v","error","-select_streams","v:0","-show_entries","stream=width,height,codec_name","-of","json",compatiblePath]));
        check(compatible.RootElement.GetProperty("streams").EnumerateArray().Any(x=>x.GetProperty("width").GetInt32()==1280&&x.GetProperty("height").GetInt32()==720&&x.GetProperty("codec_name").GetString()=="h264"),"Compatibility mode produces the configured 720p H.264 output in a real FFmpeg run");
        var pid = 0;
        streamer.ProcessStarted = id => pid = id;
        using (var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(250)))
        {
            try { await streamer.Stream(Item(clips[0]),false,_=>Task.CompletedTask,stop.Token).WaitAsync(TimeSpan.FromSeconds(8)); throw new Exception("Canceled FFmpeg did not cancel"); }
            catch(OperationCanceledException) { }
        }
        check(pid > 0 && Exited(pid),"Canceling an actual stream terminates its FFmpeg process without leaving an orphan");
        pid=0;
        try { await streamer.Stream(Item(clips[0]),false,_=>Task.FromException(new IOException("Disconnected test output")),default).WaitAsync(TimeSpan.FromSeconds(8)); throw new Exception("Output failure was ignored"); }
        catch(IOException) { }
        check(pid>0&&Exited(pid),"A viewer/output failure also terminates its actual FFmpeg process");
        var invalid=Path.Combine(root,"broken.mp4"); await File.WriteAllTextAsync(invalid,"not a media file");
        try { await streamer.Stream(Item(invalid),false,_=>Task.CompletedTask,default).WaitAsync(TimeSpan.FromSeconds(8)); throw new Exception("Unreadable media was accepted"); }
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
