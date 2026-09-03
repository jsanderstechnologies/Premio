using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Premio.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Premio.Services;

/// <summary>
/// HTTP client for retrieving IMDb TV show season and episode metadata via the Cinemeta service.
/// Requires no API key and provides complete, up-to-date IMDb season/episode manifests.
/// </summary>
public sealed partial class ImdbClient
{
    private readonly HttpClient _http;
    private readonly ILogger<ImdbClient> _logger;

    /// <summary>
    /// Initialises a new instance of <see cref="ImdbClient"/>.
    /// </summary>
    /// <param name="httpClient">Injected HTTP client.</param>
    /// <param name="logger">Injected logger.</param>
    public ImdbClient(HttpClient httpClient, ILogger<ImdbClient> logger)
    {
        _http = httpClient;
        _logger = logger;
        _http.BaseAddress = new Uri("https://v3-cinemeta.strem.io/");
        _http.Timeout = TimeSpan.FromSeconds(15);
    }

    /// <summary>
    /// Retrieves all seasons and episode counts for a TV show by its IMDb ID.
    /// </summary>
    /// <param name="imdbId">IMDb identifier (e.g. "tt9288030").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A list of season summaries discovered from IMDb, or empty list on error.</returns>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "IMDb metadata lookup is a non-critical fallback.")]
    public async Task<IReadOnlyList<TmdbSeasonSummary>> GetSeriesSeasonsAsync(
        string imdbId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(imdbId) || !imdbId.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var url = $"meta/series/{Uri.EscapeDataString(imdbId)}.json";
        try
        {
            LogFetchingImdbSeries(_logger, imdbId);
            var response = await _http.GetFromJsonAsync<CinemetaSeriesResponse>(url, cancellationToken).ConfigureAwait(false);
            var videos = response?.Meta?.Videos;
            if (videos is null || videos.Count == 0)
            {
                return [];
            }

            var seasons = new List<TmdbSeasonSummary>();
            var seasonGroups = videos
                .Where(v => v.Season >= 1)
                .GroupBy(v => v.Season)
                .OrderBy(g => g.Key);

            foreach (var group in seasonGroups)
            {
                var seasonNum = group.Key;
                var maxEpisode = group.Max(v => Math.Max(v.Number, v.Episode));
                var count = Math.Max(group.Count(), maxEpisode);

                seasons.Add(new TmdbSeasonSummary
                {
                    SeasonNumber = seasonNum,
                    EpisodeCount = count,
                    Name = $"Season {seasonNum:D2}"
                });
            }

            LogImdbSeriesFound(_logger, imdbId, seasons.Count);
            return seasons;
        }
        catch (Exception ex)
        {
            LogImdbSeriesFetchFailed(_logger, imdbId, ex.Message);
            return [];
        }
    }

    // -------------------------------------------------------------------------
    // LoggerMessage delegates (CA1848)
    // -------------------------------------------------------------------------

    [LoggerMessage(Level = LogLevel.Debug, Message = "Premio: Fetching IMDb series metadata for {ImdbId} via Cinemeta")]
    private static partial void LogFetchingImdbSeries(ILogger logger, string imdbId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Premio: Discovered {SeasonCount} seasons from IMDb for {ImdbId}")]
    private static partial void LogImdbSeriesFound(ILogger logger, string imdbId, int seasonCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Premio: Failed to fetch IMDb series metadata for {ImdbId}: {ErrorMessage}")]
    private static partial void LogImdbSeriesFetchFailed(ILogger logger, string imdbId, string errorMessage);
}
