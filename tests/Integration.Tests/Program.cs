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
using MediaBrowser.Controller.Channels;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;

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
    Check(services.Any(x => x.ServiceType == typeof(IChannel) && x.ImplementationType == typeof(LibraryChannel)), "Plugin registers a separate home library folder");
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
    var viewer = new User("viewer", "test", "test") { Id = Guid.NewGuid() };
    viewer.SetPermission(PermissionKind.EnableLiveTvAccess, true);
    var users = Proxy<IUserManager>.Make((name, args) => name == "GetUserById" && (Guid)args![0]! == viewer.Id ? viewer : null);
    var folder = new LibraryChannel(scheduler, users);
    Check(folder.IsEnabledFor(viewer.Id.ToString()) && !folder.IsEnabledFor(Guid.NewGuid().ToString()), "Folder visibility requires an existing viewer with Live TV access");
    var before = folder.DataVersion;
    plugin.UpdateConfiguration(new Configuration { GuideDays = 1, Channels = [new ChannelDefinition { Id = "home-channel", Name = "My channel", Number = "2" }] });
    await scheduler.Refresh(default);
    var listing = await folder.GetChannelItems(new InternalChannelItemQuery { UserId = viewer.Id }, default);
    Check(listing.Items.Count == 1 && listing.Items[0].Id == "home-channel" && listing.Items[0].IsLiveStream && listing.Items[0].Name.Contains("My channel"), "Home folder contains a stable playable item for each enabled channel");
    Check(before != folder.DataVersion, "Changing channel definitions invalidates Jellyfin's folder listing cache");
    var folderSource = (await folder.GetChannelItemMediaInfo("home-channel", default)).Single();
    var tvSource = await tv.GetChannelStream("home-channel", "", default);
    Check(folderSource.Path == tvSource.Path && folderSource.IsInfiniteStream && !folderSource.SupportsDirectPlay, "Home folder reuses the existing protected live stream");
    var pageTwo = await folder.GetChannelItems(new InternalChannelItemQuery { UserId = viewer.Id, StartIndex = 1, Limit = 1 }, default);
    Check(pageTwo.Items.Count == 0 && pageTwo.TotalRecordCount == 1, "Home folder pagination reports a stable total");
    viewer.SetPermission(PermissionKind.EnableLiveTvAccess, false);
    Check((await folder.GetChannelItems(new InternalChannelItemQuery { UserId = viewer.Id }, default)).Items.Count == 0, "Viewers without Live TV permission cannot list channel items");
    try { await folder.GetChannelItemMediaInfo("removed", default); throw new Exception("Removed channel accepted"); }
    catch (ArgumentException) { Check(true, "Removed channels cannot open a new folder stream"); }
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
