using System.Reflection;
using System.Xml.Serialization;
using Jellyfin.Plugin.LibraryTV;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;

int checks = 0;
void Check(bool ok, string label) { if (!ok) throw new Exception(label); checks++; Console.WriteLine("PASS: " + label); }
var directory = Path.Combine(Path.GetTempPath(), "library-tv-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    var paths = Proxy<IApplicationPaths>.Make((name, _) => name.StartsWith("get_") ? directory : null);
    var xml = Proxy<IXmlSerializer>.Make((name, _) => name.StartsWith("Deserialize") ? new Configuration() : null);
    var plugin = new Plugin(paths, xml);
    var services = new ServiceCollection();
    new Services().RegisterServices(services, null!);
    Check(services.Any(x => x.ServiceType == typeof(ILiveTvService) && x.ImplementationType == typeof(LiveTvService)), "Plugin registers its native Live TV provider in dependency injection");
    Check(plugin.Id == new Guid("512a2a0d-5101-4fe8-9651-484f077a601a"), "Plugin GUID matches install catalog");
    foreach (var page in plugin.GetPages())
        Check(plugin.GetType().Assembly.GetManifestResourceStream(page.EmbeddedResourcePath) is not null, "Dashboard HTML is embedded at the declared resource name");
    var library = Proxy<ILibraryManager>.Make((name, _) => name == "GetVirtualFolders" ? new List<VirtualFolderInfo>() : throw new NotSupportedException(name));
    var catalog = new Catalog(library);
    Check(catalog.Read(new Pool()).Count == 0, "Empty selectors do not expose the entire library");
    var scheduler = new Scheduler(catalog, paths, NullLogger<Scheduler>.Instance);
    await scheduler.Refresh(default);
    Check(scheduler.LastError is null && scheduler.LastRefresh is not null && File.Exists(scheduler.StatePath), "Schedule refresh persists state atomically");
    await scheduler.Refresh(default);
    Check(scheduler.Channels.Count == 0, "Empty configuration produces no channels");
    ILiveTvService tv = new LiveTvService(scheduler);
    Check(!(await tv.GetChannelsAsync(default)).Any(), "Native Live TV provider works against the real Jellyfin 12.2 interface");
    var source = await tv.GetChannelStream("test", "", default);
    Check(source.IsInfiniteStream && source.Path.StartsWith("http://127.0.0.1:8096/LibraryTV/stream/test?token="), "Live TV transport uses authenticated loopback URL");
    var serializer = new XmlSerializer(typeof(Configuration));
    using var writer = new StringWriter();
    serializer.Serialize(writer, new Configuration { Channels = [new ChannelDefinition { Blocks = [new TimeBlock()] }] });
    var roundtrip = (Configuration)serializer.Deserialize(new StringReader(writer.ToString()))!;
    Check(roundtrip.Channels.Length == 1 && roundtrip.Channels[0].Blocks.Length == 1 && roundtrip.StreamToken.Length >= 32, "Configuration survives Jellyfin XML storage");
    var invalid = new Configuration { InternalBaseUrl = "https://example.com" };
    try { Scheduler.Validate(invalid); throw new Exception("Non-loopback transport accepted"); }
    catch (ArgumentException) { Check(true, "Remote transport URLs are rejected"); }
    invalid = new Configuration { Channels = [new ChannelDefinition { Blocks = [new TimeBlock { Id = "default" }] }] };
    try { Scheduler.Validate(invalid); throw new Exception("Reserved ID accepted"); }
    catch (ArgumentException) { Check(true, "Reserved block IDs are rejected"); }
    Console.WriteLine($"{checks} integration checks passed.");
}
finally { Directory.Delete(directory, true); }

public class Proxy<T> : DispatchProxy where T : class
{
    public Func<string, object?[]?, object?> Handler { get; set; } = null!;
    protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!.Name, args);
    public static T Make(Func<string, object?[]?, object?> handler)
    {
        var instance = Create<T, Proxy<T>>();
        ((Proxy<T>)(object)instance).Handler = handler;
        return instance;
    }
}
