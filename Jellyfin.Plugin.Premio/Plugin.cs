using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Premio.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Premio;

/// <summary>
/// The Premio Jellyfin plugin entry point.
/// Jellyfin discovers this class automatically via the <see cref="IPlugin"/> interface.
/// </summary>
public sealed class PremioPlugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// A stable GUID that uniquely identifies this plugin across every Jellyfin
    /// installation.  Never change this value after publishing.
    /// </summary>
    public static readonly Guid StaticId = new("a1b2c3d4-e5f6-7890-abcd-ef1234567890");

    /// <inheritdoc />
    public override Guid Id => StaticId;

    /// <inheritdoc />
    public override string Name => "Premio";

    /// <inheritdoc />
    public override string Description =>
        "Search and stream content via Premiumize from within Jellyfin.";

    /// <summary>Gets the singleton instance populated during plugin initialisation.</summary>
    public static PremioPlugin? Instance { get; private set; }

    /// <summary>
    /// Initialises a new instance of <see cref="PremioPlugin"/>.
    /// </summary>
    /// <param name="applicationPaths">Jellyfin application path provider (injected by host).</param>
    /// <param name="xmlSerializer">XML serialiser for plugin configuration (injected by host).</param>
    public PremioPlugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html"
            }
        ];
    }
}
