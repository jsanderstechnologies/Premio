using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Mime;
using System.IO;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Premio.Models;
using Jellyfin.Plugin.Premio.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
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
    [SuppressMessage("Security", "CA3012:Do not use untrusted input to form regular expressions", Justification = "Static regex pattern used for hash extraction.")]
    [SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Justification = "File path is retrieved from trusted library manager BaseItem.")]
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

        if (!isRealInfoHash && requestedGuid != Guid.Empty)
        {
            var libItem = _libraryManager.GetItemById(requestedGuid);
            if (libItem is Episode ep)
            {
                var sNum = ep.AiredSeasonNumber ?? (ep.ParentIndexNumber ?? (season ?? 1));
                var epNum = ep.IndexNumber ?? (episode ?? 1);
                var showTitle = ep.SeriesName ?? ep.FindParent<Series>()?.Name ?? title;
                if (!string.IsNullOrWhiteSpace(showTitle) && PremioMetadataCache.TryGetChosenEpisodeStream(showTitle, sNum, epNum, out var chosenHash))
                {
                    targetHash = chosenHash;
                    isRealInfoHash = true;
                }
            }

            if (!isRealInfoHash && libItem is not null && !string.IsNullOrWhiteSpace(libItem.Path) && System.IO.File.Exists(libItem.Path))
            {
                try
                {
                    var fileContent = await System.IO.File.ReadAllTextAsync(libItem.Path, cancellationToken).ConfigureAwait(false);
                    var trimmed = fileContent?.Trim() ?? string.Empty;
                    if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!trimmed.Contains("/Premio/Stream", StringComparison.OrdinalIgnoreCase))
                        {
                            return Redirect(trimmed);
                        }

                        var hashMatch = Regex.Match(trimmed, @"infoHash=([a-fA-F0-9]{40})", RegexOptions.IgnoreCase);
                        if (hashMatch.Success)
                        {
                            targetHash = hashMatch.Groups[1].Value;
                            isRealInfoHash = true;
                        }
                    }
                }
                catch
                {
                    // Ignore file read exceptions
                }
            }
        }

        if (!isRealInfoHash && season.HasValue && episode.HasValue && !string.IsNullOrWhiteSpace(title))
        {
            if (PremioMetadataCache.TryGetChosenEpisodeStream(title, season.Value, episode.Value, out var chosenHash))
            {
                targetHash = chosenHash;
                isRealInfoHash = true;
            }
        }

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

            if (string.IsNullOrWhiteSpace(resolvedImdbId) && requestedGuid != Guid.Empty)
            {
                var epLibItem = _libraryManager.GetItemById(requestedGuid);
                if (epLibItem is Episode epLookup)
                {
                    resolvedImdbId = epLookup.GetProviderId("Imdb");
                    if (string.IsNullOrWhiteSpace(resolvedImdbId))
                    {
                        var sLookup = epLookup.Series ?? (epLookup.SeriesId != Guid.Empty ? _libraryManager.GetItemById(epLookup.SeriesId) as Series : epLookup.FindParent<Series>());
                        if (sLookup is not null)
                        {
                            resolvedImdbId = sLookup.GetProviderId("Imdb");
                            if (string.IsNullOrWhiteSpace(resolvedImdbId) && int.TryParse(sLookup.GetProviderId("Tmdb"), out var sTmdbId))
                            {
                                resolvedImdbId = await _tmdbClient.GetExternalImdbIdAsync("tv", sTmdbId, cancellationToken).ConfigureAwait(false);
                            }
                        }
                    }
                }
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
            var isTvShow = string.Equals(type, "tv", StringComparison.OrdinalIgnoreCase) ||
                           season.HasValue ||
                           string.Equals(cachedItem?.MediaType, "tv", StringComparison.OrdinalIgnoreCase);
            var resolvedTitle = !string.IsNullOrWhiteSpace(title) ? title : (cachedItem?.DisplayTitle ?? "Unknown Media");
            var resolvedYear = !string.IsNullOrWhiteSpace(year) ? year : cachedItem?.Year;
            var targetSeason = season ?? 1;
            var targetEpisode = episode ?? 1;

            var streamUrl = ResolvePlayableStreamUrl(directDl, isTvShow ? targetSeason : null, isTvShow ? targetEpisode : null);

            if (string.IsNullOrWhiteSpace(streamUrl))
            {
                return StatusCode(StatusCodes.Status502BadGateway, new { message = "Could not resolve stream URL from Premiumize." });
            }

            LogStreamResolved(_logger, resolvedTitle, targetHash, streamUrl);

            if (isTvShow && targetSeason > 0 && targetEpisode > 0 && !string.IsNullOrWhiteSpace(targetHash))
            {
                PremioMetadataCache.RegisterChosenEpisodeStream(resolvedTitle, targetSeason, targetEpisode, targetHash);
            }

            // Write direct stream URL to the episode's actual file on disk if in library
            if (requestedGuid != Guid.Empty)
            {
                var libraryItem = _libraryManager.GetItemById(requestedGuid);
                if (libraryItem is not null && !string.IsNullOrWhiteSpace(libraryItem.Path))
                {
                    await System.IO.File.WriteAllTextAsync(libraryItem.Path, streamUrl, cancellationToken).ConfigureAwait(false);
                    LogAddedStream(_logger, isTvShow ? $"{resolvedTitle} - S{targetSeason:D2}E{targetEpisode:D2}" : resolvedTitle, libraryItem.Path);
                }
            }

            // 2. Write .strm file & poster
            var strmPath = await _strmService.WriteMediaStrmFileAsync(
                resolvedTitle,
                resolvedYear,
                new Uri(streamUrl),
                isTvShow,
                targetSeason,
                targetEpisode,
                forceOverwrite: true,
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
    [AllowAnonymous]
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "API controller returns HTTP 500 on unexpected faults.")]
    [SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Justification = "File path is retrieved from trusted library manager BaseItem.")]
    public async Task<IActionResult> AddStream(
        [FromBody] AddStreamRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 1. If ItemId is supplied, auto-populate missing fields from Jellyfin LibraryManager
        BaseItem? libItem = null;
        if (!string.IsNullOrWhiteSpace(request.ItemId) && Guid.TryParse(request.ItemId, out var parsedId))
        {
            libItem = _libraryManager.GetItemById(parsedId);
            if (libItem is Episode ep)
            {
                request.IsTv = true;
                if (!request.Season.HasValue || request.Season.Value <= 0)
                {
                    request.Season = ep.AiredSeasonNumber ?? ep.ParentIndexNumber ?? 1;
                }

                if (!request.Episode.HasValue || request.Episode.Value <= 0)
                {
                    request.Episode = ep.IndexNumber ?? 1;
                }

                var series = ep.Series ?? (ep.SeriesId != Guid.Empty ? _libraryManager.GetItemById(ep.SeriesId) as Series : ep.FindParent<Series>());
                var resolvedShowTitle = ep.SeriesName ?? series?.Name;
                if (!string.IsNullOrWhiteSpace(resolvedShowTitle))
                {
                    request.Title = resolvedShowTitle;
                }
                else if (string.IsNullOrWhiteSpace(request.Title))
                {
                    request.Title = ep.Name;
                }

                request.Year ??= series?.ProductionYear?.ToString(CultureInfo.InvariantCulture) ?? ep.ProductionYear?.ToString(CultureInfo.InvariantCulture);
            }
            else if (libItem is Series ser)
            {
                request.IsTv = true;
                request.Title = ser.Name;
                request.Year ??= ser.ProductionYear?.ToString(CultureInfo.InvariantCulture);
            }
            else if (libItem is not null && string.IsNullOrWhiteSpace(request.Title))
            {
                request.Title = libItem.Name;
                request.Year ??= libItem.ProductionYear?.ToString(CultureInfo.InvariantCulture);
            }
        }

        // 2. If infoHash is a deterministic GUID (MediaSourceId from native dropdown), resolve to torrent hash
        if (!string.IsNullOrWhiteSpace(request.InfoHash) && Guid.TryParse(request.InfoHash, out var mediaGuid))
        {
            if (PremioMetadataCache.TryGetStreamHash(mediaGuid, out var mappedTorrentHash))
            {
                request.InfoHash = mappedTorrentHash;
            }
        }

        // 3. If infoHash is still empty, auto-resolve best stream for episode from Torrentio
        if (string.IsNullOrWhiteSpace(request.InfoHash) && libItem is Episode epFallback)
        {
            var epSeries = epFallback.Series ?? (epFallback.SeriesId != Guid.Empty ? _libraryManager.GetItemById(epFallback.SeriesId) as Series : epFallback.FindParent<Series>());
            var imdbId = epFallback.GetProviderId("Imdb") ?? epSeries?.GetProviderId("Imdb");
            if (string.IsNullOrWhiteSpace(imdbId) && epSeries is not null && int.TryParse(epSeries.GetProviderId("Tmdb"), out var sTmdb))
            {
                imdbId = await _tmdbClient.GetExternalImdbIdAsync("tv", sTmdb, cancellationToken).ConfigureAwait(false);
            }

            if (!string.IsNullOrWhiteSpace(imdbId))
            {
                var sNum = request.Season ?? 1;
                var epNum = request.Episode ?? 1;
                var streams = await _torrentioClient.GetSeriesStreamsAsync(imdbId, sNum, epNum, request.Title, request.Year, cancellationToken).ConfigureAwait(false);
                if (streams.Count > 0)
                {
                    request.InfoHash = streams[0].InfoHash;
                }
            }
        }

        LogAddStreamReceived(
            _logger,
            request.Title ?? "unknown",
            request.Season ?? 0,
            request.Episode ?? 0,
            request.InfoHash ?? "none",
            request.ItemId ?? "none");

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
            await _premiumizeClient.CreateTransferAsync(src, cancellationToken).ConfigureAwait(false);

            var directDl = await _premiumizeClient.CreateDirectDownloadAsync(src, cancellationToken).ConfigureAwait(false);
            var directStreamUrl = ResolvePlayableStreamUrl(directDl, request.Season, request.Episode);

            var host = Request.Host.HasValue ? Request.Host.Value : "127.0.0.1:8096";
            var scheme = Request.Scheme ?? "http";
            var loopbackUrl = request.IsTv && request.Season.HasValue && request.Episode.HasValue
                ? $"{scheme}://{host}/Premio/Stream?type=tv&title={Uri.EscapeDataString(request.Title)}&year={Uri.EscapeDataString(request.Year ?? string.Empty)}&season={request.Season.Value}&episode={request.Episode.Value}&infoHash={Uri.EscapeDataString(request.InfoHash ?? string.Empty)}"
                : $"{scheme}://{host}/Premio/Stream?infoHash={Uri.EscapeDataString(request.InfoHash ?? src)}&title={Uri.EscapeDataString(request.Title)}";

            var streamUrl = directStreamUrl ?? loopbackUrl;

            if (request.IsTv && request.Season.HasValue && request.Episode.HasValue && !string.IsNullOrWhiteSpace(request.InfoHash))
            {
                PremioMetadataCache.RegisterChosenEpisodeStream(request.Title, request.Season.Value, request.Episode.Value, request.InfoHash);
            }

            if (!string.IsNullOrWhiteSpace(request.ItemId) && Guid.TryParse(request.ItemId, out var itemGuid) && !string.IsNullOrWhiteSpace(request.InfoHash))
            {
                PremioMetadataCache.RegisterStreamHash(itemGuid, request.InfoHash);
                PremioMetadataCache.RegisterChosenEpisodeStream(itemGuid, request.InfoHash);
            }

            // Construct clean media filename
            var formattedTitle = request.IsTv && request.Season.HasValue && request.Episode.HasValue
                ? $"{request.Title} - S{request.Season.Value:D2}E{request.Episode.Value:D2}"
                : (!string.IsNullOrWhiteSpace(request.Year) ? $"{request.Title} ({request.Year})" : request.Title);

            string? strmPath = null;
            var contentToWrite = !string.IsNullOrWhiteSpace(request.InfoHash)
                ? $"{streamUrl}{Environment.NewLine}# Premio: infoHash={request.InfoHash}"
                : streamUrl;

            // 1. If itemId is provided, write directly to Jellyfin's exact episode item path
            if (!string.IsNullOrWhiteSpace(request.ItemId) && Guid.TryParse(request.ItemId, out var targetItemGuid))
            {
                var libraryItem = _libraryManager.GetItemById(targetItemGuid);
                if (libraryItem is not null && !string.IsNullOrWhiteSpace(libraryItem.Path))
                {
                    await System.IO.File.WriteAllTextAsync(libraryItem.Path, contentToWrite, cancellationToken).ConfigureAwait(false);
                    strmPath = libraryItem.Path;
                    LogAddedStream(_logger, formattedTitle, strmPath);

                    if (!string.IsNullOrWhiteSpace(request.InfoHash))
                    {
                        try
                        {
                            await System.IO.File.WriteAllTextAsync(libraryItem.Path + ".premio", request.InfoHash, cancellationToken).ConfigureAwait(false);
                        }
                        catch
                        {
                            // Ignore sidecar write exceptions
                        }
                    }
                }
            }

            // 2. Also write/update via StrmFileService with forceOverwrite: true
            var writtenPath = request.IsTv && request.Season.HasValue && request.Episode.HasValue
                ? await _strmService.WriteMediaStrmFileAsync(
                    request.Title,
                    request.Year,
                    new Uri(streamUrl, UriKind.RelativeOrAbsolute),
                    isTvShow: true,
                    seasonNumber: request.Season.Value,
                    episodeNumber: request.Episode.Value,
                    forceOverwrite: true,
                    cancellationToken: cancellationToken).ConfigureAwait(false)
                : await _strmService.WriteMediaStrmFileAsync(
                    formattedTitle,
                    request.Year,
                    new Uri(streamUrl, UriKind.RelativeOrAbsolute),
                    isTvShow: request.IsTv,
                    seasonNumber: 1,
                    episodeNumber: 1,
                    forceOverwrite: true,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(writtenPath) && System.IO.File.Exists(writtenPath))
            {
                await System.IO.File.WriteAllTextAsync(writtenPath, contentToWrite, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(request.InfoHash))
                {
                    try
                    {
                        await System.IO.File.WriteAllTextAsync(writtenPath + ".premio", request.InfoHash, cancellationToken).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Ignore sidecar write exceptions
                    }
                }
            }

            strmPath ??= writtenPath;

            if (string.IsNullOrWhiteSpace(strmPath))
            {
                strmPath = "saved";
            }

            // Download and save poster image
            if (request.PosterUrl is not null && strmPath != "saved")
            {
                var posterBytes = await _tmdbClient.DownloadImageBytesAsync(request.PosterUrl, cancellationToken).ConfigureAwait(false);
                if (posterBytes is not null && posterBytes.Length > 0)
                {
                    await _strmService.SavePosterImageAsync(strmPath, posterBytes, cancellationToken).ConfigureAwait(false);
                }
            }

            LogAddedStream(_logger, formattedTitle, strmPath);
            _strmService.TriggerLibraryRefresh();
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

    /// <summary>
    /// Gets available streams for a specific episode in the library and whether a stream is currently chosen.
    /// </summary>
    /// <param name="itemId">Episode item GUID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>JSON stream information.</returns>
    [HttpGet("EpisodeStreams")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [AllowAnonymous]
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Action returns 500 on unexpected errors.")]
    public async Task<IActionResult> GetEpisodeStreams(
        [FromQuery] Guid itemId,
        CancellationToken cancellationToken)
    {
        if (itemId == Guid.Empty)
        {
            return BadRequest(new { message = "itemId is required." });
        }

        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return NotFound(new { message = "Episode item not found." });
        }

        if (item is not MediaBrowser.Controller.Entities.TV.Episode episode)
        {
            return BadRequest(new { message = "Item is not an Episode." });
        }

        var seasonNumber = episode.ParentIndexNumber ?? 1;
        var episodeNumber = episode.IndexNumber ?? 1;
        var showTitle = episode.SeriesName;
        string? showYear = null;
        string? imdbId = null;

        var series = episode.Series;
        if (series is not null)
        {
            showTitle ??= series.Name;
            showYear = series.ProductionYear?.ToString(CultureInfo.InvariantCulture);
            if (series.ProviderIds is not null)
            {
                series.ProviderIds.TryGetValue("Imdb", out imdbId);
                if (string.IsNullOrWhiteSpace(imdbId) && series.ProviderIds.TryGetValue("Tmdb", out var tmdbIdStr) && int.TryParse(tmdbIdStr, out var tmdbId))
                {
                    imdbId = await _tmdbClient.GetExternalImdbIdAsync("tv", tmdbId, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        if (string.IsNullOrWhiteSpace(showTitle))
        {
            showTitle = episode.Name;
        }

        if (string.IsNullOrWhiteSpace(imdbId))
        {
            var searchResults = await _tmdbClient.SearchMultiAsync(showTitle, cancellationToken).ConfigureAwait(false);
            TmdbItem? match = null;
            for (var k = 0; k < searchResults.Count; k++)
            {
                if (string.Equals(searchResults[k].MediaType, "tv", StringComparison.OrdinalIgnoreCase))
                {
                    match = searchResults[k];
                    break;
                }
            }

            match ??= searchResults.Count > 0 ? searchResults[0] : null;
            if (match is not null)
            {
                imdbId = await _tmdbClient.GetExternalImdbIdAsync("tv", match.Id, cancellationToken).ConfigureAwait(false);
                showYear ??= match.Year;
            }
        }

        if (string.IsNullOrWhiteSpace(imdbId))
        {
            return Ok(new
            {
                hasChosenStream = false,
                currentHash = string.Empty,
                title = showTitle,
                year = showYear,
                season = seasonNumber,
                episode = episodeNumber,
                streams = Array.Empty<object>()
            });
        }

        // Check if .strm file has a chosen stream
        var hasChosenStream = false;
        var currentHash = string.Empty;
        if (!string.IsNullOrWhiteSpace(episode.Path) && System.IO.File.Exists(episode.Path))
        {
            var strmContent = await System.IO.File.ReadAllTextAsync(episode.Path, cancellationToken).ConfigureAwait(false);
            var isConfigured = (strmContent.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || strmContent.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) && !strmContent.Contains("/Premio/Stream?type=tv", StringComparison.OrdinalIgnoreCase)
                || strmContent.Contains("infoHash=", StringComparison.OrdinalIgnoreCase);

            if (isConfigured)
            {
                hasChosenStream = true;
                var matchHash = System.Text.RegularExpressions.Regex.Match(strmContent, "[a-fA-F0-9]{40}");
                if (matchHash.Success)
                {
                    currentHash = matchHash.Value;
                }
            }
        }

        // Fetch Torrentio streams
        var streams = await _torrentioClient.GetSeriesStreamsAsync(imdbId, seasonNumber, episodeNumber, showTitle, showYear, cancellationToken).ConfigureAwait(false);

        // Check cache on hashes
        var hashes = new List<string>();
        for (var i = 0; i < streams.Count; i++)
        {
            var h = streams[i].InfoHash;
            if (!string.IsNullOrWhiteSpace(h) && !hashes.Contains(h, StringComparer.OrdinalIgnoreCase))
            {
                hashes.Add(h);
            }
        }

        if (hashes.Count > 0)
        {
            var cacheResults = await _premiumizeClient.CheckCacheAsync(hashes, cancellationToken).ConfigureAwait(false);
            if (cacheResults.Count == hashes.Count)
            {
                var cacheMap = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < hashes.Count; i++)
                {
                    cacheMap[hashes[i]] = cacheResults[i];
                }

                for (var i = 0; i < streams.Count; i++)
                {
                    if (cacheMap.TryGetValue(streams[i].InfoHash, out var isCached))
                    {
                        streams[i].IsCached = isCached;
                    }
                }
            }
        }

        var sorted = streams
            .OrderByDescending(s => s.IsCached)
            .ThenByDescending(s => s.Seeders)
            .Select(s => new
            {
                infoHash = s.InfoHash,
                cleanReleaseName = s.CleanReleaseName,
                fileSize = s.FileSize,
                seeders = s.Seeders,
                isCached = s.IsCached
            })
            .ToList();

        return Ok(new
        {
            hasChosenStream,
            currentHash,
            title = showTitle,
            year = showYear,
            season = seasonNumber,
            episode = episodeNumber,
            streams = sorted
        });
    }

    /// <summary>
    /// Serves client-side JavaScript for injecting stream dropdowns directly onto episode cards in Jellyfin Web.
    /// </summary>
    /// <returns>JavaScript response.</returns>
    [HttpGet("Web/premio.js")]
    [Produces("application/javascript")]
    [AllowAnonymous]
    public IActionResult GetPremioClientScript()
    {
        Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
        Response.Headers["Pragma"] = "no-cache";
        Response.Headers["Expires"] = "0";

        const string js = """
            (function() {
                console.log('[Premio] Client script loaded.');

                // Universal change listener: captures stream/version selection anywhere in Jellyfin Web
                document.addEventListener('change', async function(e) {
                    const el = e.target;
                    if (!el || el.tagName !== 'SELECT') return;

                    const isStreamSelect = el.hasAttribute('data-title') ||
                                           el.hasAttribute('data-season') ||
                                           el.closest('.selectSourceContainer') ||
                                           el.closest('.premio-card-stream-wrap') ||
                                           el.id === 'selectSource' ||
                                           (el.className && typeof el.className === 'string' && el.className.includes('selectSource'));

                    if (!isStreamSelect) return;

                    const val = el.value;
                    if (!val) return;

                    console.log('[Premio] Captured stream selection:', val);

                    const container = el.closest('.selectContainer') || el.closest('.premio-card-stream-wrap') || el.parentElement;
                    let statusEl = container ? container.querySelector('.stream-save-status') : null;
                    if (!statusEl && container) {
                        statusEl = document.createElement('span');
                        statusEl.className = 'stream-save-status';
                        statusEl.style.cssText = 'font-size: 12px; font-weight: 600; margin-left: 8px;';
                        container.appendChild(statusEl);
                    }

                    if (statusEl) {
                        statusEl.textContent = 'Sending to Premiumize...';
                        statusEl.style.color = '#f59e0b';
                    }

                    let itemId = el.getAttribute('data-itemid') || '';
                    if (!itemId) {
                        const itemEl = el.closest('[data-id], [data-itemid]');
                        if (itemEl) itemId = itemEl.getAttribute('data-id') || itemEl.getAttribute('data-itemid') || '';
                    }
                    if (!itemId) {
                        const hash = window.location.hash || '';
                        if (hash.includes('?')) {
                            const hashParams = new URLSearchParams(hash.split('?')[1]);
                            itemId = hashParams.get('id') || '';
                        }
                    }
                    if (!itemId) {
                        const search = window.location.search || '';
                        if (search.includes('?')) {
                            const searchParams = new URLSearchParams(search);
                            itemId = searchParams.get('id') || '';
                        }
                    }

                    let title = el.getAttribute('data-title') || '';
                    let year = el.getAttribute('data-year') || '';
                    let sRaw = el.getAttribute('data-season');
                    let eRaw = el.getAttribute('data-episode');

                    const opt = el.options && el.selectedIndex >= 0 ? el.options[el.selectedIndex] : null;
                    if (opt) {
                        if (!title && opt.getAttribute('data-title')) title = opt.getAttribute('data-title');
                        if (!sRaw && opt.getAttribute('data-season')) sRaw = opt.getAttribute('data-season');
                        if (!eRaw && opt.getAttribute('data-episode')) eRaw = opt.getAttribute('data-episode');
                    }

                    const season = sRaw ? parseInt(sRaw, 10) : null;
                    const episode = eRaw ? parseInt(eRaw, 10) : null;
                    const isTv = !!(season || episode || el.getAttribute('data-season') || (el.getAttribute('data-istv') === 'true'));

                    const tok = (window.ApiClient && typeof ApiClient.accessToken === 'function') ? ApiClient.accessToken() : '';
                    let apiUrl = '/Premio/AddStream';
                    if (window.ApiClient && typeof ApiClient.getUrl === 'function') {
                        try { apiUrl = ApiClient.getUrl('Premio/AddStream'); } catch(e) {}
                    }
                    const headers = { 'Content-Type': 'application/json' };
                    if (tok) { headers['X-Emby-Token'] = tok; }

                    try {
                        const res = await fetch(apiUrl, {
                            method: 'POST',
                            headers: headers,
                            body: JSON.stringify({
                                itemId: itemId,
                                title: title,
                                year: year,
                                isTv: isTv,
                                season: season,
                                episode: episode,
                                infoHash: val
                            })
                        });

                        if (!res.ok) {
                            throw new Error('HTTP ' + res.status + ' ' + res.statusText);
                        }

                        const data = await res.json();
                        if (data && data.success) {
                            if (statusEl) {
                                statusEl.textContent = '✓ Stream ready! Saved to .strm';
                                statusEl.style.color = '#10b981';
                            }
                        } else {
                            if (statusEl) {
                                statusEl.textContent = 'Error: ' + (data && data.message ? data.message : 'Failed');
                                statusEl.style.color = '#ef4444';
                            }
                        }
                    } catch (err) {
                        console.error('[Premio] Failed to add stream:', err);
                        if (statusEl) {
                            statusEl.textContent = 'Error: ' + err.message;
                            statusEl.style.color = '#ef4444';
                        }
                    }
                }, true);
            })();
            """;

        return Content(js, "application/javascript", Encoding.UTF8);
    }

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".avi", ".mov", ".wmv", ".flv", ".webm", ".ts", ".m4v", ".m2ts"
    };

    [SuppressMessage("Security", "CA3012:Do not use untrusted input to form regular expressions", Justification = "Static regex pattern used for episode matching.")]
    private static string? ResolvePlayableStreamUrl(PremiumizeDirectDlResponse directDl, int? season = null, int? episode = null)
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

            if (season.HasValue && episode.HasValue)
            {
                var sNum = season.Value;
                var epNum = episode.Value;
                var epPattern = $@"[sS]0*{sNum}[eE]0*{epNum}(?:[^0-9]|$)";
                var matchedEp = videoFiles.FirstOrDefault(f => Regex.IsMatch(f.Path ?? string.Empty, epPattern, RegexOptions.IgnoreCase));
                if (matchedEp is not null)
                {
                    return matchedEp.StreamLink ?? matchedEp.Link;
                }
            }

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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Premio: Added stream for '{Title}' -> '{Path}'")]
    private static partial void LogAddedStream(ILogger logger, string title, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Premio: Successfully resolved stream for '{Title}' (Magnet: {InfoHash}) via Premiumize: {StreamUrl}")]
    private static partial void LogStreamResolved(ILogger logger, string title, string infoHash, string streamUrl);

    [LoggerMessage(Level = LogLevel.Error, Message = "Premio: Stream resolution failed for '{Guid}': {ErrorMessage}")]
    private static partial void LogStreamResolutionFailed(ILogger logger, Guid guid, string errorMessage);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Premio: >>> AddStream received for '{Title}' (Season {Season}, Episode {Episode}, InfoHash: {InfoHash}, ItemId: {ItemId}) <<<")]
    private static partial void LogAddStreamReceived(ILogger logger, string title, int season, int episode, string infoHash, string itemId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Premio: Failed to add stream for '{Title}': {ErrorMessage}")]
    private static partial void LogAddStreamFailed(ILogger logger, string title, string errorMessage);
}

/// <summary>
/// Parameters for adding a stream to the library.
/// </summary>
public sealed class AddStreamRequest
{
    /// <summary>Gets or sets the library item ID if already in library.</summary>
    [JsonPropertyName("itemId")]
    public string? ItemId { get; set; }

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
