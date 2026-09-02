using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
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
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Search;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Premio.Services;

/// <summary>
/// ASP.NET Core Action Filter that intercepts Jellyfin search requests
/// (/Search/Hints, /Items, /Persons, /Artists with searchTerm) to return strictly categorized TMDB results (Movies, Shows, People, Artists)
/// and dynamically serves TMDB poster and profile images.
/// </summary>
public sealed partial class SearchActionFilter : IAsyncActionFilter
{
    private static readonly Regex ItemImageRegex = new(
        @"Items/([a-fA-F0-9\-]{32,36})/Images",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly TmdbClient _tmdbClient;
    private readonly IServerApplicationHost _appHost;
    private readonly ILogger<SearchActionFilter> _logger;

    /// <summary>
    /// Initialises a new instance of <see cref="SearchActionFilter"/>.
    /// </summary>
    /// <param name="tmdbClient">Injected TMDB client.</param>
    /// <param name="appHost">Injected Jellyfin server host.</param>
    /// <param name="logger">Injected logger.</param>
    public SearchActionFilter(
        TmdbClient tmdbClient,
        IServerApplicationHost appHost,
        ILogger<SearchActionFilter> logger)
    {
        _tmdbClient = tmdbClient;
        _appHost = appHost;
        _logger = logger;
    }

    /// <inheritdoc />
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Fail-safe: search interception errors must not break native search.")]
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        // 1. Intercept Image requests for virtual TMDB items
        var requestedId = ExtractItemId(context);
        if (requestedId != Guid.Empty)
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

            // Search TMDB exclusively
            var tmdbResults = await _tmdbClient.SearchMultiAsync(searchTerm, cancellationToken).ConfigureAwait(false);
            if (tmdbResults.Count == 0)
            {
                return;
            }

            var query = context.HttpContext.Request.Query;
            var requestPath = context.HttpContext.Request.Path.Value ?? string.Empty;

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

        var requestPath = context.HttpContext.Request.Path.Value ?? string.Empty;
        if (requestPath.Contains("/Images/", StringComparison.OrdinalIgnoreCase))
        {
            var match = ItemImageRegex.Match(requestPath);
            if (match.Success && Guid.TryParse(match.Groups[1].Value, out var matchedGuid))
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
}
