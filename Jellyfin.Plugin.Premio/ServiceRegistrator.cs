using Jellyfin.Plugin.Premio.Services;
using Jellyfin.Plugin.Premio.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Mvc;
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
        // Typed HttpClients — lifetime managed by IHttpClientFactory.
        serviceCollection.AddHttpClient<PremiumizeClient>();
        serviceCollection.AddHttpClient<TmdbClient>();
        serviceCollection.AddHttpClient<TvdbClient>();
        serviceCollection.AddHttpClient<ImdbClient>();
        serviceCollection.AddHttpClient<TorrentioClient>();

        // Singleton that manages .strm file creation and lifecycle.
        serviceCollection.AddSingleton<StrmFileService>();

        // Scoped action filter for search interception
        serviceCollection.AddScoped<SearchActionFilter>();
        serviceCollection.Configure<MvcOptions>(options =>
        {
            options.Filters.AddService<SearchActionFilter>();
        });

        // Scheduled task for background cloud storage synchronization
        serviceCollection.AddSingleton<IScheduledTask, SyncPremiumizeTask>();

        // Web client injection startup filter
        serviceCollection.AddTransient<Microsoft.AspNetCore.Hosting.IStartupFilter, PremioStartupFilter>();
    }
}
