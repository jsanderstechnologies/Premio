using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net.Mime;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Premio.Models;
using Jellyfin.Plugin.Premio.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Premio.Controllers;

/// <summary>
/// REST API controller providing endpoints for TMDB metadata discovery, Torrentio stream lookup,
/// Premiumize debrid stream resolution, and .strm library addition.
/// </summary>
[ApiController]
[Route("Premio")]
[Produces(MediaTypeNames.Application.Json)]
public sealed partial class PremioController : ControllerBase
{
    private readonly TmdbClient _tmdbClient;
    private readonly TvdbClient _tvdbClient;
    private readonly ImdbClient _imdbClient;
    private readonly TorrentioClient _torrentioClient;
    private readonly PremiumizeClient _premiumizeClient;
    private readonly StrmFileService _strmService;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<PremioController> _logger;

    /// <summary>
    /// Initialises a new instance of <see cref="PremioController"/>.
    /// </summary>
    /// <param name="tmdbClient">TMDB HTTP client.</param>
    /// <param name="tvdbClient">TheTVDB HTTP client.</param>
    /// <param name="imdbClient">IMDb / Cinemeta HTTP client.</param>
    /// <param name="torrentioClient">Torrentio HTTP client.</param>
    /// <param name="premiumizeClient">Premiumize HTTP client.</param>
    /// <param name="strmService">STRM file service.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="logger">Logger.</param>
    public PremioController(
        TmdbClient tmdbClient,
        TvdbClient tvdbClient,
        ImdbClient imdbClient,
        TorrentioClient torrentioClient,
        PremiumizeClient premiumizeClient,
        StrmFileService strmService,
        ILibraryManager libraryManager,
        ILogger<PremioController> logger)
    {
        _tmdbClient = tmdbClient;
        _tvdbClient = tvdbClient;
        _imdbClient = imdbClient;
        _torrentioClient = torrentioClient;
        _premiumizeClient = premiumizeClient;
        _strmService = strmService;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <summary>
    /// Directly streams or 302-redirects to the resolved Premiumize CDN video stream.
    /// Accessible anonymously so HTML5 video players without Jellyfin auth headers can stream seamlessly.
    /// </summary>
    /// <param name="itemId">Optional route item ID.</param>
    /// <param name="mediaSourceId">Optional selected media source ID.</param>
    /// <param name="infoHash">Optional torrent infohash.</param>
    /// <param name="type">Optional media type ("movie" or "tv").</param>
    /// <param name="imdbId">Optional IMDb ID for direct stream resolution.</param>
    /// <param name="season">Optional TV season number.</param>
    /// <param name="episode">Optional TV episode number.</param>
    /// <param name="title">Optional media title.</param>
    /// <param name="year">Optional release year.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A redirect action to the playable stream or error status.</returns>
    [HttpGet("Stream/{itemId}")]
    [HttpGet("Stream")]
    [HttpHead("Stream/{itemId}")]
    [HttpHead("Stream")]
    [AllowAnonymous]
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Stream resolution errors return appropriate HTTP status.")]
    public async Task<IActionResult> Stream(
        [FromRoute] Guid? itemId,
        [FromQuery] string? mediaSourceId,
        [FromQuery] string? infoHash,
        [FromQuery] string? type,
        [FromQuery] string? imdbId,
        [FromQuery] int? season,
        [FromQuery] int? episode,
        [FromQuery] string? title,
        [FromQuery] string? year,
        CancellationToken cancellationToken)
    {
        var targetHash = !string.IsNullOrWhiteSpace(infoHash)
            ? infoHash
            : (!string.IsNullOrWhiteSpace(mediaSourceId) && mediaSourceId != "select_stream" ? mediaSourceId : null);

        var requestedGuid = itemId ?? Guid.Empty;
        if (!string.IsNullOrWhiteSpace(mediaSourceId) && Guid.TryParse(mediaSourceId, out var mediaGuid) && PremioMetadataCache.TryGetStreamHash(mediaGuid, out var mappedHash))
        {
            targetHash = mappedHash;
        }
        else if (requestedGuid != Guid.Empty && PremioMetadataCache.TryGetStreamHash(requestedGuid, out var directMappedHash))
        {
            targetHash = directMappedHash;
        }

        TmdbItem? cachedItem = null;

        if (requestedGuid != Guid.Empty)
        {
            PremioMetadataCache.TryGetItem(requestedGuid, out cachedItem);
        }

        var isRealInfoHash = !string.IsNullOrWhiteSpace(targetHash) && targetHash.Length == 40 && !string.Equals(targetHash, requestedGuid.ToString("N"), StringComparison.OrdinalIgnoreCase);

        if (!isRealInfoHash)
        {
            var isTv = string.Equals(type, "tv", StringComparison.OrdinalIgnoreCase) ||
                       season.HasValue ||
                       string.Equals(cachedItem?.MediaType, "tv", StringComparison.OrdinalIgnoreCase);

            var resolvedImdbId = imdbId;
            if (string.IsNullOrWhiteSpace(resolvedImdbId) && cachedItem is not null && cachedItem.Id > 0)
            {
                resolvedImdbId = await _tmdbClient.GetExternalImdbIdAsync(cachedItem.MediaType ?? (isTv ? "tv" : "movie"), cachedItem.Id, cancellationToken).ConfigureAwait(false);
            }

            if (!string.IsNullOrWhiteSpace(resolvedImdbId))
            {
                var queryTitle = !string.IsNullOrWhiteSpace(title) ? title : cachedItem?.DisplayTitle;
                var queryYear = !string.IsNullOrWhiteSpace(year) ? year : cachedItem?.Year;
                var sNum = season ?? 1;
                var epNum = episode ?? 1;

                var streams = isTv
                    ? await _torrentioClient.GetSeriesStreamsAsync(resolvedImdbId, sNum, epNum, queryTitle, queryYear, cancellationToken).ConfigureAwait(false)
                    : await _torrentioClient.GetMovieStreamsAsync(resolvedImdbId, queryTitle, queryYear, cancellationToken).ConfigureAwait(false);

                if (streams.Count > 0)
                {
                    targetHash = streams[0].InfoHash;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(targetHash) || targetHash.Length != 40)
        {
            return NotFound(new { message = "Stream or infoHash not found." });
        }

        try
        {
            // 1. Send magnet to Premiumize
            await _premiumizeClient.CreateTransferAsync(targetHash, cancellationToken).ConfigureAwait(false);
            var directDl = await _premiumizeClient.CreateDirectDownloadAsync(targetHash, cancellationToken).ConfigureAwait(false);
            var streamUrl = ResolvePlayableStreamUrl(directDl);

            if (string.IsNullOrWhiteSpace(streamUrl))
            {
                return StatusCode(StatusCodes.Status502BadGateway, new { message = "Could not resolve stream URL from Premiumize." });
            }

            var isTvShow = string.Equals(type, "tv", StringComparison.OrdinalIgnoreCase) ||
                           season.HasValue ||
                           string.Equals(cachedItem?.MediaType, "tv", StringComparison.OrdinalIgnoreCase);
            var resolvedTitle = !string.IsNullOrWhiteSpace(title) ? title : (cachedItem?.DisplayTitle ?? "Unknown Media");
            var resolvedYear = !string.IsNullOrWhiteSpace(year) ? year : cachedItem?.Year;
            var targetSeason = season ?? 1;
            var targetEpisode = episode ?? 1;

            // 2. Write .strm file & poster
            var strmPath = await _strmService.WriteMediaStrmFileAsync(
                resolvedTitle,
                resolvedYear,
                new Uri(streamUrl),
                isTvShow,
                targetSeason,
                targetEpisode,
                cancellationToken).ConfigureAwait(false);

            var posterUrl = cachedItem?.PosterUrl;
            if (!string.IsNullOrWhiteSpace(strmPath) && posterUrl is not null)
            {
                var posterBytes = await _tmdbClient.DownloadImageBytesAsync(posterUrl, cancellationToken).ConfigureAwait(false);
                if (posterBytes is not null && posterBytes.Length > 0)
                {
                    await _strmService.SavePosterImageAsync(strmPath, posterBytes, cancellationToken).ConfigureAwait(false);
                }
            }

            return Redirect(streamUrl);
        }
        catch (Exception ex)
        {
            LogStreamResolutionFailed(_logger, requestedGuid, ex.Message);
            return StatusCode(StatusCodes.Status500InternalServerError, new { message = ex.Message });
        }
    }

    /// <summary>
    /// Searches TMDB for movies and TV shows matching the query string.
    /// </summary>
    /// <param name="q">Search query string.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A list of matching TMDB media items.</returns>
    [HttpGet("Search")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<TmdbItem>>> Search(
        [FromQuery] string q,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(q))
        {
            return Ok(Array.Empty<TmdbItem>());
        }

        var results = await _tmdbClient.SearchMultiAsync(q, cancellationToken).ConfigureAwait(false);
        return Ok(results);
    }

    /// <summary>
    /// Retrieves full details for a TMDB movie or TV show.
    /// </summary>
    /// <param name="type">Media type: "movie" or "tv".</param>
    /// <param name="id">TMDB item ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Detailed TMDB metadata including IMDB ID.</returns>
    [HttpGet("Details")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TmdbDetailedItem>> GetDetails(
        [FromQuery] string type,
        [FromQuery] int id,
        CancellationToken cancellationToken)
    {
        var details = await _tmdbClient.GetDetailsAsync(type, id, cancellationToken).ConfigureAwait(false);
        if (details is null)
        {
            return NotFound(new { message = "Item not found on TMDB." });
        }

        return Ok(details);
    }

    /// <summary>
    /// Fetches Torrentio streams for a given IMDB ID and checks Premiumize cloud cache status.
    /// </summary>
    /// <param name="type">"movie" or "tv".</param>
    /// <param name="imdbId">IMDB ID (e.g. "tt0093058").</param>
    /// <param name="title">Optional item title to match in release names.</param>
    /// <param name="year">Optional release year to match in release names.</param>
    /// <param name="season">Season number for TV shows.</param>
    /// <param name="episode">Episode number for TV shows.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A list of available torrent streams with cache status.</returns>
    [HttpGet("Streams")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<TorrentioStreamResult>>> GetStreams(
        [FromQuery] string type,
        [FromQuery] string imdbId,
        [FromQuery] string? title = null,
        [FromQuery] string? year = null,
        [FromQuery] int season = 1,
        [FromQuery] int episode = 1,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(imdbId))
        {
            return BadRequest(new { message = "imdbId is required." });
        }

        var isTv = string.Equals(type, "tv", StringComparison.OrdinalIgnoreCase);
        var streams = isTv
            ? await _torrentioClient.GetSeriesStreamsAsync(imdbId, season, episode, title, year, cancellationToken).ConfigureAwait(false)
            : await _torrentioClient.GetMovieStreamsAsync(imdbId, title, year, cancellationToken).ConfigureAwait(false);

        if (streams.Count == 0)
        {
            return Ok(Array.Empty<TorrentioStreamResult>());
        }

        // Check Premiumize cache status for all streams
        var hashes = streams.Where(s => !string.IsNullOrWhiteSpace(s.InfoHash))
                            .Select(s => s.InfoHash)
                            .Distinct()
                            .ToList();

        if (hashes.Count > 0)
        {
            var cacheResults = await _premiumizeClient.CheckCacheAsync(hashes, cancellationToken).ConfigureAwait(false);
            if (cacheResults.Count == hashes.Count)
            {
                var cacheMap = hashes.Zip(cacheResults, (hash, cached) => new { hash, cached })
                                     .ToDictionary(x => x.hash, x => x.cached, StringComparer.OrdinalIgnoreCase);

                foreach (var stream in streams)
                {
                    if (cacheMap.TryGetValue(stream.InfoHash, out var isCached))
                    {
                        stream.IsCached = isCached;
                    }
                }
            }
        }

        // Order cached streams first, then by seeders descending
        var sorted = streams.OrderByDescending(s => s.IsCached)
                            .ThenByDescending(s => s.Seeders)
                            .ToList();

        return Ok(sorted);
    }

    /// <summary>
    /// Resolves the direct download URL for a selected stream from Premiumize,
    /// writes the .strm file, and saves the TMDB poster.
    /// </summary>
    /// <param name="request">Stream creation parameters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result status and file paths.</returns>
    [HttpPost("AddStream")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "API controller returns HTTP 500 on unexpected faults.")]
    public async Task<IActionResult> AddStream(
        [FromBody] AddStreamRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.InfoHash) && request.MagnetUrl is null)
        {
            return BadRequest(new { message = "infoHash or magnetUrl is required." });
        }

        if (string.IsNullOrWhiteSpace(request.Title))
        {
            return BadRequest(new { message = "title is required." });
        }

        try
        {
            var src = !string.IsNullOrWhiteSpace(request.InfoHash) ? request.InfoHash : request.MagnetUrl!.OriginalString;
            _ = _premiumizeClient.CreateTransferAsync(src, cancellationToken);
            var directDl = await _premiumizeClient.CreateDirectDownloadAsync(src, cancellationToken).ConfigureAwait(false);

            var streamUrl = ResolvePlayableStreamUrl(directDl);

            if (string.IsNullOrWhiteSpace(streamUrl))
            {
                return BadRequest(new { message = "Could not resolve stream URL from Premiumize DirectDL." });
            }

            // Construct clean media filename
            var formattedTitle = request.IsTv && request.Season.HasValue && request.Episode.HasValue
                ? $"{request.Title} - S{request.Season.Value:D2}E{request.Episode.Value:D2}"
                : (!string.IsNullOrWhiteSpace(request.Year) ? $"{request.Title} ({request.Year})" : request.Title);

            var strmPath = await _strmService.WriteMediaStrmFileAsync(
                formattedTitle,
                new Uri(streamUrl),
                request.IsTv,
                cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(strmPath))
            {
                return BadRequest(new { message = "Output directory is not configured in Premio settings." });
            }

            // Download and save poster image
            if (request.PosterUrl is not null)
            {
                var posterBytes = await _tmdbClient.DownloadImageBytesAsync(request.PosterUrl, cancellationToken).ConfigureAwait(false);
                if (posterBytes is not null && posterBytes.Length > 0)
                {
                    await _strmService.SavePosterImageAsync(strmPath, posterBytes, cancellationToken).ConfigureAwait(false);
                }
            }

            LogAddedStream(_logger, formattedTitle, strmPath);
            return Ok(new
            {
                success = true,
                path = strmPath,
                streamUrl = streamUrl
            });
        }
        catch (Exception ex)
        {
            LogAddStreamFailed(_logger, request.Title, ex.Message);
            return StatusCode(StatusCodes.Status500InternalServerError, new { message = ex.Message });
        }
    }

    /// <summary>
    /// Adds an entire TV show into the Jellyfin library by fetching its seasons/episodes from TMDB
    /// and generating the complete directory structure and .strm files.
    /// </summary>
    /// <param name="request">TV show addition parameters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result status and directory path.</returns>
    [HttpPost("Library/AddTvShow")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "API controller returns HTTP 500 on unexpected faults.")]
    public async Task<IActionResult> AddTvShow(
        [FromBody] AddTvShowRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Title) && request.TmdbId <= 0)
        {
            return BadRequest(new { message = "title or tmdbId is required." });
        }

        try
        {
            var tvDetails = request.TmdbId > 0
                ? await _tmdbClient.GetDetailsAsync("tv", request.TmdbId, cancellationToken).ConfigureAwait(false)
                : null;

            var title = !string.IsNullOrWhiteSpace(request.Title)
                ? request.Title
                : (tvDetails?.DisplayTitle ?? "Unknown Show");

            var year = !string.IsNullOrWhiteSpace(request.Year)
                ? request.Year
                : tvDetails?.Year;

            var imdbId = request.ImdbId ?? tvDetails?.ImdbId;
            if (string.IsNullOrWhiteSpace(imdbId) && request.TmdbId > 0)
            {
                imdbId = await _tmdbClient.GetExternalImdbIdAsync("tv", request.TmdbId, cancellationToken).ConfigureAwait(false);
            }

            if (string.IsNullOrWhiteSpace(imdbId))
            {
                imdbId = $"premio_tv_{request.TmdbId}";
            }

            byte[]? posterBytes = null;
            if (tvDetails?.PosterUrl is not null)
            {
                posterBytes = await _tmdbClient.DownloadImageBytesAsync(tvDetails.PosterUrl, cancellationToken).ConfigureAwait(false);
            }

            byte[]? backdropBytes = null;
            if (tvDetails?.BackdropUrl is not null)
            {
                backdropBytes = await _tmdbClient.DownloadImageBytesAsync(tvDetails.BackdropUrl, cancellationToken).ConfigureAwait(false);
            }

            tvDetails ??= new TmdbDetailedItem
            {
                Id = request.TmdbId,
                Name = title,
                NumberOfEpisodes = 10,
                NumberOfSeasons = 1,
                Seasons = [new TmdbSeasonSummary { SeasonNumber = 1, EpisodeCount = 10 }]
            };

            var directoryPath = await _strmService.CreateTvShowSeriesStructureAsync(
                title,
                year,
                imdbId,
                tvDetails,
                posterBytes,
                backdropBytes,
                _tmdbClient,
                _tvdbClient,
                _imdbClient,
                _torrentioClient,
                cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(directoryPath))
            {
                return BadRequest(new { message = "TV Shows output directory is not configured in Premio settings." });
            }

            LogAddedStream(_logger, title, directoryPath);
            return Ok(new
            {
                success = true,
                title = title,
                year = year,
                imdbId = imdbId,
                directory = directoryPath
            });
        }
        catch (Exception ex)
        {
            LogAddStreamFailed(_logger, request.Title, ex.Message);
            return StatusCode(StatusCodes.Status500InternalServerError, new { message = ex.Message });
        }
    }

    /// <summary>
    /// Adds an entire TV show into the Jellyfin library and renders an HTML page that redirects
    /// the browser back to the newly created library series details page.
    /// </summary>
    /// <param name="tmdbId">TMDB item ID.</param>
    /// <param name="imdbId">Optional IMDB ID.</param>
    /// <param name="title">Show title.</param>
    /// <param name="year">Optional show release year.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>HTML page with automatic redirect.</returns>
    [HttpGet("Web/AddShowAndRedirect")]
    [Produces("text/html")]
    [AllowAnonymous]
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Redirect action returns readable error page if exception occurs.")]
    public async Task<IActionResult> AddShowAndRedirect(
        [FromQuery] int tmdbId,
        [FromQuery] string? imdbId,
        [FromQuery] string title,
        [FromQuery] string? year,
        CancellationToken cancellationToken)
    {
        try
        {
            var tvDetails = tmdbId > 0
                ? await _tmdbClient.GetDetailsAsync("tv", tmdbId, cancellationToken).ConfigureAwait(false)
                : null;

            var cleanTitle = !string.IsNullOrWhiteSpace(title)
                ? title
                : (tvDetails?.DisplayTitle ?? "Unknown Show");

            var cleanYear = !string.IsNullOrWhiteSpace(year)
                ? year
                : tvDetails?.Year;

            var resolvedImdbId = imdbId ?? tvDetails?.ImdbId;
            if (string.IsNullOrWhiteSpace(resolvedImdbId) && tmdbId > 0)
            {
                resolvedImdbId = await _tmdbClient.GetExternalImdbIdAsync("tv", tmdbId, cancellationToken).ConfigureAwait(false);
            }

            if (string.IsNullOrWhiteSpace(resolvedImdbId))
            {
                resolvedImdbId = $"premio_tv_{tmdbId}";
            }

            byte[]? posterBytes = null;
            if (tvDetails?.PosterUrl is not null)
            {
                posterBytes = await _tmdbClient.DownloadImageBytesAsync(tvDetails.PosterUrl, cancellationToken).ConfigureAwait(false);
            }

            byte[]? backdropBytes = null;
            if (tvDetails?.BackdropUrl is not null)
            {
                backdropBytes = await _tmdbClient.DownloadImageBytesAsync(tvDetails.BackdropUrl, cancellationToken).ConfigureAwait(false);
            }

            tvDetails ??= new TmdbDetailedItem
            {
                Id = tmdbId,
                Name = cleanTitle,
                NumberOfEpisodes = 10,
                NumberOfSeasons = 1,
                Seasons = [new TmdbSeasonSummary { SeasonNumber = 1, EpisodeCount = 10 }]
            };

            await _strmService.CreateTvShowSeriesStructureAsync(
                cleanTitle,
                cleanYear,
                resolvedImdbId,
                tvDetails,
                posterBytes,
                backdropBytes,
                _tmdbClient,
                _tvdbClient,
                _imdbClient,
                _torrentioClient,
                cancellationToken).ConfigureAwait(false);

            Guid foundSeriesId = Guid.Empty;
            for (var i = 0; i < 6; i++)
            {
                var query = new InternalItemsQuery
                {
                    SearchTerm = cleanTitle
                };
                var items = _libraryManager.GetItemList(query);
                for (var j = 0; j < items.Count; j++)
                {
                    if (items[j] is MediaBrowser.Controller.Entities.TV.Series)
                    {
                        foundSeriesId = items[j].Id;
                        break;
                    }
                }

                if (foundSeriesId != Guid.Empty)
                {
                    break;
                }

                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }

            var seriesIdStr = foundSeriesId != Guid.Empty ? foundSeriesId.ToString("N") : string.Empty;

            var html = $$"""
                <!DOCTYPE html>
                <html>
                <head>
                    <meta charset="utf-8">
                    <title>Added to Library</title>
                    <style>
                        body {
                            background-color: #101010;
                            color: #ffffff;
                            font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Helvetica, Arial, sans-serif;
                            display: flex;
                            align-items: center;
                            justify-content: center;
                            height: 100vh;
                            margin: 0;
                            text-align: center;
                        }
                        .card {
                            background: #1c1c1c;
                            padding: 40px 60px;
                            border-radius: 12px;
                            box-shadow: 0 8px 24px rgba(0,0,0,0.5);
                            max-width: 500px;
                        }
                        .spinner {
                            border: 4px solid rgba(255,255,255,0.1);
                            width: 48px;
                            height: 48px;
                            border-radius: 50%;
                            border-left-color: #00a4dc;
                            animation: spin 1s linear infinite;
                            margin: 0 auto 20px auto;
                        }
                        @keyframes spin { 0% { transform: rotate(0deg); } 100% { transform: rotate(360deg); } }
                    </style>
                </head>
                <body>
                    <div class="card">
                        <div class="spinner"></div>
                        <h2>Added to Library!</h2>
                        <p>Opening show in your Jellyfin Library...</p>
                    </div>
                    <script>
                        var seriesId = '{{seriesIdStr}}';
                        var targetHash = seriesId ? '#/details?id=' + seriesId : '#/home.html';
                        if (window.opener && !window.opener.closed) {
                            try {
                                window.opener.location.hash = targetHash;
                                window.close();
                            } catch (e) {
                                window.location.href = '/web/index.html' + targetHash;
                            }
                        } else {
                            window.location.href = '/web/index.html' + targetHash;
                        }
                    </script>
                </body>
                </html>
                """;

            return Content(html, "text/html");
        }
        catch (Exception ex)
        {
            LogAddStreamFailed(_logger, title, ex.Message);
            return StatusCode(StatusCodes.Status500InternalServerError, ex.Message);
        }
    }

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".avi", ".mov", ".wmv", ".flv", ".webm", ".ts", ".m4v", ".m2ts"
    };

    private static string? ResolvePlayableStreamUrl(PremiumizeDirectDlResponse directDl)
    {
        if (directDl.Content is not null && directDl.Content.Count > 0)
        {
            var videoFiles = directDl.Content
                .Where(f =>
                {
                    var ext = System.IO.Path.GetExtension(f.Path);
                    return VideoExtensions.Contains(ext);
                })
                .OrderByDescending(f => f.Size)
                .ToList();

            if (videoFiles.Count > 0)
            {
                var bestFile = videoFiles[0];
                return bestFile.StreamLink ?? bestFile.Link;
            }

            var largestFile = directDl.Content.OrderByDescending(f => f.Size).FirstOrDefault();
            if (largestFile is not null && largestFile.Size > 50 * 1024 * 1024)
            {
                return largestFile.StreamLink ?? largestFile.Link;
            }
        }

        return directDl.Location;
    }

    // -------------------------------------------------------------------------
    // LoggerMessage delegates (CA1848)
    // -------------------------------------------------------------------------

    [LoggerMessage(Level = LogLevel.Information, Message = "Premio: Added stream for '{Title}' -> '{Path}'")]
    private static partial void LogAddedStream(ILogger logger, string title, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Premio: Successfully resolved stream for '{Title}' (Magnet: {InfoHash}) via Premiumize: {StreamUrl}")]
    private static partial void LogStreamResolved(ILogger logger, string title, string infoHash, string streamUrl);

    [LoggerMessage(Level = LogLevel.Error, Message = "Premio: Stream resolution failed for '{Guid}': {ErrorMessage}")]
    private static partial void LogStreamResolutionFailed(ILogger logger, Guid guid, string errorMessage);

    [LoggerMessage(Level = LogLevel.Error, Message = "Premio: Failed to add stream for '{Title}': {ErrorMessage}")]
    private static partial void LogAddStreamFailed(ILogger logger, string title, string errorMessage);
}

/// <summary>
/// Parameters for adding a stream to the library.
/// </summary>
public sealed class AddStreamRequest
{
    /// <summary>Gets or sets the item title.</summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the release year.</summary>
    [JsonPropertyName("year")]
    public string? Year { get; set; }

    /// <summary>Gets or sets a value indicating whether this is a TV show episode.</summary>
    [JsonPropertyName("isTv")]
    public bool IsTv { get; set; }

    /// <summary>Gets or sets the season number (for TV shows).</summary>
    [JsonPropertyName("season")]
    public int? Season { get; set; }

    /// <summary>Gets or sets the episode number (for TV shows).</summary>
    [JsonPropertyName("episode")]
    public int? Episode { get; set; }

    /// <summary>Gets or sets the torrent infohash.</summary>
    [JsonPropertyName("infoHash")]
    public string? InfoHash { get; set; }

    /// <summary>Gets or sets the magnet URI.</summary>
    [JsonPropertyName("magnetUrl")]
    public Uri? MagnetUrl { get; set; }

    /// <summary>Gets or sets the poster URI.</summary>
    [JsonPropertyName("posterUrl")]
    public Uri? PosterUrl { get; set; }
}

/// <summary>
/// Parameters for adding an entire TV show to the library.
/// </summary>
public sealed class AddTvShowRequest
{
    /// <summary>Gets or sets the TMDB ID.</summary>
    [JsonPropertyName("tmdbId")]
    public int TmdbId { get; set; }

    /// <summary>Gets or sets the optional IMDB ID.</summary>
    [JsonPropertyName("imdbId")]
    public string? ImdbId { get; set; }

    /// <summary>Gets or sets the TV show title.</summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the release year.</summary>
    [JsonPropertyName("year")]
    public string? Year { get; set; }
}
