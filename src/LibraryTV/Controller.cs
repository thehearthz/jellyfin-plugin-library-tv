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
    [HttpGet("libraries")]
    public object Libraries() => catalog.Libraries();

    [HttpGet("status")]
    public object Status() => new { scheduler.LastRefresh, scheduler.LastError, Channels = scheduler.Channels.Select(x => new { x.Id, x.Name, Programs = scheduler.Guide.Count(p => p.ChannelId == x.Id), Current = scheduler.Current(x.Id, DateTimeOffset.UtcNow)?.Title }) };

    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh(CancellationToken ct)
    {
        try { await scheduler.Refresh(ct); return Ok(Status()); }
        catch (ArgumentException ex) { return BadRequest(new { Error = ex.Message }); }
    }

    [HttpGet("guide/{id}")]
    public object Guide(string id) => scheduler.Guide.Where(x => x.ChannelId == id && x.End > DateTimeOffset.UtcNow).Select(x => new { x.Title, x.ItemId, x.Start, x.End, x.Commercial });

    [HttpGet("configuration")]
    public Configuration Configuration() => Plugin.Instance.Configuration;

    [HttpPost("configuration")]
    public IActionResult Save([FromBody] Configuration config)
    {
        try
        {
            Scheduler.Validate(config);
            Plugin.Instance.UpdateConfiguration(config);
            return Ok(new { Saved = true });
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
