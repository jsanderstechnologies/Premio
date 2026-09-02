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
            ? await _torrentioClient.GetSeriesStreamsAsync(imdbId, season, episode, cancellationToken).ConfigureAwait(false)
            : await _torrentioClient.GetMovieStreamsAsync(imdbId, cancellationToken).ConfigureAwait(false);

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
            var directDl = await _premiumizeClient.CreateDirectDownloadAsync(src, cancellationToken).ConfigureAwait(false);

            var streamUrl = directDl.Location;
            if (string.IsNullOrWhiteSpace(streamUrl) && directDl.Content is not null && directDl.Content.Count > 0)
            {
                streamUrl = directDl.Content[0].StreamLink ?? directDl.Content[0].Link;
            }

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
                title = formattedTitle,
                strmPath = strmPath,
                streamUrl = streamUrl
            });
        }
        catch (Exception ex)
        {
            LogAddStreamFailed(_logger, request.Title, ex.Message);
            return StatusCode(StatusCodes.Status500InternalServerError, new { message = ex.Message });
        }
    }

    // -------------------------------------------------------------------------
    // LoggerMessage delegates (CA1848)
    // -------------------------------------------------------------------------

    [LoggerMessage(Level = LogLevel.Information, Message = "Premio: Added stream for '{Title}' -> '{Path}'")]
    private static partial void LogAddedStream(ILogger logger, string title, string path);

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
