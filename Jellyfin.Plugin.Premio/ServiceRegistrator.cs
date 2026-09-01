using Jellyfin.Plugin.Premio.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Premio;

/// <summary>
/// Implements <see cref="IPluginServiceRegistrator"/> so that Jellyfin automatically
/// registers Premio services into the host DI container at startup (Jellyfin >= 10.9).
/// </summary>
public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // Typed HttpClient — lifetime managed by IHttpClientFactory.
        serviceCollection.AddHttpClient<PremiumizeClient>();

        // Singleton that manages .strm file creation and lifecycle.
        serviceCollection.AddSingleton<StrmFileService>();
    }
}
