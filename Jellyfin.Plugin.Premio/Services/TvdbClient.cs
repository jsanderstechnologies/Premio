using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Premio.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Premio.Services;

/// <summary>
/// HTTP client for retrieving TV show seasons and episode metadata from TheTVDB v4 API.
/// </summary>
public sealed partial class TvdbClient
{
    private readonly HttpClient _http;
    private readonly ILogger<TvdbClient> _logger;
    private readonly SemaphoreSlim _authLock = new(1, 1);
    private string? _cachedToken;
    private DateTimeOffset _tokenExpiry = DateTimeOffset.MinValue;

    /// <summary>
    /// Initialises a new instance of <see cref="TvdbClient"/>.
    /// </summary>
    /// <param name="httpClient">Injected HTTP client.</param>
    /// <param name="logger">Injected logger.</param>
    public TvdbClient(HttpClient httpClient, ILogger<TvdbClient> logger)
    {
        _http = httpClient;
        _logger = logger;
        _http.BaseAddress = new Uri("https://api4.thetvdb.com/v4/");
        _http.Timeout = TimeSpan.FromSeconds(15);
    }

    private static string? ApiKey =>
        PremioPlugin.Instance?.Configuration?.TvdbApiKey?.Trim();

    /// <summary>
    /// Ensures a valid bearer token is available for TheTVDB API requests.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A valid bearer token or null if unauthenticated / not configured.</returns>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "TVDB login failures are non-critical.")]
    public async Task<string?> GetAuthTokenAsync(CancellationToken cancellationToken = default)
    {
        var key = ApiKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(_cachedToken) && DateTimeOffset.UtcNow < _tokenExpiry)
        {
            return _cachedToken;
        }

        await _authLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!string.IsNullOrWhiteSpace(_cachedToken) && DateTimeOffset.UtcNow < _tokenExpiry)
            {
                return _cachedToken;
            }

            LogLoggingInToTvdb(_logger);
            var req = new TvdbLoginRequest { ApiKey = key };
            var res = await _http.PostAsJsonAsync("login", req, cancellationToken).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                LogTvdbLoginFailed(_logger, res.StatusCode.ToString());
                return null;
            }

            var authRes = await res.Content.ReadFromJsonAsync<TvdbLoginResponse>(cancellationToken: cancellationToken).ConfigureAwait(false);
            var token = authRes?.Data?.Token;
            if (!string.IsNullOrWhiteSpace(token))
            {
                _cachedToken = token;
                // Tokens are typically valid for 30 days; refresh comfortably after 20 days.
                _tokenExpiry = DateTimeOffset.UtcNow.AddDays(20);
                LogTvdbLoginSuccess(_logger);
                return _cachedToken;
            }

            return null;
        }
        catch (Exception ex)
        {
            LogTvdbLoginException(_logger, ex.Message);
            return null;
        }
        finally
        {
            _authLock.Release();
        }
    }

    /// <summary>
    /// Discovers all seasons and episode counts for a TV show by IMDb ID or title from TheTVDB v4.
    /// </summary>
    /// <param name="imdbId">Optional IMDb ID (e.g. "tt9288030").</param>
    /// <param name="title">Optional series title fallback.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A list of discovered season summaries or empty list on error.</returns>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "TVDB lookups are fallback metadata.")]
    public async Task<IReadOnlyList<TmdbSeasonSummary>> GetSeriesSeasonsAsync(
        string? imdbId,
        string? title,
        CancellationToken cancellationToken = default)
    {
        var token = await GetAuthTokenAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
        {
            return [];
        }

        try
        {
            var tvdbSeriesId = 0;

            // 1. Try remote ID lookup using IMDb ID
            if (!string.IsNullOrWhiteSpace(imdbId) && imdbId.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"search/remoteid/{Uri.EscapeDataString(imdbId)}");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                using var res = await _http.SendAsync(req, cancellationToken).ConfigureAwait(false);
                if (res.IsSuccessStatusCode)
                {
                    var remoteRes = await res.Content.ReadFromJsonAsync<TvdbRemoteIdResponse>(cancellationToken: cancellationToken).ConfigureAwait(false);
                    var matched = remoteRes?.Data?.FirstOrDefault(d => d.Series is not null && d.Series.Id > 0);
                    if (matched?.Series is not null)
                    {
                        tvdbSeriesId = matched.Series.Id;
                    }
                }
            }

            // 2. Fallback: Search by title
            if (tvdbSeriesId <= 0 && !string.IsNullOrWhiteSpace(title))
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"search?query={Uri.EscapeDataString(title)}&type=series");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                using var res = await _http.SendAsync(req, cancellationToken).ConfigureAwait(false);
                if (res.IsSuccessStatusCode)
                {
                    var searchRes = await res.Content.ReadFromJsonAsync<TvdbSearchResponse>(cancellationToken: cancellationToken).ConfigureAwait(false);
                    var firstResult = searchRes?.Data?.FirstOrDefault();
                    if (firstResult is not null && int.TryParse(firstResult.TvdbId, out var parsedId) && parsedId > 0)
                    {
                        tvdbSeriesId = parsedId;
                    }
                }
            }

            if (tvdbSeriesId <= 0)
            {
                return [];
            }

            // 3. Fetch extended series details containing all episodes
            using var extReq = new HttpRequestMessage(HttpMethod.Get, $"series/{tvdbSeriesId}/extended");
            extReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var extRes = await _http.SendAsync(extReq, cancellationToken).ConfigureAwait(false);
            if (!extRes.IsSuccessStatusCode)
            {
                return [];
            }

            var extData = await extRes.Content.ReadFromJsonAsync<TvdbSeriesExtendedResponse>(cancellationToken: cancellationToken).ConfigureAwait(false);
            var episodes = extData?.Data?.Episodes;
            if (episodes is null || episodes.Count == 0)
            {
                return [];
            }

            var seasons = new List<TmdbSeasonSummary>();
            var seasonGroups = episodes
                .Where(e => e.SeasonNumber >= 1)
                .GroupBy(e => e.SeasonNumber)
                .OrderBy(g => g.Key);

            foreach (var group in seasonGroups)
            {
                var sNum = group.Key;
                var maxEp = group.Max(e => e.Number);
                var count = Math.Max(group.Count(), maxEp);

                seasons.Add(new TmdbSeasonSummary
                {
                    SeasonNumber = sNum,
                    EpisodeCount = count,
                    Name = $"Season {sNum:D2}"
                });
            }

            LogTvdbSeriesFound(_logger, tvdbSeriesId, seasons.Count);
            return seasons;
        }
        catch (Exception ex)
        {
            LogTvdbLookupFailed(_logger, imdbId ?? title ?? "unknown", ex.Message);
            return [];
        }
    }

    // -------------------------------------------------------------------------
    // LoggerMessage delegates (CA1848)
    // -------------------------------------------------------------------------

    [LoggerMessage(Level = LogLevel.Debug, Message = "Premio: Authenticating with TheTVDB v4 API")]
    private static partial void LogLoggingInToTvdb(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Premio: Successfully authenticated with TheTVDB v4")]
    private static partial void LogTvdbLoginSuccess(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Premio: TheTVDB authentication failed with status {StatusCode}")]
    private static partial void LogTvdbLoginFailed(ILogger logger, string statusCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Premio: Exception while authenticating with TheTVDB: {ErrorMessage}")]
    private static partial void LogTvdbLoginException(ILogger logger, string errorMessage);

    [LoggerMessage(Level = LogLevel.Information, Message = "Premio: Discovered {SeasonCount} seasons from TheTVDB series ID {SeriesId}")]
    private static partial void LogTvdbSeriesFound(ILogger logger, int seriesId, int seasonCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Premio: TheTVDB lookup failed for {Identifier}: {ErrorMessage}")]
    private static partial void LogTvdbLookupFailed(ILogger logger, string identifier, string errorMessage);
}
