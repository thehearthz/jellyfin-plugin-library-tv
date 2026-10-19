using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.LibraryTV;

// Jellyfin surfaces an IChannel provider as a separate My Media/home folder.
public sealed class LibraryChannel : IChannel, IRequiresMediaInfoCallback
{
    private readonly Lazy<Scheduler> scheduler;
    private readonly Lazy<IUserManager> users;
    private readonly StartupGate? startup;
    public LibraryChannel(IServiceProvider services, StartupGate startup)
    {
        this.startup = startup;
        scheduler = new(() => services.GetRequiredService<Scheduler>());
        users = new(() => services.GetRequiredService<IUserManager>());
    }
    internal LibraryChannel(Scheduler scheduler, IUserManager users)
    {
        this.scheduler = new(() => scheduler);
        this.users = new(() => users);
    }
    public string Name => "Library TV";
    public string Description => "Tune into your always-on library channels. Each channel joins the current scheduled program.";
    public string HomePageUrl => "https://github.com/thehearthz/jellyfin-plugin-library-tv";
    public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;
    // Jellyfin caches channel listings. Invalidate the cache when channel definitions change,
    // while keeping playable item IDs stable and avoiding a library query on every browse.
    public string DataVersion => startup?.IsReady == false ? "starting" : "1-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(scheduler.Value.Channels.Select(x => new { x.Id, x.Name, x.Number })))));

    public bool IsEnabledFor(string userId) => startup?.IsReady != false && Guid.TryParse(userId, out var id)
        && users.Value.GetUserById(id)?.HasPermission(PermissionKind.EnableLiveTvAccess) == true;

    public InternalChannelFeatures GetChannelFeatures() => new()
    {
        MediaTypes = [ChannelMediaType.Video], ContentTypes = [ChannelMediaContentType.Clip],
        SupportsContentDownloading = false
    };

    public Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsEnabledFor(query.UserId.ToString("N")) || !string.IsNullOrEmpty(query.FolderId))
            return Task.FromResult(new ChannelItemResult { TotalRecordCount = 0 });
        var channels = scheduler.Value.Channels.OrderBy(x => ChannelNumbers.Key(x.Number)).ThenBy(x => x.Name, StringComparer.Ordinal).ToArray();
        var items = channels.Skip(query.StartIndex ?? 0).Take(query.Limit ?? int.MaxValue).Select(x => new ChannelItemInfo
        {
            Id = x.Id, Name = $"{x.Number} · {x.Name}", Type = ChannelItemType.Media,
            MediaType = ChannelMediaType.Video, ContentType = ChannelMediaContentType.Clip,
            IsLiveStream = true, Overview = "Always on. Play to join the current scheduled program. See Live TV → Guide for the schedule."
        }).ToArray();
        return Task.FromResult(new ChannelItemResult { Items = items, TotalRecordCount = channels.Length });
    }

    public Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(string id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        startup?.RequireReady();
        if (!scheduler.Value.Channels.Any(x => x.Id == id))
            throw new ArgumentException("This channel is no longer enabled.", nameof(id));
        return Task.FromResult<IEnumerable<MediaSourceInfo>>([LiveTvService.Source(id)]);
    }

    public IEnumerable<ImageType> GetSupportedChannelImages() => [];
    public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken ct) =>
        Task.FromException<DynamicImageResponse>(new NotSupportedException("No channel artwork is supplied."));
}
