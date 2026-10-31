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
    await CreditsTests.Run(Check, directory, paths);
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
    // Smart channels: genres, years, decades and chosen titles in any combination.
    {
        var smartRoot = Path.Combine(directory, "smart"); Directory.CreateDirectory(smartRoot);
        var smartLibrary = Guid.NewGuid();
        MediaBrowser.Controller.Entities.Movies.Movie Film(string name, int year, params string[] genres) => new() { Id = Guid.NewGuid(), Name = name, Path = Path.Combine(smartRoot, name + ".mkv"), ProductionYear = year, Genres = genres, RunTimeTicks = TimeSpan.FromHours(1).Ticks };
        var films = new[] { Film("Laugh 1994", 1994, "Comedy"), Film("Laugh 2003", 2003, "Comedy"), Film("Scare 1996", 1996, "Horror"), Film("Drama 1999", 1999, "Drama", "Comedy"), Film("Odd 1971", 1971, "Drama") };
        var smartCatalog = new Catalog(Proxy<ILibraryManager>.Make((name, args) => name switch
        {
            "GetItemList" => films.Cast<MediaBrowser.Controller.Entities.BaseItem>().ToList(),
            "GetItemById" => films.FirstOrDefault(x => x.Id == (Guid)args![0]!),
            "GetVirtualFolders" => new List<VirtualFolderInfo>(),
            _ => throw new NotSupportedException(name)
        }));
        string[] Names(Pool pool) => smartCatalog.Read(pool).Select(x => x.Title).Order().ToArray();
        var libs = new[] { smartLibrary.ToString() };
        Check(Names(new Pool { LibraryIds = libs, Genres = ["Comedy", "Horror"] }).SequenceEqual(["Drama 1999", "Laugh 1994", "Laugh 2003", "Scare 1996"]), "Several genres match any one of them");
        Check(Names(new Pool { LibraryIds = libs, Decades = [1990] }).SequenceEqual(["Drama 1999", "Laugh 1994", "Scare 1996"]), "A decade matches every year in it");
        Check(Names(new Pool { LibraryIds = libs, Years = [1971, 2003] }).SequenceEqual(["Laugh 2003", "Odd 1971"]), "Several years match any one of them");
        Check(Names(new Pool { LibraryIds = libs, Genres = ["Comedy"], Decades = [1990] }).SequenceEqual(["Drama 1999", "Laugh 1994"]), "Genres and decades together must both match");
        Check(Names(new Pool { LibraryIds = libs, Genres = ["Horror"], Years = [2003], AlwaysItemIds = [films[4].Id.ToString()] }).SequenceEqual(["Odd 1971"]), "Chosen shows and movies air whether or not they match, and nothing else does when nothing else matches");
        Check(Names(new Pool { LibraryIds = libs, Genres = ["Comedy"], Decades = [1990], AlwaysItemIds = [films[4].Id.ToString()] }).SequenceEqual(["Drama 1999", "Laugh 1994", "Odd 1971"]), "A smart channel combines matches with chosen titles");
        Check(Names(new Pool { AlwaysItemIds = [films[2].Id.ToString()] }).SequenceEqual(["Scare 1996"]), "A channel with no libraries airs only its chosen titles");
        // A collection only links to its movies, so reading its library or the collection itself must open it.
        var collection = new MediaBrowser.Controller.Entities.Movies.BoxSet { Id = Guid.NewGuid(), Name = "Laugh collection", LinkedChildren = [new() { ItemId = films[0].Id }, new() { ItemId = films[1].Id }] };
        var linkedLibrary = Proxy<ILibraryManager>.Make((name, args) => name switch
        {
            "GetItemList" => ((InternalItemsQuery)args![0]!).ParentId == smartLibrary ? new List<MediaBrowser.Controller.Entities.BaseItem> { collection } : new List<MediaBrowser.Controller.Entities.BaseItem>(),
            "GetItemById" => (Guid)args![0]! == collection.Id ? collection : films.FirstOrDefault(x => x.Id == (Guid)args![0]!),
            "GetItemsResult" or "GetVirtualFolders" => throw new NotSupportedException(name),
            _ => throw new NotSupportedException(name)
        });
        var linkedCatalog = new Catalog(linkedLibrary);
        var previousLibrary = MediaBrowser.Controller.Entities.BaseItem.LibraryManager;
        MediaBrowser.Controller.Entities.BaseItem.LibraryManager = linkedLibrary;
        try
        {
            Check(linkedCatalog.Read(new Pool { LibraryIds = libs }).Select(x => x.Title).Order().SequenceEqual(["Laugh 1994", "Laugh 2003"]), "A Collections library airs the movies its collections link to");
            Check(linkedCatalog.Read(new Pool { AlwaysItemIds = [collection.Id.ToString()] }).Select(x => x.Title).Order().SequenceEqual(["Laugh 1994", "Laugh 2003"]), "A chosen collection airs its movies");
            Check(linkedCatalog.Read(new Pool { ItemIds = [collection.Id.ToString()], Decades = [2000] }).Select(x => x.Title).SequenceEqual(["Laugh 2003"]), "Collection contents follow a pool's filters");
        }
        finally { MediaBrowser.Controller.Entities.BaseItem.LibraryManager = previousLibrary; }
        var smartConfig = new Configuration { AutoChannelStartNumber = 200, Channels = [new ChannelDefinition { Id = "m", Number = "200" }], SmartChannels = [
            new SmartChannel { Id = "one", Name = "Nineties comedy", LibraryIds = libs, Genres = ["Comedy"], Decades = [1990] },
            new SmartChannel { Id = "two", Name = "Fixed", Number = "201" }, new SmartChannel { Id = "three", Name = "Off", Number = "5", Enabled = false }] };
        Check(smartCatalog.PrepareAutomaticChannels(smartConfig) && smartConfig.SmartChannels[0].Number == "202", "Smart channels receive free numbers that skip manual and fixed ones");
        smartConfig.AutomaticCreditsSpeed = 8;
        var made = smartCatalog.Channels(smartConfig);
        Check(made.Where(x => x.Id.StartsWith("smart-")).All(x => x.CreditsSpeed == 8), "Smart channels use the credits speed set for automatic channels");
        Check(made.Count(x => x.Id.StartsWith("smart-")) == 2 && made.Single(x => x.Id == "smart-one").Source is { Genres: ["Comedy"], Decades: [1990] }, "Enabled smart channels become channels with their criteria as the content");
        smartConfig.SmartChannels[1].Number = "202";
        try { smartCatalog.Channels(smartConfig); throw new Exception("Duplicate smart number accepted"); }
        catch (ArgumentException) { Check(true, "A smart channel cannot share a number with another channel"); }
        foreach (var (label, bad) in new (string, SmartChannel)[] { ("a year before 1850", new() { Years = [1200] }), ("a decade not on a multiple of ten", new() { Decades = [1995] }), ("an unnamed smart channel", new() { Name = " " }) })
        {
            try { Scheduler.Validate(new Configuration { SmartChannels = [bad] }); throw new Exception("Accepted " + label); }
            catch (ArgumentException) { Check(true, "Rejected " + label); }
        }
    }
    autoConfig.AutomaticCreditsSpeed = 4;
    Check(autoCatalog.Channels(autoConfig).Where(x => x.Id.StartsWith("auto-")).All(x => x.CreditsSpeed == 4), "Automatic channels use the credits speed set for them");
    try { Scheduler.Validate(new Configuration { AutomaticCreditsSpeed = 3 }); throw new Exception("Bad credits speed accepted"); }
    catch (ArgumentException) { Check(true, "An unknown credits speed on automatic channels is rejected"); }
    autoConfig.AutomaticCreditsSpeed = 1;
    autoConfig.AutomaticCommercialCount = 3;
    Check(autoCatalog.Channels(autoConfig).Where(x => x.Id.StartsWith("auto-")).All(x => x.CommercialCount == 3), "Automatic channels get commercial breaks from the shared library");
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
    Check(Catalog.YearFromPath("/media/Commercials/1980s/Cola (1985) 1080p.mp4") == 1985 && Catalog.YearFromPath("/media/ads/80s/Cola.mp4") == 1980
        && Catalog.YearFromPath("/media/ads/1990s/clip.mkv") == 1990 && Catalog.YearFromPath("/media/ads/clip 1080p x264.mkv") is null, "Commercial years come from year or decade names in folders and files, ignoring resolutions and codecs");
    Check(Catalog.YearFromPath("/ads/Nickelodeon Gak '94.mp4") == 1994, "Short years with an apostrophe are recognised");
    MediaBrowser.Model.Entities.ChapterInfo Chapter(string name, double seconds) => new() { Name = name, StartPositionTicks = TimeSpan.FromSeconds(seconds).Ticks };
    var chapterLists = new Dictionary<Guid, List<MediaBrowser.Model.Entities.ChapterInfo>>();
    var chapterCatalog = new Catalog(Proxy<ILibraryManager>.Make((_, _) => null), null, Proxy<MediaBrowser.Controller.Chapters.IChapterManager>.Make((name, args) => name == "GetChapters" ? chapterLists[(Guid)args![0]!] : throw new NotSupportedException(name)));
    var endCredits = new Media(Guid.NewGuid(), "Episode", "e.mkv", 1320, "show", 1, 1);
    var openingOnly = endCredits with { Id = Guid.NewGuid() };
    var midCredits = endCredits with { Id = Guid.NewGuid() };
    chapterLists[endCredits.Id] = [Chapter("Intro", 0), Chapter("Episode", 60), Chapter("End Credits", 1250)];
    chapterLists[openingOnly.Id] = [Chapter("Opening Credits", 0), Chapter("Episode", 90)];
    chapterLists[midCredits.Id] = [Chapter("Episode", 0), Chapter("Credits", 1200), Chapter("Stinger", 1290)];
    var creditsAt = chapterCatalog.CreateCreditsReader();
    Check(creditsAt(endCredits) == (1250, 1320) && creditsAt(openingOnly) is null && creditsAt(midCredits) == (1200, 1290), "Credits are found from chapter names, ignoring opening credits and ending before a scene after them");
    // A shared commercial library is sorted by network and decade, then used by channels without their own clips.
    var sharedFolder = Path.Combine(directory, "shared commercials");
    Directory.CreateDirectory(sharedFolder);
    Video Clip(string file) => new() { Id = Guid.NewGuid(), Name = file, Path = Path.Combine(sharedFolder, file), RunTimeTicks = TimeSpan.FromSeconds(30).Ticks };
    var sharedClips = new[] { Clip("Nickelodeon Gak '94.mp4"), Clip("Adult Swim bump 2004.mp4"), Clip("Cola.mp4") };
    var kidsMovie = new Movie { Id = Guid.NewGuid(), Name = "Kids movie", Path = Path.Combine(sharedFolder, "kids.mkv"), RunTimeTicks = TimeSpan.FromMinutes(30).Ticks, ProductionYear = 1996, Studios = ["Nickelodeon"] };
    var sharedCatalog = new Catalog(Proxy<ILibraryManager>.Make((name, args) => name switch
    {
        "GetItemById" => sharedClips.Cast<BaseItem>().Append(kidsMovie).FirstOrDefault(x => x.Id == (Guid)args![0]!),
        "GetVirtualFolders" => new List<VirtualFolderInfo>(),
        _ => throw new NotSupportedException(name)
    }));
    var sharedPaths = Proxy<IApplicationPaths>.Make((_, _) => sharedFolder);
    var keptConfig = plugin.Configuration;
    plugin.UpdateConfiguration(new Configuration { GuideDays = 1, SharedCommercials = new Pool { ItemIds = sharedClips.Select(x => x.Id.ToString()).ToArray() },
        Channels = [new ChannelDefinition { Id = "kids", Name = "Kids", CommercialCount = 1, MovieBreaks = 0, Source = new Pool { ItemIds = [kidsMovie.Id.ToString()] } }] });
    var sharedScheduler = new Scheduler(sharedCatalog, sharedPaths, NullLogger<Scheduler>.Instance);
    Planner.MinimumVariety = 1; // A three-clip library: this checks accurate matching, not widening for variety.
    await sharedScheduler.Refresh(default);
    Planner.MinimumVariety = 8;
    plugin.UpdateConfiguration(keptConfig);
    Check(sharedScheduler.ClipLibrary.Select(x => $"{x.Kind}:{x.Network}:{x.Decade}:{x.Count}").SequenceEqual(["Commercials:Adult Swim:2000:1", "Commercials:Nickelodeon:1990:1", "Commercials:General:0:1"]),
        "Each refresh sorts the shared commercial library by the network and year in its file names");
    Check(sharedScheduler.GuideFor("kids").Where(x => x.Commercial).All(x => x.ItemId == sharedClips[0].Id) && sharedScheduler.GuideFor("kids").Any(x => x.Commercial),
        "A channel without its own commercials plays the shared commercials sorted to its programs' network");
    // The clip browser lists clips as filed, and rules and edits re-file them without renaming files.
    var browsed = sharedScheduler.BrowseClips(new ClipFilter(null, null, null, null, null), 0, 50);
    Check(browsed.Total == 3 && browsed.Items.Select(x => x.Network).SequenceEqual(["Adult Swim", "Nickelodeon", ""]) && browsed.Items[1].Year == 1994 && browsed.Networks.SequenceEqual(["Adult Swim", "Nickelodeon"])
        && sharedScheduler.BrowseClips(new ClipFilter("cola", null, null, null, null), 0, 50).Items.Single().Why.StartsWith("General")
        && sharedScheduler.ClipIds(new ClipFilter(null, null, "General", null, "network")).Length == 1 && sharedScheduler.BrowseClips(new ClipFilter(null, null, null, 2000, null), 0, 50).Total == 1,
        "The clip browser lists every shared clip with its network and year, and filters by name, network, decade and problem");
    var kidsMatches = sharedScheduler.MatchesFor(sharedClips[0].Id.ToString("N"));
    Check(kidsMatches is { Total: 1 } && kidsMatches.Programs[0].StartsWith("Kids movie") && sharedScheduler.MatchesFor(sharedClips[1].Id.ToString("N"))!.Total == 0 && sharedScheduler.MatchesFor(sharedClips[2].Id.ToString("N"))!.Total == 0,
        "A clip says which programs it can play with: its network's programs, and none for a network no program has");
    plugin.UpdateConfiguration(new Configuration { GuideDays = 1, SharedCommercials = new Pool { ItemIds = sharedClips.Select(x => x.Id.ToString()).ToArray() },
        ClipOverrides = [new ClipEdit { ItemId = sharedClips[2].Id.ToString(), Network = "Nickelodeon", Year = 1997 }],
        ClipRules = [new ClipEdit { Contains = "adult swim", Disabled = true }],
        Channels = [new ChannelDefinition { Id = "kids", Name = "Kids", CommercialCount = 1, MovieBreaks = 0, Source = new Pool { ItemIds = [kidsMovie.Id.ToString()] } }] });
    Planner.MinimumVariety = 1;
    await sharedScheduler.Refresh(default);
    Planner.MinimumVariety = 8;
    plugin.UpdateConfiguration(keptConfig);
    var rebrowsed = sharedScheduler.BrowseClips(new ClipFilter(null, null, null, null, null), 0, 50).Items;
    Check(rebrowsed.Single(x => x.Title.StartsWith("Cola")) is { Network: "Nickelodeon", Year: 1997 } cola && cola.Sources.SequenceEqual(["edited"]) && rebrowsed.Single(x => x.Title.StartsWith("Adult")) is { Disabled: true } off && off.Sources[0].StartsWith("rule"),
        "Edits and rules show in the browser: a re-filed clip, and a clip turned off by a rule");
    Check(sharedScheduler.ClipLibrary.Select(x => $"{x.Network}:{x.Decade}:{x.Count}").SequenceEqual(["Nickelodeon:1990:2"]) && sharedScheduler.GuideFor("kids").Where(x => x.Commercial).All(x => x.ItemId != sharedClips[1].Id),
        "The coverage summary and the guide use the re-filed clips, and a turned-off clip never plays");
    var horrorSeries = new MediaBrowser.Controller.Entities.TV.Series { Id = Guid.NewGuid(), Name = "Night Terrors", ProductionYear = 1987, Genres = ["Horror"], Studios = ["Adult Swim"], ProviderIds = new() { ["Tvdb"] = "4242" } };
    var copySeries = new MediaBrowser.Controller.Entities.TV.Series { Id = Guid.NewGuid(), Name = "Night Terrors", ProductionYear = 1987, Genres = ["Horror"], Studios = ["Adult Swim"], ProviderIds = new() { ["Tvdb"] = "4242" } };
    MediaBrowser.Controller.Entities.TV.Episode Ep(MediaBrowser.Controller.Entities.TV.Series series, int number, string folder) => new() { Id = Guid.NewGuid(), Name = "Part " + number, SeriesId = series.Id, SeriesName = series.Name, ParentIndexNumber = 1, IndexNumber = number, Path = Path.Combine(directory, folder + number + ".mkv"), RunTimeTicks = TimeSpan.FromMinutes(22).Ticks, PremiereDate = new DateTime(1988, 1, number) };
    var showFiles = new BaseItem[] { Ep(horrorSeries, 1, "hd"), Ep(horrorSeries, 2, "hd"), Ep(copySeries, 2, "4k"), Ep(copySeries, 3, "4k") };
    var showLibrary = Guid.NewGuid();
    var showCatalog = new Catalog(Proxy<ILibraryManager>.Make((name, args) => name switch
    {
        "GetItemList" => showFiles.ToList(),
        "GetItemById" => new BaseItem[] { horrorSeries, copySeries }.FirstOrDefault(x => x.Id == (Guid)args![0]!),
        _ => throw new NotSupportedException(name)
    }));
    var showMedia = showCatalog.Read(new Pool { LibraryIds = [showLibrary.ToString()] });
    Check(showMedia.Select(x => x.Episode).SequenceEqual([1, 2, 3]) && showMedia.Select(x => x.Series).Distinct().Count() == 1, "The same show in two libraries is one show, and a duplicated episode airs once");
    Check(showMedia.All(x => x.Year == 1988 && x.Genres!.Contains("Horror")), "Episodes use their air date for commercial matching and inherit the show's genres for restrictions");
    Check(showMedia.All(x => x.Studios!.SequenceEqual(["Adult Swim"]) && x.Show == "Night Terrors"), "Episodes inherit the show's network and title for commercial and bumper matching");
    Check(showCatalog.Read(new Pool { LibraryIds = [showLibrary.ToString()], Genres = ["horror"] }).Count == 3, "Genre filters also match episodes whose genres are set on the show");
    var sixteenNine = new ChannelDefinition();
    var dvd = StreamPlan.For(new SourceFormat("h264", "yuv420p", 8, false, "aac", 2, 48000, 720, 480, 8.0 / 9, 4.0 / 3), sixteenNine);
    Check(!dvd.CopyVideo && dvd.CopyAudio && dvd.FrameWidth == 854 && dvd.FrameHeight == 480, "A 4:3 DVD episode is encoded into a 16:9 picture at its own height, keeping its audio");
    var scope = StreamPlan.For(new SourceFormat("h264", "yuv420p", 8, false, "aac", 2, 48000, 1920, 800, 1, 2.4), sixteenNine);
    Check(!scope.CopyVideo && scope.FrameWidth == 1280 && scope.FrameHeight == 720, "A 2.4:1 film is letterboxed into a 720p picture, keeping the encoding light");
    var anamorphic = StreamPlan.For(new SourceFormat("h264", "yuv420p", 8, false, "aac", 2, 48000, 720, 480, 32.0 / 27, 16.0 / 9), sixteenNine);
    var flat = StreamPlan.For(new SourceFormat("h264", "yuv420p", 8, false, "aac", 2, 48000, 1920, 1040, 1, 1920.0 / 1040), sixteenNine);
    Check(anamorphic.CopyVideo && flat.CopyVideo, "Widescreen DVD video and 1.85:1 films are close enough to 16:9 to play without encoding");
    Check(StreamPlan.For(new SourceFormat("h264", "yuv420p", 8, false, "aac", 2, 48000, 1920, 1080, 1, 16.0 / 9), sixteenNine).CopyVideo
        && StreamPlan.For(new SourceFormat("h264", "yuv420p", 8, false, "aac", 2, 48000, 1280, 720), sixteenNine).CopyVideo, "16:9 H.264 programs are still copied without encoding");
    Check(StreamPlan.For(new SourceFormat("h264", "yuv420p", 8, false, "aac", 2, 48000, 640, 480, 1, 4.0 / 3), new ChannelDefinition { PictureShape = "4:3" }).CopyVideo
        && StreamPlan.For(new SourceFormat("h264", "yuv420p", 8, false, "aac", 2, 48000, 640, 480, 1, 4.0 / 3), new ChannelDefinition { PictureShape = "off" }).CopyVideo, "4:3 channels copy 4:3 programs, and the shape can be turned off");
    CaptionTrack Track(string language, bool forced = false, bool sdh = false, bool isDefault = false, string codec = "subrip") => new(codec, language, forced, sdh, isDefault, null, 0);
    var tracks = new[] { Track("eng", forced: true), Track("spa"), Track("eng"), Track("eng", sdh: true), Track("fre"), Track("eng", codec: "eia_608") };
    Check(CaptionTrack.Pick(tracks, "on", "English") == tracks[3] && CaptionTrack.Pick(tracks, "forced", "en") == tracks[0] && CaptionTrack.Pick(tracks, "on", "French") == tracks[4]
        && CaptionTrack.Pick(tracks, "on", "ger") is null && CaptionTrack.Pick(tracks, "off", "eng") is null && CaptionTrack.Pick([tracks[5]], "on", "") is null,
        "Caption tracks are chosen by language name or code, preferring SDH over forced-only tracks, and unsupported or missing languages are skipped");
    var subtitled = new SourceFormat("h264", "yuv420p", 8, false, "aac", 2, 48000, 1920, 1080, 1, 16.0 / 9, [Track("eng")]);
    Check(!StreamPlan.For(subtitled, new ChannelDefinition { Captions = "on" }).CopyVideo && StreamPlan.For(subtitled, new ChannelDefinition()).CopyVideo
        && StreamPlan.For(subtitled, new ChannelDefinition { Captions = "on" }).Describe().Contains("captions burned in (eng)"), "Captions mean encoding the video, only when a caption track is used, and Channel health says so");
    AudioTrack Sound(string language, int index, bool isDefault = false, bool commentary = false, string codec = "aac", int channels = 2) => new(codec, language, channels, 48000, isDefault, commentary, index);
    var sounds = new[] { Sound("spa", 0, isDefault: true, codec: "ac3", channels: 6), Sound("eng", 1, commentary: true), Sound("eng", 2), Sound("fre", 3) };
    Check(AudioTrack.Pick(sounds, "English") == sounds[2] && AudioTrack.Pick(sounds, "") == sounds[0] && AudioTrack.Pick(sounds, "German") == sounds[0] && AudioTrack.Pick(sounds, "fr") == sounds[3] && AudioTrack.Pick([], "eng") is null,
        "The preferred audio language picks that track, skipping commentary, and otherwise the file's default track plays");
    var titled = new[] { Sound("eng", 0, isDefault: true) with { Title = "Stereo" }, Sound("eng", 1, codec: "ac3", channels: 6) with { Title = "Dolby Surround" }, Sound("eng", 2, codec: "ac3", channels: 2) };
    Check(AudioTrack.Pick(titled, "", "dolby") == titled[1] && AudioTrack.Pick(titled, "", "nothing") == titled[0] && AudioTrack.Pick([Sound("eng", 0), Sound("eng", 1, channels: 6)], "") is { Index: 1 },
        "A preferred audio title picks the matching track, and without a default the track with the most channels plays");
    var dubbed = new SourceFormat("h264", "yuv420p", 8, false, "ac3", 6, 48000, 1920, 1080, 1, 16.0 / 9, null, sounds);
    var englishPlan = StreamPlan.For(dubbed, new ChannelDefinition { AudioLanguage = "eng" });
    var defaultPlan = StreamPlan.For(dubbed, new ChannelDefinition());
    Check(englishPlan.AudioIndex == 2 && englishPlan.CopyAudio && englishPlan.Describe().Contains("(eng audio)") && defaultPlan.AudioIndex == 0 && !defaultPlan.CopyAudio,
        "The chosen audio track decides whether audio is copied, and Channel health names its language");
    var pgsPlan = StreamPlan.For(subtitled with { Captions = [Track("eng", codec: "hdmv_pgs_subtitle") with { EmbeddedIndex = 2 }] }, new ChannelDefinition { Captions = "on" });
    var pgsArgs = ProgramStreamer.Command("ffmpeg", new Airing("c", Guid.NewGuid(), "Show", "/media/show.mkv", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), false), pgsPlan, DateTimeOffset.UtcNow).ArgumentList;
    Check(pgsArgs[pgsArgs.IndexOf("-filter_complex") + 1].StartsWith("[0:v:0][0:s:2]overlay") && pgsArgs[pgsArgs.IndexOf("-map") + 1] == "[video]" && !pgsArgs.Contains("-vf"),
        "Picture subtitles from discs are overlaid from the same file, so they are seeked with the video");
    Check(ProgramStreamer.Ratio("4:3") == 4.0 / 3 && ProgramStreamer.Ratio("0:1") is null && ProgramStreamer.Ratio("N/A") is null, "Aspect ratios from Jellyfin and FFprobe are read, and unknown ones are ignored");
    var part = DateTimeOffset.UtcNow;
    var parts = new List<Airing> { new("g", preservedIdFor(1), "Show", "s.mkv", part, part.AddMinutes(11), false), new("g", preservedIdFor(9), "Ad", "a.mp4", part.AddMinutes(11), part.AddMinutes(12), true),
        new("g", preservedIdFor(1), "Show", "s.mkv", part.AddMinutes(12), part.AddMinutes(23), false, Offset: 660), new("g", preservedIdFor(9), "Ad", "a.mp4", part.AddMinutes(23), part.AddMinutes(24), true) };
    var listings = LiveTvService.Listings(parts);
    Check(listings.Count == 2 && listings[0].End == part.AddMinutes(23) && listings[1].Commercial, "The Live TV guide lists a program once, including the breaks inside it");
    static Guid preservedIdFor(int n) => new(n, 0, 0, new byte[8]);
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
    using var rulesWriter = new StringWriter();
    serializer.Serialize(rulesWriter, new Configuration { SharedCommercials = new Pool { FolderIds = [Guid.Empty.ToString()] }, SharedBumpers = new Pool { LibraryIds = [Guid.Empty.ToString()] }, Restrictions = [new ContentRule { Start = "07:00", End = "20:30", Days = [DayOfWeek.Monday], Genres = ["Horror", "Thriller"] }], Channels = [new ChannelDefinition { AlwaysEncode = true, MatchCommercialEra = false, MatchCommercialNetwork = false, CommercialNetwork = "Toonami", Blocks = [new TimeBlock { CommercialNetwork = "Adult Swim" }],
        EpisodeBreaks = 3, MovieBreaks = 0, PictureShape = "4:3", Bumpers = new Pool { FolderIds = [Guid.Empty.ToString()] }, Captions = "forced", CaptionLanguage = "English", AudioLanguage = "Japanese", CreditsSpeed = 4 }], AutomaticCommercialCount = 5,
        ClipRules = [new ClipEdit { Contains = "bump", Kind = "bumper", Disabled = true }], ClipOverrides = [new ClipEdit { ItemId = Guid.Empty.ToString(), Network = "Toonami", Year = 2001, Show = "Gumball" }] });
    var rules = (Configuration)serializer.Deserialize(new StringReader(rulesWriter.ToString()))!;
    Check(rules.Restrictions.Single().Genres.SequenceEqual(["Horror", "Thriller"]) && rules.Restrictions[0].Days.SequenceEqual([DayOfWeek.Monday]) && rules.Channels[0].AlwaysEncode && !rules.Channels[0].MatchCommercialEra
        && !rules.Channels[0].MatchCommercialNetwork && rules.Channels[0].CommercialNetwork == "Toonami" && rules.Channels[0].Blocks[0].CommercialNetwork == "Adult Swim", "Content restrictions, commercial matching and playback settings survive XML storage");
    Check(rules.Channels[0].EpisodeBreaks == 3 && rules.Channels[0].MovieBreaks == 0 && rules.Channels[0].PictureShape == "4:3" && rules.Channels[0].Bumpers.FolderIds.Length == 1, "Breaks, picture shape and bumpers survive XML storage");
    Check(rules.SharedCommercials.FolderIds.Length == 1 && rules.SharedBumpers.LibraryIds.Length == 1, "The shared commercial and bumper libraries survive XML storage");
    Check(rules.ClipRules.Single() is { Contains: "bump", Kind: "bumper", Disabled: true } && rules.ClipOverrides.Single() is { Network: "Toonami", Year: 2001, Show: "Gumball", Disabled: null }, "Clip rules and edits survive XML storage");
    var olderXml = System.Text.RegularExpressions.Regex.Replace(rulesWriter.ToString().Replace("<AlwaysEncode>true</AlwaysEncode>", "<Transcode>true</Transcode>"), @"\s*<MatchCommercialEra>false</MatchCommercialEra>", "");
    if (olderXml == rulesWriter.ToString()) throw new Exception("Test XML was not rewritten");
    var older = (Configuration)serializer.Deserialize(new StringReader(olderXml))!;
    var legacyChannel = (Configuration)serializer.Deserialize(new StringReader(System.Text.RegularExpressions.Regex.Replace(olderXml, @"\s*<MatchCommercialNetwork>false</MatchCommercialNetwork>", "")))!;
    var oldestXml = System.Text.RegularExpressions.Regex.Replace(olderXml, @"\s*<(EpisodeBreaks|MovieBreaks|PictureShape|Captions|CaptionLanguage|AudioLanguage)>[^<]*</\1>|\s*<Bumpers>.*?</Bumpers>", "", System.Text.RegularExpressions.RegexOptions.Singleline);
    var oldest = (Configuration)serializer.Deserialize(new StringReader(oldestXml))!;
    Check(oldestXml != olderXml && legacyChannel.Channels[0].MatchCommercialNetwork && oldest.Channels[0].EpisodeBreaks == 1 && oldest.Channels[0].MovieBreaks == 2 && oldest.Channels[0].PictureShape == "16:9" && oldest.Channels[0].Bumpers.FolderIds.Length == 0,
        "Channels saved by older versions match networks, break inside programs and use a 16:9 picture");
    Check(!older.Channels[0].AlwaysEncode && older.Channels[0].MatchCommercialEra, "Channels saved by older versions switch to encode-only-when-needed playback and era-matched commercials");
    Check(rules.Channels[0].Captions == "forced" && rules.Channels[0].CaptionLanguage == "English" && rules.Channels[0].AudioLanguage == "Japanese" && rules.Channels[0].CreditsSpeed == 4 && rules.AutomaticCommercialCount == 5 && oldest.Channels[0].Captions == "off" && oldest.Channels[0].AudioLanguage == "", "Caption settings survive XML storage, and older channels start with captions off");
    foreach (var bad in new[] { new ContentRule { Genres = [], Tags = [] }, new ContentRule { Start = "9pm" }, new ContentRule { Days = [] } })
    {
        try { Scheduler.Validate(new Configuration { Restrictions = [bad] }); throw new Exception("Invalid content restriction accepted"); }
        catch (ArgumentException) { }
    }
    Check(true, "Content restrictions need a genre or tag, valid times and at least one day");
    try { Scheduler.Validate(new Configuration { Channels = [new ChannelDefinition { CommercialNetwork = new string('n', 101) }] }); throw new Exception("Overlong network accepted"); }
    catch (ArgumentException) { Check(true, "Overlong commercial network names are rejected"); }
    foreach (var bad in new[] { new ChannelDefinition { EpisodeBreaks = 6 }, new ChannelDefinition { MovieBreaks = -1 }, new ChannelDefinition { PictureShape = "21:9" }, new ChannelDefinition { Captions = "always" }, new ChannelDefinition { AudioLanguage = new string('a', 41) }, new ChannelDefinition { CreditsSpeed = 9 } })
    {
        try { Scheduler.Validate(new Configuration { Channels = [bad] }); throw new Exception("Invalid break or picture setting accepted"); }
        catch (ArgumentException) { }
    }
    Check(true, "Break counts outside 0–5 and unknown picture shapes are rejected");
    try { Scheduler.Validate(new Configuration { Channels = [new ChannelDefinition { Source = new Pool { EpisodesInARow = 101 } }] }); throw new Exception("Out-of-range episodes in a row accepted"); }
    catch (ArgumentException) { Check(true, "Out-of-range episodes in a row values are rejected"); }
    var privateConfig = new Configuration { StreamToken = new string('x', 64), InternalBaseUrl = "http://127.0.0.1:18096/private", Channels = [new ChannelDefinition { Id = "backup-channel", Name = "Backed up", Number = "42.1" }], Restrictions = [new ContentRule()] };
    var backup = SettingsBackup.Export(privateConfig);
    var exported = System.Text.Json.JsonSerializer.Serialize(backup);
    Check(!exported.Contains("StreamToken") && !exported.Contains("InternalBaseUrl") && !exported.Contains(privateConfig.StreamToken) && !exported.Contains("18096"), "Settings backups exclude private transport tokens and server connection addresses");
    var destination = new Configuration { StreamToken = new string('y',64), InternalBaseUrl = "http://127.0.0.1:28096" };
    var imported = backup.ApplyTo(destination);
    Check(imported.Restrictions.Single().Genres.SequenceEqual(["Horror"]), "Settings backups include content restrictions");
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
    foreach (var (label, bad) in new (string, ChannelDefinition)[] {
        ("a relative watermark path", new() { Watermark = "logo.png" }), ("a watermark that is not an image", new() { Watermark = "/etc/passwd" }), ("an unknown padding", new() { PadMinutes = 7 }),
        ("an unknown break mode", new() { BreakMode = "random" }), ("a longest break shorter than the usual", new() { CommercialCount = 3, CommercialCountMax = 2 }),
        ("an unknown block start mode", new() { Blocks = [new TimeBlock { StartMode = "later" }] }), ("an unknown pool order", new() { Source = new Pool { Order = "backwards" } }),
        ("a last year before the first", new() { Source = new Pool { MinYear = 2000, MaxYear = 1990 } }) })
    {
        try { Scheduler.Validate(new Configuration { Channels = [bad] }); throw new Exception("Accepted " + label); }
        catch (ArgumentException) { Check(true, "Rejected " + label); }
    }
    Scheduler.Validate(new Configuration { Channels = [new ChannelDefinition { PadMinutes = 30, BreakMode = "interval", CommercialCount = 1, CommercialCountMax = 3, Watermark = OperatingSystem.IsWindows() ? @"C:\logo.png" : "/media/logo.png",
        Blocks = [new TimeBlock { StartMode = "exact", Fill = "fit", MaxLateMinutes = 10, GuideTitle = "Saturday Morning" }], Source = new Pool { Order = "chronological", MaxRating = "TV-PG", MinYear = 1980, MaxYear = 1999, Turns = [new ShowTurn { Show = "X", Episodes = 3 }] } }] });
    Check(true, "The new schedule, break, pool and image settings pass validation");
    var hidden = new List<Airing> { new("c", Guid.NewGuid(), "A", "a", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(10), false), new("c", Guid.NewGuid(), "Ad", "ad", DateTimeOffset.UnixEpoch.AddMinutes(10), DateTimeOffset.UnixEpoch.AddMinutes(11), true), new("c", Guid.NewGuid(), "B", "b", DateTimeOffset.UnixEpoch.AddMinutes(11), DateTimeOffset.UnixEpoch.AddMinutes(20), false) };
    var shown = LiveTvService.Listings(hidden, true);
    Check(shown.Count == 2 && shown[0].End == DateTimeOffset.UnixEpoch.AddMinutes(11) && LiveTvService.Listings(hidden).Count == 3, "A guide that hides breaks runs the program through them");
    {
        var gpuSource = new SourceFormat("hevc", "yuv420p", 8, false, "aac", 2, 48000, 1280, 720, 1, 16.0 / 9);
        var gpuAiring = new Airing("c", Guid.NewGuid(), "t", "/m.mkv", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), false);
        System.Collections.ObjectModel.Collection<string> Command(string encoderName, string quality = "balanced", Watermark? mark = null)
        {
            var plan = StreamPlan.For(gpuSource, new ChannelDefinition { EncodeQuality = quality }) with { Encoder = encoderName, Device = encoderName == "vaapi" ? "/dev/dri/renderD129" : "", Mark = mark };
            return ProgramStreamer.Command("ffmpeg", gpuAiring, plan, DateTimeOffset.UtcNow).ArgumentList;
        }
        var nv = Command("nvenc", "high");
        Check(nv.Contains("h264_nvenc") && !nv.Contains("libx264") && nv[nv.IndexOf("-cq") + 1] == "23" && nv[nv.IndexOf("-preset") + 1] == "p4" && nv.Contains("-forced-idr") && nv[nv.IndexOf("-vf") + 1].EndsWith("format=yuv420p"), "NVENC encodes with the chosen quality, and the picture filters stay in software");
        var qs = Command("qsv", "best");
        Check(qs.Contains("h264_qsv") && qs[qs.IndexOf("-global_quality") + 1] == "19" && !qs.Contains("-vaapi_device"), "Quick Sync encodes with the chosen quality");
        var va = Command("vaapi");
        Check(va.Contains("h264_vaapi") && va[va.IndexOf("-vaapi_device") + 1] == "/dev/dri/renderD129" && va[va.IndexOf("-vf") + 1].EndsWith("format=nv12,hwupload") && va[va.IndexOf("-qp") + 1] == "26", "VAAPI names its device and uploads the finished picture to the card");
        var vaMark = Command("vaapi", mark: new Watermark("/logo.png", "top-left", 70, 100, 10));
        Check(vaMark[vaMark.IndexOf("-filter_complex") + 1].EndsWith("format=yuv420p,format=nv12,hwupload[video]"), "VAAPI uploads after the logo is drawn");
        var cpuCommand = Command("cpu");
        Check(cpuCommand.Contains("libx264") && !cpuCommand.Contains("-vaapi_device") && StreamPlan.For(gpuSource, new ChannelDefinition()).Encoder == "cpu", "The CPU encoder is the default and unchanged");
        var copied = ProgramStreamer.Command("ffmpeg", gpuAiring, StreamPlan.Copy with { Encoder = "vaapi" }, DateTimeOffset.UtcNow).ArgumentList;
        Check(copied.Contains("copy") && !copied.Contains("-vaapi_device") && !copied.Contains("h264_vaapi"), "Copied programs never touch the encoder");
        foreach (var (label, bad) in new (string, Configuration)[] { ("an unknown hardware encoder", new() { HardwareEncoder = "cuda" }), ("a VAAPI device outside /dev", new() { HardwareDevice = "/etc/passwd" }), ("a VAAPI device with ..", new() { HardwareDevice = "/dev/../etc/x" }) })
        {
            try { Scheduler.Validate(bad); throw new Exception("Accepted " + label); }
            catch (ArgumentException) { Check(true, "Rejected " + label); }
        }
        try { Scheduler.Validate(new Configuration { Channels = [new ChannelDefinition { EncodeWith = "gpu" }] }); throw new Exception("Bad encode-with accepted"); }
        catch (ArgumentException) { Check(true, "A channel can only be pinned to the CPU"); }
        Scheduler.Validate(new Configuration { HardwareEncoder = "vaapi", HardwareDevice = "/dev/dri/renderD128", Channels = [new ChannelDefinition { EncodeWith = "cpu" }] });
        Check(true, "Hardware encoder settings pass validation");
    }
    {
        var hdrFormat = new SourceFormat("h264", "yuv420p", 8, false, "aac", 2, 48000, 1280, 720, 1, 16.0 / 9, Hdr: true);
        var hdrPlan = StreamPlan.For(hdrFormat, new ChannelDefinition());
        var hdrArgs = ProgramStreamer.Command("ffmpeg", new Airing("c", Guid.NewGuid(), "t", "/hdr.mkv", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), false), hdrPlan, DateTimeOffset.UtcNow).ArgumentList;
        Check(hdrPlan.ToneMap && !hdrPlan.CopyVideo && StreamPlan.For(hdrFormat with { Hdr = false }, new ChannelDefinition()).CopyVideo, "HDR video is never copied, even when it is 8-bit H.264");
        Check(hdrArgs[hdrArgs.IndexOf("-vf") + 1].Contains("zscale=t=linear") && hdrArgs[hdrArgs.IndexOf("-vf") + 1].Contains("tonemap=tonemap=hable") && hdrArgs[hdrArgs.IndexOf("-color_trc") + 1] == "bt709" && hdrPlan.Describe().Contains("tone-mapped"), "An HDR program gets the tone-mapping filters and BT.709 tags, and Channel health says so");
        Check(HighDynamicRange.Is("smpte2084") && HighDynamicRange.Is("ARIB-STD-B67") && !HighDynamicRange.Is("bt709") && !HighDynamicRange.Is(null), "PQ and HLG transfers count as HDR");
    }
    {
        var odd = new SourceFormat("hevc", "yuv420p", 8, false, "ac3", 6, 48000, 1920, 1080, 1, 16.0 / 9);
        string Arg(System.Diagnostics.ProcessStartInfo c, string name) => c.ArgumentList[c.ArgumentList.IndexOf(name) + 1];
        var airing = new Airing("c", Guid.NewGuid(), "t", "/m.mkv", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), false);
        var normal = StreamPlan.For(odd, new ChannelDefinition());
        var best = StreamPlan.For(odd, new ChannelDefinition { EncodeQuality = "best", EncodeHeight = 1080 });
        var low = StreamPlan.For(odd, new ChannelDefinition { EncodeQuality = "fast", EncodeHeight = 480 });
        var normalCommand = ProgramStreamer.Command("ffmpeg", airing, normal, DateTimeOffset.UtcNow);
        var bestCommand = ProgramStreamer.Command("ffmpeg", airing, best, DateTimeOffset.UtcNow);
        Check(normal.FrameHeight == 720 && best.FrameHeight == 1080 && best.FrameWidth == 1920 && low.FrameHeight == 480 && low.FrameWidth == 854, "The tallest encoded picture follows the channel's setting");
        Check(Arg(normalCommand, "-crf") == "23" && Arg(normalCommand, "-preset") == "ultrafast" && Arg(normalCommand, "-b:a") == "192k" && Arg(bestCommand, "-crf") == "17" && Arg(bestCommand, "-preset") == "faster" && Arg(bestCommand, "-b:a") == "256k"
            && Arg(ProgramStreamer.Command("ffmpeg", airing, low, DateTimeOffset.UtcNow), "-crf") == "27", "Encoding quality sets the x264 preset, quality and audio bitrate, with balanced matching earlier versions");
        var always = StreamPlan.For(odd, new ChannelDefinition { AlwaysEncode = true, EncodeHeight = 1080, EncodeQuality = "high" });
        Check(always.Normalize && always.FrameWidth == 1920 && always.Describe().Contains("1080p") && StreamPlan.For(odd, new ChannelDefinition { EncodeQuality = "bogus", EncodeHeight = 999 }).FrameHeight == 720, "Always encode uses the chosen height, and unknown values fall back to the defaults");
        try { Scheduler.Validate(new Configuration { Channels = [new ChannelDefinition { EncodeQuality = "ultra" }] }); throw new Exception("Bad quality accepted"); }
        catch (ArgumentException) { Check(true, "Unknown encoding quality is rejected"); }
    }
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
