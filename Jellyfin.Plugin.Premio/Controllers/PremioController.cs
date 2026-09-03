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
    private readonly TorrentioClient _torrentioClient;
    private readonly PremiumizeClient _premiumizeClient;
    private readonly StrmFileService _strmService;
    private readonly ILogger<PremioController> _logger;

    /// <summary>
    /// Initialises a new instance of <see cref="PremioController"/>.
    /// </summary>
    /// <param name="tmdbClient">TMDB HTTP client.</param>
    /// <param name="torrentioClient">Torrentio HTTP client.</param>
    /// <param name="premiumizeClient">Premiumize HTTP client.</param>
    /// <param name="strmService">STRM file service.</param>
    /// <param name="logger">Logger.</param>
    public PremioController(
        TmdbClient tmdbClient,
        TorrentioClient torrentioClient,
        PremiumizeClient premiumizeClient,
        StrmFileService strmService,
        ILogger<PremioController> logger)
    {
        _tmdbClient = tmdbClient;
        _torrentioClient = torrentioClient;
        _premiumizeClient = premiumizeClient;
        _strmService = strmService;
        _logger = logger;
    }

    /// <summary>
    /// Directly streams or 302-redirects to the resolved Premiumize CDN video stream.
    /// Accessible anonymously so HTML5 video players without Jellyfin auth headers can stream seamlessly.
    /// </summary>
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

        if (!isRealInfoHash && cachedItem is not null)
        {
            var isTv = string.Equals(cachedItem.MediaType, "tv", StringComparison.OrdinalIgnoreCase);
            var imdbId = cachedItem.Id > 0
                ? await _tmdbClient.GetExternalImdbIdAsync(cachedItem.MediaType ?? (isTv ? "tv" : "movie"), cachedItem.Id, cancellationToken).ConfigureAwait(false)
                : null;

            if (!string.IsNullOrWhiteSpace(imdbId))
            {
                var streams = isTv
                    ? await _torrentioClient.GetSeriesStreamsAsync(imdbId, 1, 1, cachedItem?.DisplayTitle, cachedItem?.Year, cancellationToken).ConfigureAwait(false)
                    : await _torrentioClient.GetMovieStreamsAsync(imdbId, cachedItem?.DisplayTitle, cachedItem?.Year, cancellationToken).ConfigureAwait(false);

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

            var title = cachedItem?.DisplayTitle ?? "Unknown Media";
            var year = cachedItem?.Year;
            var isTvShow = string.Equals(cachedItem?.MediaType, "tv", StringComparison.OrdinalIgnoreCase);

            // 2. Write .strm file & poster
            var strmPath = await _strmService.WriteMediaStrmFileAsync(
                title,
                year,
                new Uri(streamUrl),
                isTvShow,
                1,
                1,
                cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(strmPath) && cachedItem?.PosterUrl is not null)
            {
                var posterBytes = await _tmdbClient.DownloadImageBytesAsync(cachedItem.PosterUrl, cancellationToken).ConfigureAwait(false);
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
