using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.LibraryTV;

[ApiController]
[Authorize]
public sealed class BuilderController : ControllerBase
{
    // The shell explains administrator access. All data APIs stay admin-only.
    [HttpGet("LibraryTV/builder")]
    public IActionResult Builder()
    {
        using var stream = typeof(Plugin).Assembly.GetManifestResourceStream("Jellyfin.Plugin.LibraryTV.Web.config.html")!;
        using var reader = new StreamReader(stream);
        // Plugin Pages already owns the surrounding page. Avoid nesting Jellyfin's
        // page-router layout inside its content container.
        var html = reader.ReadToEnd().Replace("data-role=\"page\"", "")
            .Replace("class=\"page type-interior pluginConfigurationPage ltv-app\"", "class=\"ltv-app\"");
        return Content(html, "text/html; charset=utf-8");
    }
}
