using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.LibraryTV;

public sealed class LiveTvService : ILiveTvService
{
    private readonly Lazy<Scheduler> scheduler;
    private readonly StartupGate? startup;
    private readonly Func<Guid, ProgramDetails?> details;
    public LiveTvService(IServiceProvider services, StartupGate startup)
    {
        this.startup = startup;
        scheduler = new(() => services.GetRequiredService<Scheduler>());
        var catalog = new Lazy<Catalog>(() => services.GetRequiredService<Catalog>());
        details = id => catalog.Value.Details(id);
    }
    internal LiveTvService(Scheduler scheduler, Func<Guid, ProgramDetails?>? details = null)
    {
        this.scheduler = new(() => scheduler);
        this.details = details ?? (_ => null);
    }
    private IReadOnlyList<ChannelDefinition> Channels => startup?.IsReady == false ? [] : scheduler.Value.Channels;
    public string Name => "Library TV";
    public string HomePageUrl => "https://github.com/thehearthz/jellyfin-plugin-library-tv";
    public Task<IEnumerable<ChannelInfo>> GetChannelsAsync(CancellationToken ct) => Task.FromResult(Channels.Select(x => new ChannelInfo { Id = x.Id, Name = x.Name, Number = x.Number, ChannelType = ChannelType.TV, ImagePath = ProgramStreamer.IsImage(x.Logo ?? "") && File.Exists(x.Logo) ? x.Logo : null }));
    public Task<IEnumerable<ProgramInfo>> GetProgramsAsync(string channelId, DateTime start, DateTime end, CancellationToken ct)
    {
        var known = new Dictionary<Guid, ProgramDetails?>();
        return Task.FromResult<IEnumerable<ProgramInfo>>(Listings(startup?.IsReady == false ? [] : scheduler.Value.GuideFor(channelId), HidesBreaks(channelId))
            .Where(x => x.Start.UtcDateTime < end && x.End.UtcDateTime > start)
            .Select(x => Program(x, x.Commercial ? null : known.TryGetValue(x.ItemId, out var found) ? found : known[x.ItemId] = details(x.ItemId))).ToList());
    }

    // A guide entry with what the library knows about it, so the guide shows its picture, episode, genres and
    // rating, and Live TV's Movies, Shows, Kids, News and Sports sections find it.
    internal static ProgramInfo Program(Airing x, ProgramDetails? about)
    {
        var info = new ProgramInfo
        {
            Id = $"{x.ChannelId}-{x.Start.UtcTicks}", ChannelId = x.ChannelId, Name = x.Heading ?? x.Title,
            StartDate = x.Start.UtcDateTime, EndDate = x.End.UtcDateTime, Overview = Planner.IsIdent(x.Path) ? "Station ident" : x.Commercial ? "Commercial break" : x.Heading is null ? "From your Jellyfin library" : x.Title,
            // A new Etag when what airs in a slot changes makes Jellyfin replace its copy of the entry.
            Etag = $"{x.ItemId:N}-{x.End.UtcTicks}", IsRepeat = true,
        };
        if (about is null) return info;
        var genres = about.Genres.ToList();
        bool Has(params string[] names) => genres.Any(g => names.Any(n => g.Contains(n, StringComparison.OrdinalIgnoreCase)));
        info.Name = x.Heading ?? about.Name ?? x.Title;
        info.EpisodeTitle = x.Heading is not null ? (about.Series ? $"{about.Name}: {about.EpisodeTitle}" : about.Name) : about.EpisodeTitle;
        info.SeasonNumber = about.Season;
        info.EpisodeNumber = about.Episode;
        info.IsSeries = about.Series;
        info.IsMovie = about.Movie;
        info.SeriesId = about.SeriesId;
        info.Genres = genres;
        info.IsKids = Has("Kids", "Children", "Family");
        info.IsSports = Has("Sport");
        info.IsNews = Has("News");
        info.OfficialRating = about.Rating;
        info.ProductionYear = about.Year;
        info.OriginalAirDate = about.Aired;
        info.CommunityRating = about.Score;
        info.Overview = about.Overview is { Length: > 0 } overview ? overview : info.Overview;
        if (about.Image is { } image && File.Exists(image)) (info.ImagePath, info.HasImage) = (image, true);
        return info;
    }

    // The guide lists a program once, from its first part to its last, with the breaks inside it.
    private bool HidesBreaks(string channelId) => startup?.IsReady != false && scheduler.Value.Channels.FirstOrDefault(x => x.Id == channelId)?.GuideHidesBreaks == true;

    // With hideBreaks, commercials are left out and the program before them runs through them.
    internal static List<Airing> Listings(IReadOnlyList<Airing> guide, bool hideBreaks = false)
    {
        var listings = new List<Airing>();
        var program = -1;
        foreach (var entry in guide)
        {
            if (hideBreaks && entry.Commercial)
            {
                if (program >= 0 && listings[^1].End == entry.Start) listings[^1] = listings[^1] with { End = entry.End };
                continue;
            }
            if (!entry.Commercial && entry.Offset > 0 && program >= 0 && listings[program].ItemId == entry.ItemId)
            {
                listings.RemoveRange(program + 1, listings.Count - program - 1);
                listings[program] = listings[program] with { End = entry.End };
                continue;
            }
            if (!entry.Commercial) program = listings.Count;
            listings.Add(entry);
        }
        return listings;
    }
    internal static string SourceId(string id) => "librarytv-" + id;
    internal static MediaSourceInfo Source(string id)
    {
        var c = Plugin.Instance.Configuration;
        // Bound input analysis for the real-time transport. Jellyfin's fallback is
        // 200 seconds, which can make an otherwise healthy channel take minutes to tune.
        return new MediaSourceInfo { Id = SourceId(id), Path = $"{c.InternalBaseUrl.TrimEnd('/')}/LibraryTV/stream/{Uri.EscapeDataString(id)}?token={Uri.EscapeDataString(c.StreamToken)}", Protocol = MediaProtocol.Http, Container = "ts", IsInfiniteStream = true, SupportsDirectPlay = false, SupportsDirectStream = true, SupportsTranscoding = true, RequiresOpening = false, RequiresClosing = false, BufferMs = 1500, AnalyzeDurationMs = 3000 };
    }
    public Task<MediaSourceInfo> GetChannelStream(string channelId, string streamId, CancellationToken ct) { startup?.RequireReady(); return Task.FromResult(Source(channelId)); }
    public Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(string channelId, CancellationToken ct) { startup?.RequireReady(); return Task.FromResult(new List<MediaSourceInfo> { Source(channelId) }); }
    public Task CloseLiveStream(string id, CancellationToken ct) => Task.CompletedTask;
    public Task ResetTuner(string id, CancellationToken ct) => Task.CompletedTask;
    public Task<IEnumerable<TimerInfo>> GetTimersAsync(CancellationToken ct) => Task.FromResult(Enumerable.Empty<TimerInfo>());
    public Task<IEnumerable<SeriesTimerInfo>> GetSeriesTimersAsync(CancellationToken ct) => Task.FromResult(Enumerable.Empty<SeriesTimerInfo>());
    public Task<SeriesTimerInfo> GetNewTimerDefaultsAsync(CancellationToken ct, ProgramInfo? program = null) => Task.FromResult(new SeriesTimerInfo());
    public Task CancelTimerAsync(string id, CancellationToken ct) => Task.FromException(new NotSupportedException("Recordings are not supported."));
    public Task CancelSeriesTimerAsync(string id, CancellationToken ct) => Task.FromException(new NotSupportedException("Recordings are not supported."));
    public Task CreateTimerAsync(TimerInfo info, CancellationToken ct) => Task.FromException(new NotSupportedException("Recordings are not supported."));
    public Task CreateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken ct) => Task.FromException(new NotSupportedException("Recordings are not supported."));
    public Task UpdateTimerAsync(TimerInfo info, CancellationToken ct) => Task.FromException(new NotSupportedException("Recordings are not supported."));
    public Task UpdateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken ct) => Task.FromException(new NotSupportedException("Recordings are not supported."));
}
