using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LibraryTV;

// Upstream's v3 reflection interface keeps Plugin Pages an optional dependency.
public sealed class PluginPagesIntegration(ILogger<PluginPagesIntegration> log) : IHostedService
{
    public const string PageId = "library-tv-builder";
    public static string Status { get; private set; } = "Not checked";
    public static string RegistrationJson => JsonSerializer.Serialize(new
    {
        id = PageId, url = "/LibraryTV/builder", displayText = "Library TV Builder", icon = "live_tv"
    });

    public Task StartAsync(CancellationToken ct)
    {
        var assembly = AssemblyLoadContext.All.SelectMany(x => x.Assemblies)
            .FirstOrDefault(x => x.GetName().Name == "Jellyfin.Plugin.PluginPages");
        if (assembly is null)
        {
            Status = "Not installed. Install Plugin Pages 3+ and File Transformation, then restart Jellyfin.";
            return Task.CompletedTask;
        }
        try { Register(assembly); Status = "Connected — Library TV Builder is registered in Plugin Pages."; }
        catch (Exception ex)
        {
            Status = "Registration failed. Use Dashboard → Plugins → Library TV and check Plugin Pages compatibility.";
            log.LogWarning(ex, "Could not register Library TV Builder with Plugin Pages");
        }
        return Task.CompletedTask;
    }

    public static void Register(Assembly assembly)
    {
        var type = assembly.GetType("Jellyfin.Plugin.PluginPages.PluginInterface", true)!;
        var register = type.GetMethod("RegisterPage", BindingFlags.Public | BindingFlags.Static)
            ?? throw new MissingMethodException("Plugin Pages 3+ is required.");
        var payloadType = register.GetParameters().Single().ParameterType;
        var parse = payloadType.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static, [typeof(string)])
            ?? throw new MissingMethodException("Plugin Pages JSON payload parser is unavailable.");
        register.Invoke(null, [parse.Invoke(null, [RegistrationJson])]);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
