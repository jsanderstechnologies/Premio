using System;
using Jellyfin.Plugin.Premio.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace Jellyfin.Plugin.Premio;

/// <summary>
/// Startup filter that registers <see cref="PremioWebInjectionMiddleware"/> into the ASP.NET pipeline.
/// </summary>
public sealed class PremioStartupFilter : IStartupFilter
{
    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        ArgumentNullException.ThrowIfNull(next);

        return app =>
        {
            app.UseMiddleware<PremioWebInjectionMiddleware>();
            next(app);
        };
    }
}
