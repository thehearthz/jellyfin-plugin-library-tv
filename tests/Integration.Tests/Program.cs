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
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Authorization;

int checks = 0;
void Check(bool ok, string label) { if (!ok) throw new Exception(label); checks++; Console.WriteLine("PASS: " + label); }
var directory = Path.Combine(Path.GetTempPath(), "library-tv-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    var paths = Proxy<IApplicationPaths>.Make((name, _) => name.StartsWith("get_") ? directory : null);
    var xml = Proxy<IXmlSerializer>.Make((name, _) => name.StartsWith("Deserialize") ? new Configuration() : null);
    var plugin = new Plugin(paths, xml);
    await StartupTests.Run(Check, paths);
    await PlaybackTests.Run(Check, directory);
    await TransportTests.Run(Check, directory);
    var services = new ServiceCollection();
    new Services().RegisterServices(services, null!);
    Check(services.Any(x => x.ServiceType == typeof(ILiveTvService) && x.ImplementationType == typeof(LiveTvService)), "Plugin registers its native Live TV provider in dependency injection");
    Check(services.Any(x => x.ServiceType == typeof(IChannel) && x.ImplementationType == typeof(LibraryChannel)), "Plugin registers a separate home library folder");
    Check(plugin.Id == new Guid("512a2a0d-5101-4fe8-9651-484f077a601a"), "Plugin GUID matches install catalog");
    Check(ChannelNumbers.Key("007.02") == ChannelNumbers.Key("7-2"), "Subchannel aliases and leading zeros share one conflict key");
    var orderedNumbers = new[] { "42", "7-10", "7.2", "7" }.OrderBy(ChannelNumbers.Key).ToArray();
    Check(orderedNumbers.SequenceEqual(new[] { "7", "7.2", "7-10", "42" }), "Custom channel numbers sort by major and subchannel rather than decimals or text");
    try { ChannelNumbers.ValidateUnique([new ChannelDefinition { Name = "First", Number = "7.2" }, new ChannelDefinition { Name = "Second", Number = "007-02" }]); throw new Exception("Duplicate number accepted"); }
    catch (ArgumentException ex) { Check(ex.Message.Contains("First") && ex.Message.Contains("Second"), "Conflicting channel numbers identify both channels"); }
    try { ChannelNumbers.Key("0"); throw new Exception("Zero channel number accepted"); }
    catch (ArgumentException) { Check(true, "Invalid channel numbers are rejected"); }
    var autoLibraryId = Guid.NewGuid();
    var autoFolders = new List<VirtualFolderInfo> { new() { ItemId = autoLibraryId.ToString("N"), Name = "Original library" } };
    var autoCatalog = new Catalog(Proxy<ILibraryManager>.Make((name, _) => name == "GetVirtualFolders" ? autoFolders : throw new NotSupportedException(name)));
    var autoConfig = new Configuration { AutoChannels = true, AutoLibraryIds = [autoLibraryId.ToString()], Channels = [new ChannelDefinition { Id = "manual", Number = "100" }] };
    Check(autoCatalog.PrepareAutomaticChannels(autoConfig) && autoConfig.AutomaticChannels[0].Number == "101", "Automatic channels skip reserved manual numbers and accept either GUID format");
    var legacyConfig = new Configuration { AutoChannels = true, AutoLibraryIds = [autoLibraryId.ToString()], Channels = [new ChannelDefinition { Id = "legacy-manual", Number = "1" }] };
    autoCatalog.PrepareAutomaticChannels(legacyConfig, preserveLegacyNumbers: true);
    Check(legacyConfig.AutomaticChannels[0].Number == "101", "Upgrading keeps the previous automatic channel number when available");
    autoConfig.Channels[0].Number = "999";
    var secondLibraryId = Guid.NewGuid();
    autoConfig.AutoLibraryIds = [autoLibraryId.ToString(), secondLibraryId.ToString()];
    autoFolders.Add(new VirtualFolderInfo { ItemId = secondLibraryId.ToString("N"), Name = "Added library" });
    autoCatalog.PrepareAutomaticChannels(autoConfig);
    Check(autoConfig.AutomaticChannels.First(x => Guid.Parse(x.LibraryId) == autoLibraryId).Number == "101", "Automatic numbers remain stable when manual channels and libraries change");
    autoConfig.AutomaticChannels[0].Name = "Custom station";
    autoConfig.AutomaticChannels[0].Number = "7-10";
    Check(autoCatalog.Channels(autoConfig).Any(x => x.Name == "Custom station" && x.Number == "7-10"), "Automatic channels use custom names and subchannel numbers");
    autoConfig.Channels[0].Number = "7.10";
    try { autoCatalog.Channels(autoConfig); throw new Exception("Manual/automatic number conflict accepted"); }
    catch (ArgumentException) { Check(true, "Manual and automatic channels cannot share the same number"); }
    var queryCount = 0;
    var scanCatalog = new Catalog(Proxy<ILibraryManager>.Make((name, _) => { if (name == "GetItemList") { queryCount++; return Array.Empty<BaseItem>(); } throw new NotSupportedException(name); }));
    var read = scanCatalog.CreateRefreshReader();
    var scanPool = new Pool { LibraryIds = [autoLibraryId.ToString()] };
    var firstRead = read(scanPool);
    firstRead.Add(new Media(Guid.NewGuid(), "Not cached", "", 10, "", 0, 0));
    Check(read(new Pool { LibraryIds = scanPool.LibraryIds, Shuffle = false }).Count == 0 && queryCount == 1, "Matching pools share one library query per refresh and never share mutable result lists");
    scanCatalog.CreateRefreshReader()(scanPool);
    Check(queryCount == 2, "A later refresh scans again so newly added media can appear");
    PluginPagesIntegration.Register(typeof(Jellyfin.Plugin.PluginPages.PluginInterface).Assembly);
    var registration = System.Text.Json.JsonDocument.Parse(Jellyfin.Plugin.PluginPages.PluginInterface.Registered!.Json).RootElement;
    Check(registration.GetProperty("id").GetString() == "library-tv-builder" && registration.GetProperty("url").GetString() == "/LibraryTV/builder", "Plugin Pages v3 receives the stable builder route through its reflection interface");
    using var pagesLifetime = new TestLifetime();
    var pagesGate = new StartupGate(pagesLifetime, Proxy<MediaBrowser.Controller.IServerApplicationHost>.Make((_, _) => true), (_, _) => Task.CompletedTask);
    new PluginPagesIntegration(NullLogger<PluginPagesIntegration>.Instance, pagesGate).Connect();
    Check(PluginPagesIntegration.Status.Contains("Not installed"), "Missing Plugin Pages keeps the dashboard builder available without failing startup");
    Check(typeof(Jellyfin.Plugin.LibraryTV.Controller).GetCustomAttributes<AuthorizeAttribute>().Single().Policy == "RequiresElevation", "Media browsing and configuration remain protected by the administrator policy");
    var builderShell = (Microsoft.AspNetCore.Mvc.ContentResult)new BuilderController().Builder();
    Check(builderShell.Content!.Contains("Library TV Builder") && !builderShell.Content.Contains("data-role=\"page\""), "Plugin Pages receives the builder fragment without a nested Jellyfin page layout");
    InternalItemsQuery? browseQuery = null;
    var movie = new Movie { Id = Guid.NewGuid(), Name = "Library movie", ProductionYear = 2024, Overview = new string('a', 300) };
    var browseLibrary = Proxy<ILibraryManager>.Make((name, args) =>
    {
        if (name == "GetItemsResult") { browseQuery = (InternalItemsQuery)args![0]!; return new QueryResult<BaseItem>(0, 120, new BaseItem[] { movie }); }
        if (name == "GetItemById") return (Guid)args![0]! == movie.Id ? movie : null;
        throw new NotSupportedException(name);
    });
    var browserCatalog = new Catalog(browseLibrary);
    var libraryId = Guid.NewGuid();
    var mediaPage = browserCatalog.Browse("movies", libraryId, " Library ", 36, 1000);
    Check(browseQuery!.ParentId == libraryId && browseQuery.SearchTerm == "Library" && browseQuery.StartIndex == 36 && browseQuery.Limit == 60, "Content picker queries are searchable, paginated, library-scoped and capped");
    Check(mediaPage.TotalRecordCount == 120 && mediaPage.Items[0].Name == movie.Name && mediaPage.Items[0].Type == "Movie" && mediaPage.Items[0].Overview.Length <= 241, "Content picker returns lightweight titles, types and bounded descriptions");
    Check(browserCatalog.Resolve([movie.Id, movie.Id, Guid.NewGuid()]).Length == 1, "Existing selections resolve names while ignoring duplicates and deleted media");
    try { browserCatalog.Browse("anything", default, null, 0, 20); throw new Exception("Unsupported media kind accepted"); }
    catch (ArgumentException) { Check(true, "Unknown content types are rejected"); }
    foreach (var page in plugin.GetPages())
        Check(plugin.GetType().Assembly.GetManifestResourceStream(page.EmbeddedResourcePath) is not null, "Dashboard HTML is embedded at the declared resource name");
    var library = Proxy<ILibraryManager>.Make((name, _) => name == "GetVirtualFolders" ? new List<VirtualFolderInfo>() : throw new NotSupportedException(name));
    var catalog = new Catalog(library);
    Check(catalog.Read(new Pool()).Count == 0, "Empty selectors do not expose the entire library");
    var mounted = new Video { Id = Guid.NewGuid(), Name = "Mounted", Path = Path.Combine(directory, "mounted.mp4"), RunTimeTicks = TimeSpan.FromMinutes(5).Ticks };
    var offlineMedia = new Video { Id = Guid.NewGuid(), Name = "Offline", Path = Path.Combine(directory, "offline-mount", "offline.mp4"), RunTimeTicks = TimeSpan.FromMinutes(5).Ticks };
    var mountCatalog = new Catalog(Proxy<ILibraryManager>.Make((name, args) => name == "GetItemById" ? new[] { mounted, offlineMedia }.FirstOrDefault(x => x.Id == (Guid)args![0]!) : throw new NotSupportedException(name)));
    var mountReader = mountCatalog.CreateRefreshReader();
    Check(mountReader(new Pool { ItemIds = [mounted.Id.ToString(), offlineMedia.Id.ToString()] }).Select(x => x.Title).SequenceEqual(["Mounted"]), "Refresh skips media whose storage folder is unavailable without checking every file");
    var scheduler = new Scheduler(catalog, paths, NullLogger<Scheduler>.Instance);
    await scheduler.Refresh(default);
    Check(scheduler.LastError is null && scheduler.LastRefresh is not null && File.Exists(scheduler.StatePath), "Schedule refresh persists state atomically");
    await scheduler.Refresh(default);
    Check(scheduler.Channels.Count == 0, "Empty configuration produces no channels");
    ILiveTvService tv = new LiveTvService(scheduler);
    Check(!(await tv.GetChannelsAsync(default)).Any(), "Native Live TV provider works against the real Jellyfin 12.2 interface");
    var source = await tv.GetChannelStream("test", "", default);
    Check(source.AnalyzeDurationMs == 3000, "Live playback bounds input analysis to three seconds instead of Jellyfin's long fallback");
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
    Check(folderSource.AnalyzeDurationMs == 3000, "Home folder playback uses the same bounded input analysis as Live TV");
    var tvSource = await tv.GetChannelStream("home-channel", "", default);
    Check(folderSource.Path == tvSource.Path && folderSource.IsInfiniteStream && !folderSource.SupportsDirectPlay, "Home folder reuses the existing protected live stream");
    var pageTwo = await folder.GetChannelItems(new InternalChannelItemQuery { UserId = viewer.Id, StartIndex = 1, Limit = 1 }, default);
    Check(pageTwo.Items.Count == 0 && pageTwo.TotalRecordCount == 1, "Home folder pagination reports a stable total");
    viewer.SetPermission(PermissionKind.EnableLiveTvAccess, false);
    Check((await folder.GetChannelItems(new InternalChannelItemQuery { UserId = viewer.Id }, default)).Items.Count == 0, "Viewers without Live TV permission cannot list channel items");
    var preservedId = Guid.NewGuid();
    var startAt = DateTimeOffset.UtcNow.AddMinutes(-5);
    var seededState = new Dictionary<string, ChannelState> { ["home-channel"] = new ChannelState {
        Guide = Enumerable.Range(0, 2000).Select(n => new Airing("home-channel", preservedId, "Program " + n, "test.mkv", startAt.AddMinutes(n), startAt.AddMinutes(n + 1), false)).ToList(),
        Rotations = new() { ["default"] = new Rotation { Last = preservedId, Seen = [preservedId] } }
    } };
    await File.WriteAllTextAsync(scheduler.StatePath, System.Text.Json.JsonSerializer.Serialize(seededState));
    var restarted = new Scheduler(catalog, paths, NullLogger<Scheduler>.Instance);
    await restarted.Refresh(default);
    Check(restarted.Current("home-channel", startAt.AddMinutes(1500))?.Title == "Program 1500" && restarted.Current("home-channel", startAt.AddMinutes(2000)) is null, "Indexed current-program lookup handles interior entries and exclusive end boundaries");
    var indexedProvider = new LiveTvService(restarted);
    var indexedGuide = (await indexedProvider.GetProgramsAsync("home-channel", startAt.AddMinutes(1500).UtcDateTime, startAt.AddMinutes(1501).UtcDateTime, default)).ToArray();
    Check(indexedGuide.Length == 1 && indexedGuide[0].Name == "Program 1500", "Live TV program queries use the channel index and exclusive time boundaries");
    var guideController = new Jellyfin.Plugin.LibraryTV.Controller(catalog, restarted, null!);
    Check(((System.Collections.IEnumerable)guideController.Guide("home-channel")).Cast<object>().Count() == 40 && ((System.Collections.IEnumerable)guideController.Guide("home-channel", -10, 10000)).Cast<object>().Count() == 200, "Guide previews have a small default and enforce an upper response limit");
    var editedConfig = plugin.Configuration;
    editedConfig.Channels[0].Enabled = false;
    await restarted.SaveConfiguration(editedConfig, default);
    await restarted.Refresh(default);
    var dormant = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, ChannelState>>(await File.ReadAllTextAsync(restarted.StatePath))!;
    Check(dormant["home-channel"].Rotations["default"].Last == preservedId && restarted.GuideFor("home-channel").Count == 0 && restarted.Current("home-channel", startAt.AddMinutes(1500)) is null, "Disabling a channel retains progress but removes its streams and published guide");
    editedConfig.Channels[0].Enabled = true;
    editedConfig.Channels[0].Number = "7.2";
    await restarted.SaveConfiguration(editedConfig, default);
    await restarted.Refresh(default);
    Check(restarted.Channels.Single().Number == "7.2" && restarted.Current("home-channel", startAt.AddMinutes(1500))?.ItemId == preservedId, "Re-enabling and renumbering preserve the existing schedule and episode cursor");
    try { await folder.GetChannelItemMediaInfo("removed", default); throw new Exception("Removed channel accepted"); }
    catch (ArgumentException) { Check(true, "Removed channels cannot open a new folder stream"); }
    var workingConfig = plugin.Configuration;
    plugin.UpdateConfiguration(new Configuration { AutoLibraryIds = ["not-a-library-id"] });
    var malformedRefresh = await guideController.Refresh(default);
    var malformedRebuild = await guideController.Rebuild("home-channel", default);
    plugin.UpdateConfiguration(workingConfig);
    Check(malformedRefresh is Microsoft.AspNetCore.Mvc.BadRequestObjectResult && malformedRebuild is Microsoft.AspNetCore.Mvc.BadRequestObjectResult, "Malformed saved settings return a readable bad-request error from refresh and rebuild");
    var serializer = new XmlSerializer(typeof(Configuration));
    using var writer = new StringWriter();
    serializer.Serialize(writer, new Configuration { Channels = [new ChannelDefinition { Blocks = [new TimeBlock()] }] });
    var roundtrip = (Configuration)serializer.Deserialize(new StringReader(writer.ToString()))!;
    Check(roundtrip.Channels.Length == 1 && roundtrip.Channels[0].Blocks.Length == 1 && roundtrip.StreamToken.Length >= 32, "Configuration survives Jellyfin XML storage");
    using var turnsWriter = new StringWriter();
    serializer.Serialize(turnsWriter, new Configuration { Channels = [new ChannelDefinition { Source = new Pool { EpisodesInARow = 5 } }] });
    var legacyXml = System.Text.RegularExpressions.Regex.Replace(turnsWriter.ToString(), @"\s*<EpisodesInARow>5</EpisodesInARow>", "");
    var turnsRoundtrip = (Configuration)serializer.Deserialize(new StringReader(turnsWriter.ToString()))!;
    var legacyTurns = (Configuration)serializer.Deserialize(new StringReader(legacyXml))!;
    Check(turnsRoundtrip.Channels[0].Source.EpisodesInARow == 5 && legacyTurns.Channels[0].Source.EpisodesInARow == 2, "Episodes in a row survives XML storage and older settings default to two");
    try { Scheduler.Validate(new Configuration { Channels = [new ChannelDefinition { Source = new Pool { EpisodesInARow = 101 } }] }); throw new Exception("Out-of-range episodes in a row accepted"); }
    catch (ArgumentException) { Check(true, "Out-of-range episodes in a row values are rejected"); }
    var privateConfig = new Configuration { StreamToken = new string('x', 64), InternalBaseUrl = "http://127.0.0.1:18096/private", Channels = [new ChannelDefinition { Id = "backup-channel", Name = "Backed up", Number = "42.1" }] };
    var backup = SettingsBackup.Export(privateConfig);
    var exported = System.Text.Json.JsonSerializer.Serialize(backup);
    Check(!exported.Contains("StreamToken") && !exported.Contains("InternalBaseUrl") && !exported.Contains(privateConfig.StreamToken) && !exported.Contains("18096"), "Settings backups exclude private transport tokens and server connection addresses");
    var destination = new Configuration { StreamToken = new string('y',64), InternalBaseUrl = "http://127.0.0.1:28096" };
    var imported = backup.ApplyTo(destination);
    Check(imported.Channels.Single().Number == "42.1" && imported.Channels.Single().Id == "backup-channel" && imported.StreamToken == destination.StreamToken && imported.InternalBaseUrl == destination.InternalBaseUrl, "Restoring settings retains channel IDs while preserving this server's token and connection address");
    backup.FormatVersion = 99;
    try { backup.ApplyTo(destination); throw new Exception("Unknown backup format accepted"); }
    catch (ArgumentException) { Check(destination.Channels.Length == 0, "Unsupported backups are rejected without changing current settings"); }
    backup.FormatVersion = 1; backup.Channels = [new ChannelDefinition { Number = "bad number" }];
    try { backup.ApplyTo(destination); throw new Exception("Invalid backup settings accepted"); }
    catch (ArgumentException) { Check(true, "Restored settings pass the same numbering and schedule validation as normal saves"); }
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
