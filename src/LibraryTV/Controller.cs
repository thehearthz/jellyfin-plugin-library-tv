using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.LibraryTV;

[ApiController]
[Route("LibraryTV")]
[Authorize(Policy = "RequiresElevation")]
public sealed class Controller(Catalog catalog, Scheduler scheduler, Broadcasts broadcasts) : ControllerBase
{
    [HttpGet("media")]
    public ActionResult<CatalogPage> Browse([FromQuery] string kind = "shows", [FromQuery] Guid libraryId = default,
        [FromQuery] string? search = null, [FromQuery] int start = 0, [FromQuery] int limit = 36)
    {
        if (search?.Length > 200) return BadRequest(new { Error = "Search must be 200 characters or fewer." });
        try { return catalog.Browse(kind, libraryId, search, start, limit); }
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
    public object Status() => new { scheduler.LastRefresh, scheduler.LastError, PluginPages = PluginPagesIntegration.Status, Channels = scheduler.Channels.Select(x => new { x.Id, x.Name, x.Number, Programs = scheduler.GuideFor(x.Id).Count, Current = scheduler.Current(x.Id, DateTimeOffset.UtcNow)?.Title, HasUpcomingPrograms = scheduler.GuideFor(x.Id).Any(p => p.End > DateTimeOffset.UtcNow) }) };

    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh(CancellationToken ct)
    {
        try { await scheduler.Refresh(ct); return Ok(Status()); }
        catch (ArgumentException ex) { return BadRequest(new { Error = ex.Message }); }
    }

    [HttpGet("guide/{id}")]
    public object Guide(string id, [FromQuery] int start = 0, [FromQuery] int limit = 40) => scheduler.GuideFor(id).Where(x => x.End > DateTimeOffset.UtcNow).Skip(Math.Max(0, start)).Take(Math.Clamp(limit, 1, 200)).Select(x => new { x.Title, x.ItemId, x.Start, x.End, x.Commercial });

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
        catch (Exception ex) when (ex is ArgumentException or TimeZoneNotFoundException or InvalidTimeZoneException or FormatException) { return BadRequest(new { Error = ex.Message }); }
    }

    [AllowAnonymous]
    [HttpGet("stream/{id}")]
    public async Task Stream(string id, [FromQuery] string? token, CancellationToken ct)
    {
        // Only Jellyfin itself may open transport URLs. Clients use normal Live TV authorization.
        var address = HttpContext.Connection.RemoteIpAddress;
        if (address is null || !System.Net.IPAddress.IsLoopback(address) || token is null || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(token)), SHA256.HashData(Encoding.UTF8.GetBytes(Plugin.Instance.Configuration.StreamToken)) ))
        { Response.StatusCode = 403; return; }
        if (scheduler.Current(id, DateTimeOffset.UtcNow) is null) { Response.StatusCode = 404; return; }
        Response.ContentType = "video/mp2t";
        Response.Headers.CacheControl = "no-store";
        try { await broadcasts.Watch(id, Response.Body, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (InvalidOperationException) when (!Response.HasStarted) { Response.StatusCode = 503; }
    }
}
