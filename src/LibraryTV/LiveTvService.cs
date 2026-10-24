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
    public LiveTvService(IServiceProvider services, StartupGate startup)
    {
        this.startup = startup;
        scheduler = new(() => services.GetRequiredService<Scheduler>());
    }
    internal LiveTvService(Scheduler scheduler) => this.scheduler = new(() => scheduler);
    private IReadOnlyList<ChannelDefinition> Channels => startup?.IsReady == false ? [] : scheduler.Value.Channels;
    public string Name => "Library TV";
    public string HomePageUrl => "https://github.com/thehearthz/jellyfin-plugin-library-tv";
    public Task<IEnumerable<ChannelInfo>> GetChannelsAsync(CancellationToken ct) => Task.FromResult(Channels.Select(x => new ChannelInfo { Id = x.Id, Name = x.Name, Number = x.Number, ChannelType = ChannelType.TV }));
    public Task<IEnumerable<ProgramInfo>> GetProgramsAsync(string channelId, DateTime start, DateTime end, CancellationToken ct) => Task.FromResult(Listings(startup?.IsReady == false ? [] : scheduler.Value.GuideFor(channelId)).Where(x => x.Start.UtcDateTime < end && x.End.UtcDateTime > start).Select(x => new ProgramInfo
    {
        Id = $"{x.ChannelId}-{x.Start.UtcTicks}", ChannelId = x.ChannelId, Name = x.Title,
        StartDate = x.Start.UtcDateTime, EndDate = x.End.UtcDateTime, Overview = x.Commercial ? "Commercial break" : "From your Jellyfin library"
    }));

    // The guide lists a program once, from its first part to its last, with the breaks inside it.
    internal static List<Airing> Listings(IReadOnlyList<Airing> guide)
    {
        var listings = new List<Airing>();
        var program = -1;
        foreach (var entry in guide)
        {
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
    internal static MediaSourceInfo Source(string id)
    {
        var c = Plugin.Instance.Configuration;
        // Bound input analysis for the real-time transport. Jellyfin's fallback is
        // 200 seconds, which can make an otherwise healthy channel take minutes to tune.
        return new MediaSourceInfo { Id = "librarytv-" + id, Path = $"{c.InternalBaseUrl.TrimEnd('/')}/LibraryTV/stream/{Uri.EscapeDataString(id)}?token={Uri.EscapeDataString(c.StreamToken)}", Protocol = MediaProtocol.Http, Container = "ts", IsInfiniteStream = true, SupportsDirectPlay = false, SupportsDirectStream = true, SupportsTranscoding = true, RequiresOpening = false, RequiresClosing = false, BufferMs = 1500, AnalyzeDurationMs = 3000 };
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
