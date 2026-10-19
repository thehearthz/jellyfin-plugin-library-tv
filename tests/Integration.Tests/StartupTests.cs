using Jellyfin.Plugin.LibraryTV;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

public static class StartupTests
{
    public static async Task Run(Action<bool, string> check, IApplicationPaths paths)
    {
        using var lifetime = new TestLifetime();
        var coreReady = false;
        var coreChecks = 0;
        var host = Proxy<IServerApplicationHost>.Make((name, _) =>
        {
            if (name != "get_CoreStartupHasCompleted") throw new NotSupportedException(name);
            Interlocked.Increment(ref coreChecks);
            return coreReady;
        });
        var poll = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePoll = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var settle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSettle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timers = 0;
        var gate = new StartupGate(lifetime, host, (duration, ct) =>
        {
            Interlocked.Increment(ref timers);
            if (duration == TimeSpan.FromSeconds(1)) { poll.TrySetResult(); return releasePoll.Task.WaitAsync(ct); }
            check(duration == TimeSpan.FromSeconds(30), "Background startup work has a 30-second settling period");
            settle.TrySetResult();
            return releaseSettle.Task.WaitAsync(ct);
        });
        var first = gate.WaitAsync(default);
        var second = gate.WaitAsync(default);
        check(!first.IsCompleted && !second.IsCompleted && coreChecks == 0 && timers == 0, "Readiness waits for ApplicationStarted without polling or doing work before the host starts");
        lifetime.Started.Cancel();
        await poll.Task.WaitAsync(TimeSpan.FromSeconds(5));
        check(!gate.IsReady && !first.IsCompleted, "Host startup alone cannot start library work before Jellyfin core startup completes");
        coreReady = true;
        releasePoll.TrySetResult();
        await settle.Task.WaitAsync(TimeSpan.FromSeconds(5));
        check(!gate.IsReady && !first.IsCompleted, "Library work remains deferred throughout the settling period");
        releaseSettle.TrySetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        check(gate.IsReady && timers == 2, "Both workers share one readiness task and one settling timer");

        using var pendingLifetime = new TestLifetime();
        var pendingGate = new StartupGate(pendingLifetime, host, (_, ct) => Task.Delay(Timeout.Infinite, ct));
        var queries = 0;
        var library = Proxy<ILibraryManager>.Make((name, _) => { Interlocked.Increment(ref queries); throw new Exception("Unexpected startup library query: " + name); });
        var catalog = new Catalog(library, pendingGate);
        var scheduler = new Scheduler(catalog, paths, NullLogger<Scheduler>.Instance, pendingGate);
        var resolutions = 0;
        var deferredServices = Proxy<IServiceProvider>.Make((_, _) => { resolutions++; throw new Exception("Startup resolved deferred services"); });
        using var refresh = new RefreshService(deferredServices, NullLogger<RefreshService>.Instance, pendingGate);
        using var pages = new PluginPagesIntegration(NullLogger<PluginPagesIntegration>.Instance, pendingGate);
        var deferredTv = new LiveTvService(deferredServices, pendingGate);
        var deferredFolder = new LibraryChannel(deferredServices, pendingGate);
        await refresh.StartAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        await pages.StartAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        check(queries == 0 && resolutions == 0 && !scheduler.IsReady && !File.Exists(scheduler.StatePath), "Both hosted services return from startup without resolving heavy services, scanning libraries or writing a schedule");
        check(!(await deferredTv.GetChannelsAsync(default)).Any() && !(await deferredTv.GetProgramsAsync("test", DateTime.UtcNow, DateTime.UtcNow.AddHours(1), default)).Any() && deferredFolder.DataVersion == "starting" && !deferredFolder.IsEnabledFor(Guid.NewGuid().ToString()) && resolutions == 0, "Early channel-provider calls do not resolve scheduler, library or user services");
        var controller = new Jellyfin.Plugin.LibraryTV.Controller(deferredServices, pendingGate);
        _ = controller.Status();
        try { controller.Libraries(); throw new Exception("Controller bypassed startup gate"); }
        catch(StartupPendingException) { check(resolutions == 0,"Startup status and early HTTP requests do not resolve library, scheduler, broadcaster or encoder services"); }
        try { catalog.Libraries(); throw new Exception("Catalog bypassed startup gate"); }
        catch (StartupPendingException) { check(queries == 0, "Requests during startup cannot trigger early library queries"); }
        try { await scheduler.Refresh(default); throw new Exception("Refresh bypassed startup gate"); }
        catch (StartupPendingException) { check(queries == 0, "A manual refresh during startup reports waiting instead of starting schedule work"); }
        pendingLifetime.StopApplication();
        await refresh.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        await pages.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        check(queries == 0 && resolutions == 0 && !pendingGate.IsReady, "Shutdown while waiting cancels both workers without performing deferred work");

        using var independentLifetime = new TestLifetime();
        var independentHost = Proxy<IServerApplicationHost>.Make((_, _) => true);
        var independentGate = new StartupGate(independentLifetime, independentHost, (_, _) => Task.CompletedTask);
        using var caller = new CancellationTokenSource();
        var canceledWait = independentGate.WaitAsync(caller.Token);
        caller.Cancel();
        try { await canceledWait; throw new Exception("Canceled readiness waiter continued"); }
        catch (OperationCanceledException) { }
        independentLifetime.Started.Cancel();
        await independentGate.WaitAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        check(independentGate.IsReady, "Canceling one caller does not cancel shared readiness for other workers");
    }
}

public sealed class TestLifetime : IHostApplicationLifetime, IDisposable
{
    public CancellationTokenSource Started { get; } = new();
    private readonly CancellationTokenSource stopping = new();
    private readonly CancellationTokenSource stopped = new();
    public CancellationToken ApplicationStarted => Started.Token;
    public CancellationToken ApplicationStopping => stopping.Token;
    public CancellationToken ApplicationStopped => stopped.Token;
    public void StopApplication() => stopping.Cancel();
    public void Dispose() { Started.Dispose(); stopping.Dispose(); stopped.Dispose(); }
}
