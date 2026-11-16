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
    // A program's file and formats from the library, for watching it from its start.
    private readonly Func<Guid, MediaSourceInfo[]> sources;
    public const string FromStartFolder = "from-start";
    public LibraryChannel(IServiceProvider services, StartupGate startup)
    {
        this.startup = startup;
        scheduler = new(() => services.GetRequiredService<Scheduler>());
        users = new(() => services.GetRequiredService<IUserManager>());
        sources = id => services.GetRequiredService<Catalog>().Sources(id, services.GetRequiredService<IMediaSourceManager>());
    }
    internal LibraryChannel(Scheduler scheduler, IUserManager users, Func<Guid, MediaSourceInfo[]>? sources = null)
    {
        this.scheduler = new(() => scheduler);
        this.users = new(() => users);
        this.sources = sources ?? (_ => []);
    }

    // The program on each channel now (during a break, the one the break interrupts or follows).
    internal IEnumerable<(ChannelDefinition Channel, Airing Program)> OnNow(DateTimeOffset now)
    {
        foreach (var channel in scheduler.Value.Channels.OrderBy(x => ChannelNumbers.Key(x.Number)).ThenBy(x => x.Name, StringComparer.Ordinal))
            if (scheduler.Value.GuideFor(channel.Id).LastOrDefault(x => !x.Commercial && x.Start <= now && x.ItemId != Guid.Empty) is { } program && now - program.End < TimeSpan.FromMinutes(30))
                yield return (channel, program);
    }
    public string Name => "Library TV";
    public string Description => "Tune into your always-on library channels. Each channel joins the current scheduled program.";
    public string HomePageUrl => "https://github.com/thehearthz/jellyfin-plugin-library-tv";
    public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;
    // Jellyfin caches channel listings. Invalidate the cache when channel definitions change,
    // while keeping playable item IDs stable and avoiding a library query on every browse.
    // What is on now is part of it, so "Watch from the start" lists the current programs.
    public string DataVersion => startup?.IsReady == false ? "starting" : "2-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(new { Channels = scheduler.Value.Channels.Select(x => new { x.Id, x.Name, x.Number }), Now = OnNow(DateTimeOffset.UtcNow).Select(x => x.Program.ItemId) }))));

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
        if (!IsEnabledFor(query.UserId.ToString("N")) || !string.IsNullOrEmpty(query.FolderId) && query.FolderId != FromStartFolder)
            return Task.FromResult(new ChannelItemResult { TotalRecordCount = 0 });
        if (query.FolderId == FromStartFolder)
        {
            // Each channel's current program, played from its beginning like any library video. The channel keeps running.
            var programs = OnNow(DateTimeOffset.UtcNow).ToArray();
            return Task.FromResult(new ChannelItemResult
            {
                TotalRecordCount = programs.Length,
                Items = programs.Skip(query.StartIndex ?? 0).Take(query.Limit ?? int.MaxValue).Select(x => new ChannelItemInfo
                {
                    Id = $"start-{x.Channel.Id}-{x.Program.ItemId:N}", Name = $"{x.Channel.Number} · {x.Program.Title}", Type = ChannelItemType.Media,
                    MediaType = ChannelMediaType.Video, ContentType = ChannelMediaContentType.Clip, IsLiveStream = false,
                    Overview = $"On {x.Channel.Name} now. Plays from the beginning, on its own; the channel carries on live."
                }).ToList()
            });
        }
        var channels = scheduler.Value.Channels.OrderBy(x => ChannelNumbers.Key(x.Number)).ThenBy(x => x.Name, StringComparer.Ordinal).ToArray();
        var entries = channels.Select(x => new ChannelItemInfo
        {
            Id = x.Id, Name = $"{x.Number} · {x.Name}", Type = ChannelItemType.Media,
            MediaType = ChannelMediaType.Video, ContentType = ChannelMediaContentType.Clip,
            IsLiveStream = true, Overview = "Always on. Play to join the current scheduled program. See Live TV → Guide for the schedule."
        }).ToList();
        if (channels.Length > 0) entries.Add(new ChannelItemInfo { Id = FromStartFolder, Name = "Watch from the start", Type = ChannelItemType.Folder, Overview = "What each channel is showing now, from the beginning." });
        return Task.FromResult(new ChannelItemResult { Items = entries.Skip(query.StartIndex ?? 0).Take(query.Limit ?? int.MaxValue).ToList(), TotalRecordCount = entries.Count });
    }

    public Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(string id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        startup?.RequireReady();
        if (id.StartsWith("start-", StringComparison.Ordinal) && id.Length > 39 && Guid.TryParseExact(id[^32..], "N", out var item))
        {
            var channel = id[6..^33];
            if (!scheduler.Value.Channels.Any(x => x.Id == channel)) throw new ArgumentException("This channel is no longer enabled.", nameof(id));
            // Only what the channel has aired lately, so the folder never becomes a way into the rest of the library.
            var now = DateTimeOffset.UtcNow;
            if (!scheduler.Value.GuideFor(channel).Any(x => x.ItemId == item && !x.Commercial && x.Start <= now && now - x.End < TimeSpan.FromHours(6))) throw new ArgumentException("This program is no longer on.", nameof(id));
            var found = sources(item);
            if (found.Length == 0) throw new ArgumentException("This program is no longer in the library.", nameof(id));
            return Task.FromResult<IEnumerable<MediaSourceInfo>>(found);
        }
        if (!scheduler.Value.Channels.Any(x => x.Id == id))
            throw new ArgumentException("This channel is no longer enabled.", nameof(id));
        return Task.FromResult<IEnumerable<MediaSourceInfo>>([LiveTvService.Source(id)]);
    }

    public IEnumerable<ImageType> GetSupportedChannelImages() => [];
    public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken ct) =>
        Task.FromException<DynamicImageResponse>(new NotSupportedException("No channel artwork is supplied."));
}
