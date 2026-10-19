using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.LibraryTV;

public sealed class Plugin : BasePlugin<Configuration>, IHasWebPages
{
    public static Plugin Instance { get; private set; } = null!;
    public Plugin(IApplicationPaths paths, IXmlSerializer serializer) : base(paths, serializer) => Instance = this;
    public override string Name => "Library TV";
    public override string Description => "Always-on virtual television from your own libraries, with no idle encoding.";
    public override Guid Id => new("512a2a0d-5101-4fe8-9651-484f077a601a");
    public IEnumerable<PluginPageInfo> GetPages() => [new() { Name = "librarytv", EmbeddedResourcePath = GetType().Namespace + ".Web.config.html" }];
}

public sealed class Services : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost host)
    {
        services.AddSingleton<Catalog>();
        services.AddSingleton<StartupGate>();
        services.AddSingleton<Scheduler>();
        services.AddSingleton<Broadcasts>();
        services.AddSingleton<IProgramStreamer, ProgramStreamer>();
        services.AddSingleton<MediaBrowser.Controller.LiveTv.ILiveTvService, LiveTvService>();
        services.AddSingleton<MediaBrowser.Controller.Channels.IChannel, LibraryChannel>();
        services.AddHostedService<RefreshService>();
        services.AddHostedService<PluginPagesIntegration>();
    }
}
