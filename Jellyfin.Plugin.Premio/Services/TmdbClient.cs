using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Premio.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Premio.Services;

/// <summary>
/// Typed HTTP client for searching The Movie Database (TMDB) API and retrieving metadata and posters.
/// </summary>
public sealed partial class TmdbClient
{
    private const string DefaultTmdbKey = "b2ee93c834a0ec94e09f53c15ca7c908";
    private readonly HttpClient _http;
    private readonly ILogger<TmdbClient> _logger;

    /// <summary>
    /// Initialises a new instance of <see cref="TmdbClient"/>.
    /// </summary>
    /// <param name="httpClient">Injected HTTP client.</param>
    /// <param name="logger">Injected logger.</param>
    public TmdbClient(HttpClient httpClient, ILogger<TmdbClient> logger)
    {
        _http = httpClient;
        _logger = logger;
        _http.BaseAddress = new Uri("https://api.themoviedb.org/3/");
        _http.Timeout = TimeSpan.FromSeconds(15);
    }

    private static string ApiKey
    {
        get
        {
            var userKey = PremioPlugin.Instance?.Configuration?.TmdbApiKey;
            return !string.IsNullOrWhiteSpace(userKey) ? userKey.Trim() : DefaultTmdbKey;
        }
    }

    /// <summary>
    /// Searches TMDB for movies and TV shows matching <paramref name="query"/>.
    /// </summary>
    /// <param name="query">Search term.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A list of matching TMDB items.</returns>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "TMDB search errors are non-critical and should not disrupt local search.")]
    public async Task<IReadOnlyList<TmdbItem>> SearchMultiAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        try
        {
            var url = $"search/multi?query={Uri.EscapeDataString(query)}&api_key={ApiKey}&include_adult=false";
            LogSearchingTmdb(_logger, query);

            var response = await _http
                .GetFromJsonAsync<TmdbSearchResponse>(url, cancellationToken)
                .ConfigureAwait(false);

            return response?.Results ?? [];
        }
        catch (Exception ex)
        {
            LogTmdbSearchFailed(_logger, query, ex.Message);
            return [];
        }
    }

    /// <summary>
    /// Downloads image bytes from a given TMDB image URI.
    /// </summary>
    /// <param name="imageUri">Absolute image URI.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Image bytes or null if download failed.</returns>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Image download failures are non-critical.")]
    public async Task<byte[]?> DownloadImageBytesAsync(
        Uri imageUri,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(imageUri);

        try
        {
            return await _http.GetByteArrayAsync(imageUri, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogImageDownloadFailed(_logger, imageUri.AbsoluteUri, ex.Message);
            return null;
        }
    }

    // -------------------------------------------------------------------------
    // LoggerMessage delegates (CA1848)
    // -------------------------------------------------------------------------

    [LoggerMessage(Level = LogLevel.Debug, Message = "Premio: Searching TMDB for '{Query}'")]
    private static partial void LogSearchingTmdb(ILogger logger, string query);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Premio: TMDB search failed for query '{Query}': {ErrorMessage}")]
    private static partial void LogTmdbSearchFailed(ILogger logger, string query, string errorMessage);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Premio: Failed to download poster from '{Uri}': {ErrorMessage}")]
    private static partial void LogImageDownloadFailed(ILogger logger, string uri, string errorMessage);
}
