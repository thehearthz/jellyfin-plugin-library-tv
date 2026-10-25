using Jellyfin.Plugin.LibraryTV;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;

public static class PlaybackTests
{
    public static async Task Run(Action<bool, string> check, string directory)
    {
        var original = Plugin.Instance.Configuration;
        var root = Path.Combine(directory, "playback");
        Directory.CreateDirectory(root);
        try
        {
            var items = Enumerable.Range(1, 3).Select(n => new Video { Id = Guid.NewGuid(), Name = "Clip " + n, Path = Path.Combine(root, "clip" + n + ".mp4"), RunTimeTicks = TimeSpan.FromHours(1).Ticks }).ToArray();
            foreach (var item in items) await File.WriteAllTextAsync(item.Path, "test metadata fixture");
            var catalog = new Catalog(Proxy<ILibraryManager>.Make((name, args) => name switch
            {
                "GetItemById" => items.FirstOrDefault(x => x.Id == (Guid)args![0]!),
                "GetVirtualFolders" => new List<MediaBrowser.Model.Entities.VirtualFolderInfo>(),
                _ => throw new NotSupportedException(name)
            }));
            Plugin.Instance.UpdateConfiguration(new Configuration { GuideDays = 1, Channels = [new ChannelDefinition { Id = "playback", Name = "Playback", Source = new Pool { ItemIds = items.Select(x => x.Id.ToString()).ToArray(), Shuffle = false } }] });
            var number = 0;
            async Task<Scheduler> Fresh()
            {
                var folder = Path.Combine(root, "schedule-" + ++number);
                Directory.CreateDirectory(folder);
                var paths = Proxy<IApplicationPaths>.Make((_, _) => folder);
                var scheduler = new Scheduler(catalog, paths, NullLogger<Scheduler>.Instance);
                await scheduler.Refresh(default);
                return scheduler;
            }
            var scheduler = await Fresh();
            var initial = scheduler.Current("playback", DateTimeOffset.UtcNow)!;
            var nextBefore = scheduler.GuideFor("playback")[1];
            var rebuiltFrom = await scheduler.RebuildFuture("playback", default);
            check(rebuiltFrom == initial.End && scheduler.Current("playback", DateTimeOffset.UtcNow)!.ItemId == initial.ItemId && scheduler.GuideFor("playback")[1].ItemId == nextBefore.ItemId, "The scheduler rebuild endpoint preserves the live program and next episode while applying the new tail");
            var goodConfiguration = Plugin.Instance.Configuration;
            var diskBeforeFailure = await File.ReadAllTextAsync(scheduler.StatePath);
            var guideBeforeFailure = scheduler.GuideFor("playback").Select(x => (x.ItemId,x.Start,x.End)).ToArray();
            Plugin.Instance.UpdateConfiguration(new Configuration { GuideDays=1, Channels=[goodConfiguration.Channels[0],new ChannelDefinition { Id="broken-scan",Number="2",Source=new Pool {FolderIds=[Guid.NewGuid().ToString()]} }] });
            try { await scheduler.RebuildFuture("playback",default); throw new Exception("Failed scan was accepted"); }
            catch(NotSupportedException) { }
            check(diskBeforeFailure == await File.ReadAllTextAsync(scheduler.StatePath) && guideBeforeFailure.SequenceEqual(scheduler.GuideFor("playback").Select(x=>(x.ItemId,x.Start,x.End))),"A later channel scan failure leaves both the published schedule and saved episode state unchanged");
            Plugin.Instance.UpdateConfiguration(goodConfiguration);
            var began = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var shared = new DelegateStreamer(async (_, _, write, ct) =>
            {
                began.TrySetResult();
                try { await release.Task.WaitAsync(ct); await write(new byte[188]); await Task.Delay(Timeout.Infinite, ct); }
                finally { exited.TrySetResult(); }
            });
            using (var live = new Broadcasts(scheduler, shared, NullLogger<Broadcasts>.Instance))
            using (var firstStop = new CancellationTokenSource())
            using (var secondStop = new CancellationTokenSource())
            {
                var firstOutput = new SignalStream();
                var secondOutput = new SignalStream();
                var first = live.Watch("playback", firstOutput, firstStop.Token);
                await began.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var second = live.Watch("playback", secondOutput, secondStop.Token);
                release.TrySetResult();
                await Task.WhenAll(firstOutput.Received.Task, secondOutput.Received.Task).WaitAsync(TimeSpan.FromSeconds(5));
                check(shared.Calls == 1 && live.StatusFor("playback").Viewers == 2, "Two viewers of the same channel share one media worker");
                firstStop.Cancel(); await IgnoreCancellation(first);
                check(live.StatusFor("playback").Viewers == 1 && !exited.Task.IsCompleted, "Disconnecting one viewer keeps the shared channel alive for the other viewer");
                secondStop.Cancel(); await IgnoreCancellation(second);
                await exited.Task.WaitAsync(TimeSpan.FromSeconds(5));
                check(live.StatusFor("playback").Viewers == 0, "The final viewer disconnect stops the media worker with no idle playback");
            }
            var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finishStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            DelegateStreamer restart = null!;
            restart = new DelegateStreamer(async (_, _, write, ct) =>
            {
                if (restart.Calls > 1) { await write(new byte[188]); await Task.Delay(Timeout.Infinite, ct); }
                // The first worker drops its slow viewer, then takes time to exit like a killed FFmpeg process.
                try { while (true) await write(new byte[188]); }
                finally { stopping.TrySetResult(); await finishStop.Task; }
            });
            using (var live = new Broadcasts(scheduler, restart, NullLogger<Broadcasts>.Instance))
            using (var slowStop = new CancellationTokenSource())
            using (var lateStop = new CancellationTokenSource())
            {
                var slow = live.Watch("playback", new StalledStream(), slowStop.Token);
                await stopping.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var lateOutput = new SignalStream();
                var late = live.Watch("playback", lateOutput, lateStop.Token);
                var received = await Task.WhenAny(lateOutput.Received.Task, late, Task.Delay(TimeSpan.FromSeconds(5))) == lateOutput.Received.Task;
                finishStop.TrySetResult();
                check(received && restart.Calls == 2, "A viewer joining while the previous worker shuts down starts a fresh worker instead of receiving an empty stream");
                slowStop.Cancel(); lateStop.Cancel();
                await IgnoreCancellation(slow); await IgnoreCancellation(late);
            }
            scheduler = await Fresh();
            check(scheduler.LastRefreshMilliseconds >= 0 && scheduler.WarningsFor("playback").Length == 0, "Refresh timing and source diagnostics are captured during normal refresh without another library scan");
            var failure = new DelegateStreamer(async (item, _, write, ct) =>
            {
                if (item.ItemId == items[0].Id) throw new FileNotFoundException();
                await write(new byte[188]); await Task.Delay(Timeout.Infinite, ct);
            });
            var guideImports = 0;
            var guideTask = Proxy<MediaBrowser.Model.Tasks.IScheduledTask>.Make((name, _) => name == "get_Key" ? "RefreshGuide" : null);
            var guideWorker = Proxy<MediaBrowser.Model.Tasks.IScheduledTaskWorker>.Make((name, _) => name switch { "get_ScheduledTask" => guideTask, "get_State" => MediaBrowser.Model.Tasks.TaskState.Idle, _ => null });
            var taskManager = Proxy<MediaBrowser.Model.Tasks.ITaskManager>.Make((name, args) =>
            {
                if (name == "get_ScheduledTasks") return new[] { guideWorker };
                if (name == "QueueScheduledTask" && args![0] == guideTask) { guideImports++; return null; }
                throw new NotSupportedException(name);
            });
            using (var live = new Broadcasts(scheduler, failure, NullLogger<Broadcasts>.Instance, taskManager))
            using (var stop = new CancellationTokenSource())
            {
                var output = new SignalStream();
                var watch = live.Watch("playback", output, stop.Token);
                await output.Received.Task.WaitAsync(TimeSpan.FromSeconds(5));
                check(failure.Calls == 2 && scheduler.Current("playback", DateTimeOffset.UtcNow)!.ItemId == items[1].Id && live.StatusFor("playback").Recoveries == 1, "A missing program brings the next scheduled item forward without breaking the viewer stream");
                check(guideImports == 1, "Recovery asks Jellyfin to re-import the shifted guide once");
                var after = scheduler.Current("playback", DateTimeOffset.UtcNow)!;
                await scheduler.SkipFailed(initial, DateTimeOffset.UtcNow, default);
                check(scheduler.Current("playback", DateTimeOffset.UtcNow)!.Start == after.Start, "A stale recovery request cannot shift the same channel twice");
                stop.Cancel(); await IgnoreCancellation(watch);
            }
            scheduler = await Fresh();
            var broken = new DelegateStreamer((_, _, _, _) => Task.FromException(new IOException("Bad test media")));
            using (var live = new Broadcasts(scheduler, broken, NullLogger<Broadcasts>.Instance))
            {
                try { await live.Watch("playback", new MemoryStream(), default).WaitAsync(TimeSpan.FromSeconds(5)); throw new Exception("Repeated failures did not stop playback"); }
                catch (IOException) { }
                check(broken.Calls == 3 && live.StatusFor("playback").Recoveries == 2, "Three consecutive bad items stop recovery rather than creating an unbounded retry loop");
            }
            scheduler = await Fresh();
            using (var live = new Broadcasts(scheduler, broken, NullLogger<Broadcasts>.Instance))
            {
                var transport = new Controller(catalog, scheduler, live) { ControllerContext = new() { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() } };
                transport.HttpContext.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
                await transport.Stream("playback", Plugin.Instance.Configuration.StreamToken, default).WaitAsync(TimeSpan.FromSeconds(5));
                check(transport.Response.StatusCode == 503, "A channel that cannot play returns a clean unavailable response instead of an unhandled server error");
            }
            scheduler = await Fresh();
            DelegateStreamer stalledOnce = null!;
            stalledOnce = new DelegateStreamer(async (_, _, write, ct) =>
            {
                await write(new byte[188]);
                if (stalledOnce.Calls == 1) throw new PlaybackInterruptedException(true, true);
                await Task.Delay(Timeout.Infinite, ct);
            });
            var stalledProgram = scheduler.Current("playback", DateTimeOffset.UtcNow)!;
            using (var live = new Broadcasts(scheduler, stalledOnce, NullLogger<Broadcasts>.Instance))
            using (var stop = new CancellationTokenSource())
            {
                var output = new CountingStream();
                var watch = live.Watch("playback", output, stop.Token);
                await output.WaitFor(2 * 188 + 188 * 7, TimeSpan.FromSeconds(5));
                var status = live.StatusFor("playback");
                check(stalledOnce.Calls == 2 && scheduler.Current("playback", DateTimeOffset.UtcNow)!.Start == stalledProgram.Start && status.Recoveries == 1 && status.LastProblem!.Contains("restarted automatically") && !watch.IsCompleted,
                    "A stalled program restarts automatically at the current position while the viewer stays connected");
                stop.Cancel(); await IgnoreCancellation(watch);
            }
            scheduler = await Fresh();
            var shortProgram = scheduler.Current("playback", DateTimeOffset.UtcNow)!;
            var shortFile = new DelegateStreamer(async (item, _, write, ct) =>
            {
                await write(new byte[188]);
                if (item.ItemId != shortProgram.ItemId) await Task.Delay(Timeout.Infinite, ct);
            });
            using (var live = new Broadcasts(scheduler, shortFile, NullLogger<Broadcasts>.Instance))
            using (var stop = new CancellationTokenSource())
            {
                var output = new CountingStream();
                var watch = live.Watch("playback", output, stop.Token);
                await output.WaitFor(2 * 188, TimeSpan.FromSeconds(5));
                check(shortFile.Calls == 2 && scheduler.Current("playback", DateTimeOffset.UtcNow)!.ItemId != shortProgram.ItemId && live.StatusFor("playback").Playback == "Test playback",
                    "A file shorter than its listed runtime is followed at once by the next program instead of dead air");
                stop.Cancel(); await IgnoreCancellation(watch);
            }
            var ad = new Video { Id = Guid.NewGuid(), Name = "Ad", Path = Path.Combine(root, "ad.mp4"), RunTimeTicks = TimeSpan.FromSeconds(30).Ticks };
            await File.WriteAllTextAsync(ad.Path, "test metadata fixture");
            items = [.. items, ad];
            var plainConfiguration = Plugin.Instance.Configuration;
            Plugin.Instance.UpdateConfiguration(new Configuration { GuideDays = 1, Channels = [new ChannelDefinition { Id = "playback", Name = "Playback", CommercialCount = 1,
                Source = new Pool { ItemIds = items.Take(3).Select(x => x.Id.ToString()).ToArray(), Shuffle = false }, Commercials = new Pool { ItemIds = [ad.Id.ToString()] } }] });
            scheduler = await Fresh();
            var splitProgram = scheduler.Current("playback", DateTimeOffset.UtcNow)!;
            check(splitProgram.Offset == 0 && scheduler.GuideFor("playback").TakeWhile(x => x.Commercial || x.ItemId == splitProgram.ItemId).Count(x => !x.Commercial) == 3, "An hour-long movie is planned in three parts around two commercial breaks");
            var splitFailure = new DelegateStreamer(async (item, _, write, ct) =>
            {
                if (item.ItemId == splitProgram.ItemId) throw new FileNotFoundException();
                await write(new byte[188]); await Task.Delay(Timeout.Infinite, ct);
            });
            using (var live = new Broadcasts(scheduler, splitFailure, NullLogger<Broadcasts>.Instance))
            using (var stop = new CancellationTokenSource())
            {
                var output = new SignalStream();
                var watch = live.Watch("playback", output, stop.Token);
                await output.Received.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var guide = scheduler.GuideFor("playback");
                check(splitFailure.Calls == 2 && !guide.TakeWhile(x => x.Commercial || x.ItemId == splitProgram.ItemId).Any(x => x.Offset > 0) && scheduler.Current("playback", DateTimeOffset.UtcNow)!.ItemId == ad.Id
                    && guide.Zip(guide.Skip(1)).All(x => x.First.End <= x.Second.Start), "A missing movie skips its later parts and inner breaks in one step, going straight to the break after it");
                stop.Cancel(); await IgnoreCancellation(watch);
            }
            // With the restart picture shape, a program of a new shape restarts the players watching the channel.
            Plugin.Instance.UpdateConfiguration(new Configuration { GuideDays = 1, Channels = [new ChannelDefinition { Id = "playback", Name = "Playback", CommercialCount = 1, MovieBreaks = 0, PictureShape = "restart",
                Source = new Pool { ItemIds = items.Take(3).Select(x => x.Id.ToString()).ToArray(), Shuffle = false }, Commercials = new Pool { ItemIds = [ad.Id.ToString()] } }] });
            scheduler = await Fresh();
            var narrow = scheduler.Current("playback", DateTimeOffset.UtcNow)!;
            var commands = new List<(string Session, MediaBrowser.Model.Session.PlayRequest Request)>();
            var remote = Proxy<MediaBrowser.Controller.Session.ISessionController>.Make((name, _) => name is "get_IsSessionActive" or "get_SupportsMediaControl" ? true : null);
            var watcher = new MediaBrowser.Controller.Session.SessionInfo(null!, NullLogger.Instance) { Capabilities = new() { SupportsMediaControl = true }, SessionControllers = [remote], Id = "living-room", UserId = Guid.NewGuid(), NowPlayingItem = new MediaBrowser.Model.Dto.BaseItemDto { Id = Guid.NewGuid() },
                PlayState = new MediaBrowser.Model.Session.PlayerStateInfo { MediaSourceId = "librarytv-playback" } };
            var bystander = new MediaBrowser.Controller.Session.SessionInfo(null!, NullLogger.Instance) { Capabilities = new() { SupportsMediaControl = true }, SessionControllers = [remote], Id = "bedroom", NowPlayingItem = new MediaBrowser.Model.Dto.BaseItemDto { Id = Guid.NewGuid() },
                PlayState = new MediaBrowser.Model.Session.PlayerStateInfo { MediaSourceId = "something-else" } };
            var sessionManager = Proxy<MediaBrowser.Controller.Session.ISessionManager>.Make((name, args) =>
            {
                if (name == "get_Sessions") return new[] { watcher, bystander };
                if (name == "SendPlayCommand") { lock (commands) commands.Add(((string)args![1]!, (MediaBrowser.Model.Session.PlayRequest)args[2]!)); return Task.CompletedTask; }
                throw new NotSupportedException(name);
            });
            Broadcasts.ShapeRestartDelay = TimeSpan.FromMilliseconds(50);
            var shaped = new DelegateStreamer(async (item, _, write, ct) =>
            {
                await write(new byte[188]);
                if (item.ItemId != narrow.ItemId && item.ItemId != ad.Id) await Task.Delay(Timeout.Infinite, ct);
            }, item => item.ItemId == narrow.ItemId ? 4.0 / 3 : 16.0 / 9);
            using (var live = new Broadcasts(scheduler, shaped, NullLogger<Broadcasts>.Instance, null, sessionManager))
            using (var stop = new CancellationTokenSource())
            {
                var output = new CountingStream();
                var watch = live.Watch("playback", output, stop.Token);
                var until = DateTime.UtcNow.AddSeconds(5);
                while (shaped.Calls < 3 && DateTime.UtcNow < until) await Task.Delay(20);
                await Task.Delay(300);
                lock (commands)
                    check(shaped.Calls == 3 && shaped.Shapes[1] == 4.0 / 3 && commands.Count == 1 && commands[0].Session == "living-room" && commands[0].Request.ItemIds.SequenceEqual([watcher.NowPlayingItem.Id])
                        && commands[0].Request.PlayCommand == MediaBrowser.Model.Session.PlayCommand.PlayNow,
                        "When the next program has another shape, only the players watching the channel are restarted, and the commercial before it keeps the earlier program's shape");
                stop.Cancel(); await IgnoreCancellation(watch);
            }
            Plugin.Instance.UpdateConfiguration(plainConfiguration);
            scheduler = await Fresh();
            var before = scheduler.Current("playback", DateTimeOffset.UtcNow)!;
            var unavailable = new DelegateStreamer((_, _, _, _) => Task.FromException(new EncoderUnavailableException()));
            using (var live = new Broadcasts(scheduler, unavailable, NullLogger<Broadcasts>.Instance))
            {
                try { await live.Watch("playback", new MemoryStream(), default).WaitAsync(TimeSpan.FromSeconds(5)); throw new Exception("Missing encoder did not stop playback"); }
                catch (EncoderUnavailableException) { }
                check(unavailable.Calls == 1 && scheduler.Current("playback", DateTimeOffset.UtcNow)!.Start == before.Start && live.StatusFor("playback").LastProblem!.Contains("FFmpeg"), "An unavailable encoder reports a configuration error without skipping legitimate programs");
            }
        }
        finally { Plugin.Instance.UpdateConfiguration(original); }
    }

    private static async Task IgnoreCancellation(Task task) { try { await task.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { } }
    private sealed class DelegateStreamer(Func<Airing, ChannelDefinition, Func<byte[], Task>, CancellationToken, Task> stream, Func<Airing, double?>? shape = null) : IProgramStreamer
    {
        private int calls;
        public int Calls => Volatile.Read(ref calls);
        public List<double?> Shapes { get; } = [];
        public async Task<string> Stream(Airing program, ChannelDefinition channel, double? shape, Func<byte[], Task> output, CancellationToken ct, Action<string, bool>? report = null)
        {
            Interlocked.Increment(ref calls);
            lock (Shapes) Shapes.Add(shape);
            await stream(program, channel, output, ct);
            return "Test playback";
        }
        public Task<double?> Shape(Airing program, CancellationToken ct) => Task.FromResult(shape?.Invoke(program));
    }
    private sealed class StalledStream : MemoryStream
    {
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => await Task.Delay(Timeout.Infinite, ct);
    }
    private sealed class CountingStream : MemoryStream
    {
        private long total;
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) { await base.WriteAsync(buffer, ct); Interlocked.Add(ref total, buffer.Length); }
        public async Task WaitFor(long bytes, TimeSpan timeout)
        {
            var until = DateTime.UtcNow + timeout;
            while (Interlocked.Read(ref total) < bytes) { if (DateTime.UtcNow > until) throw new TimeoutException("Expected stream output did not arrive."); await Task.Delay(20); }
        }
    }
    private sealed class SignalStream : MemoryStream
    {
        public TaskCompletionSource Received { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) { await base.WriteAsync(buffer, ct); Received.TrySetResult(); }
    }
}
