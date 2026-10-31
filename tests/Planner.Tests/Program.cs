using System.Text.Json;
using Jellyfin.Plugin.LibraryTV;

int passed = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); passed++; }
Media Episode(int n, string series = "show") => new(new Guid(n, 0, 0, new byte[8]), "Episode " + n, "episode.mkv", 1800, series, 1, n);
var episodes = Enumerable.Range(1, 12).Select(n => Episode(n)).Reverse().ToList();
var sequential = new Pool { Shuffle = true };
var rotation = new Rotation();
Check(Enumerable.Range(1, 12).All(n => Planner.Next(episodes, sequential, rotation, "test")!.Episode == n), "Shuffling never breaks episode order");
Check(Planner.Next(episodes, sequential, rotation, "test")!.Episode == 1, "Series repeats only after exhaustion");
rotation = new();
var once = new Pool { RepeatWhenExhausted = false };
for (var i = 0; i < 12; i++) Planner.Next(episodes, once, rotation, "test");
Check(Planner.Next(episodes, once, rotation, "test") is null, "No-repeat pool stops after exhaustion");
episodes.Add(Episode(13));
Check(Planner.Next(episodes, once, rotation, "test")!.Episode == 13, "New library episodes join an exhausted pool");
rotation = new Rotation { Remaining = [episodes[0].Id, episodes[1].Id] };
var deleted = episodes[0].Id;
Check(Planner.Next(episodes.Where(x => x.Id != deleted).ToList(), sequential, rotation, "test")!.Id != deleted, "Deleted media is removed from the rotation");

var now = new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.Zero);
var channel = new ChannelDefinition { Id = "test", Source = new Pool { Shuffle = false }, Blocks = [new TimeBlock { Id = "evening", Start = "18:00", End = "23:59", MaxItemsPerDay = 3, Source = new Pool { Shuffle = false } }] };
var fallback = new Media(Guid.NewGuid(), "Filler", "filler.mp4", 1800, "", 0, 0);
var pools = new Dictionary<string, List<Media>> { ["default"] = [fallback], ["evening"] = Enumerable.Range(1, 12).Select(n => Episode(n)).ToList(), ["commercials"] = [] };
var state = new ChannelState();
Planner.Extend(channel, state, pools, now, now.AddDays(1), TimeZoneInfo.Utc);
var first = state.Guide.Where(x => x.Title.StartsWith("Episode")).ToArray();
Check(first.Select(x => x.Title).SequenceEqual(new[] { "Episode 1", "Episode 2", "Episode 3" }), "Day one daily block airs episodes 1,2,3");
state = JsonSerializer.Deserialize<ChannelState>(JsonSerializer.Serialize(state))!;
Planner.Extend(channel, state, pools, now.AddDays(1), now.AddDays(2), TimeZoneInfo.Utc);
var second = state.Guide.Where(x => x.Start >= now.AddDays(1) && x.Title.StartsWith("Episode")).ToArray();
Check(second.Select(x => x.Title).SequenceEqual(new[] { "Episode 4", "Episode 5", "Episode 6" }), "Restart and day two carry on with episodes 4,5,6");
var existing = state.Guide.ToArray();
Planner.Extend(channel, state, pools, now.AddDays(1), now.AddDays(2), TimeZoneInfo.Utc);
Check(existing.SequenceEqual(state.Guide), "Repeated guide refresh does not alter scheduled programs");
Check(state.Guide.Zip(state.Guide.Skip(1)).All(pair => pair.First.End == pair.Second.Start), "Fallback produces a continuous gap-free schedule");
var previousStart = state.Guide.First(x => x.Title == "Episode 4").Start;
Check(previousStart == now.AddDays(1).AddHours(18), "Daily time block starts at its configured time with aligned media");

channel.Blocks = [new TimeBlock { Id = "night", Start = "23:00", End = "02:00", Days = [DayOfWeek.Sunday] }];
Check(Planner.ActiveBlock(channel, now.AddDays(1).AddHours(1), TimeZoneInfo.Utc)?.Date == new DateOnly(2026,10,18), "Overnight block belongs to its start day");
Check(Planner.ActiveBlock(channel, now.AddDays(1).AddHours(3), TimeZoneInfo.Utc) is null, "Overnight block stops after end time");
var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
channel.Blocks = [new TimeBlock { Start = "18:00", End = "21:00" }];
Check(Planner.ActiveBlock(channel, now.AddHours(22), zone) is not null, "Blocks use the configured local time zone");
channel.Blocks = []; channel.CommercialCount = 2; channel.MovieBreaks = 0;
var ad = new Media(Guid.NewGuid(), "Ad", "ad.mp4", 30, "", 0, 0);
pools["commercials"] = [ad];
state = new();
Planner.Extend(channel, state, pools, now, now.AddHours(2), TimeZoneInfo.Utc);
Check(!state.Guide[0].Commercial && state.Guide[1].Commercial && state.Guide[2].Commercial && !state.Guide[3].Commercial, "Configured commercials appear between programs");
Check(state.Guide[3].Start == now.AddMinutes(31), "Commercial durations are included in the guide");
var movies = Enumerable.Range(30, 4).Select(n => Episode(n) with { Series = "", Season = 0, Episode = 0 }).ToList();
rotation = new();
var cycle = Enumerable.Range(0,4).Select(_ => Planner.Next(movies, sequential, rotation, "movie")!.Id).ToArray();
Check(cycle.Distinct().Count() == 4, "Shuffle avoids repeats until the full pool has aired");
Check(Planner.Next(movies, sequential, rotation, "movie")!.Id != cycle[^1], "Movie cycle boundary avoids immediate repeat");
Check(Planner.Next([], sequential, new(), "empty") is null, "Empty content pools terminate safely");
var rebuildChannel = new ChannelDefinition { Id = "rebuild", Source = new Pool { Shuffle = false } };
var rebuildPools = new Dictionary<string, List<Media>> { ["default"] = Enumerable.Range(1, 4).Select(n => Episode(n)).ToList(), ["commercials"] = [] };
var rebuildState = new ChannelState();
Planner.Extend(rebuildChannel, rebuildState, rebuildPools, now, now.AddHours(4), TimeZoneInfo.Utc);
var originalPlan = rebuildState.Guide.Select(x => (x.ItemId, x.Start, x.End)).ToArray();
rebuildState = JsonSerializer.Deserialize<ChannelState>(JsonSerializer.Serialize(rebuildState))!;
var appliedAt = Planner.RewindFuture(rebuildState, now.AddMinutes(5));
Check(appliedAt == now.AddMinutes(30) && rebuildState.Guide.Count == 1, "Future rebuild preserves the current episode and begins at its end");
Check(rebuildState.Rotations["default"].Last == Episode(1).Id && rebuildState.Rotations["default"].Seen.SequenceEqual(new[] { Episode(1).Id }), "Rewinding across multiple repeat cycles restores the committed episode cursor");
Planner.Extend(rebuildChannel, rebuildState, rebuildPools, now.AddMinutes(5), now.AddHours(4), TimeZoneInfo.Utc, appliedAt);
Check(originalPlan.SequenceEqual(rebuildState.Guide.Select(x => (x.ItemId, x.Start, x.End))), "Rebuilding unchanged content after a restart neither skips nor duplicates episodes");
rebuildPools["default"].RemoveAll(x => x.Episode == 4);
Planner.Extend(rebuildChannel, rebuildState, rebuildPools, now.AddMinutes(5), now.AddHours(6), TimeZoneInfo.Utc);
appliedAt = Planner.RewindFuture(rebuildState, now.AddMinutes(5));
Planner.Extend(rebuildChannel, rebuildState, rebuildPools, now.AddMinutes(5), now.AddHours(4), TimeZoneInfo.Utc, appliedAt);
Check(rebuildState.Guide[1].ItemId == Episode(2).Id && rebuildState.Guide.All(x => x.ItemId != Episode(4).Id), "Rebuild handles media deleted between planning passes without skipping the next episode");
var legacyState = new ChannelState { Guide = [new("rebuild", Episode(1).Id, "Legacy 1", "test", now, now.AddMinutes(30), false), new("rebuild", Episode(2).Id, "Legacy 2", "test", now.AddMinutes(30), now.AddHours(1), false)], Rotations = new() { ["default"] = new Rotation { Last = Episode(2).Id, Seen = [Episode(1).Id, Episode(2).Id], Remaining = [Episode(3).Id] } } };
Planner.Extend(rebuildChannel, legacyState, rebuildPools, now, now.AddHours(3), TimeZoneInfo.Utc);
Check(Planner.RewindFuture(legacyState, now.AddMinutes(5)) == now.AddHours(1) && legacyState.Guide.Count == 2 && legacyState.Rotations["default"].Last == Episode(2).Id, "Older guides without undo metadata stay fixed while their newer tail can be rebuilt safely");
var daily = new ChannelDefinition { Id = "daily-rebuild", Source = new Pool(), Blocks = [new TimeBlock { Id = "show", Start = "18:00", End = "23:00", MaxItemsPerDay = 3, Source = new Pool { Shuffle = false } }] };
var dailyPools = new Dictionary<string,List<Media>> { ["default"] = [fallback], ["show"] = Enumerable.Range(1,12).Select(n=>Episode(n)).ToList(), ["commercials"] = [] };
var dailyState = new ChannelState();
Planner.Extend(daily, dailyState, dailyPools, now, now.AddDays(2), TimeZoneInfo.Utc);
var dailyBoundary = Planner.RewindFuture(dailyState, now.AddHours(18).AddMinutes(5));
Planner.Extend(daily, dailyState, dailyPools, now.AddHours(18).AddMinutes(5), now.AddDays(2), TimeZoneInfo.Utc, dailyBoundary);
Check(dailyState.Guide.Where(x=>x.Start>=now.AddHours(18)&&x.Start<now.AddDays(1)&&x.Title.StartsWith("Episode")).Select(x=>x.Title).SequenceEqual(new[]{"Episode 1","Episode 2","Episode 3"}), "Rebuilding restores daily quotas as well as episode order");
var offline = new Rotation { Last = Episode(2).Id, Remaining = [Episode(3).Id], Seen = [Episode(1).Id,Episode(2).Id] };
Planner.Next([], sequential, offline, "offline");
Check(offline.Last == Episode(2).Id && offline.Remaining.Count == 1 && offline.Seen.Count == 2, "An unavailable pool does not erase saved rotation progress");
var tinyChannel = new ChannelDefinition {Id="tiny",CommercialCount=20};
var tiny = new Media(Guid.NewGuid(),"Short clip","short.mp4",1,"",0,0);
var capped = new ChannelState();
Planner.Extend(tinyChannel,capped,new Dictionary<string,List<Media>>{["default"]=[tiny],["commercials"]=[tiny with {Id=Guid.NewGuid()}]},now,now.AddDays(7),TimeZoneInfo.Utc);
Check(capped.Guide.Count==Planner.MaxGuideEntries,"Very short clips and large commercial breaks cannot create an unbounded guide");
var downtime = new ChannelState { Guide = [new("tiny", tiny.Id, "Before downtime", "short.mp4", now.AddDays(-6).AddMinutes(-1), now.AddDays(-6), false)] };
Planner.Extend(new ChannelDefinition { Id = "tiny" }, downtime, new Dictionary<string,List<Media>>{["default"]=[tiny with { Seconds = 60 }],["commercials"]=[]}, now, now.AddDays(1), TimeZoneInfo.Utc);
Check(downtime.Guide.Any(x => x.Start <= now && x.End > now) && downtime.Guide[^1].End >= now.AddDays(1) && downtime.Guide.All(x => x.End >= now.AddHours(-6)), "Catch-up after downtime still reaches the current time instead of spending the guide limit on the past");
var serverCulture = System.Globalization.CultureInfo.CurrentCulture;
try
{
    System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("th-TH");
    var localized = new ChannelState();
    Planner.Extend(new ChannelDefinition { Id = "localized", Blocks = [new TimeBlock { Id = "evening", Start = "18:00", End = "23:59", MaxItemsPerDay = 3 }] }, localized, pools, now, now.AddDays(1), TimeZoneInfo.Utc);
    Check(localized.DailyCounts.Count > 0, "Daily limits survive refreshes on servers whose locale uses a non-Gregorian calendar");
}
finally { System.Globalization.CultureInfo.CurrentCulture = serverCulture; }
List<Media> Show(string name, int offset, int count) => Enumerable.Range(offset + 1, count).Select(n => Episode(n, name) with { Episode = n - offset }).ToList();
string Code(Media m) => m.Series + m.Episode;
// Selection order is A, B, C; episodes are listed backwards to prove they are sorted.
var mixed = new[] { "A", "B", "C" }.SelectMany((name, i) => Show(name, 100 * (i + 1), 6).AsEnumerable().Reverse()).ToList();
var mixedRotation = new Rotation();
var pairs = Enumerable.Range(0, 18).Select(_ => Code(Planner.Next(mixed, new Pool { Shuffle = false }, mixedRotation, "mix")!)).ToArray();
Check(string.Join(" ", pairs.Take(8)) == "A1 A2 B1 B2 C1 C2 A3 A4" && pairs.Distinct().Count() == 18, "Shows air two episodes at a time in turn and every episode still airs once per cycle");
var straight = new Rotation();
Check(string.Join(" ", Enumerable.Range(0, 7).Select(_ => Code(Planner.Next(mixed, new Pool { Shuffle = false, EpisodesInARow = 0 }, straight, "mix")!))) == "A1 A2 A3 A4 A5 A6 B1", "Zero episodes in a row keeps each show running straight through");
var uneven = Show("A", 100, 10).Concat(Show("B", 200, 2)).ToList();
var unevenRotation = new Rotation();
var unevenOrder = Enumerable.Range(0, 12).Select(_ => Planner.Next(uneven, new Pool { Shuffle = false }, unevenRotation, "uneven")!).ToArray();
var shortShow = Array.FindIndex(unevenOrder, x => x.Series == "B");
Check(shortShow > 0 && shortShow < 10 && unevenOrder[shortShow + 1].Series == "B" && unevenOrder.Where(x => x.Series == "A").Select(x => x.Episode).SequenceEqual(Enumerable.Range(1, 10)), "A short show is placed inside a long show's run, keeping each show in episode order");
var shuffledRotation = new Rotation();
var shuffledOrder = Enumerable.Range(0, 18).Select(_ => Planner.Next(mixed, new Pool(), shuffledRotation, "shuffle")!).ToArray();
Check(shuffledOrder.Chunk(2).All(turn => turn[0].Series == turn[1].Series && turn[1].Episode == turn[0].Episode + 1) && shuffledOrder.GroupBy(x => x.Series).All(g => g.Select(x => x.Episode).SequenceEqual(Enumerable.Range(1, 6))), "Shuffled shows still air in pairs with episodes in order");
var saved = new Rotation { Last = mixed.Single(x => Code(x) == "A2").Id, Seen = mixed.Where(x => Code(x) is "A1" or "A2").Select(x => x.Id).ToList(), Remaining = new[] { "A3", "A4", "A5", "A6", "B1", "B2", "B3", "B4", "B5", "B6", "C1", "C2", "C3", "C4", "C5", "C6" }.Select(c => mixed.Single(x => Code(x) == c).Id).ToList() };
Check(string.Join(" ", Enumerable.Range(0, 6).Select(_ => Code(Planner.Next(mixed, new Pool { Shuffle = false }, saved, "saved")!))) == "A3 A4 B1 B2 C1 C2", "Channels saved before this setting switch to two-episode turns without losing their place");
var blockChannel = new ChannelDefinition { Id = "turns", Source = new Pool(), Blocks = [new TimeBlock { Id = "prime", Start = "18:00", End = "23:00", MaxItemsPerDay = 4, Source = new Pool { Shuffle = false } }] };
var blockState = new ChannelState();
var blockPools = new Dictionary<string, List<Media>> { ["default"] = [fallback], ["prime"] = mixed, ["commercials"] = [] };
Planner.Extend(blockChannel, blockState, blockPools, now, now.AddDays(1), TimeZoneInfo.Utc);
Check(string.Join(" ", blockState.Guide.Where(x => x.Title.StartsWith("Episode")).Select(x => Code(mixed.Single(m => m.Id == x.ItemId)))) == "A1 A2 B1 B2", "A time block with several shows mixes them instead of filling up with one show");
var plannedTurns = blockState.Guide.Select(x => (x.ItemId, x.Start)).ToArray();
var turnsBoundary = Planner.RewindFuture(blockState, now.AddMinutes(5));
Planner.Extend(blockChannel, blockState, blockPools, now.AddMinutes(5), now.AddDays(1), TimeZoneInfo.Utc, turnsBoundary);
Check(plannedTurns.SequenceEqual(blockState.Guide.Select(x => (x.ItemId, x.Start))), "Rebuilding future slots reproduces the same mixed episode order");

// One show in both the default lineup and a time block continues one episode sequence.
var longShow = Enumerable.Range(1, 100).Select(n => Episode(n)).ToList();
var sharedChannel = new ChannelDefinition { Id = "shared", Source = new Pool { Shuffle = false }, Blocks = [new TimeBlock { Id = "prime", Start = "18:00", End = "20:00", MaxItemsPerDay = 2, Source = new Pool { Shuffle = false } }] };
var sharedPools = new Dictionary<string, List<Media>> { ["default"] = longShow, ["prime"] = longShow, ["commercials"] = [] };
var sharedState = new ChannelState();
Planner.Extend(sharedChannel, sharedState, sharedPools, now, now.AddDays(1), TimeZoneInfo.Utc);
var aired = sharedState.Guide.Select(x => longShow.Single(m => m.Id == x.ItemId).Episode).ToArray();
Check(aired.SequenceEqual(Enumerable.Range(1, aired.Length)), "A show in both the lineup and a time block moves to the next episode instead of repeating");
var sharedPlan = sharedState.Guide.Select(x => (x.ItemId, x.Start)).ToArray();
sharedState = JsonSerializer.Deserialize<ChannelState>(JsonSerializer.Serialize(sharedState))!;
var sharedBoundary = Planner.RewindFuture(sharedState, now.AddMinutes(5));
Check(sharedState.Series[Episode(1).Series] == new SeriesCursor(1, 1), "Rewinding restores the channel's series position");
Planner.Extend(sharedChannel, sharedState, sharedPools, now.AddMinutes(5), now.AddDays(1), TimeZoneInfo.Utc, sharedBoundary);
Check(sharedPlan.SequenceEqual(sharedState.Guide.Select(x => (x.ItemId, x.Start))), "Rebuilding a shared show reproduces the same episodes");
var twoBlocks = new ChannelDefinition { Id = "two-blocks", Source = new Pool(), Blocks = [
    new TimeBlock { Id = "early", Start = "17:00", End = "18:00", MaxItemsPerDay = 1, Source = new Pool { Shuffle = false } },
    new TimeBlock { Id = "late", Start = "22:00", End = "23:00", MaxItemsPerDay = 1, Source = new Pool { Shuffle = false } }] };
var twoBlockState = new ChannelState();
Planner.Extend(twoBlocks, twoBlockState, new Dictionary<string, List<Media>> { ["default"] = [fallback], ["early"] = longShow, ["late"] = longShow, ["commercials"] = [] }, now, now.AddDays(2), TimeZoneInfo.Utc);
Check(twoBlockState.Guide.Where(x => x.Title.StartsWith("Episode")).Select(x => x.Title).SequenceEqual(new[] { "Episode 1", "Episode 2", "Episode 3", "Episode 4" }), "Two time blocks with the same show split its episodes instead of each starting at episode 1");
var upgraded = new ChannelState { Rotations = new() { ["default"] = new Rotation { Last = Episode(6).Id, EpisodesInARow = 2, Seen = Enumerable.Range(1, 6).Select(n => Episode(n).Id).ToList(), Remaining = Enumerable.Range(7, 94).Select(n => Episode(n).Id).ToList() } } };
Planner.Extend(new ChannelDefinition { Id = "upgraded", Source = new Pool { Shuffle = false } }, upgraded, new Dictionary<string, List<Media>> { ["default"] = longShow, ["commercials"] = [] }, now, now.AddHours(2), TimeZoneInfo.Utc);
Check(upgraded.Guide[0].Title == "Episode 7" && upgraded.Guide[1].Title == "Episode 8", "Channels saved before series positions existed continue where they left off");

// Content restrictions keep matching genres off the air during their window.
var horror = new Media(Guid.NewGuid(), "Scary", "scary.mkv", 7200, "", 0, 0, 1980, ["Horror"]);
var comedy = new Media(Guid.NewGuid(), "Funny", "funny.mkv", 5400, "", 0, 0, 1990, ["Comedy"]);
var daytime = new[] { new ContentRule { Start = "06:00", End = "21:00", Genres = ["horror"] } };
var restrictedChannel = new ChannelDefinition { Id = "restricted", Source = new Pool { Shuffle = false } };
var restrictedState = new ChannelState();
var restrictedPools = new Dictionary<string, List<Media>> { ["default"] = [horror, comedy], ["commercials"] = [] };
Planner.Extend(restrictedChannel, restrictedState, restrictedPools, now, now.AddDays(2), TimeZoneInfo.Utc, null, daytime);
var scary = restrictedState.Guide.Where(x => x.ItemId == horror.Id).ToArray();
Check(scary.Length > 0 && scary.All(x => x.End.TimeOfDay <= TimeSpan.FromHours(6) && x.End.Date == x.Start.Date || x.Start.TimeOfDay >= TimeSpan.FromHours(21)), "Restricted genres never overlap their blocked hours and still air outside them");
Check(restrictedState.Guide.Zip(restrictedState.Guide.Skip(1)).All(x => x.First.End == x.Second.Start), "Other content fills the blocked hours without gaps");
var restrictedPlan = restrictedState.Guide.Select(x => (x.ItemId, x.Start)).ToArray();
var restrictedBoundary = Planner.RewindFuture(restrictedState, now.AddMinutes(5));
Planner.Extend(restrictedChannel, restrictedState, restrictedPools, now.AddMinutes(5), now.AddDays(2), TimeZoneInfo.Utc, restrictedBoundary, daytime);
Check(restrictedPlan.SequenceEqual(restrictedState.Guide.Select(x => (x.ItemId, x.Start))), "Rebuilding with restrictions reproduces the same schedule");
var overnight = new[] { new ContentRule { Start = "22:00", End = "02:00", Days = [DayOfWeek.Saturday], Genres = [], Tags = ["kids-unsafe"] } };
var tagged = comedy with { Tags = ["Kids-Unsafe"] };
var saturday = new DateTimeOffset(2026, 10, 17, 0, 0, 0, TimeSpan.Zero);
Check(Planner.Restricted(tagged, saturday.AddHours(25), saturday.AddHours(25.5), overnight, TimeZoneInfo.Utc) && !Planner.Restricted(tagged, saturday.AddHours(26), saturday.AddHours(27), overnight, TimeZoneInfo.Utc) && !Planner.Restricted(tagged, saturday.AddHours(-1), saturday, overnight, TimeZoneInfo.Utc), "Overnight restrictions belong to the day they start and match tags case-insensitively");
Check(!Planner.Restricted(comedy, saturday.AddHours(23), saturday.AddHours(24), overnight, TimeZoneInfo.Utc), "Content without a restricted genre or tag is never blocked");
var onlyHorror = new ChannelState();
Planner.Extend(restrictedChannel, onlyHorror, new Dictionary<string, List<Media>> { ["default"] = [horror], ["commercials"] = [] }, now, now.AddDays(1), TimeZoneInfo.Utc, null, daytime);
Check(onlyHorror.Guide.All(x => x.ItemId == horror.Id) && onlyHorror.Guide.Zip(onlyHorror.Guide.Skip(1)).Any(x => x.Second.Start > x.First.End) && onlyHorror.Guide.All(x => !Planner.Restricted(horror, x.Start, x.End, daytime, TimeZoneInfo.Utc))
    && onlyHorror.Rotations["default"].Remaining.Concat(onlyHorror.Rotations["default"].Seen).Count(x => x == horror.Id) == 1, "A pool with nothing allowed leaves a gap instead of breaking its rules, and its rotation stays intact");

// The matching tests below use tiny libraries; widening for variety is tested separately at the end.
Planner.MinimumVariety = 1;
// Commercials follow the decade of the program they come after.
Media Ad(int year) => new(Guid.NewGuid(), "Ad " + year, "ad.mp4", 30, "", 0, 0, year);
var adPool = new List<Media> { Ad(1983), Ad(1987), Ad(1994), Ad(1998), Ad(2006) };
Media Program(int year) => new(Guid.NewGuid(), "Program " + year, "program.mkv", 1800, "", 0, 0, year);
var eraChannel = new ChannelDefinition { Id = "era", CommercialCount = 2, MovieBreaks = 0, Source = new Pool { Shuffle = false } };
var eraPrograms = new List<Media> { Program(1988), Program(1992), Program(1971), Program(0) };
var eraState = new ChannelState();
Planner.Extend(eraChannel, eraState, new Dictionary<string, List<Media>> { ["default"] = eraPrograms, ["commercials"] = adPool }, now, now.AddHours(2.1), TimeZoneInfo.Utc);
int[] AdsAfter(int index) => eraState.Guide.Skip(index * 3 + 1).Take(2).Select(x => adPool.Single(a => a.Id == x.ItemId).Year).ToArray();
Check(AdsAfter(0).All(x => x / 10 == 198) && AdsAfter(0).Distinct().Count() == 2, "Commercials after a 1980s program come from the 1980s and rotate");
Check(AdsAfter(1).All(x => x / 10 == 199), "Commercials after a 1990s program come from the 1990s");
Check(AdsAfter(2).All(x => x / 10 == 198), "A decade without commercials uses the nearest decade that has them");
Check(eraState.Rotations.ContainsKey("commercials") && eraState.Guide[10].Commercial, "Programs without a known year use the full commercial pool");
var eraPlan = eraState.Guide.Select(x => (x.ItemId, x.Start)).ToArray();
var eraBoundary = Planner.RewindFuture(eraState, now.AddMinutes(5));
Planner.Extend(eraChannel, eraState, new Dictionary<string, List<Media>> { ["default"] = eraPrograms, ["commercials"] = adPool }, now.AddMinutes(5), now.AddHours(2.1), TimeZoneInfo.Utc, eraBoundary);
Check(eraPlan.SequenceEqual(eraState.Guide.Select(x => (x.ItemId, x.Start))), "Rebuilding reproduces era-matched commercial breaks");
eraChannel.MatchCommercialEra = false;
var anyEra = new ChannelState();
Planner.Extend(eraChannel, anyEra, new Dictionary<string, List<Media>> { ["default"] = eraPrograms, ["commercials"] = adPool }, now, now.AddHours(1), TimeZoneInfo.Utc);
Check(anyEra.Rotations.Keys.Where(x => x.StartsWith("commercials")).SequenceEqual(["commercials"]), "Era matching can be turned off per channel");

// Commercials follow the network a program aired on, then its decade.
Media Clip(string path, int year) => new(Guid.NewGuid(), path, path, 30, "", 0, 0, year);
var networkAds = new List<Media> { Clip("/ads/Adult Swim/2000s/one.mp4", 2003), Clip("/ads/AdultSwim/two.mp4", 2005), Clip("/ads/Nickelodeon/1990s/kids.mp4", 1994),
    Clip("/ads/Nickelodeon/2000s/kids.mp4", 2004), Clip("/ads/Toonami/bump.mp4", 2001), Clip("/ads/General/cola.mp4", 1995) };
Media Network(string name, int year, string[]? studios = null, string[]? tags = null) => new(Guid.NewGuid(), name, name + ".mkv", 1800, "", 0, 0, year, [], tags ?? [], studios ?? []);
var nightShow = Network("Night", 2004, ["Adult Swim"]);
var kidsShow = Network("Kids", 1996, ["Nickelodeon"]);
var otherShow = Network("Other", 1995, ["Fox"]);
var taggedShow = Network("Tagged", 2001, ["Cartoon Network"], ["Toonami"]);
var networkChannel = new ChannelDefinition { Id = "networks", CommercialCount = 2, MovieBreaks = 0, EpisodeBreaks = 0, Source = new Pool { Shuffle = false } };
var networkPools = new Dictionary<string, List<Media>> { ["default"] = [nightShow, kidsShow, otherShow, taggedShow], ["commercials"] = networkAds };
var networkState = new ChannelState();
Planner.Extend(networkChannel, networkState, networkPools, now, now.AddHours(2.1), TimeZoneInfo.Utc);
string[] Breaks(ChannelState s, Media program) { var at = s.Guide.FindIndex(x => x.ItemId == program.Id); return s.Guide.Skip(at + 1).Take(2).Select(x => x.Path).ToArray(); }
Check(Breaks(networkState, nightShow).All(x => x.Contains("Adult")) && Breaks(networkState, nightShow).Distinct().Count() == 2, "Adult Swim shows get Adult Swim commercials, including folders written as one word");
Check(Breaks(networkState, kidsShow).All(x => x == "/ads/Nickelodeon/1990s/kids.mp4"), "Nickelodeon shows get Nickelodeon commercials from their own decade");
Check(Breaks(networkState, otherShow).All(x => x == "/ads/General/cola.mp4"), "A show from a network without commercials gets only clips not filed under any network");
Check(Breaks(networkState, taggedShow).All(x => x == "/ads/Toonami/bump.mp4"), "A tag such as Toonami takes priority over the show's studio");
var networkPlan = networkState.Guide.Select(x => (x.ItemId, x.Start)).ToArray();
var networkBoundary = Planner.RewindFuture(networkState, now.AddMinutes(5));
Planner.Extend(networkChannel, networkState, networkPools, now.AddMinutes(5), now.AddHours(2.1), TimeZoneInfo.Utc, networkBoundary);
Check(networkPlan.SequenceEqual(networkState.Guide.Select(x => (x.ItemId, x.Start))), "Rebuilding reproduces network-matched commercial breaks");
networkChannel.CommercialNetwork = "toonami";
var overridden = new ChannelState();
Planner.Extend(networkChannel, overridden, networkPools, now, now.AddHours(2.1), TimeZoneInfo.Utc);
Check(overridden.Guide.Where(x => x.Commercial).All(x => x.Path == "/ads/Toonami/bump.mp4"), "A channel's commercial network applies to every break");
networkChannel.CommercialNetwork = "";
networkChannel.Blocks = [new TimeBlock { Id = "late", Start = "00:00", End = "01:00", Source = new Pool { Shuffle = false }, CommercialNetwork = "Nickelodeon" }];
var blockNetwork = new ChannelState();
Planner.Extend(networkChannel, blockNetwork, new Dictionary<string, List<Media>>(networkPools) { ["late"] = [nightShow] }, now, now.AddHours(1.1), TimeZoneInfo.Utc);
Check(blockNetwork.Guide.Where(x => x.Commercial && x.Start < now.AddHours(1)).All(x => x.Path.Contains("Nickelodeon/2000s")), "A time block's commercial network overrides the program's own network");
networkChannel.Blocks = []; networkChannel.MatchCommercialNetwork = false;
var anyNetwork = new ChannelState();
Planner.Extend(networkChannel, anyNetwork, networkPools, now, now.AddHours(2.1), TimeZoneInfo.Utc);
Check(anyNetwork.Rotations.Keys.All(x => !x.Contains('@')), "Network matching can be turned off per channel");
Check(Planner.FiledUnder("/ads/ABC Promos/x.mp4", "abc") && !Planner.FiledUnder("/ads/abcd/x.mp4", "abc") && Planner.FiledUnder("/ads/cartoon_network/x.mp4", "cartoon network")
    && !Planner.FiledUnder("/ads/General/Cartoon Network.Promo/x.mp4", "promo x"), "Network names match whole words or longer run-together names, never fragments of other words");

// Commercial breaks inside programs: one per episode, two per movie, at chapter marks when available.
var episode22 = Episode(1, "toon") with { Seconds = 1320 };
var movie90 = new Media(Guid.NewGuid(), "Feature", "feature.mkv", 5400, "", 0, 0, 1999);
var shortClip = new Media(Guid.NewGuid(), "Short", "short.mkv", 300, "", 0, 0);
var ad30 = new Media(Guid.NewGuid(), "Ad", "/ads/ad.mp4", 30, "", 0, 0);
var breaksChannel = new ChannelDefinition { Id = "breaks", CommercialCount = 2, Source = new Pool { Shuffle = false } };
var breakPools = new Dictionary<string, List<Media>> { ["default"] = [episode22, movie90, shortClip], ["commercials"] = [ad30], ["bumpers"] = [] };
var breakState = new ChannelState();
Planner.Extend(breaksChannel, breakState, breakPools, now, now.AddHours(2.5), TimeZoneInfo.Utc);
string Pattern(IEnumerable<Airing> guide) => string.Join(" ", guide.Select(x => x.Commercial ? "c" : x.Title[0] + (x.Offset > 0 ? "+" : "")));
Check(Pattern(breakState.Guide.Take(17)) == "E c c E+ c c F c c F+ c c F+ c c S c", "Episodes get one break in the middle, movies two, short clips none, and every program is followed by a break");
var feature = breakState.Guide.Where(x => x.ItemId == movie90.Id).Take(3).ToArray();
Check(feature.Select(x => x.Offset).SequenceEqual([0, 1800, 3600]) && feature.Sum(x => (x.End - x.Start).TotalSeconds) == 5400 && breakState.Guide.Zip(breakState.Guide.Skip(1)).All(x => x.First.End == x.Second.Start), "Program parts continue where the last part stopped, with no time lost or gaps");
var marked = new ChannelState();
Planner.Extend(breaksChannel, marked, breakPools, now, now.AddHours(1), TimeZoneInfo.Utc, null, null, m => m.Id == episode22.Id ? [200, 610, 1250] : []);
Check(marked.Guide.First(x => x.Offset > 0).Offset == 610, "A break lands on the chapter mark nearest the middle of a show");
var breakPlan = breakState.Guide.Select(x => (x.ItemId, x.Start, x.Offset)).ToArray();
breakState = JsonSerializer.Deserialize<ChannelState>(JsonSerializer.Serialize(breakState))!;
var midProgram = now.AddMinutes(25);
var breakBoundary = Planner.RewindFuture(breakState, midProgram);
Check(breakBoundary == breakState.Guide[^1].End && breakState.Guide.Last().Commercial && breakState.Guide.Count(x => x.ItemId == movie90.Id) == 3, "Rebuilding during a program keeps all of its parts and breaks");
Planner.Extend(breaksChannel, breakState, breakPools, midProgram, now.AddHours(2.5), TimeZoneInfo.Utc, breakBoundary);
Check(breakPlan.SequenceEqual(breakState.Guide.Select(x => (x.ItemId, x.Start, x.Offset))), "Rebuilding reproduces the same split programs and breaks");
breaksChannel.EpisodeBreaks = 0; breaksChannel.MovieBreaks = 0;
var noBreaks = new ChannelState();
Planner.Extend(breaksChannel, noBreaks, breakPools, now, now.AddHours(2.5), TimeZoneInfo.Utc);
Check(noBreaks.Guide.All(x => x.Offset == 0) && Pattern(noBreaks.Guide.Take(6)) == "E c c F c c", "Breaks inside programs can be turned off");

// Bumpers go into and out of every break, matched to the network like commercials.
var bumpOut = new Media(Guid.NewGuid(), "Bump", "/bumps/Adult Swim/bump.mp4", 5, "", 0, 0);
var bumpGeneric = new Media(Guid.NewGuid(), "Bump", "/bumps/General/bump.mp4", 5, "", 0, 0);
var swimShow = episode22 with { Studios = ["Adult Swim"] };
var bumperChannel = new ChannelDefinition { Id = "bumpers", CommercialCount = 1, MovieBreaks = 0, Source = new Pool { Shuffle = false } };
var bumperPools = new Dictionary<string, List<Media>> { ["default"] = [swimShow, movie90], ["commercials"] = [ad30], ["bumpers"] = [bumpOut, bumpGeneric] };
var bumperState = new ChannelState();
Planner.Extend(bumperChannel, bumperState, bumperPools, now, now.AddHours(2.5), TimeZoneInfo.Utc);
Check(string.Join(" ", bumperState.Guide.Take(13).Select(x => x.Commercial ? x.Title[0].ToString() : "P")) == "P B A B P B A B P B A B P", "Each break is a bumper, the commercials, then a bumper");
Check(bumperState.Guide.Take(8).Where(x => x.Title == "Bump").All(x => x.Path.Contains("Adult Swim")) && bumperState.Guide.Skip(8).Take(4).Where(x => x.Title == "Bump").All(x => x.Path.Contains("General")), "Bumpers follow the program's network, and other programs get general bumpers");
bumperChannel.CommercialCount = 0;
var bumpersOnly = new ChannelState();
Planner.Extend(bumperChannel, bumpersOnly, bumperPools, now, now.AddHours(2.5), TimeZoneInfo.Utc);
Check(string.Join(" ", bumpersOnly.Guide.Take(4).Select(x => x.Commercial ? "B" : "P")) == "P B P B" && bumpersOnly.Guide.All(x => x.Offset == 0), "Without commercials, one bumper plays between programs and programs are not split");

// Commercials are sorted from their file names alone, using common networks and the most specific name.
var index = new Planner.NetworkIndex([]);
Media Named(string file) => new(Guid.NewGuid(), file, "/commercials/" + file, 30, "", 0, 0);
Check(index.For(Named("Fox Kids promo 1996.mp4")).SequenceEqual(["fox kids"]) && index.For(Named("FOX Sunday night.mp4")).SequenceEqual(["fox"]), "The most specific network name wins, so Fox Kids clips are not FOX clips");
Check(index.For(Named("Nickelodeon Gak 1994.mp4")).SequenceEqual(["nickelodeon"]) && index.For(Named("Cola 1985.mp4")).Length == 0 && index.Name("adult swim") == "Adult Swim", "Common networks are recognised in file names without network folders");
var flatAds = new List<Media> { Named("Adult Swim bump.mp4"), Named("Nickelodeon Gak.mp4"), Named("Cola.mp4") };
var kidsOnly = new ChannelDefinition { Id = "kids", CommercialCount = 1, EpisodeBreaks = 0, MovieBreaks = 0, Source = new Pool { Shuffle = false } };
var kidsState = new ChannelState();
Planner.Extend(kidsOnly, kidsState, new Dictionary<string, List<Media>> { ["default"] = [Network("Other", 1995, ["Fox"])], ["commercials"] = flatAds }, now, now.AddHours(3), TimeZoneInfo.Utc);
Check(kidsState.Guide.Where(x => x.Commercial).All(x => x.Path == "/commercials/Cola.mp4"), "Clips named after a network no program on the channel uses still never play as general commercials");

// Matching commercials and bumpers to the right program, with the reason recorded for the guide preview.
Media Toon(string show, int year, string[] studios, string[]? tags = null) => new(Guid.NewGuid(), show + " episode", show + ".mkv", 1320, show, 1, 1, year, [], tags ?? [], studios, show);
Media Ad2(string file, int year = 0) => new(Guid.NewGuid(), file, "/clips/" + file, 15, "", 0, 0, year);
var sponge = Toon("SpongeBob SquarePants", 2001, ["Nickelodeon Animation Studio"]);
var rugrats = Toon("Rugrats", 1993, ["Nickelodeon"]);
var aqua = Toon("Aqua Teen Hunger Force", 2003, ["Williams Street"]);
var simpsons = Toon("The Simpsons", 1992, ["Fox"]);
var smallTitle = Toon("Up", 2009, ["Pixar"]);
var matchAds = new List<Media> { Ad2("Nick Gak 1995.mp4", 1995), Ad2("Nickelodeon promo 2002.mp4", 2002), Ad2("Nick Jr promo 1996.mp4", 1996), Ad2("Adult Swim bump 2004.mp4", 2004),
    Ad2("SpongeBob SquarePants bumper.mp4", 2001), Ad2("Simpsons promo.mp4", 1992), Ad2("Cola 1990.mp4", 1990), Ad2("Wake Up cereal 2009.mp4", 2009) };
var matchChannel = new ChannelDefinition { Id = "match", CommercialCount = 1, EpisodeBreaks = 0, MovieBreaks = 0, Source = new Pool { Shuffle = false } };
var matchState = new ChannelState();
Planner.Extend(matchChannel, matchState, new Dictionary<string, List<Media>> { ["default"] = [sponge, rugrats, aqua, simpsons, smallTitle], ["commercials"] = matchAds }, now, now.AddHours(2), TimeZoneInfo.Utc);
Airing After(Media program) => matchState.Guide[matchState.Guide.FindIndex(x => x.ItemId == program.Id) + 1];
Check(After(sponge).Path.EndsWith("SpongeBob SquarePants bumper.mp4") && After(sponge).Match == "made for SpongeBob SquarePants", "A clip named after a show plays with that show");
Check(After(rugrats).Path.EndsWith("Nick Gak 1995.mp4") && After(rugrats).Match == "Nickelodeon (studio “Nickelodeon”) · 1990s" && matchState.Guide.Select((x, i) => (x, i)).Where(e => e.x.Commercial && e.x.Path.Contains("SpongeBob")).All(e => matchState.Guide.Take(e.i).Last(g => !g.Commercial).ItemId == sponge.Id),
    "Nicknames such as “Nick” count as the network, and a show's own bumper never plays with another show");
Check(After(aqua).Path.EndsWith("Adult Swim bump 2004.mp4") && After(aqua).Match!.StartsWith("Adult Swim (studio “Williams Street”)"), "Production companies such as Williams Street stand for their network");
Check(After(simpsons).Path.EndsWith("Simpsons promo.mp4") && After(smallTitle).Path.EndsWith("Wake Up cereal 2009.mp4") && After(smallTitle).Match == "general: no clips for this program's network · 2000s",
    "Titles match without a leading “The”, and short titles like “Up” never match ordinary words");
var nickIndex = new Planner.NetworkIndex([]);
Check(nickIndex.For(Ad2("Nick Jr promo 1996.mp4")).SequenceEqual(["nick jr"]) && nickIndex.Resolve("Nickelodeon Animation Studio", x => x == "nickelodeon") == "nickelodeon" && nickIndex.Resolve("Cartoon Network Studios", x => x == "cartoon network") == "cartoon network",
    "Nick Jr clips stay Nick Jr, and studio names containing a network resolve to it");
var spongeOnly = new ChannelState();
Planner.Extend(new ChannelDefinition { Id = "sponge", CommercialCount = 2, Source = new Pool() }, spongeOnly,
    new Dictionary<string, List<Media>> { ["default"] = [rugrats], ["commercials"] = [Ad2("SpongeBob SquarePants bumper.mp4")] }, now, now.AddHours(1), TimeZoneInfo.Utc, null, null, null, new Planner.NetworkIndex([], ["SpongeBob SquarePants"]));
Check(spongeOnly.Guide.All(x => !x.Commercial && x.Offset == 0), "When no clip suits a program, its breaks are left out instead of playing another show's clips, and it is not split");

// Speeding through credits: the credits become a faster part, anything after them plays normally.
var withCredits = Episode(1, "credits") with { Seconds = 1320 };
var creditsChannel = new ChannelDefinition { Id = "credits", CommercialCount = 1, CreditsSpeed = 4, Source = new Pool { Shuffle = false } };
var creditsPools = new Dictionary<string, List<Media>> { ["default"] = [withCredits], ["commercials"] = [ad30], ["bumpers"] = [] };
(double, double)? CreditsAt(Media m) => (1200, 1300);
var creditsState = new ChannelState();
Planner.Extend(creditsChannel, creditsState, creditsPools, now, now.AddHours(1), TimeZoneInfo.Utc, null, null, null, null, CreditsAt);
var secondStart = creditsState.Guide.FindIndex(1, x => !x.Commercial && x.Offset == 0);
var firstAiring = creditsState.Guide.Take(secondStart).ToArray();
var creditsPart = firstAiring.Single(x => x.Speed > 1);
Check(creditsPart.Offset == 1200 && (creditsPart.End - creditsPart.Start).TotalSeconds == 25 && creditsPart.Speed == 4
    && firstAiring.Last(x => !x.Commercial).Offset == 1300 && (firstAiring.Last(x => !x.Commercial).End - firstAiring.Last(x => !x.Commercial).Start).TotalSeconds == 20
    && firstAiring.SkipWhile(x => x.Speed == 1).TakeWhile(x => !x.Commercial).Count() == 2 && firstAiring.Last().Commercial,
    "Credits play four times faster as their own part, a scene after them plays normally, and the break comes after the program");
Check(firstAiring.Where(x => !x.Commercial && x.Speed == 1 && x.Offset < 1200).All(x => x.Offset + (x.End - x.Start).TotalSeconds <= 1200.001), "Breaks inside a program fall before its credits");
var creditsPlan = creditsState.Guide.Select(x => (x.ItemId, x.Start, x.Offset, x.Speed)).ToArray();
var creditsBoundary = Planner.RewindFuture(creditsState, creditsPart.Start.AddSeconds(5));
Planner.Extend(creditsChannel, creditsState, creditsPools, creditsPart.Start.AddSeconds(5), now.AddHours(1), TimeZoneInfo.Utc, creditsBoundary, null, null, null, CreditsAt);
Check(creditsPlan.SequenceEqual(creditsState.Guide.Select(x => (x.ItemId, x.Start, x.Offset, x.Speed))), "Rebuilding during sped-up credits keeps the rest of the program and reproduces the plan");
var normalSpeed = new ChannelState();
Planner.Extend(creditsChannel, normalSpeed, creditsPools, now, now.AddHours(1), TimeZoneInfo.Utc);
Check(normalSpeed.Guide.All(x => x.Speed == 1), "Programs without credits markers play at normal speed");

// Small matches are widened step by step so breaks don't repeat, without ever using another network's clips.
Planner.MinimumVariety = 8;
Media Filed(string name, int year) => new(Guid.NewGuid(), name, "/clips/" + name + ".mp4", 30, "", 0, 0, year);
var nick90s = Enumerable.Range(1, 2).Select(n => Filed($"Nickelodeon 199{n} ad {n}", 1990 + n)).ToList();
var nick00s = Enumerable.Range(1, 3).Select(n => Filed($"Nickelodeon 200{n} ad {n}", 2000 + n)).ToList();
var general90s = Enumerable.Range(1, 2).Select(n => Filed($"Cola 199{n} ad {n}", 1990 + n)).ToList();
var generalAny = Enumerable.Range(1, 6).Select(n => Filed($"Cereal 201{n} ad {n}", 2010 + n)).ToList();
var swim = Enumerable.Range(1, 5).Select(n => Filed($"Adult Swim bump {n}", 2004)).ToList();
var varietyShow = new Media(Guid.NewGuid(), "Doug", "doug.mkv", 1320, "doug", 1, 1, 1993, [], [], ["Nickelodeon"], "Doug Funnie Show");
var varietyChannel = new ChannelDefinition { Id = "variety", CommercialCount = 2, EpisodeBreaks = 1, Source = new Pool() };
var varietyState = new ChannelState();
Planner.Extend(varietyChannel, varietyState, new Dictionary<string, List<Media>> { ["default"] = [varietyShow], ["commercials"] = [.. nick90s, .. nick00s, .. general90s, .. generalAny, .. swim] }, now, now.AddHours(6), TimeZoneInfo.Utc);
var varietyAds = varietyState.Guide.Where(x => x.Commercial).ToArray();
var firstEight = varietyAds.Take(8).Select(x => x.ItemId).ToArray();
Check(firstEight.Distinct().Count() == 8 && varietyAds.Select(x => x.ItemId).Distinct().Count() == 13 && varietyAds.All(x => !swim.Any(a => a.Id == x.ItemId)),
    "A show with few matching commercials gets at least eight different ones before any repeats, never another network's");
Check(varietyAds.Select(x => x.ItemId).Intersect(nick90s.Concat(nick00s).Select(x => x.Id)).Count() == 5 && varietyAds.Select(x => x.ItemId).Intersect(general90s.Select(x => x.Id)).Count() == 2 && varietyAds.Select(x => x.ItemId).Intersect(generalAny.Select(x => x.Id)).Count() == 6,
    "Widening adds the network's other years, then general clips from the decade, then other general clips, until there are enough");
Check(varietyAds[0].Match == "Nickelodeon (studio “Nickelodeon”) · 1990s; widened with Nickelodeon clips from any year, general clips from the 1990s, all general clips so breaks don't repeat",
    "Guide preview says when and how a break was widened");
var plenty = Enumerable.Range(1, 10).Select(n => Filed($"Nickelodeon 199{n % 10} ad {n}", 1995)).ToList();
var preciseState = new ChannelState();
Planner.Extend(varietyChannel, preciseState, new Dictionary<string, List<Media>> { ["default"] = [varietyShow], ["commercials"] = [.. plenty, .. generalAny] }, now, now.AddHours(3), TimeZoneInfo.Utc);
Check(preciseState.Guide.Where(x => x.Commercial).All(x => plenty.Any(p => p.Id == x.ItemId)) && preciseState.Guide.First(x => x.Commercial).Match == "Nickelodeon (studio “Nickelodeon”) · 1990s",
    "With enough accurate clips, breaks stay accurate and are not widened");
var noise = new Random(7);
uint[] Noise(int count) => Enumerable.Range(0, count).Select(_ => (uint)noise.NextInt64(0, 1L << 32)).ToArray();
var sharedCredits = Noise(200);
var printA = Noise(300).Concat(sharedCredits).ToArray();
var printB = Noise(250).Concat(sharedCredits).Concat(Noise(20)).ToArray();
// A few noisy bits and one dropout in the shared credits still match.
printB[260] ^= 0b10101; printB[300] = ~printB[300];
var creditsMatch = CreditsDetector.Match(printA, printB);
Check(creditsMatch is { StartA: 300, EndA: 500, StartB: 250, EndB: 450 }, "Matching closing music is found in two episodes at different times, despite small differences");
Check(CreditsDetector.Match(Noise(400), Noise(400)) is null, "Episodes with different endings share no credits");
Check(CreditsDetector.Match(Noise(300).Concat(sharedCredits.Take(80)).ToArray(), Noise(300).Concat(sharedCredits.Take(80)).ToArray()) is null, "A shared sting shorter than fifteen seconds is not credits");
var sampled = Enumerable.Range(0, 60).Select(n => (Time: 100.0 + n * 2, Black: n is >= 30 and < 55 ? (n == 40 ? 20 : 95) : 10)).ToList();
Check(CreditsDetector.BlackRun(sampled, 220) == (160, 210), "Credits on black run past a single logo frame and stop where the picture returns");
Check(CreditsDetector.BlackRun(sampled.Select(x => x with { Black = x.Time >= 200 ? 95 : 10 }).ToList(), 220) == (200, 220), "Black credits at the very end run to the end of the file");
Check(CreditsDetector.BlackRun(sampled.Select(x => x with { Black = x.Time is >= 150 and < 160 ? 95 : 10 }).ToList(), 220) is null, "A short fade to black is not credits");
Check(CreditsDetector.ParseBlackFrames("[Parsed_blackframe_1 @ 0x1] frame:3 pblack:97 pts:768 t:6.000000 type:I last_keyframe:3", 100).SequenceEqual([(106.0, 97)]), "Black frame times are read relative to where the scan started");
// Rules and edits re-file clips without renaming files.
Media At(string path, int year = 0) => new(Guid.NewGuid(), Path.GetFileNameWithoutExtension(path), path, 30, "", 0, 0, year);
var bumpClip = At("/Ads/Adult Swim/bump 1.mp4", 2004);
var colaClip = At("/Ads/Cola.mp4");
var dupClip = At("/Ads/Dup.mp4", 1999);
var offClip = At("/Ads/Nickelodeon/Broken 1995.mp4", 1995);
var folderClip = At("C:\\Ads\\Toonami\\2000s\\one.mp4", 2001);
var editConfig = new Configuration
{
    ClipRules = [new ClipEdit { Contains = "bump", Kind = "bumper" }, new ClipEdit { Contains = "/toonami/2000s/", Network = "Toonami" }, new ClipEdit { Contains = "/Ads/", Year = 1990 }],
    ClipOverrides = [new ClipEdit { ItemId = colaClip.Id.ToString(), Network = "Nickelodeon", Year = 1996, Show = "Rugrats" }, new ClipEdit { ItemId = offClip.Id.ToString(), Disabled = true }, new ClipEdit { ItemId = bumpClip.Id.ToString(), Kind = "commercial", Network = "General" }],
};
var edited = ClipEdits.Resolve([(bumpClip, "commercials"), (colaClip, "commercials"), (offClip, "commercials"), (folderClip, "bumpers"), (dupClip, "commercials"), (dupClip, "commercials")], editConfig);
ClipEntry Of(Media clip) => edited.Single(x => x.Clip.Id == clip.Id);
Check(Of(bumpClip).Kind == "commercials" && Of(bumpClip).Label == new ClipLabel("", null) && Of(bumpClip).Clip.Year == 1990 && Of(bumpClip).Sources.SequenceEqual(["rule “bump”", "rule “/Ads/”", "edited"]),
    "A clip edit beats the rules, which apply in order, and a rule can match part of a name");
Check(Of(colaClip).Clip.Year == 1996 && Of(colaClip).Label == new ClipLabel("Nickelodeon", "Rugrats") && Of(offClip).Disabled && ClipEdits.Of(edited, "commercials").All(x => x.Id != offClip.Id),
    "Edits set a clip's year, network and show, and turned-off clips are left out of the lists the planner uses");
Check(Of(folderClip).Label?.Network == "Toonami" && edited.Count(x => x.Clip.Id == dupClip.Id) == 1 && Of(dupClip).Clip.Year == 1990, "Rules match any folder separator and case, and a clip listed twice is one entry");
var movedBumpers = ClipEdits.Resolve([(At("/Ads/Cola bump.mp4"), "commercials"), (At("/Ads/Cola spot.mp4"), "commercials")], new Configuration { ClipRules = [new ClipEdit { Contains = "bump", Kind = "bumper" }] });
Check(ClipEdits.Of(movedBumpers, "bumpers").Count == 1 && ClipEdits.Of(movedBumpers, "commercials").Count == 1, "A name rule can turn clips chosen as commercials into bumpers");
var editLabels = new Dictionary<Guid, ClipLabel> { [colaClip.Id] = new("Adult Swim", null), [dupClip.Id] = new("", null), [bumpClip.Id] = new(null, "Rugrats") };
var editIndex = new Planner.NetworkIndex([], ["Rugrats"], editLabels);
Check(editIndex.For(colaClip).SequenceEqual(["adult swim"]) && editIndex.For(Named("Nickelodeon x.mp4")).SequenceEqual(["nickelodeon"]) && editIndex.For(dupClip).Length == 0 && editIndex.ShowsIn(bumpClip).SequenceEqual(["rugrats"]),
    "A clip's set network or show replaces what its path says, and General removes the network");
var labelledState = new ChannelState();
Planner.Extend(matchChannel, labelledState, new Dictionary<string, List<Media>> { ["default"] = [aqua, rugrats], ["commercials"] = [colaClip with { Year = 2004 }, bumpClip with { Year = 1993 }] }, now, now.AddHours(2), TimeZoneInfo.Utc, null, null, null, editIndex);
Airing Following(Media program) => labelledState.Guide[labelledState.Guide.FindIndex(x => x.ItemId == program.Id) + 1];
Check(Following(aqua).ItemId == colaClip.Id && Following(rugrats).ItemId == bumpClip.Id, "A clip set to a network plays with that network's programs, and one set to a show only with that show");

// Block timing: exact starts, allowed lateness, fitting whole programs, and padding to round times.
{
    var day = new DateTimeOffset(2026, 10, 19, 0, 0, 0, TimeSpan.Zero);
    Media Hour(int n) => new(new Guid(5000 + n, 0, 0, new byte[8]), "Long " + n, "long.mkv", 3000, "", 0, 0);
    Media Half(int n, string show = "cartoon") => Episode(900 + n, show) with { Seconds = 1500, Show = show, Episode = n };
    Media Spot(int n) => new(new Guid(6000 + n, 0, 0, new byte[8]), "Spot " + n, "spot.mp4", 20, "", 0, 0);
    var spots = Enumerable.Range(1, 6).Select(Spot).ToList();
    var longs = Enumerable.Range(1, 40).Select(Hour).ToList();
    var cartoons = Enumerable.Range(1, 30).Select(n => Half(n)).ToList();
    List<Airing> Plan(ChannelDefinition c, List<Media> main, List<Media> block, List<Media> ads, double hours = 30)
    {
        var st = new ChannelState();
        var blocks = c.Blocks.ToDictionary(b => b.Id, _ => block);
        var p = new Dictionary<string, List<Media>>(blocks) { ["default"] = main, ["commercials"] = ads };
        Planner.Extend(c, st, p, day, day.AddHours(hours), TimeZoneInfo.Utc);
        return st.Guide;
    }
    ChannelDefinition WithBlock(TimeBlock b, int pad = 0) => new() { Id = "timing", Source = new Pool { Shuffle = false }, Blocks = [b], PadMinutes = pad };
    TimeBlock Evening(string mode = "soft", int late = 0, string fill = "count") => new() { Id = "evening", Start = "18:00", End = "19:00", StartMode = mode, MaxLateMinutes = late, Fill = fill, Source = new Pool { Shuffle = false } };

    var soft = Plan(WithBlock(Evening()), longs, cartoons, spots);
    Check(soft.Any(x => x.Start < day.AddHours(18) && x.End > day.AddHours(18)), "A soft block lets the program before it run past its start");
    var exact = Plan(WithBlock(Evening("exact")), longs, cartoons, spots);
    Check(exact.All(x => !(x.Start < day.AddHours(18) && x.End > day.AddHours(18))) && exact.First(x => x.Title.StartsWith("cartoon") || x.Title.Contains("Episode 9")).Start == day.AddHours(18), "An exact block starts on the minute, because nothing before it runs past its start");
    Check(exact.Where(x => x.End <= day.AddHours(18) && x.Start >= day.AddHours(17)).Any(x => x.Commercial), "The time before an exact block is filled with clips");
    var late = Plan(WithBlock(Evening("soft", 10)), longs, cartoons, spots);
    Check(late.Where(x => x.Start >= day.AddHours(18) && x.End <= day.AddHours(19)).All(x => !x.Title.Contains("Episode 9")) , "A soft block that would start more than its allowed lateness after the program before it is skipped");
    var tolerant = Plan(WithBlock(Evening("soft", 60)), longs, cartoons, spots);
    Check(tolerant.Any(x => x.Title.Contains("Episode 9") && x.Start >= day.AddHours(18)), "A block within its allowed lateness still airs");
    var fitted = Plan(WithBlock(Evening("exact", 0, "fit")), longs, cartoons, spots);
    var inBlock = fitted.Where(x => x.Title.Contains("Episode 9") && x.Start >= day.AddHours(18) && x.Start < day.AddHours(19)).ToArray();
    Check(inBlock.Length == 2 && inBlock.All(x => x.End <= day.AddHours(19)), "A fitted block airs only whole programs that end before it does");
    Check(fitted.Where(x => x.Start >= inBlock[^1].End && x.Start < day.AddHours(19)).All(x => x.Commercial) && fitted.Where(x => x.Start >= day.AddHours(19)).First().End > day.AddHours(19), "What is left of a fitted block is filled with clips, and the next program starts at its end");
    var nothing = Plan(WithBlock(Evening("exact", 0, "fit")), longs, cartoons, []);
    Check(nothing.All(x => !(x.Start < day.AddHours(19) && x.End > day.AddHours(19) && x.Title.Contains("Episode 9"))), "A fitted block with no clips waits out the leftover time");

    var padded = Plan(new ChannelDefinition { Id = "padded", Source = new Pool { Shuffle = false }, PadMinutes = 30 }, cartoons.Select(x => x with { Seconds = 1320 }).ToList(), [], spots, 6);
    var programs = padded.Where(x => !x.Commercial).ToArray();
    Check(programs.Length > 8 && programs.All(x => x.Start.Minute % 30 == 0 && x.Start.Second == 0), "Padding starts every program on the half hour");
    Check(padded.Where(x => x.Commercial && programs.Any(p => p.Start >= x.End)).All(x => x.End <= programs.First(p => p.Start >= x.End).Start) && padded.Count(x => x.Commercial) > 8, "Padding is made of clips that finish before the next program");
    var tight = Plan(new ChannelDefinition { Id = "tight", Source = new Pool { Shuffle = false }, PadMinutes = 30 }, cartoons.Select(x => x with { Seconds = 1800 }).ToList(), [], spots, 3);
    Check(tight.All(x => !x.Commercial), "A program that already ends on the half hour needs no padding");
    var unpadded = Plan(new ChannelDefinition { Id = "unpadded", Source = new Pool { Shuffle = false } }, cartoons.Select(x => x with { Seconds = 1320 }).ToList(), [], spots, 3);
    Check(unpadded.Count == 9 && unpadded.All(x => !x.Commercial), "Without padding, programs follow one another directly");

    // Breaks inside programs.
    var movie = new Media(new Guid(7000, 0, 0, new byte[8]), "Feature", "feature.mkv", 3600, "", 0, 0);
    int Parts(ChannelDefinition c) { var g = Plan(c, [movie], [], spots, 1); return g.TakeWhile(x => x.Start < day.AddHours(1)).Count(x => !x.Commercial && x.ItemId == movie.Id); }
    Check(Parts(new ChannelDefinition { Id = "iv", CommercialCount = 1, BreakMode = "interval", BreakIntervalMinutes = 20, Source = new Pool() }) == 3, "Interval breaks fall every set number of minutes");
    Check(Parts(new ChannelDefinition { Id = "iv", CommercialCount = 1, BreakMode = "interval", BreakIntervalMinutes = 15, MaxBreaksPerProgram = 1, Source = new Pool() }) == 2, "A program has at most the set number of breaks");
    Check(Parts(new ChannelDefinition { Id = "iv", CommercialCount = 1, BreakMode = "interval", BreakIntervalMinutes = 20, BreakFreeEndMinutes = 25, Source = new Pool() }) == 2, "A break-free ending keeps the last minutes of a program unbroken");
    Check(Parts(new ChannelDefinition { Id = "iv", CommercialCount = 1, MovieBreaks = 2, MinBreakProgramMinutes = 90, Source = new Pool() }) == 1, "Programs shorter than the minimum are not split");
    var varied = Plan(new ChannelDefinition { Id = "varied", CommercialCount = 1, CommercialCountMax = 4, EpisodeBreaks = 0, Source = new Pool { Shuffle = false } }, cartoons, [], Enumerable.Range(1, 12).Select(Spot).ToList(), 24);
    var lengths = new List<int>();
    var run = 0;
    foreach (var entry in varied) { if (entry.Commercial) run++; else if (run > 0) { lengths.Add(run); run = 0; } else run = 0; }
    Check(lengths.Count > 10 && lengths.All(x => x is >= 1 and <= 4) && lengths.Distinct().Count() > 1, "Breaks vary in length when a range is set");
}

// Playback orders and turns.
{
    Media Movie(int n, int year, string title) => new(new Guid(8000 + n, 0, 0, new byte[8]), title, "m.mkv", 5400, "", 0, 0, year, Show: title);
    var films = new List<Media> { Movie(1, 1995, "Zeta"), Movie(2, 1985, "Mu"), Movie(3, 2005, "Alpha"), Movie(4, 1990, "Kappa") };
    string Run(Pool pool, List<Media> media, int count, string seed = "order") { var r = new Rotation(); return string.Join(" ", Enumerable.Range(0, count).Select(_ => Planner.Next(media, pool, r, seed)!.Title)); }
    Check(Run(new Pool { Order = "chronological" }, films, 4) == "Mu Kappa Zeta Alpha", "Chronological order airs by release date");
    Check(Run(new Pool { Order = "alphabetical" }, films, 4) == "Alpha Kappa Mu Zeta", "Alphabetical order airs by title");
    var twelve = Enumerable.Range(1, 12).Select(n => Episode(n)).ToList();
    var starts = new[] { "a", "b", "c", "d", "e", "f" }.Select(seed => Run(new Pool { Shuffle = false, RandomStart = true }, twelve, 1, seed)).Distinct().Count();
    Check(starts > 1, "A random start begins the first cycle at different places");
    var cycleRotation = new Rotation();
    var cycle2 = Enumerable.Range(0, 12).Select(_ => Planner.Next(twelve, new Pool { Shuffle = false, RandomStart = true }, cycleRotation, "c")!.Episode).ToList();
    Check(cycle2.Distinct().Count() == 12, "A random start still airs every episode once per cycle");

    Media Part(string show, int n, string name) => Episode(300 + n + (show == "B" ? 50 : 0), show) with { Show = show, Episode = n, Title = $"{show} — S01E{n:00} {name}" };
    var parted = new[] { Part("A", 1, "Intro"), Part("A", 2, "Two Parter (Part 1)"), Part("A", 3, "Two Parter (Part 2)"), Part("A", 4, "Finale"), Part("B", 1, "One"), Part("B", 2, "Two"), Part("B", 3, "Three"), Part("B", 4, "Four") }.ToList();
    string Order(Pool pool) { var r = new Rotation(); return string.Join(" ", Enumerable.Range(0, 8).Select(_ => { var m = Planner.Next(parted, pool, r, "p")!; return m.Series + m.Episode; })); }
    Check(Order(new Pool { Shuffle = false, EpisodesInARow = 2 }).Replace(" ", "").Contains("A2B"), "Without the option, two episodes in a row can end in the middle of a two-parter");
    var together = Order(new Pool { Shuffle = false, EpisodesInARow = 2, KeepMultiPart = true });
    Check(together.StartsWith("A1 A2 A3") && !together.Contains("A2 B"), "Two-part episodes stay together in one turn");
    Check(Planner.PartOf("Show — S01E02 The Trip (Part 1)") == ("the trip", 1) && Planner.PartOf("Show — S01E03 The Trip, Part Two") == ("the trip", 2) && Planner.PartOf("Show — S01E03 Plain") is null, "Part numbers are read from episode names");
    var turnPool = new Pool { Shuffle = false, EpisodesInARow = 1, Turns = [new ShowTurn { Show = "A", Episodes = 3 }] };
    Check(Order(turnPool).StartsWith("A1 A2 A3 B1"), "A show can have its own number of episodes in a row");
}
Check(Catalog_RatingAllowed("TV-14", "TV-PG") == false && Catalog_RatingAllowed("TV-G", "TV-PG") && Catalog_RatingAllowed(null, "G") && Catalog_RatingAllowed("PG", ""), "A pool's highest rating keeps unrated programs and drops stricter ones");
static bool Catalog_RatingAllowed(string? rating, string max)
{
    var ladder = new[] { new[] { "g", "tv-y", "tv-g" }, new[] { "tv-y7" }, new[] { "pg", "tv-pg" }, new[] { "pg-13", "tv-14" }, new[] { "r", "tv-ma" } };
    int Level(string? r) => Array.FindIndex(ladder, l => l.Contains((r ?? "").ToLowerInvariant()));
    return max.Length == 0 || Level(rating) < 0 || Level(max) < 0 || Level(rating) <= Level(max);
}
Console.WriteLine($"{passed} scheduling checks passed.");
