using MediaBrowser.Controller;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.LibraryTV;

public sealed class StartupGate
{
    public static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(30);
    private readonly IHostApplicationLifetime lifetime;
    private readonly IServerApplicationHost host;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly object sync = new();
    private Task? ready;
    private int isReady;

    public StartupGate(IHostApplicationLifetime lifetime, IServerApplicationHost host)
        : this(lifetime, host, Task.Delay) { }

    internal StartupGate(IHostApplicationLifetime lifetime, IServerApplicationHost host, Func<TimeSpan, CancellationToken, Task> delay)
    {
        this.lifetime = lifetime;
        this.host = host;
        this.delay = delay;
    }

    public bool IsReady => Volatile.Read(ref isReady) == 1;

    public Task WaitAsync(CancellationToken ct)
    {
        // Both workers share one readiness task and one settling timer.
        lock (sync) return (ready ??= WaitForServer()).WaitAsync(ct);
    }

    public void RequireReady()
    {
        if (!IsReady) throw new StartupPendingException();
    }

    private async Task WaitForServer()
    {
        var stopped = lifetime.ApplicationStopping;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        await started.Task.WaitAsync(stopped).ConfigureAwait(false);
        // Jellyfin runs core startup tasks after starting its generic host.
        while (!host.CoreStartupHasCompleted)
            await delay(TimeSpan.FromSeconds(1), stopped).ConfigureAwait(false);
        await delay(SettleDelay, stopped).ConfigureAwait(false);
        stopped.ThrowIfCancellationRequested();
        Volatile.Write(ref isReady, 1);
    }
}

public sealed class StartupPendingException : Exception
{
    public StartupPendingException() : base("Library TV is waiting for Jellyfin startup to finish, followed by a 30-second settling period. Try again shortly.") { }
}

public sealed class StartupExceptionFilter : ExceptionFilterAttribute
{
    public override void OnException(ExceptionContext context)
    {
        if (context.Exception is not StartupPendingException) return;
        context.HttpContext.Response.Headers.RetryAfter = "5";
        context.Result = new ObjectResult(new { Error = context.Exception.Message }) { StatusCode = 503 };
        context.ExceptionHandled = true;
    }
}
