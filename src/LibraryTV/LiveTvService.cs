using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.MediaInfo;

namespace Jellyfin.Plugin.LibraryTV;

public sealed class LiveTvService(Scheduler scheduler) : ILiveTvService
{
    public string Name => "Library TV";
    public string HomePageUrl => "https://github.com/thehearthz/jellyfin-plugin-library-tv";
    public Task<IEnumerable<ChannelInfo>> GetChannelsAsync(CancellationToken ct) => Task.FromResult(scheduler.Channels.Select(x => new ChannelInfo { Id = x.Id, Name = x.Name, Number = x.Number, ChannelType = ChannelType.TV }));
    public Task<IEnumerable<ProgramInfo>> GetProgramsAsync(string channelId, DateTime start, DateTime end, CancellationToken ct) => Task.FromResult(scheduler.GuideFor(channelId).Where(x => x.Start.UtcDateTime < end && x.End.UtcDateTime > start).Select(x => new ProgramInfo
    {
        Id = $"{x.ChannelId}-{x.Start.UtcTicks}", ChannelId = x.ChannelId, Name = x.Title,
        StartDate = x.Start.UtcDateTime, EndDate = x.End.UtcDateTime, Overview = x.Commercial ? "Commercial break" : "From your Jellyfin library"
    }));
    internal static MediaSourceInfo Source(string id)
    {
        var c = Plugin.Instance.Configuration;
        return new MediaSourceInfo { Id = "librarytv-" + id, Path = $"{c.InternalBaseUrl.TrimEnd('/')}/LibraryTV/stream/{Uri.EscapeDataString(id)}?token={Uri.EscapeDataString(c.StreamToken)}", Protocol = MediaProtocol.Http, Container = "ts", IsInfiniteStream = true, SupportsDirectPlay = false, SupportsDirectStream = true, SupportsTranscoding = true, RequiresOpening = false, RequiresClosing = false, BufferMs = 1500 };
    }
    public Task<MediaSourceInfo> GetChannelStream(string channelId, string streamId, CancellationToken ct) => Task.FromResult(Source(channelId));
    public Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(string channelId, CancellationToken ct) => Task.FromResult(new List<MediaSourceInfo> { Source(channelId) });
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
