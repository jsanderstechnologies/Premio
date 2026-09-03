using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Premio.Configuration;

/// <summary>
/// Serialised configuration for the Premio plugin.
/// Values are persisted to Jellyfin''s plugin configuration directory as XML.
/// </summary>
public sealed class PluginConfiguration : BasePluginConfiguration
{
    // -------------------------------------------------------------------------
    // Premiumize API settings
    // -------------------------------------------------------------------------

    /// <summary>
    /// Gets or sets the Premiumize API key (also called "API token").
    /// Obtain yours at https://www.premiumize.me/account under "API".
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the optional personal TMDB API key (v3 auth).
    /// If left empty, a built-in default key will be used.
    /// </summary>
    public string TmdbApiKey { get; set; } = string.Empty;

    // -------------------------------------------------------------------------
    // Network / HTTP settings
    // -------------------------------------------------------------------------

    /// <summary>
    /// Gets or sets the base URL for the Premiumize v2 API.
    /// Override only if you are running behind a reverse proxy.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1056:URI-like properties should not be strings",
        Justification = "Configuration property serialized as XML by Jellyfin; System.Uri is not round-trippable by the host serializer.")]
    public string ApiBaseUrl { get; set; } = "https://www.premiumize.me/api";

    /// <summary>
    /// Gets or sets the HTTP request timeout in seconds for Premiumize API calls.
    /// </summary>
    public int RequestTimeoutSeconds { get; set; } = 30;

    // -------------------------------------------------------------------------
    // .strm file settings
    // -------------------------------------------------------------------------

    /// <summary>
    /// Gets or sets the absolute path to the directory where .strm files are written by default.
    /// Jellyfin must have read access to this directory.
    /// </summary>
    public string StrmOutputDirectory { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the absolute path to the directory where Movie .strm files are written.
    /// When not set, falls back to <see cref="StrmOutputDirectory"/>.
    /// </summary>
    public string MoviesStrmDirectory { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the absolute path to the directory where TV Show .strm files are written.
    /// When not set, falls back to <see cref="StrmOutputDirectory"/>.
    /// </summary>
    public string TvShowsStrmDirectory { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether existing .strm files are overwritten
    /// on refresh. When <see langword="false"/> an existing file is left unchanged.
    /// </summary>
    public bool OverwriteExistingStrmFiles { get; set; } = true;

    // -------------------------------------------------------------------------
    // Search & Stream Filter settings
    // -------------------------------------------------------------------------

    /// <summary>
    /// Gets or sets a value indicating whether to filter torrent streams to only H.264 / x264 releases.
    /// Recommended for maximum direct play compatibility across web and low-power devices.
    /// </summary>
    public bool OnlyX264Streams { get; set; } = true;

    /// <summary>
    /// Gets or sets the maximum number of search results returned per query.
    /// </summary>
    public int MaxSearchResults { get; set; } = 50;
}
