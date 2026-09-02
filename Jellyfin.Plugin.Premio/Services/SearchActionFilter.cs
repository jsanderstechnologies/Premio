using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Premio.Models;
using MediaBrowser.Controller;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Search;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Premio.Services;

/// <summary>
/// ASP.NET Core Action Filter that intercepts Jellyfin search, details, image, and playback requests
/// for virtual TMDB items and existing library items to provide native item details with a Torrentio stream version dropdown
/// and automatic Premiumize debrid library creation on stream selection.
/// </summary>
public sealed partial class SearchActionFilter : IAsyncActionFilter
{
    private static readonly Regex ItemImageRegex = new(
        @"Items/([a-fA-F0-9\-]{32,36})/Images",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ItemDetailsRegex = new(
        @"(?:/Users/[a-fA-F0-9\-]{32,36})?/Items/([a-fA-F0-9\-]{32,36})(?:$|\?|/)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex PlaybackInfoRegex = new(
        @"Items/([a-fA-F0-9\-]{32,36})/PlaybackInfo",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly TmdbClient _tmdbClient;
    private readonly TorrentioClient _torrentioClient;
    private readonly PremiumizeClient _premiumizeClient;
    private readonly StrmFileService _strmService;
    private readonly IServerApplicationHost _appHost;
    private readonly ILogger<SearchActionFilter> _logger;

    /// <summary>
    /// Initialises a new instance of <see cref="SearchActionFilter"/>.
    /// </summary>
    /// <param name="tmdbClient">Injected TMDB client.</param>
    /// <param name="torrentioClient">Injected Torrentio client.</param>
    /// <param name="premiumizeClient">Injected Premiumize client.</param>
    /// <param name="strmService">Injected STRM file service.</param>
    /// <param name="appHost">Injected Jellyfin server host.</param>
    /// <param name="logger">Injected logger.</param>
    public SearchActionFilter(
        TmdbClient tmdbClient,
        TorrentioClient torrentioClient,
        PremiumizeClient premiumizeClient,
        StrmFileService strmService,
        IServerApplicationHost appHost,
        ILogger<SearchActionFilter> logger)
    {
        _tmdbClient = tmdbClient;
        _torrentioClient = torrentioClient;
        _premiumizeClient = premiumizeClient;
        _strmService = strmService;
        _appHost = appHost;
        _logger = logger;
    }

    /// <inheritdoc />
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Fail-safe: interception errors must not break native Jellyfin functions.")]
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var requestPath = context.HttpContext.Request.Path.Value ?? string.Empty;
        var cancellationToken = context.HttpContext.RequestAborted;

        // ---------------------------------------------------------------------
        // 1. Intercept Image requests (Primary & Backdrop) for virtual items
        // ---------------------------------------------------------------------
        if (requestPath.Contains("/Images/", StringComparison.OrdinalIgnoreCase))
        {
            var requestedId = ExtractItemId(context);
            if (requestedId != Guid.Empty)
            {
                var isBackdrop = requestPath.Contains("/Backdrop", StringComparison.OrdinalIgnoreCase);

                if (isBackdrop)
                {
                    if (PremioMetadataCache.TryGetBackdropBytes(requestedId, out var cachedBg) && cachedBg is not null)
                    {
                        context.Result = new FileContentResult(cachedBg, "image/jpeg");
                        return;
                    }

                    if (PremioMetadataCache.TryGetBackdropUri(requestedId, out var bgUri) && bgUri is not null)
                    {
                        var downloadedBg = await _tmdbClient.DownloadImageBytesAsync(bgUri, cancellationToken).ConfigureAwait(false);
                        if (downloadedBg is not null && downloadedBg.Length > 0)
                        {
                            PremioMetadataCache.SetBackdropBytes(requestedId, downloadedBg);
                            context.Result = new FileContentResult(downloadedBg, "image/jpeg");
                            return;
                        }
                    }
                }
                else
                {
                    if (PremioMetadataCache.TryGetImageBytes(requestedId, out var cachedBytes) && cachedBytes is not null)
                    {
                        context.Result = new FileContentResult(cachedBytes, "image/jpeg");
                        return;
                    }

                    if (PremioMetadataCache.TryGetPosterUri(requestedId, out var posterUri) && posterUri is not null)
                    {
                        var downloadedBytes = await _tmdbClient.DownloadImageBytesAsync(posterUri, cancellationToken).ConfigureAwait(false);
                        if (downloadedBytes is not null && downloadedBytes.Length > 0)
                        {
                            PremioMetadataCache.SetImageBytes(requestedId, downloadedBytes);
                            context.Result = new FileContentResult(downloadedBytes, "image/jpeg");
                            return;
                        }
                    }
                }
            }
        }

        // ---------------------------------------------------------------------
        // 2. Intercept Virtual Item Details screen opening (GET /Items/{id} or /Users/{userId}/Items/{id})
        // ---------------------------------------------------------------------
        if (HttpMethods.IsGet(context.HttpContext.Request.Method) &&
            !requestPath.Contains("/Images/", StringComparison.OrdinalIgnoreCase) &&
            !requestPath.Contains("/PlaybackInfo", StringComparison.OrdinalIgnoreCase))
        {
            var requestedId = ExtractItemId(context);
            if (requestedId != Guid.Empty && PremioMetadataCache.TryGetItem(requestedId, out var cachedItem) && cachedItem is not null && cachedItem.Id > 0)
            {
                var detailsDto = await BuildItemDetailsDtoAsync(requestedId, cachedItem, cancellationToken).ConfigureAwait(false);
                if (detailsDto is not null)
                {
                    context.Result = new ObjectResult(detailsDto);
                    return;
                }
            }
        }

        // ---------------------------------------------------------------------
        // 3. Intercept PlaybackInfo (when user selects a stream version and plays)
        // ---------------------------------------------------------------------
        if (requestPath.Contains("/PlaybackInfo", StringComparison.OrdinalIgnoreCase))
        {
            var requestedId = ExtractItemId(context);
            if (requestedId != Guid.Empty && PremioMetadataCache.TryGetItem(requestedId, out var cachedItem) && cachedItem is not null)
            {
                var playbackResult = await HandlePlaybackInfoAsync(context, requestedId, cachedItem, cancellationToken).ConfigureAwait(false);
                if (playbackResult is not null)
                {
                    context.Result = new ObjectResult(playbackResult);
                    return;
                }
            }
        }

        var executedContext = await next().ConfigureAwait(false);

        // ---------------------------------------------------------------------
        // 4. Enrich existing library item details with Torrentio stream version dropdown
        // ---------------------------------------------------------------------
        if (executedContext.Result is ObjectResult objResult && objResult.Value is BaseItemDto libraryDto &&
            (libraryDto.Type == BaseItemKind.Movie || libraryDto.Type == BaseItemKind.Series || libraryDto.Type == BaseItemKind.Episode))
        {
            await EnrichExistingLibraryItemDtoAsync(libraryDto, cancellationToken).ConfigureAwait(false);
            return;
        }

        // ---------------------------------------------------------------------
        // 5. Intercept Search requests (/Search/Hints and /Items?searchTerm=...)
        // ---------------------------------------------------------------------
        var searchTerm = context.HttpContext.Request.Query["searchTerm"].ToString();
        if (string.IsNullOrWhiteSpace(searchTerm))
        {
            return;
        }

        try
        {
            // Search TMDB exclusively
            var tmdbResults = await _tmdbClient.SearchMultiAsync(searchTerm, cancellationToken).ConfigureAwait(false);
            if (tmdbResults.Count == 0)
            {
                return;
            }

            var query = context.HttpContext.Request.Query;
            var includeTypes = ParseQuerySet(query, "includeItemTypes");
            var excludeTypes = ParseQuerySet(query, "excludeItemTypes");
            var mediaTypes = ParseQuerySet(query, "mediaTypes");

            if (requestPath.Contains("/Persons", StringComparison.OrdinalIgnoreCase))
            {
                includeTypes.Add("Person");
            }

            if (requestPath.Contains("/Artists", StringComparison.OrdinalIgnoreCase))
            {
                includeTypes.Add("MusicArtist");
            }

            if (executedContext.Result is ObjectResult objectResult && objectResult.Value is not null)
            {
                if (objectResult.Value is SearchHintResult hintResult)
                {
                    objectResult.Value = EnrichSearchHints(hintResult, tmdbResults, includeTypes, excludeTypes, mediaTypes);
                }
                else if (objectResult.Value is QueryResult<BaseItemDto> itemResult)
                {
                    objectResult.Value = EnrichQueryResult(itemResult, tmdbResults, includeTypes, excludeTypes, mediaTypes);
                }
            }
        }
        catch (Exception ex)
        {
            LogSearchInterceptionFailed(_logger, searchTerm, ex.Message);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Auto-save background task catches all exceptions to prevent unhandled thread faults.")]
    private async Task<BaseItemDto> BuildItemDetailsDtoAsync(
        Guid requestedId,
        TmdbItem item,
        CancellationToken cancellationToken)
    {
        var isTv = string.Equals(item.MediaType, "tv", StringComparison.OrdinalIgnoreCase);

        // 1. Fetch rich TMDB details & IMDB external ID
        var details = await _tmdbClient.GetDetailsAsync(item.MediaType ?? (isTv ? "tv" : "movie"), item.Id, cancellationToken).ConfigureAwait(false);
        var imdbId = details?.ImdbId ?? await _tmdbClient.GetExternalImdbIdAsync(item.MediaType ?? (isTv ? "tv" : "movie"), item.Id, cancellationToken).ConfigureAwait(false);

        // 2. Fetch Torrentio streams
        IReadOnlyList<TorrentioStreamResult> streams = Array.Empty<TorrentioStreamResult>();
        if (!string.IsNullOrWhiteSpace(imdbId))
        {
            streams = isTv
                ? await _torrentioClient.GetSeriesStreamsAsync(imdbId, 1, 1, cancellationToken).ConfigureAwait(false)
                : await _torrentioClient.GetMovieStreamsAsync(imdbId, cancellationToken).ConfigureAwait(false);
        }

        // 3. Populate MediaSources (Version dropdown)
        var mediaSources = new List<MediaSourceInfo>
        {
            new MediaSourceInfo
            {
                Id = "select_stream",
                Name = "Select a Stream",
                Type = MediaSourceType.Default,
                IsRemote = true,
                SupportsDirectPlay = false,
                SupportsDirectStream = false,
                SupportsTranscoding = false
            }
        };

        foreach (var stream in streams)
        {
            var sizeStr = !string.IsNullOrWhiteSpace(stream.FileSize) ? $" ({stream.FileSize})" : string.Empty;
            var label = $"{stream.CleanReleaseName}{sizeStr}";

            mediaSources.Add(new MediaSourceInfo
            {
                Id = !string.IsNullOrWhiteSpace(stream.InfoHash) ? stream.InfoHash : Guid.NewGuid().ToString("N"),
                Name = label,
                Path = !string.IsNullOrWhiteSpace(stream.InfoHash) ? $"magnet:?xt=urn:btih:{stream.InfoHash}" : string.Empty,
                Type = MediaSourceType.Default,
                Container = "mkv",
                VideoType = VideoType.VideoFile,
                IsRemote = true,
                SupportsDirectPlay = true,
                SupportsDirectStream = true,
                SupportsTranscoding = true
            });
        }

        // 4. Register Backdrop if available
        if (details?.BackdropUrl is not null)
        {
            PremioMetadataCache.RegisterBackdrop(requestedId, details.BackdropUrl);
        }

        var displayTitle = details?.DisplayTitle ?? item.DisplayTitle;
        var yearStr = details?.Year ?? item.Year;
        var prodYear = int.TryParse(yearStr, out var y) ? (int?)y : null;

        // Auto-save best stream into library on details page load
        if (streams.Count > 0)
        {
            var bestStream = streams[0];
            _ = Task.Run(async () =>
            {
                try
                {
                    _ = _premiumizeClient.CreateTransferAsync(bestStream.InfoHash, cancellationToken);
                    var directDl = await _premiumizeClient.CreateDirectDownloadAsync(bestStream.InfoHash, cancellationToken).ConfigureAwait(false);
                    var streamUrl = directDl.Location;
                    if (string.IsNullOrWhiteSpace(streamUrl) && directDl.Content is not null && directDl.Content.Count > 0)
                    {
                        streamUrl = directDl.Content[0].StreamLink ?? directDl.Content[0].Link;
                    }

                    if (!string.IsNullOrWhiteSpace(streamUrl))
                    {
                        var formattedTitle = !string.IsNullOrWhiteSpace(yearStr) ? $"{displayTitle} ({yearStr})" : displayTitle;
                        var strmPath = await _strmService.WriteMediaStrmFileAsync(formattedTitle, new Uri(streamUrl), isTv, cancellationToken).ConfigureAwait(false);
                        LogStreamResolved(_logger, displayTitle, bestStream.InfoHash, streamUrl);

                        if (!string.IsNullOrWhiteSpace(strmPath) && item.PosterUrl is not null)
                        {
                            var posterBytes = await _tmdbClient.DownloadImageBytesAsync(item.PosterUrl, cancellationToken).ConfigureAwait(false);
                            if (posterBytes is not null && posterBytes.Length > 0)
                            {
                                await _strmService.SavePosterImageAsync(strmPath, posterBytes, cancellationToken).ConfigureAwait(false);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogPlaybackResolutionFailed(_logger, displayTitle, ex.Message);
                }
            }, cancellationToken);
        }

        var dto = new BaseItemDto
        {
            Id = requestedId,
            ServerId = _appHost.SystemId,
            Name = displayTitle,
            OriginalTitle = details?.Title ?? item.Title,
            Overview = details?.Overview ?? item.Overview,
            Taglines = !string.IsNullOrWhiteSpace(details?.Tagline) ? new[] { details.Tagline } : Array.Empty<string>(),
            Genres = details?.Genres?.Select(g => g.Name).ToArray() ?? Array.Empty<string>(),
            CommunityRating = details is not null ? (float)details.VoteAverage : (float)item.VoteAverage,
            RunTimeTicks = details?.Runtime > 0 ? TimeSpan.FromMinutes(details.Runtime.Value).Ticks : null,
            ProductionYear = prodYear,
            Type = isTv ? BaseItemKind.Series : BaseItemKind.Movie,
            MediaType = MediaType.Video,
            IsFolder = isTv,
            PrimaryImageAspectRatio = 2.0 / 3.0,
            ImageTags = new Dictionary<ImageType, string> { { ImageType.Primary, "premio_" + item.Id } },
            BackdropImageTags = details?.BackdropUrl is not null ? new[] { "premio_bg_" + item.Id } : null,
            MediaSources = mediaSources.ToArray(),
            LocationType = LocationType.Remote
        };

        return dto;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Library enrichment catches all exceptions to avoid disrupting native library item rendering.")]
    private async Task EnrichExistingLibraryItemDtoAsync(BaseItemDto itemDto, CancellationToken cancellationToken)
    {
        try
        {
            var isTv = itemDto.Type == BaseItemKind.Series || itemDto.Type == BaseItemKind.Episode;
            string? imdbId = null;

            if (itemDto.ProviderIds is not null)
            {
                itemDto.ProviderIds.TryGetValue("Imdb", out imdbId);
                if (string.IsNullOrWhiteSpace(imdbId) && itemDto.ProviderIds.TryGetValue("Tmdb", out var tmdbIdStr) && int.TryParse(tmdbIdStr, out var tmdbId))
                {
                    imdbId = await _tmdbClient.GetExternalImdbIdAsync(isTv ? "tv" : "movie", tmdbId, cancellationToken).ConfigureAwait(false);
                }
            }

            if (string.IsNullOrWhiteSpace(imdbId))
            {
                var searchResults = await _tmdbClient.SearchMultiAsync(itemDto.Name, cancellationToken).ConfigureAwait(false);
                var match = searchResults.FirstOrDefault(r => isTv
                    ? string.Equals(r.MediaType, "tv", StringComparison.OrdinalIgnoreCase)
                    : string.Equals(r.MediaType, "movie", StringComparison.OrdinalIgnoreCase));

                if (match is not null)
                {
                    imdbId = await _tmdbClient.GetExternalImdbIdAsync(match.MediaType ?? (isTv ? "tv" : "movie"), match.Id, cancellationToken).ConfigureAwait(false);
                }
            }

            if (string.IsNullOrWhiteSpace(imdbId))
            {
                return;
            }

            var streams = isTv
                ? await _torrentioClient.GetSeriesStreamsAsync(imdbId, 1, 1, cancellationToken).ConfigureAwait(false)
                : await _torrentioClient.GetMovieStreamsAsync(imdbId, cancellationToken).ConfigureAwait(false);

            if (streams.Count == 0)
            {
                return;
            }

            var mediaSources = new List<MediaSourceInfo>
            {
                new MediaSourceInfo
                {
                    Id = "select_stream",
                    Name = "Select a Stream",
                    Type = MediaSourceType.Default,
                    IsRemote = true,
                    SupportsDirectPlay = false,
                    SupportsDirectStream = false,
                    SupportsTranscoding = false
                }
            };

            if (itemDto.MediaSources is not null && itemDto.MediaSources.Length > 0)
            {
                foreach (var existing in itemDto.MediaSources)
                {
                    if (existing.Id != "select_stream")
                    {
                        var updatedName = !string.IsNullOrWhiteSpace(existing.Name) && !existing.Name.StartsWith("Current", StringComparison.OrdinalIgnoreCase)
                            ? $"Current: {existing.Name}"
                            : existing.Name;

                        existing.Name = updatedName;
                        mediaSources.Add(existing);
                    }
                }
            }

            foreach (var stream in streams)
            {
                var sizeStr = !string.IsNullOrWhiteSpace(stream.FileSize) ? $" ({stream.FileSize})" : string.Empty;
                var label = $"{stream.CleanReleaseName}{sizeStr}";

                mediaSources.Add(new MediaSourceInfo
                {
                    Id = stream.InfoHash,
                    Name = label,
                    Path = $"magnet:?xt=urn:btih:{stream.InfoHash}",
                    Type = MediaSourceType.Default,
                    Container = "mkv",
                    VideoType = VideoType.VideoFile,
                    IsRemote = true,
                    SupportsDirectPlay = true,
                    SupportsDirectStream = true,
                    SupportsTranscoding = true
                });
            }

            itemDto.MediaSources = mediaSources.ToArray();

            // Register item in Premio cache for playback stream switching
            var syntheticItem = new TmdbItem
            {
                Id = 0,
                Title = itemDto.Name,
                MediaType = isTv ? "tv" : "movie",
                ReleaseDate = itemDto.ProductionYear?.ToString(CultureInfo.InvariantCulture)
            };
            PremioMetadataCache.Register(itemDto.Id, syntheticItem);
        }
        catch (Exception ex)
        {
            LogLibraryEnrichmentFailed(_logger, itemDto.Name, ex.Message);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "PlaybackInfo must gracefully return stream URL or fallback without crashing.")]
    private async Task<PlaybackInfoResponse?> HandlePlaybackInfoAsync(
        ActionExecutingContext context,
        Guid requestedId,
        TmdbItem item,
        CancellationToken cancellationToken)
    {
        var mediaSourceId = context.HttpContext.Request.Query["MediaSourceId"].ToString();

        // If mediaSourceId is not specified or placeholder, fetch best stream from Torrentio
        if (string.IsNullOrWhiteSpace(mediaSourceId) || string.Equals(mediaSourceId, "select_stream", StringComparison.OrdinalIgnoreCase))
        {
            var isTv = string.Equals(item.MediaType, "tv", StringComparison.OrdinalIgnoreCase);
            var imdbId = item.Id > 0
                ? await _tmdbClient.GetExternalImdbIdAsync(item.MediaType ?? (isTv ? "tv" : "movie"), item.Id, cancellationToken).ConfigureAwait(false)
                : null;

            if (string.IsNullOrWhiteSpace(imdbId))
            {
                var searchResults = await _tmdbClient.SearchMultiAsync(item.DisplayTitle, cancellationToken).ConfigureAwait(false);
                var match = searchResults.FirstOrDefault(r => isTv
                    ? string.Equals(r.MediaType, "tv", StringComparison.OrdinalIgnoreCase)
                    : string.Equals(r.MediaType, "movie", StringComparison.OrdinalIgnoreCase));

                if (match is not null)
                {
                    imdbId = await _tmdbClient.GetExternalImdbIdAsync(match.MediaType ?? (isTv ? "tv" : "movie"), match.Id, cancellationToken).ConfigureAwait(false);
                }
            }

            if (!string.IsNullOrWhiteSpace(imdbId))
            {
                var streams = isTv
                    ? await _torrentioClient.GetSeriesStreamsAsync(imdbId, 1, 1, cancellationToken).ConfigureAwait(false)
                    : await _torrentioClient.GetMovieStreamsAsync(imdbId, cancellationToken).ConfigureAwait(false);

                if (streams.Count > 0)
                {
                    mediaSourceId = streams[0].InfoHash;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(mediaSourceId) || string.Equals(mediaSourceId, "select_stream", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            // 1. Send magnet to Premiumize Transfer manager & DirectDL
            _ = _premiumizeClient.CreateTransferAsync(mediaSourceId, cancellationToken);
            var directDl = await _premiumizeClient.CreateDirectDownloadAsync(mediaSourceId, cancellationToken).ConfigureAwait(false);
            var streamUrl = directDl.Location;
            if (string.IsNullOrWhiteSpace(streamUrl) && directDl.Content is not null && directDl.Content.Count > 0)
            {
                streamUrl = directDl.Content[0].StreamLink ?? directDl.Content[0].Link;
            }

            if (string.IsNullOrWhiteSpace(streamUrl))
            {
                return null;
            }

            LogStreamResolved(_logger, item.DisplayTitle, mediaSourceId, streamUrl);

            // 2. Write corresponding .strm file and save poster to Jellyfin Library
            var isTv = string.Equals(item.MediaType, "tv", StringComparison.OrdinalIgnoreCase);
            var formattedTitle = !string.IsNullOrWhiteSpace(item.Year)
                ? $"{item.DisplayTitle} ({item.Year})"
                : item.DisplayTitle;

            var strmPath = await _strmService.WriteMediaStrmFileAsync(formattedTitle, new Uri(streamUrl), isTv, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(strmPath) && item.PosterUrl is not null)
            {
                var posterBytes = await _tmdbClient.DownloadImageBytesAsync(item.PosterUrl, cancellationToken).ConfigureAwait(false);
                if (posterBytes is not null && posterBytes.Length > 0)
                {
                    await _strmService.SavePosterImageAsync(strmPath, posterBytes, cancellationToken).ConfigureAwait(false);
                }
            }

            // 3. Return PlaybackInfoResponse with direct Premiumize stream URL
            return new PlaybackInfoResponse
            {
                MediaSources = new[]
                {
                    new MediaSourceInfo
                    {
                        Id = mediaSourceId,
                        Path = streamUrl,
                        Type = MediaSourceType.Default,
                        Container = "mkv",
                        VideoType = VideoType.VideoFile,
                        IsRemote = true,
                        SupportsDirectPlay = true,
                        SupportsDirectStream = true,
                        SupportsTranscoding = true
                    }
                },
                PlaySessionId = Guid.NewGuid().ToString("N")
            };
        }
        catch (Exception ex)
        {
            LogPlaybackResolutionFailed(_logger, item.DisplayTitle, ex.Message);
            return null;
        }
    }

    private static Guid ExtractItemId(ActionExecutingContext context)
    {
        if (context.ActionArguments.TryGetValue("itemId", out var val) && val is not null)
        {
            if (val is Guid g)
            {
                return g;
            }

            if (val is string s && Guid.TryParse(s, out var parsed))
            {
                return parsed;
            }
        }

        if (context.ActionArguments.TryGetValue("id", out var valId) && valId is not null)
        {
            if (valId is Guid g2)
            {
                return g2;
            }

            if (valId is string s2 && Guid.TryParse(s2, out var parsed2))
            {
                return parsed2;
            }
        }

        var requestPath = context.HttpContext.Request.Path.Value ?? string.Empty;
        var imgMatch = ItemImageRegex.Match(requestPath);
        if (imgMatch.Success && Guid.TryParse(imgMatch.Groups[1].Value, out var matchedGuid))
        {
            return matchedGuid;
        }

        var detailsMatch = ItemDetailsRegex.Match(requestPath);
        if (detailsMatch.Success && Guid.TryParse(detailsMatch.Groups[1].Value, out var detailsGuid))
        {
            return detailsGuid;
        }

        var pbMatch = PlaybackInfoRegex.Match(requestPath);
        if (pbMatch.Success && Guid.TryParse(pbMatch.Groups[1].Value, out var pbGuid))
        {
            return pbGuid;
        }

        return Guid.Empty;
    }

    private static HashSet<string> ParseQuerySet(IQueryCollection query, string key)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var raw = query[key].ToString();
        if (!string.IsNullOrWhiteSpace(raw))
        {
            foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                set.Add(part);
            }
        }

        return set;
    }

    private static bool MatchesFilter(
        TmdbItem item,
        HashSet<string> includeTypes,
        HashSet<string> excludeTypes,
        HashSet<string> mediaTypes)
    {
        var isMovie = string.Equals(item.MediaType, "movie", StringComparison.OrdinalIgnoreCase);
        var isTv = string.Equals(item.MediaType, "tv", StringComparison.OrdinalIgnoreCase);
        var isPerson = string.Equals(item.MediaType, "person", StringComparison.OrdinalIgnoreCase);

        var isMusicDept = isPerson && (
            string.Equals(item.KnownForDepartment, "Music", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(item.KnownForDepartment, "Sound", StringComparison.OrdinalIgnoreCase));

        // 1. Evaluate explicit excludeItemTypes
        if (isMovie && excludeTypes.Contains("Movie"))
        {
            return false;
        }

        if (isTv && (excludeTypes.Contains("Series") || excludeTypes.Contains("Episode")))
        {
            return false;
        }

        if (isPerson)
        {
            if (isMusicDept && excludeTypes.Contains("MusicArtist"))
            {
                return false;
            }

            if (!isMusicDept && excludeTypes.Contains("Person"))
            {
                return false;
            }
        }

        // 2. Suppress generic video collection queries (Videos section)
        if (includeTypes.Contains("Video") && !includeTypes.Contains("Movie") && !includeTypes.Contains("Series"))
        {
            return false;
        }

        // 3. Evaluate includeItemTypes
        if (includeTypes.Count > 0)
        {
            if (isMovie)
            {
                return includeTypes.Contains("Movie");
            }

            if (isTv)
            {
                return includeTypes.Contains("Series") || includeTypes.Contains("Episode");
            }

            if (isPerson)
            {
                if (includeTypes.Contains("MusicArtist") && !includeTypes.Contains("Person"))
                {
                    return isMusicDept;
                }

                if (includeTypes.Contains("Person") && !includeTypes.Contains("MusicArtist"))
                {
                    return !isMusicDept;
                }

                return includeTypes.Contains("Person") || includeTypes.Contains("MusicArtist");
            }

            return false;
        }

        // 4. Evaluate mediaTypes (e.g. Audio requests)
        if (mediaTypes.Count > 0)
        {
            if (mediaTypes.Contains("Audio") && !mediaTypes.Contains("Video"))
            {
                return isMusicDept;
            }
        }

        return true;
    }

    private static SearchHintResult EnrichSearchHints(
        SearchHintResult hintResult,
        IReadOnlyList<TmdbItem> tmdbItems,
        HashSet<string> includeTypes,
        HashSet<string> excludeTypes,
        HashSet<string> mediaTypes)
    {
        var existingHints = new List<SearchHint>(hintResult.SearchHints);

        foreach (var tmdbItem in tmdbItems)
        {
            if (!MatchesFilter(tmdbItem, includeTypes, excludeTypes, mediaTypes))
            {
                continue;
            }

            var itemGuid = GenerateDeterministicGuid($"tmdb:{tmdbItem.MediaType}:{tmdbItem.Id}");
            PremioMetadataCache.Register(itemGuid, tmdbItem);

            var (kind, mediaType, isFolder, isPerson) = ResolveMediaTypes(tmdbItem, includeTypes);

            var displayName = !isPerson && !string.IsNullOrWhiteSpace(tmdbItem.Year)
                ? $"{tmdbItem.DisplayTitle} ({tmdbItem.Year})"
                : tmdbItem.DisplayTitle;

            var prodYear = int.TryParse(tmdbItem.Year, out var y) ? (int?)y : null;

            var hint = new SearchHint
            {
                Id = itemGuid,
                Name = displayName,
                Type = kind,
                MediaType = mediaType,
                IsFolder = isFolder,
                PrimaryImageTag = "premio_" + tmdbItem.Id,
                PrimaryImageAspectRatio = isPerson ? 1.0 : 2.0 / 3.0,
                ProductionYear = prodYear
            };

            existingHints.Add(hint);
        }

        return new SearchHintResult(existingHints.ToArray(), existingHints.Count);
    }

    private QueryResult<BaseItemDto> EnrichQueryResult(
        QueryResult<BaseItemDto> queryResult,
        IReadOnlyList<TmdbItem> tmdbItems,
        HashSet<string> includeTypes,
        HashSet<string> excludeTypes,
        HashSet<string> mediaTypes)
    {
        var existingItems = new List<BaseItemDto>(queryResult.Items);
        var serverId = _appHost.SystemId;

        foreach (var tmdbItem in tmdbItems)
        {
            if (!MatchesFilter(tmdbItem, includeTypes, excludeTypes, mediaTypes))
            {
                continue;
            }

            var itemGuid = GenerateDeterministicGuid($"tmdb:{tmdbItem.MediaType}:{tmdbItem.Id}");
            PremioMetadataCache.Register(itemGuid, tmdbItem);

            var (kind, mediaType, isFolder, isPerson) = ResolveMediaTypes(tmdbItem, includeTypes);

            var displayName = !isPerson && !string.IsNullOrWhiteSpace(tmdbItem.Year)
                ? $"{tmdbItem.DisplayTitle} ({tmdbItem.Year})"
                : tmdbItem.DisplayTitle;

            var prodYear = int.TryParse(tmdbItem.Year, out var y) ? (int?)y : null;

            var dto = new BaseItemDto
            {
                Id = itemGuid,
                ServerId = serverId,
                Name = displayName,
                Type = kind,
                MediaType = mediaType,
                IsFolder = isFolder,
                Overview = tmdbItem.Overview,
                ProductionYear = prodYear,
                PrimaryImageAspectRatio = isPerson ? 1.0 : 2.0 / 3.0,
                ImageTags = new Dictionary<ImageType, string> { { ImageType.Primary, "premio_" + tmdbItem.Id } },
                LocationType = LocationType.Remote
            };

            existingItems.Add(dto);
        }

        return new QueryResult<BaseItemDto>(0, existingItems.Count, existingItems);
    }

    private static (BaseItemKind Kind, MediaType MediaType, bool IsFolder, bool IsPerson) ResolveMediaTypes(
        TmdbItem item,
        HashSet<string> includeTypes)
    {
        if (string.Equals(item.MediaType, "tv", StringComparison.OrdinalIgnoreCase))
        {
            return (BaseItemKind.Series, MediaType.Video, true, false);
        }

        if (string.Equals(item.MediaType, "person", StringComparison.OrdinalIgnoreCase))
        {
            var isMusic = (includeTypes.Contains("MusicArtist") && !includeTypes.Contains("Person")) ||
                          string.Equals(item.KnownForDepartment, "Music", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(item.KnownForDepartment, "Sound", StringComparison.OrdinalIgnoreCase);

            return (isMusic ? BaseItemKind.MusicArtist : BaseItemKind.Person, MediaType.Unknown, isMusic, true);
        }

        return (BaseItemKind.Movie, MediaType.Video, false, false);
    }

    private static Guid GenerateDeterministicGuid(string id)
    {
        var input = $"premio:{id}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return new Guid(hash.AsSpan(0, 16));
    }

    // -------------------------------------------------------------------------
    // LoggerMessage delegates (CA1848)
    // -------------------------------------------------------------------------

    [LoggerMessage(Level = LogLevel.Warning, Message = "Premio: Search interception failed for query '{SearchTerm}': {ErrorMessage}")]
    private static partial void LogSearchInterceptionFailed(ILogger logger, string searchTerm, string errorMessage);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Premio: Playback resolution failed for title '{Title}': {ErrorMessage}")]
    private static partial void LogPlaybackResolutionFailed(ILogger logger, string title, string errorMessage);

    [LoggerMessage(Level = LogLevel.Information, Message = "Premio: Successfully resolved stream for '{Title}' (Magnet: {InfoHash}) via Premiumize: {StreamUrl}")]
    private static partial void LogStreamResolved(ILogger logger, string title, string infoHash, string streamUrl);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Premio: Failed to enrich existing library item '{Title}': {ErrorMessage}")]
    private static partial void LogLibraryEnrichmentFailed(ILogger logger, string title, string errorMessage);
}
