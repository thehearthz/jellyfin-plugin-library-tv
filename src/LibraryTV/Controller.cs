using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.LibraryTV;

[ApiController]
[Route("LibraryTV")]
[Authorize(Policy = "RequiresElevation")]
[TypeFilter(typeof(StartupExceptionFilter))]
public sealed class Controller : ControllerBase
{
    private readonly StartupGate? startup;
    private readonly Lazy<Catalog> catalogService;
    private readonly Lazy<Scheduler> schedulerService;
    private readonly Lazy<Broadcasts?> broadcastService;
    private readonly Lazy<CreditsScanner?> creditsService = new(() => null);
    public Controller(IServiceProvider services, StartupGate startup)
    {
        this.startup = startup;
        catalogService = new(() => services.GetRequiredService<Catalog>());
        schedulerService = new(() => services.GetRequiredService<Scheduler>());
        broadcastService = new(() => services.GetRequiredService<Broadcasts>());
        creditsService = new(() => services.GetService<CreditsScanner>());
    }
    internal Controller(Catalog catalog, Scheduler scheduler, Broadcasts? broadcasts)
    {
        catalogService = new(() => catalog);
        schedulerService = new(() => scheduler);
        broadcastService = new(() => broadcasts);
    }
    private Catalog catalog { get { startup?.RequireReady(); return catalogService.Value; } }
    private Scheduler scheduler { get { startup?.RequireReady(); return schedulerService.Value; } }
    private Broadcasts? broadcasts { get { startup?.RequireReady(); return broadcastService.Value; } }
    [HttpGet("media")]
    public ActionResult<CatalogPage> Browse([FromQuery] string kind = "shows", [FromQuery] Guid libraryId = default,
        [FromQuery] string? search = null, [FromQuery] int start = 0, [FromQuery] int limit = 36,
        [FromQuery] string? genre = null, [FromQuery] int minYear = 0, [FromQuery] int maxYear = 0)
    {
        if (search?.Length > 200 || genre?.Length > 200) return BadRequest(new { Error = "Search and genre must be 200 characters or fewer." });
        if (minYear is < 0 or > 2200 || maxYear is < 0 or > 2200) return BadRequest(new { Error = "Years must be between 1850 and 2200." });
        try { return catalog.Browse(kind, libraryId, search, start, limit, genre, minYear, maxYear); }
        catch (ArgumentException ex) { return BadRequest(new { Error = ex.Message }); }
    }

    [HttpPost("media/resolve")]
    public ActionResult<CatalogItem[]> Resolve([FromBody] Guid[] ids)
    {
        try { return catalog.Resolve(ids); }
        catch (ArgumentException ex) { return BadRequest(new { Error = ex.Message }); }
    }

    [HttpGet("libraries")]
    public object Libraries() => catalog.Libraries();

    [HttpGet("status")]
    public object Status() => startup?.IsReady == false
        ? new { IsReady = false, LastRefresh = (DateTimeOffset?)null, LastRefreshMilliseconds = (double?)null, LastError = (string?)null, PluginPages = PluginPagesIntegration.Status, Channels = Array.Empty<object>() }
        : new { scheduler.IsReady, scheduler.LastRefresh, scheduler.LastRefreshMilliseconds, scheduler.LastError, PluginPages = PluginPagesIntegration.Status, scheduler.ClipLibrary, ClipMinimum = Planner.MinimumVariety, CreditsScan = creditsService.Value?.Status, Channels = scheduler.Channels.Select(x => new { x.Id, x.Name, x.Number, x.AlwaysEncode, Programs = scheduler.GuideFor(x.Id).Count, Current = scheduler.Current(x.Id, DateTimeOffset.UtcNow)?.Title, HasUpcomingPrograms = scheduler.GuideFor(x.Id).Any(p => p.End > DateTimeOffset.UtcNow), Warnings = scheduler.WarningsFor(x.Id), Runtime = broadcasts?.StatusFor(x.Id) }) };

    [HttpPost("rebuild/{id}")]
    public async Task<IActionResult> Rebuild(string id, CancellationToken ct)
    {
        try { return Ok(new { AppliedFrom = await scheduler.RebuildFuture(id, ct) }); }
        catch (Exception ex) when (IsInvalidSettings(ex)) { return BadRequest(new { Error = ex.Message }); }
    }

    [HttpGet("backup")]
    public SettingsBackup Backup() => SettingsBackup.Export(Plugin.Instance.Configuration);

    [HttpPost("backup/import")]
    [RequestSizeLimit(1048576)]
    public async Task<IActionResult> Import([FromBody] SettingsBackup backup, CancellationToken ct)
    {
        try
        {
            await scheduler.SaveConfiguration(backup.ApplyTo(Plugin.Instance.Configuration), ct);
            return Ok(new { Saved = true });
        }
        catch (Exception ex) when (IsInvalidSettings(ex)) { return BadRequest(new { Error = ex.Message }); }
    }

    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh(CancellationToken ct)
    {
        try { await scheduler.Refresh(ct); return Ok(Status()); }
        catch (Exception ex) when (IsInvalidSettings(ex)) { return BadRequest(new { Error = ex.Message }); }
    }

    // The shared and channel clips as filed, with the settings' rules and edits applied (as of the last refresh).
    [HttpGet("clips")]
    public ActionResult<ClipPage> Clips([FromQuery] string? search = null, [FromQuery] string? kind = null, [FromQuery] string? network = null,
        [FromQuery] int? decade = null, [FromQuery] string? problem = null, [FromQuery] int start = 0, [FromQuery] int limit = 50)
        => search?.Length > 200 || network?.Length > 100 ? BadRequest(new { Error = "Search must be 200 characters or fewer." })
            : scheduler.BrowseClips(new ClipFilter(search, kind, network, decade, problem), start, limit);

    [HttpGet("clips/ids")]
    public ActionResult<string[]> ClipIds([FromQuery] string? search = null, [FromQuery] string? kind = null, [FromQuery] string? network = null,
        [FromQuery] int? decade = null, [FromQuery] string? problem = null)
        => search?.Length > 200 || network?.Length > 100 ? BadRequest(new { Error = "Search must be 200 characters or fewer." })
            : scheduler.ClipIds(new ClipFilter(search, kind, network, decade, problem));

    [HttpGet("clips/{id}/matches")]
    public ActionResult<ClipMatches> ClipMatches(string id) => scheduler.MatchesFor(id.Replace("-", "").ToLowerInvariant()) is { } matches ? matches : NotFound(new { Error = "That clip isn't in the clip library yet. Save and refresh first." });

    [HttpGet("guide/{id}")]
    public object Guide(string id, [FromQuery] int start = 0, [FromQuery] int limit = 40) => scheduler.GuideFor(id).Where(x => x.End > DateTimeOffset.UtcNow).Skip(Math.Max(0, start)).Take(Math.Clamp(limit, 1, 200)).Select(x => new { x.Title, x.ItemId, x.Start, x.End, x.Commercial, x.Match, x.Speed, Kind = !x.Commercial ? "Program" : x.Undo?.PoolKey.StartsWith("bumpers", StringComparison.Ordinal) == true ? "Bumper" : "Commercial" });

    [HttpGet("configuration")]
    public Configuration Configuration() => Plugin.Instance.Configuration;

    [HttpPost("configuration")]
    public async Task<IActionResult> Save([FromBody] Configuration config, CancellationToken ct)
    {
        try
        {
            await scheduler.SaveConfiguration(config, ct);
            return Ok(new { Saved = true, config.AutomaticChannels });
        }
        catch (Exception ex) when (IsInvalidSettings(ex)) { return BadRequest(new { Error = ex.Message }); }
    }

    private static bool IsInvalidSettings(Exception ex) => ex is ArgumentException or FormatException or TimeZoneNotFoundException or InvalidTimeZoneException;

    [AllowAnonymous]
    [HttpGet("stream/{id}")]
    public async Task Stream(string id, [FromQuery] string? token, CancellationToken ct)
    {
        // Only Jellyfin itself may open transport URLs. Clients use normal Live TV authorization.
        var address = HttpContext.Connection.RemoteIpAddress;
        if (address is null || !System.Net.IPAddress.IsLoopback(address) || token is null || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(token)), SHA256.HashData(Encoding.UTF8.GetBytes(Plugin.Instance.Configuration.StreamToken)) ))
        { Response.StatusCode = 403; return; }
        var now = DateTimeOffset.UtcNow;
        // A short gap left by content restrictions is bridged by the broadcast, so tuning in may start in one.
        if (scheduler.Current(id, now) is null && !scheduler.GuideFor(id).Any(x => x.Start > now && x.Start - now <= Broadcasts.MaxGap)) { Response.StatusCode = 404; return; }
        Response.ContentType = "video/mp2t";
        Response.Headers.CacheControl = "no-store";
        try { await broadcasts!.Watch(id, Response.Body, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        // Playback failures are already logged and shown in Channel health.
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            if (Response.HasStarted) HttpContext.Abort(); else Response.StatusCode = 503;
        }
    }
}
