using Jellyfin.Plugin.Popfeed.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Popfeed;

/// <summary>
/// Registers Popfeed services.
/// </summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHttpClient();
        serviceCollection.AddSingleton<PopfeedAtProtoClient>();
        serviceCollection.AddSingleton<IPopfeedWatchStateWriter, PopfeedWatchedListWriter>();
        serviceCollection.AddSingleton<PopfeedSyncStatusStore>();
        serviceCollection.AddSingleton<PopfeedSyncService>();
        serviceCollection.AddHostedService<ServerMediator>();
    }
}