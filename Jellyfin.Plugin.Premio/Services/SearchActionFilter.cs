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
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
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
    private static readonly Regex GuidExtractionRegex = new(
        @"[a-fA-F0-9]{8}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{12}|[a-fA-F0-9]{32}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly TmdbClient _tmdbClient;
    private readonly TorrentioClient _torrentioClient;
    private readonly PremiumizeClient _premiumizeClient;
    private readonly StrmFileService _strmService;
    private readonly ILibraryManager _libraryManager;
    private readonly IServerApplicationHost _appHost;
    private readonly ILogger<SearchActionFilter> _logger;

    /// <summary>
    /// Initialises a new instance of <see cref="SearchActionFilter"/>.
    /// </summary>
    /// <param name="tmdbClient">Injected TMDB client.</param>
    /// <param name="torrentioClient">Injected Torrentio client.</param>
    /// <param name="premiumizeClient">Injected Premiumize client.</param>
    /// <param name="strmService">Injected STRM file service.</param>
    /// <param name="libraryManager">Injected library manager.</param>
    /// <param name="appHost">Injected Jellyfin server host.</param>
    /// <param name="logger">Injected logger.</param>
    public SearchActionFilter(
        TmdbClient tmdbClient,
        TorrentioClient torrentioClient,
        PremiumizeClient premiumizeClient,
        StrmFileService strmService,
        ILibraryManager libraryManager,
        IServerApplicationHost appHost,
        ILogger<SearchActionFilter> logger)
    {
        _tmdbClient = tmdbClient;
        _torrentioClient = torrentioClient;
        _premiumizeClient = premiumizeClient;
        _strmService = strmService;
        _libraryManager = libraryManager;
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
            var tag = context.HttpContext.Request.Query["tag"].ToString();
            var isBackdrop = requestPath.Contains("/Backdrop", StringComparison.OrdinalIgnoreCase) || tag.Contains("_bg_", StringComparison.OrdinalIgnoreCase);

            if (requestedId != Guid.Empty || !string.IsNullOrWhiteSpace(tag))
            {
                if (isBackdrop)
                {
                    if (requestedId != Guid.Empty && PremioMetadataCache.TryGetBackdropBytes(requestedId, out var cachedBg) && cachedBg is not null)
                    {
                        context.Result = new FileContentResult(cachedBg, "image/jpeg");
                        return;
                    }

                    if (requestedId != Guid.Empty && PremioMetadataCache.TryGetBackdropUri(requestedId, out var bgUri) && bgUri is not null)
                    {
                        var downloadedBg = await _tmdbClient.DownloadImageBytesAsync(bgUri, cancellationToken).ConfigureAwait(false);
                        if (downloadedBg is not null && downloadedBg.Length > 0)
                        {
                            PremioMetadataCache.SetBackdropBytes(requestedId, downloadedBg);
                            context.Result = new FileContentResult(downloadedBg, "image/jpeg");
                            return;
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(tag) && tag.StartsWith("premio_", StringComparison.OrdinalIgnoreCase))
                    {
                        var tmdbIdStr = tag.Replace("premio_bg_", "", StringComparison.OrdinalIgnoreCase).Replace("premio_", "", StringComparison.OrdinalIgnoreCase);
                        if (int.TryParse(tmdbIdStr, out var tmdbId))
                        {
                            var details = await _tmdbClient.GetDetailsAsync("movie", tmdbId, cancellationToken).ConfigureAwait(false)
                                          ?? await _tmdbClient.GetDetailsAsync("tv", tmdbId, cancellationToken).ConfigureAwait(false);
                            if (details?.BackdropUrl is not null)
                            {
                                var downloaded = await _tmdbClient.DownloadImageBytesAsync(details.BackdropUrl, cancellationToken).ConfigureAwait(false);
                                if (downloaded is not null && downloaded.Length > 0)
                                {
                                    if (requestedId != Guid.Empty)
                                    {
                                        PremioMetadataCache.SetBackdropBytes(requestedId, downloaded);
                                    }
                                    context.Result = new FileContentResult(downloaded, "image/jpeg");
                                    return;
                                }
                            }
                        }
                    }
                }
                else
                {
                    if (requestedId != Guid.Empty && PremioMetadataCache.TryGetImageBytes(requestedId, out var cachedBytes) && cachedBytes is not null)
                    {
                        context.Result = new FileContentResult(cachedBytes, "image/jpeg");
                        return;
                    }

                    if (requestedId != Guid.Empty && PremioMetadataCache.TryGetPosterUri(requestedId, out var posterUri) && posterUri is not null)
                    {
                        var downloadedBytes = await _tmdbClient.DownloadImageBytesAsync(posterUri, cancellationToken).ConfigureAwait(false);
                        if (downloadedBytes is not null && downloadedBytes.Length > 0)
                        {
                            PremioMetadataCache.SetImageBytes(requestedId, downloadedBytes);
                            context.Result = new FileContentResult(downloadedBytes, "image/jpeg");
                            return;
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(tag) && tag.StartsWith("premio_", StringComparison.OrdinalIgnoreCase))
                    {
                        var tmdbIdStr = tag.Replace("premio_bg_", "", StringComparison.OrdinalIgnoreCase).Replace("premio_", "", StringComparison.OrdinalIgnoreCase);
                        if (int.TryParse(tmdbIdStr, out var tmdbId))
                        {
                            var details = await _tmdbClient.GetDetailsAsync("movie", tmdbId, cancellationToken).ConfigureAwait(false)
                                          ?? await _tmdbClient.GetDetailsAsync("tv", tmdbId, cancellationToken).ConfigureAwait(false);
                            if (details?.PosterUrl is not null)
                            {
                                var downloaded = await _tmdbClient.DownloadImageBytesAsync(details.PosterUrl, cancellationToken).ConfigureAwait(false);
                                if (downloaded is not null && downloaded.Length > 0)
                                {
                                    if (requestedId != Guid.Empty)
                                    {
                                        PremioMetadataCache.SetImageBytes(requestedId, downloaded);
                                    }
                                    context.Result = new FileContentResult(downloaded, "image/jpeg");
                                    return;
                                }
                            }
                        }
                    }
                }
            }
        }

        // ---------------------------------------------------------------------
        // 2. Intercept Virtual Item Details, Favorite / Add to Library, Auxiliary endpoints & Batch item requests
        // ---------------------------------------------------------------------
        if (HttpMethods.IsPost(context.HttpContext.Request.Method) && requestPath.Contains("/FavoriteItems/", StringComparison.OrdinalIgnoreCase))
        {
            var requestedId = ExtractItemId(context);
            if (requestedId != Guid.Empty && PremioMetadataCache.TryGetItem(requestedId, out var cachedItem) && cachedItem is not null)
            {
                var isTv = string.Equals(cachedItem.MediaType, "tv", StringComparison.OrdinalIgnoreCase);
                if (isTv)
                {
                    var tvDetails = cachedItem.Id > 0
                        ? await _tmdbClient.GetDetailsAsync("tv", cachedItem.Id, cancellationToken).ConfigureAwait(false)
                        : null;

                    var imdbId = tvDetails?.ImdbId ?? (cachedItem.Id > 0 ? await _tmdbClient.GetExternalImdbIdAsync("tv", cachedItem.Id, cancellationToken).ConfigureAwait(false) : null);
                    if (string.IsNullOrWhiteSpace(imdbId))
                    {
                        imdbId = $"premio_tv_{cachedItem.Id}";
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
                        Id = cachedItem.Id,
                        Name = cachedItem.DisplayTitle,
                        NumberOfEpisodes = 10,
                        NumberOfSeasons = 1,
                        Seasons = [new TmdbSeasonSummary { SeasonNumber = 1, EpisodeCount = 10 }]
                    };

                    _ = _strmService.CreateTvShowSeriesStructureAsync(
                        cachedItem.DisplayTitle,
                        cachedItem.Year,
                        imdbId,
                        tvDetails,
                        posterBytes,
                        backdropBytes,
                        cancellationToken);

                    context.Result = new ObjectResult(new UserItemDataDto
                    {
                        IsFavorite = true,
                        Key = requestedId.ToString("N")
                    });
                    return;
                }
            }
        }

        if (HttpMethods.IsGet(context.HttpContext.Request.Method))
        {
            var requestedId = ExtractItemId(context);
            if (requestedId != Guid.Empty && PremioMetadataCache.TryGetItem(requestedId, out var cachedItem) && cachedItem is not null)
            {
                if (requestPath.Contains("/Intros", StringComparison.OrdinalIgnoreCase) ||
                    requestPath.Contains("/Similar", StringComparison.OrdinalIgnoreCase) ||
                    requestPath.Contains("/SpecialFeatures", StringComparison.OrdinalIgnoreCase))
                {
                    context.Result = new ObjectResult(new QueryResult<BaseItemDto>(0, 0, Array.Empty<BaseItemDto>()));
                    return;
                }

                if (requestPath.Contains("/ThemeMedia", StringComparison.OrdinalIgnoreCase) ||
                    requestPath.Contains("/ThemeSongs", StringComparison.OrdinalIgnoreCase) ||
                    requestPath.Contains("/ThemeVideos", StringComparison.OrdinalIgnoreCase))
                {
                    context.Result = new ObjectResult(new ThemeMediaResult
                    {
                        OwnerId = requestedId,
                        Items = Array.Empty<BaseItemDto>(),
                        TotalRecordCount = 0
                    });
                    return;
                }

                if (!requestPath.Contains("/Images/", StringComparison.OrdinalIgnoreCase) &&
                    !requestPath.Contains("/PlaybackInfo", StringComparison.OrdinalIgnoreCase) &&
                    !requestPath.Contains("/stream", StringComparison.OrdinalIgnoreCase))
                {
                    var detailsDto = await BuildItemDetailsDtoAsync(requestedId, cachedItem, cancellationToken).ConfigureAwait(false);
                    if (detailsDto is not null)
                    {
                        if (context.HttpContext.Request.Query.ContainsKey("ids"))
                        {
                            context.Result = new ObjectResult(new QueryResult<BaseItemDto>(0, 1, new[] { detailsDto }));
                        }
                        else
                        {
                            context.Result = new ObjectResult(detailsDto);
                        }
                        return;
                    }
                }
            }
        }

        // ---------------------------------------------------------------------
        // 3. Intercept PlaybackInfo and Video Stream requests
        // ---------------------------------------------------------------------
        if (requestPath.Contains("/PlaybackInfo", StringComparison.OrdinalIgnoreCase) ||
            (requestPath.Contains("/Videos/", StringComparison.OrdinalIgnoreCase) && requestPath.Contains("/stream", StringComparison.OrdinalIgnoreCase)))
        {
            LogInterceptedPlaybackInfo(_logger, requestPath);
            var requestedId = ExtractItemId(context);
            LogExtractedItemId(_logger, requestedId);

            if (requestedId != Guid.Empty)
            {
                if (!PremioMetadataCache.TryGetItem(requestedId, out var cachedItem) || cachedItem is null)
                {
                    var libItem = _libraryManager.GetItemById(requestedId);
                    if (libItem is not null)
                    {
                        var isTv = libItem is Series || libItem is Episode;
                        cachedItem = new TmdbItem
                        {
                            Id = 0,
                            Title = libItem.Name,
                            MediaType = isTv ? "tv" : "movie",
                            ReleaseDate = libItem.ProductionYear?.ToString(CultureInfo.InvariantCulture)
                        };
                    }
                }

                if (cachedItem is not null)
                {
                    LogResolvingItemForPlayback(_logger, cachedItem.DisplayTitle);
                    if (requestPath.Contains("/stream", StringComparison.OrdinalIgnoreCase))
                    {
                        var streamUrl = await ResolveDirectStreamUrlAsync(context, cachedItem, cancellationToken).ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(streamUrl))
                        {
                            context.Result = new RedirectResult(streamUrl);
                            return;
                        }
                    }
                    else
                    {
                        var playbackResult = await HandlePlaybackInfoAsync(context, requestedId, cachedItem, cancellationToken).ConfigureAwait(false);
                        if (playbackResult is not null)
                        {
                            context.Result = new ObjectResult(playbackResult);
                            return;
                        }
                    }
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

    private async Task<BaseItemDto> BuildItemDetailsDtoAsync(
        Guid requestedId,
        TmdbItem item,
        CancellationToken cancellationToken)
    {
        var isTv = string.Equals(item.MediaType, "tv", StringComparison.OrdinalIgnoreCase);

        if (item.Id == 0 && !string.IsNullOrWhiteSpace(item.Title))
        {
            var searchResults = await _tmdbClient.SearchMultiAsync(item.Title, cancellationToken).ConfigureAwait(false);
            var match = FindMatchingItem(searchResults, isTv);
            if (match is not null)
            {
                item = match;
            }
        }

        // 1. Fetch rich TMDB details & IMDB external ID
        var details = item.Id > 0
            ? await _tmdbClient.GetDetailsAsync(item.MediaType ?? (isTv ? "tv" : "movie"), item.Id, cancellationToken).ConfigureAwait(false)
            : null;
        var imdbId = details?.ImdbId ?? (item.Id > 0 ? await _tmdbClient.GetExternalImdbIdAsync(item.MediaType ?? (isTv ? "tv" : "movie"), item.Id, cancellationToken).ConfigureAwait(false) : null);

        // 2. Fetch Torrentio streams for Movies only (TV shows do not perform magnet searches on search details)
        IReadOnlyList<TorrentioStreamResult> streams = Array.Empty<TorrentioStreamResult>();
        if (!isTv && !string.IsNullOrWhiteSpace(imdbId))
        {
            streams = await _torrentioClient.GetMovieStreamsAsync(imdbId, item.DisplayTitle, item.Year, cancellationToken).ConfigureAwait(false);
        }

        // 3. Populate MediaSources (Version dropdown for Movies)
        var defaultStreams = new[]
        {
            new MediaStream { Type = MediaStreamType.Video, Index = 0, Codec = "h264", IsDefault = true },
            new MediaStream { Type = MediaStreamType.Audio, Index = 1, Codec = "aac", IsDefault = true }
        };

        var mediaSources = new List<MediaSourceInfo>();

        if (!isTv)
        {
            var defaultStreamGuid = GenerateDeterministicGuid($"select_stream:{requestedId}");
            PremioMetadataCache.Register(defaultStreamGuid, item);
            var defaultStreamId = defaultStreamGuid.ToString("N");

            mediaSources.Add(new MediaSourceInfo
            {
                Id = defaultStreamId,
                Name = "Select a Stream",
                Path = $"/Premio/Stream/{requestedId}?mediaSourceId={defaultStreamId}",
                Protocol = MediaProtocol.Http,
                Type = MediaSourceType.Default,
                Container = "mp4",
                VideoType = VideoType.VideoFile,
                IsRemote = true,
                SupportsDirectPlay = true,
                SupportsDirectStream = true,
                SupportsTranscoding = true,
                MediaStreams = defaultStreams.ToList()
            });

            foreach (var stream in streams)
            {
                var sizeStr = !string.IsNullOrWhiteSpace(stream.FileSize) ? $" ({stream.FileSize})" : string.Empty;
                var label = $"{stream.CleanReleaseName}{sizeStr}";
                var rawHash = stream.InfoHash ?? string.Empty;
                var streamGuid = GenerateDeterministicGuid($"stream:{requestedId}:{rawHash}");
                PremioMetadataCache.Register(streamGuid, item);
                if (!string.IsNullOrWhiteSpace(rawHash))
                {
                    PremioMetadataCache.RegisterStreamHash(streamGuid, rawHash);
                }

                var streamId = streamGuid.ToString("N");

                mediaSources.Add(new MediaSourceInfo
                {
                    Id = streamId,
                    Name = label,
                    Path = $"/Premio/Stream/{requestedId}?mediaSourceId={streamId}&infoHash={rawHash}",
                    Protocol = MediaProtocol.Http,
                    Type = MediaSourceType.Default,
                    Container = "mp4",
                    VideoType = VideoType.VideoFile,
                    IsRemote = true,
                    SupportsDirectPlay = true,
                    SupportsDirectStream = true,
                    SupportsTranscoding = true,
                    MediaStreams = defaultStreams.ToList()
                });
            }
        }

        // 4. Register Backdrop if available
        if (details?.BackdropUrl is not null)
        {
            PremioMetadataCache.RegisterBackdrop(requestedId, details.BackdropUrl);
        }

        var displayTitle = details?.DisplayTitle ?? item.DisplayTitle;
        var yearStr = details?.Year ?? item.Year;
        var prodYear = int.TryParse(yearStr, out var y) ? (int?)y : null;

        var dto = new BaseItemDto
        {
            Id = requestedId,
            ServerId = _appHost.SystemId,
            Name = displayTitle,
            OriginalTitle = details?.Title ?? item.Title,
            Overview = details?.Overview ?? item.Overview,
            Taglines = !string.IsNullOrWhiteSpace(details?.Tagline) ? new[] { details.Tagline } : (isTv ? new[] { "Premio: Add to Library to watch seasons & episodes" } : Array.Empty<string>()),
            Genres = details?.Genres?.Select(g => g.Name).ToArray() ?? Array.Empty<string>(),
            CommunityRating = details is not null ? (float)details.VoteAverage : (float)item.VoteAverage,
            RunTimeTicks = details?.Runtime > 0 ? TimeSpan.FromMinutes(details.Runtime.Value).Ticks : null,
            ProductionYear = prodYear,
            Type = isTv ? BaseItemKind.Series : BaseItemKind.Movie,
            MediaType = isTv ? MediaType.Unknown : MediaType.Video,
            IsFolder = isTv,
            CanDownload = false,
            PrimaryImageAspectRatio = 2.0 / 3.0,
            ImageTags = new Dictionary<ImageType, string> { { ImageType.Primary, "premio_" + item.Id } },
            BackdropImageTags = details?.BackdropUrl is not null ? new[] { "premio_bg_" + item.Id } : null,
            MediaSources = mediaSources.ToArray(),
            LocationType = LocationType.FileSystem,
            MediaStreams = isTv ? Array.Empty<MediaStream>() : defaultStreams,
            People = Array.Empty<BaseItemPerson>(),
            RemoteTrailers = Array.Empty<MediaUrl>(),
            ProviderIds = new Dictionary<string, string> { { "Tmdb", item.Id.ToString(CultureInfo.InvariantCulture) } }
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
                var match = FindMatchingItem(searchResults, isTv);

                if (match is not null)
                {
                    imdbId = await _tmdbClient.GetExternalImdbIdAsync(match.MediaType ?? (isTv ? "tv" : "movie"), match.Id, cancellationToken).ConfigureAwait(false);
                }
            }

            if (string.IsNullOrWhiteSpace(imdbId))
            {
                return;
            }

            var itemYear = itemDto.ProductionYear?.ToString(CultureInfo.InvariantCulture);
            var streams = isTv
                ? await _torrentioClient.GetSeriesStreamsAsync(imdbId, 1, 1, itemDto.Name, itemYear, cancellationToken).ConfigureAwait(false)
                : await _torrentioClient.GetMovieStreamsAsync(imdbId, itemDto.Name, itemYear, cancellationToken).ConfigureAwait(false);

            if (streams.Count == 0)
            {
                return;
            }

            var mediaSources = new List<MediaSourceInfo>();

            var defaultStreams = new[]
            {
                new MediaStream { Type = MediaStreamType.Video, Index = 0, Codec = "h264", IsDefault = true },
                new MediaStream { Type = MediaStreamType.Audio, Index = 1, Codec = "aac", IsDefault = true }
            };

            // If item already exists in library with a current stream, update its properties for browser DirectPlay
            if (itemDto.MediaSources is not null && itemDto.MediaSources.Length > 0)
            {
                foreach (var existing in itemDto.MediaSources)
                {
                    if (existing.Id != "select_stream")
                    {
                        var updatedName = !string.IsNullOrWhiteSpace(existing.Name) && !existing.Name.StartsWith("Current", StringComparison.OrdinalIgnoreCase)
                            ? $"Current: {existing.Name}"
                            : (string.IsNullOrWhiteSpace(existing.Name) ? "Current: Saved Stream" : existing.Name);

                        existing.Name = updatedName;
                        existing.Container = "mp4";
                        existing.SupportsDirectPlay = true;
                        existing.SupportsDirectStream = true;
                        existing.SupportsTranscoding = true;
                        existing.Protocol = MediaProtocol.Http;
                        if (existing.MediaStreams is null || existing.MediaStreams.Count == 0)
                        {
                            existing.MediaStreams = defaultStreams.ToList();
                        }
                        mediaSources.Add(existing);
                    }
                }
            }

            if (mediaSources.Count == 0)
            {
                var defaultStreamGuid = GenerateDeterministicGuid($"select_stream:{itemDto.Id}");
                var syntheticItem = new TmdbItem
                {
                    Id = 0,
                    Title = itemDto.Name,
                    MediaType = isTv ? "tv" : "movie",
                    ReleaseDate = itemDto.ProductionYear?.ToString(CultureInfo.InvariantCulture)
                };
                PremioMetadataCache.Register(defaultStreamGuid, syntheticItem);
                var defaultStreamId = defaultStreamGuid.ToString("N");

                mediaSources.Add(new MediaSourceInfo
                {
                    Id = defaultStreamId,
                    Name = "Select a Stream",
                    Path = $"/Premio/Stream/{itemDto.Id}?mediaSourceId={defaultStreamId}",
                    Protocol = MediaProtocol.Http,
                    Type = MediaSourceType.Default,
                    Container = "mp4",
                    VideoType = VideoType.VideoFile,
                    IsRemote = true,
                    SupportsDirectPlay = true,
                    SupportsDirectStream = true,
                    SupportsTranscoding = true,
                    MediaStreams = defaultStreams.ToList()
                });
            }

            var syntheticItemForStreams = new TmdbItem
            {
                Id = 0,
                Title = itemDto.Name,
                MediaType = isTv ? "tv" : "movie",
                ReleaseDate = itemDto.ProductionYear?.ToString(CultureInfo.InvariantCulture)
            };
            PremioMetadataCache.Register(itemDto.Id, syntheticItemForStreams);

            foreach (var stream in streams)
            {
                var sizeStr = !string.IsNullOrWhiteSpace(stream.FileSize) ? $" ({stream.FileSize})" : string.Empty;
                var label = $"{stream.CleanReleaseName}{sizeStr}";
                var rawHash = stream.InfoHash ?? string.Empty;
                var streamGuid = GenerateDeterministicGuid($"stream:{itemDto.Id}:{rawHash}");
                PremioMetadataCache.Register(streamGuid, syntheticItemForStreams);
                if (!string.IsNullOrWhiteSpace(rawHash))
                {
                    PremioMetadataCache.RegisterStreamHash(streamGuid, rawHash);
                }

                var streamId = streamGuid.ToString("N");

                mediaSources.Add(new MediaSourceInfo
                {
                    Id = streamId,
                    Name = label,
                    Path = $"/Premio/Stream/{itemDto.Id}?mediaSourceId={streamId}&infoHash={rawHash}",
                    Protocol = MediaProtocol.Http,
                    Type = MediaSourceType.Default,
                    Container = "mp4",
                    VideoType = VideoType.VideoFile,
                    IsRemote = true,
                    SupportsDirectPlay = true,
                    SupportsDirectStream = true,
                    SupportsTranscoding = true,
                    MediaStreams = defaultStreams.ToList()
                });
            }

            itemDto.Container = "mp4";
            itemDto.MediaStreams = defaultStreams;
            itemDto.MediaSources = mediaSources.ToArray();
        }
        catch (Exception ex)
        {
            LogLibraryEnrichmentFailed(_logger, itemDto.Name, ex.Message);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Direct stream resolution must not crash the filter.")]
    private async Task<string?> ResolveDirectStreamUrlAsync(
        ActionExecutingContext context,
        TmdbItem item,
        CancellationToken cancellationToken)
    {
        var mediaSourceId = ExtractMediaSourceId(context);
        var requestedId = ExtractItemId(context);

        if (!string.IsNullOrWhiteSpace(mediaSourceId) && Guid.TryParse(mediaSourceId, out var parsedMediaGuid) && PremioMetadataCache.TryGetStreamHash(parsedMediaGuid, out var mappedHash))
        {
            mediaSourceId = mappedHash;
        }

        // If mediaSourceId is not a valid 40-char torrent infohash (e.g. item ID, select_stream, null, or GUID without mapped hash), fetch best stream from Torrentio
        var isRealInfoHash = !string.IsNullOrWhiteSpace(mediaSourceId) && mediaSourceId.Length == 40 && !string.Equals(mediaSourceId, requestedId.ToString("N"), StringComparison.OrdinalIgnoreCase);

        if (!isRealInfoHash)
        {
            var isTv = string.Equals(item.MediaType, "tv", StringComparison.OrdinalIgnoreCase);
            var imdbId = item.Id > 0
                ? await _tmdbClient.GetExternalImdbIdAsync(item.MediaType ?? (isTv ? "tv" : "movie"), item.Id, cancellationToken).ConfigureAwait(false)
                : null;

            if (string.IsNullOrWhiteSpace(imdbId))
            {
                var searchResults = await _tmdbClient.SearchMultiAsync(item.DisplayTitle, cancellationToken).ConfigureAwait(false);
                var match = FindMatchingItem(searchResults, isTv);

                if (match is not null)
                {
                    imdbId = await _tmdbClient.GetExternalImdbIdAsync(match.MediaType ?? (isTv ? "tv" : "movie"), match.Id, cancellationToken).ConfigureAwait(false);
                }
            }

            if (!string.IsNullOrWhiteSpace(imdbId))
            {
                var streams = isTv
                    ? await _torrentioClient.GetSeriesStreamsAsync(imdbId, 1, 1, item.DisplayTitle, item.Year, cancellationToken).ConfigureAwait(false)
                    : await _torrentioClient.GetMovieStreamsAsync(imdbId, item.DisplayTitle, item.Year, cancellationToken).ConfigureAwait(false);

                if (streams.Count > 0)
                {
                    mediaSourceId = streams[0].InfoHash;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(mediaSourceId) || mediaSourceId.Length != 40)
        {
            return null;
        }

        try
        {
            // 1. Send magnet to Premiumize Transfer manager & DirectDL
            await _premiumizeClient.CreateTransferAsync(mediaSourceId, cancellationToken).ConfigureAwait(false);
            var directDl = await _premiumizeClient.CreateDirectDownloadAsync(mediaSourceId, cancellationToken).ConfigureAwait(false);
            var streamUrl = ResolvePlayableStreamUrl(directDl);

            if (string.IsNullOrWhiteSpace(streamUrl))
            {
                return null;
            }

            LogStreamResolved(_logger, item.DisplayTitle, mediaSourceId, streamUrl);

            // 2. Write corresponding .strm file and save poster to Jellyfin Library
            var isTv = string.Equals(item.MediaType, "tv", StringComparison.OrdinalIgnoreCase);
            var season = 1;
            var episode = 1;

            if (context.HttpContext.Request.Query.TryGetValue("SeasonNumber", out var sStr) && int.TryParse(sStr, out var sVal))
            {
                season = sVal;
            }

            if (context.HttpContext.Request.Query.TryGetValue("EpisodeNumber", out var eStr) && int.TryParse(eStr, out var eVal))
            {
                episode = eVal;
            }

            var strmPath = await _strmService.WriteMediaStrmFileAsync(
                item.DisplayTitle,
                item.Year,
                new Uri(streamUrl),
                isTv,
                season,
                episode,
                cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(strmPath) && item.PosterUrl is not null)
            {
                var posterBytes = await _tmdbClient.DownloadImageBytesAsync(item.PosterUrl, cancellationToken).ConfigureAwait(false);
                if (posterBytes is not null && posterBytes.Length > 0)
                {
                    await _strmService.SavePosterImageAsync(strmPath, posterBytes, cancellationToken).ConfigureAwait(false);
                }
            }

            return streamUrl;
        }
        catch (Exception ex)
        {
            LogPlaybackResolutionFailed(_logger, item.DisplayTitle, ex.Message);
            return null;
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "PlaybackInfo must gracefully return stream URL or fallback without crashing.")]
    private async Task<PlaybackInfoResponse?> HandlePlaybackInfoAsync(
        ActionExecutingContext context,
        Guid requestedId,
        TmdbItem item,
        CancellationToken cancellationToken)
    {
        var rawMediaSourceId = ExtractMediaSourceId(context);
        var streamUrl = await ResolveDirectStreamUrlAsync(context, item, cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(streamUrl))
        {
            return null;
        }

        var sourceId = !string.IsNullOrWhiteSpace(rawMediaSourceId) ? rawMediaSourceId : requestedId.ToString("N");

        return new PlaybackInfoResponse
        {
            MediaSources = new[]
            {
                new MediaSourceInfo
                {
                    Id = sourceId,
                    Path = streamUrl,
                    Protocol = MediaProtocol.Http,
                    Type = MediaSourceType.Default,
                    Container = "mp4",
                    VideoType = VideoType.VideoFile,
                    IsRemote = true,
                    SupportsDirectPlay = true,
                    SupportsDirectStream = true,
                    SupportsTranscoding = true,
                    MediaStreams = new[]
                    {
                        new MediaStream { Type = MediaStreamType.Video, Index = 0, Codec = "h264", IsDefault = true },
                        new MediaStream { Type = MediaStreamType.Audio, Index = 1, Codec = "aac", IsDefault = true }
                    }
                }
            },
            PlaySessionId = Guid.NewGuid().ToString("N")
        };
    }

    private static string? ExtractMediaSourceId(ActionExecutingContext context)
    {
        var queryVal = context.HttpContext.Request.Query["MediaSourceId"].ToString();
        if (!string.IsNullOrWhiteSpace(queryVal))
        {
            return queryVal;
        }

        foreach (var kvp in context.ActionArguments)
        {
            if (kvp.Value is null)
            {
                continue;
            }

            if (string.Equals(kvp.Key, "mediaSourceId", StringComparison.OrdinalIgnoreCase) && kvp.Value is string sVal && !string.IsNullOrWhiteSpace(sVal))
            {
                return sVal;
            }

            var prop = kvp.Value.GetType().GetProperty("MediaSourceId");
            if (prop is not null)
            {
                var val = prop.GetValue(kvp.Value)?.ToString();
                if (!string.IsNullOrWhiteSpace(val))
                {
                    return val;
                }
            }
        }

        return null;
    }

    private static Guid ExtractItemId(ActionExecutingContext context)
    {
        if (context.HttpContext.Request.Query.TryGetValue("ids", out var qIds) && !string.IsNullOrWhiteSpace(qIds))
        {
            var firstId = qIds.ToString().Split(',')[0].Trim();
            if (Guid.TryParse(firstId, out var parsedQ) && parsedQ != Guid.Empty)
            {
                return parsedQ;
            }
        }

        if (context.HttpContext.Request.Query.TryGetValue("itemId", out var qItemId) && Guid.TryParse(qItemId, out var parsedQ2) && parsedQ2 != Guid.Empty)
        {
            return parsedQ2;
        }

        if (context.HttpContext.Request.Query.TryGetValue("id", out var qId) && Guid.TryParse(qId, out var parsedQ3) && parsedQ3 != Guid.Empty)
        {
            return parsedQ3;
        }

        if (context.ActionArguments.TryGetValue("itemId", out var val) && val is not null)
        {
            if (val is Guid g && g != Guid.Empty)
            {
                return g;
            }

            if (val is string s && Guid.TryParse(s, out var parsed) && parsed != Guid.Empty)
            {
                return parsed;
            }
        }

        if (context.ActionArguments.TryGetValue("id", out var valId) && valId is not null)
        {
            if (valId is Guid g2 && g2 != Guid.Empty)
            {
                return g2;
            }

            if (valId is string s2 && Guid.TryParse(s2, out var parsed2) && parsed2 != Guid.Empty)
            {
                return parsed2;
            }
        }

        foreach (var arg in context.ActionArguments.Values)
        {
            if (arg is Guid gArg && gArg != Guid.Empty)
            {
                return gArg;
            }

            if (arg is string sArg && Guid.TryParse(sArg, out var pArg) && pArg != Guid.Empty)
            {
                return pArg;
            }
        }

        var requestPath = context.HttpContext.Request.Path.Value ?? string.Empty;
        var guidMatches = GuidExtractionRegex.Matches(requestPath);
        for (var i = guidMatches.Count - 1; i >= 0; i--)
        {
            if (Guid.TryParse(guidMatches[i].Value, out var matchedGuid) && matchedGuid != Guid.Empty)
            {
                return matchedGuid;
            }
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
                LocationType = LocationType.FileSystem
            };

            existingItems.Add(dto);
        }

        return new QueryResult<BaseItemDto>(0, existingItems.Count, existingItems);
    }

    private static TmdbItem? FindMatchingItem(IReadOnlyList<TmdbItem> results, bool isTv)
    {
        for (var i = 0; i < results.Count; i++)
        {
            var item = results[i];
            if (isTv && string.Equals(item.MediaType, "tv", StringComparison.OrdinalIgnoreCase))
            {
                return item;
            }

            if (!isTv && string.Equals(item.MediaType, "movie", StringComparison.OrdinalIgnoreCase))
            {
                return item;
            }
        }

        return results.Count > 0 ? results[0] : null;
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

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".avi", ".mov", ".wmv", ".flv", ".webm", ".ts", ".m4v", ".m2ts"
    };

    private static string? ResolvePlayableStreamUrl(PremiumizeDirectDlResponse directDl)
    {
        if (directDl.Content is not null && directDl.Content.Count > 0)
        {
            // 1. Filter out non-video files (.txt, .nfo, .exe, .jpg, .png, .srt, etc.) and pick the largest video file
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

            // 2. If no standard video extension matched, select the largest file by size (ignoring tiny text/nfo files)
            var largestFile = directDl.Content.OrderByDescending(f => f.Size).FirstOrDefault();
            if (largestFile is not null && largestFile.Size > 50 * 1024 * 1024)
            {
                return largestFile.StreamLink ?? largestFile.Link;
            }
        }

        return directDl.Location;
    }

    private static Guid GenerateDeterministicGuid(string id)
    {
        var input = $"premio:{id}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return new Guid(hash.AsSpan(0, 16));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Premio: Intercepted PlaybackInfo request: {Path}")]
    private static partial void LogInterceptedPlaybackInfo(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Premio: Extracted ItemId: {ItemId}")]
    private static partial void LogExtractedItemId(ILogger logger, Guid itemId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Premio: Resolving item for playback: {Title}")]
    private static partial void LogResolvingItemForPlayback(ILogger logger, string title);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Premio: Search interception failed for query '{SearchTerm}': {ErrorMessage}")]
    private static partial void LogSearchInterceptionFailed(ILogger logger, string searchTerm, string errorMessage);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Premio: Playback resolution failed for title '{Title}': {ErrorMessage}")]
    private static partial void LogPlaybackResolutionFailed(ILogger logger, string title, string errorMessage);

    [LoggerMessage(Level = LogLevel.Information, Message = "Premio: Successfully resolved stream for '{Title}' (Magnet: {InfoHash}) via Premiumize: {StreamUrl}")]
    private static partial void LogStreamResolved(ILogger logger, string title, string infoHash, string streamUrl);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Premio: Failed to enrich existing library item '{Title}': {ErrorMessage}")]
    private static partial void LogLibraryEnrichmentFailed(ILogger logger, string title, string errorMessage);
}
