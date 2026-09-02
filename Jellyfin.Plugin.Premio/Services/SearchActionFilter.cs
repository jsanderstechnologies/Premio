using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Premio.Models;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Search;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Premio.Services;

/// <summary>
/// ASP.NET Core Action Filter that intercepts Jellyfin search requests
/// (/Search/Hints and /Items with searchTerm) to inject TMDB results and serves TMDB poster images for virtual items.
/// </summary>
public sealed partial class SearchActionFilter : IAsyncActionFilter
{
    private static readonly Regex TvPattern = new(
        @"[sS]\d{1,2}[eE]\d{1,2}|[sS]eason\s*\d+|[eE]pisode\s*\d+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ItemImageRegex = new(
        @"Items/([a-fA-F0-9\-]{36})/Images",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly PremiumizeClient _client;
    private readonly TmdbClient _tmdbClient;
    private readonly StrmFileService _strmService;
    private readonly ILogger<SearchActionFilter> _logger;

    /// <summary>
    /// Initialises a new instance of <see cref="SearchActionFilter"/>.
    /// </summary>
    /// <param name="client">Injected Premiumize REST API client.</param>
    /// <param name="tmdbClient">Injected TMDB client.</param>
    /// <param name="strmService">Injected STRM file service.</param>
    /// <param name="logger">Injected logger.</param>
    public SearchActionFilter(
        PremiumizeClient client,
        TmdbClient tmdbClient,
        StrmFileService strmService,
        ILogger<SearchActionFilter> logger)
    {
        _client = client;
        _tmdbClient = tmdbClient;
        _strmService = strmService;
        _logger = logger;
    }

    /// <inheritdoc />
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Fail-safe: search interception errors must not break native search.")]
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        // 1. Intercept Image requests for virtual TMDB / Premio items
        var requestPath = context.HttpContext.Request.Path.Value ?? string.Empty;
        if (requestPath.Contains("/Images/", StringComparison.OrdinalIgnoreCase))
        {
            var match = ItemImageRegex.Match(requestPath);
            if (match.Success && Guid.TryParse(match.Groups[1].Value, out var requestedId))
            {
                if (PremioMetadataCache.TryGetImageBytes(requestedId, out var cachedBytes) && cachedBytes is not null)
                {
                    context.Result = new FileContentResult(cachedBytes, "image/jpeg");
                    return;
                }

                if (PremioMetadataCache.TryGetPosterUri(requestedId, out var posterUri) && posterUri is not null)
                {
                    var downloadedBytes = await _tmdbClient.DownloadImageBytesAsync(posterUri, context.HttpContext.RequestAborted).ConfigureAwait(false);
                    if (downloadedBytes is not null && downloadedBytes.Length > 0)
                    {
                        PremioMetadataCache.SetImageBytes(requestedId, downloadedBytes);
                        context.Result = new FileContentResult(downloadedBytes, "image/jpeg");
                        return;
                    }
                }
            }
        }

        var executedContext = await next().ConfigureAwait(false);

        // 2. Intercept Search requests
        var searchTerm = context.HttpContext.Request.Query["searchTerm"].ToString();
        if (string.IsNullOrWhiteSpace(searchTerm))
        {
            return;
        }

        try
        {
            var cancellationToken = context.HttpContext.RequestAborted;

            // Search TMDB and Premiumize
            var tmdbResults = await _tmdbClient.SearchMultiAsync(searchTerm, cancellationToken).ConfigureAwait(false);
            var searchResults = await _client.SearchAsync(searchTerm, cancellationToken).ConfigureAwait(false);

            if (tmdbResults.Count == 0 && searchResults.Count == 0)
            {
                return;
            }

            if (executedContext.Result is ObjectResult objectResult && objectResult.Value is not null)
            {
                if (objectResult.Value is SearchHintResult hintResult)
                {
                    objectResult.Value = await EnrichSearchHintsAsync(hintResult, tmdbResults, searchResults, cancellationToken).ConfigureAwait(false);
                }
                else if (objectResult.Value is QueryResult<BaseItemDto> itemResult)
                {
                    objectResult.Value = await EnrichQueryResultAsync(itemResult, tmdbResults, searchResults, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            LogSearchInterceptionFailed(_logger, searchTerm, ex.Message);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Background task catches all exceptions to avoid unhandled thread faults.")]
    private Task<SearchHintResult> EnrichSearchHintsAsync(
        SearchHintResult hintResult,
        IReadOnlyList<TmdbItem> tmdbItems,
        IReadOnlyList<PremiumizeSearchItem> premiumizeItems,
        CancellationToken cancellationToken)
    {
        var existingHints = new List<SearchHint>(hintResult.SearchHints);

        // 1. Add TMDB search results with poster tags
        foreach (var tmdbItem in tmdbItems)
        {
            if (string.Equals(tmdbItem.MediaType, "person", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var isTv = string.Equals(tmdbItem.MediaType, "tv", StringComparison.OrdinalIgnoreCase);
            var itemGuid = GenerateDeterministicGuid($"tmdb:{tmdbItem.MediaType}:{tmdbItem.Id}");
            PremioMetadataCache.Register(itemGuid, tmdbItem);

            var displayName = $"[Premio] {tmdbItem.DisplayTitle}" + (!string.IsNullOrWhiteSpace(tmdbItem.Year) ? $" ({tmdbItem.Year})" : string.Empty);
            var prodYear = int.TryParse(tmdbItem.Year, out var y) ? (int?)y : null;

            var hint = new SearchHint
            {
                Id = itemGuid,
                Name = displayName,
                Type = isTv ? BaseItemKind.Series : BaseItemKind.Movie,
                MediaType = MediaType.Video,
                PrimaryImageTag = "premio_" + tmdbItem.Id,
                PrimaryImageAspectRatio = 2.0 / 3.0,
                ProductionYear = prodYear
            };

            existingHints.Add(hint);
        }

        // 2. Add any direct Premiumize cloud items
        var primaryTmdb = tmdbItems.Count > 0 ? tmdbItems[0] : null;
        foreach (var item in premiumizeItems)
        {
            if (string.Equals(item.Type, "folder", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var isTv = TvPattern.IsMatch(item.Name);
            var itemGuid = GenerateDeterministicGuid(item.Id);

            var matchedTmdb = tmdbItems.FirstOrDefault(t =>
                item.Name.Contains(t.DisplayTitle, StringComparison.OrdinalIgnoreCase)) ?? primaryTmdb;

            var displayName = matchedTmdb is not null && !string.IsNullOrWhiteSpace(matchedTmdb.DisplayTitle)
                ? $"[Premio Cloud] {matchedTmdb.DisplayTitle}{(matchedTmdb.Year is not null ? $" ({matchedTmdb.Year})" : string.Empty)}"
                : $"[Premio Cloud] {item.Name}";

            var hint = new SearchHint
            {
                Id = itemGuid,
                Name = displayName,
                Type = isTv ? BaseItemKind.Episode : BaseItemKind.Movie,
                MediaType = MediaType.Video,
                PrimaryImageTag = matchedTmdb is not null ? "premio_" + matchedTmdb.Id : null,
                PrimaryImageAspectRatio = 2.0 / 3.0
            };

            if (matchedTmdb is not null)
            {
                PremioMetadataCache.Register(itemGuid, matchedTmdb);
            }

            existingHints.Add(hint);

            // Write corresponding .strm file and download TMDB poster in background
            _ = Task.Run(async () =>
            {
                try
                {
                    var streamUrl = await _client.GetStreamUrlAsync(item.Id, cancellationToken).ConfigureAwait(false);
                    if (Uri.TryCreate(streamUrl, UriKind.Absolute, out var uri))
                    {
                        var strmPath = await _strmService.WriteMediaStrmFileAsync(item.Name, uri, isTv, cancellationToken).ConfigureAwait(false);

                        if (!string.IsNullOrWhiteSpace(strmPath) && matchedTmdb?.PosterUrl is not null)
                        {
                            var posterBytes = await _tmdbClient.DownloadImageBytesAsync(matchedTmdb.PosterUrl, cancellationToken).ConfigureAwait(false);
                            if (posterBytes is not null && posterBytes.Length > 0)
                            {
                                await _strmService.SavePosterImageAsync(strmPath, posterBytes, cancellationToken).ConfigureAwait(false);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogStreamResolutionFailed(_logger, item.Id, ex.Message);
                }
            }, cancellationToken);
        }

        return Task.FromResult(new SearchHintResult(existingHints.ToArray(), existingHints.Count));
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Background task catches all exceptions to avoid unhandled thread faults.")]
    private Task<QueryResult<BaseItemDto>> EnrichQueryResultAsync(
        QueryResult<BaseItemDto> queryResult,
        IReadOnlyList<TmdbItem> tmdbItems,
        IReadOnlyList<PremiumizeSearchItem> premiumizeItems,
        CancellationToken cancellationToken)
    {
        var existingItems = new List<BaseItemDto>(queryResult.Items);

        // 1. Add TMDB items
        foreach (var tmdbItem in tmdbItems)
        {
            if (string.Equals(tmdbItem.MediaType, "person", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var isTv = string.Equals(tmdbItem.MediaType, "tv", StringComparison.OrdinalIgnoreCase);
            var itemGuid = GenerateDeterministicGuid($"tmdb:{tmdbItem.MediaType}:{tmdbItem.Id}");
            PremioMetadataCache.Register(itemGuid, tmdbItem);

            var displayName = $"[Premio] {tmdbItem.DisplayTitle}" + (!string.IsNullOrWhiteSpace(tmdbItem.Year) ? $" ({tmdbItem.Year})" : string.Empty);
            var prodYear = int.TryParse(tmdbItem.Year, out var y) ? (int?)y : null;

            var dto = new BaseItemDto
            {
                Id = itemGuid,
                Name = displayName,
                Type = isTv ? BaseItemKind.Series : BaseItemKind.Movie,
                MediaType = MediaType.Video,
                Overview = tmdbItem.Overview,
                ProductionYear = prodYear,
                PrimaryImageAspectRatio = 2.0 / 3.0,
                ImageTags = new Dictionary<ImageType, string> { { ImageType.Primary, "premio_" + tmdbItem.Id } },
                IsFolder = false
            };

            existingItems.Add(dto);
        }

        // 2. Add direct Premiumize items
        var primaryTmdb = tmdbItems.Count > 0 ? tmdbItems[0] : null;
        foreach (var item in premiumizeItems)
        {
            if (string.Equals(item.Type, "folder", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var isTv = TvPattern.IsMatch(item.Name);
            var itemGuid = GenerateDeterministicGuid(item.Id);

            var matchedTmdb = tmdbItems.FirstOrDefault(t =>
                item.Name.Contains(t.DisplayTitle, StringComparison.OrdinalIgnoreCase)) ?? primaryTmdb;

            var displayName = matchedTmdb is not null && !string.IsNullOrWhiteSpace(matchedTmdb.DisplayTitle)
                ? $"[Premio Cloud] {matchedTmdb.DisplayTitle}{(matchedTmdb.Year is not null ? $" ({matchedTmdb.Year})" : string.Empty)}"
                : $"[Premio Cloud] {item.Name}";

            var dto = new BaseItemDto
            {
                Id = itemGuid,
                Name = displayName,
                Type = isTv ? BaseItemKind.Episode : BaseItemKind.Movie,
                MediaType = MediaType.Video,
                Overview = matchedTmdb?.Overview,
                PrimaryImageAspectRatio = 2.0 / 3.0,
                ImageTags = matchedTmdb is not null ? new Dictionary<ImageType, string> { { ImageType.Primary, "premio_" + matchedTmdb.Id } } : null,
                IsFolder = false
            };

            if (matchedTmdb is not null)
            {
                PremioMetadataCache.Register(itemGuid, matchedTmdb);
            }

            existingItems.Add(dto);

            // Write corresponding .strm file and download TMDB poster in background
            _ = Task.Run(async () =>
            {
                try
                {
                    var streamUrl = await _client.GetStreamUrlAsync(item.Id, cancellationToken).ConfigureAwait(false);
                    if (Uri.TryCreate(streamUrl, UriKind.Absolute, out var uri))
                    {
                        var strmPath = await _strmService.WriteMediaStrmFileAsync(item.Name, uri, isTv, cancellationToken).ConfigureAwait(false);

                        if (!string.IsNullOrWhiteSpace(strmPath) && matchedTmdb?.PosterUrl is not null)
                        {
                            var posterBytes = await _tmdbClient.DownloadImageBytesAsync(matchedTmdb.PosterUrl, cancellationToken).ConfigureAwait(false);
                            if (posterBytes is not null && posterBytes.Length > 0)
                            {
                                await _strmService.SavePosterImageAsync(strmPath, posterBytes, cancellationToken).ConfigureAwait(false);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogStreamResolutionFailed(_logger, item.Id, ex.Message);
                }
            }, cancellationToken);
        }

        return Task.FromResult(new QueryResult<BaseItemDto>(0, existingItems.Count, existingItems));
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Premio: Failed to resolve stream for item ID '{ItemId}': {ErrorMessage}")]
    private static partial void LogStreamResolutionFailed(ILogger logger, string itemId, string errorMessage);
}
