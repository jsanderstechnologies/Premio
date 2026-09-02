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
/// Typed HTTP client for searching and retrieving torrent streams from Torrentio.
/// </summary>
public sealed partial class TorrentioClient
{
    private readonly HttpClient _http;
    private readonly ILogger<TorrentioClient> _logger;

    /// <summary>
    /// Initialises a new instance of <see cref="TorrentioClient"/>.
    /// </summary>
    /// <param name="httpClient">Injected HTTP client.</param>
    /// <param name="logger">Injected logger.</param>
    public TorrentioClient(HttpClient httpClient, ILogger<TorrentioClient> logger)
    {
        _http = httpClient;
        _logger = logger;
        _http.BaseAddress = new Uri("https://torrentio.strem.fun/");
        _http.Timeout = TimeSpan.FromSeconds(15);
    }

    /// <summary>
    /// Retrieves available torrent streams for a movie given its IMDB ID.
    /// </summary>
    /// <param name="imdbId">The IMDB identifier (e.g. "tt0093058").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A list of available torrent streams.</returns>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Torrentio lookup failures are non-critical and should not break the UI.")]
    public async Task<IReadOnlyList<TorrentioStreamResult>> GetMovieStreamsAsync(
        string imdbId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imdbId);

        try
        {
            var url = $"stream/movie/{Uri.EscapeDataString(imdbId)}.json";
            LogFetchingMovieStreams(_logger, imdbId);

            var response = await _http
                .GetFromJsonAsync<TorrentioResponse>(url, cancellationToken)
                .ConfigureAwait(false);

            return response?.Streams ?? [];
        }
        catch (Exception ex)
        {
            LogTorrentioError(_logger, imdbId, ex.Message);
            return [];
        }
    }

    /// <summary>
    /// Retrieves available torrent streams for a series episode given its IMDB ID, season, and episode number.
    /// </summary>
    /// <param name="imdbId">The IMDB identifier (e.g. "tt0903747").</param>
    /// <param name="season">Season number (1-based).</param>
    /// <param name="episode">Episode number (1-based).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A list of available torrent streams.</returns>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Torrentio lookup failures are non-critical and should not break the UI.")]
    public async Task<IReadOnlyList<TorrentioStreamResult>> GetSeriesStreamsAsync(
        string imdbId,
        int season,
        int episode,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imdbId);

        try
        {
            var url = $"stream/series/{Uri.EscapeDataString(imdbId)}:{season}:{episode}.json";
            LogFetchingSeriesStreams(_logger, imdbId, season, episode);

            var response = await _http
                .GetFromJsonAsync<TorrentioResponse>(url, cancellationToken)
                .ConfigureAwait(false);

            return response?.Streams ?? [];
        }
        catch (Exception ex)
        {
            LogTorrentioError(_logger, $"{imdbId}:{season}:{episode}", ex.Message);
            return [];
        }
    }

    // -------------------------------------------------------------------------
    // LoggerMessage delegates (CA1848)
    // -------------------------------------------------------------------------

    [LoggerMessage(Level = LogLevel.Debug, Message = "Premio: Fetching Torrentio streams for movie '{ImdbId}'")]
    private static partial void LogFetchingMovieStreams(ILogger logger, string imdbId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Premio: Fetching Torrentio streams for series '{ImdbId}' S{Season}E{Episode}")]
    private static partial void LogFetchingSeriesStreams(ILogger logger, string imdbId, int season, int episode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Premio: Torrentio stream fetch failed for '{Query}': {ErrorMessage}")]
    private static partial void LogTorrentioError(ILogger logger, string query, string errorMessage);
}
