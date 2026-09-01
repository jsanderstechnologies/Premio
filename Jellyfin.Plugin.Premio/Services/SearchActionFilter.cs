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
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Search;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Premio.Services;

/// <summary>
/// ASP.NET Core Action Filter that intercepts Jellyfin search requests
/// (/Search/Hints and /Items with searchTerm) and enriches results with Premiumize cloud items.
/// </summary>
public sealed partial class SearchActionFilter : IAsyncActionFilter
{
    private static readonly Regex TvPattern = new(
        @"[sS]\d{1,2}[eE]\d{1,2}|[sS]eason\s*\d+|[eE]pisode\s*\d+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly PremiumizeClient _client;
    private readonly StrmFileService _strmService;
    private readonly ILogger<SearchActionFilter> _logger;

    /// <summary>
    /// Initialises a new instance of <see cref="SearchActionFilter"/>.
    /// </summary>
    /// <param name="client">Injected Premiumize REST API client.</param>
    /// <param name="strmService">Injected STRM file service.</param>
    /// <param name="logger">Injected logger.</param>
    public SearchActionFilter(
        PremiumizeClient client,
        StrmFileService strmService,
        ILogger<SearchActionFilter> logger)
    {
        _client = client;
        _strmService = strmService;
        _logger = logger;
    }

    /// <inheritdoc />
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Fail-safe: search interception errors must not break native search.")]
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var executedContext = await next().ConfigureAwait(false);

        var config = PremioPlugin.Instance?.Configuration;
        if (string.IsNullOrWhiteSpace(config?.ApiKey))
        {
            return;
        }

        var searchTerm = context.HttpContext.Request.Query["searchTerm"].ToString();
        if (string.IsNullOrWhiteSpace(searchTerm))
        {
            return;
        }

        try
        {
            var cancellationToken = context.HttpContext.RequestAborted;
            var searchResults = await _client.SearchAsync(searchTerm, cancellationToken).ConfigureAwait(false);

            if (searchResults.Count == 0)
            {
                return;
            }

            if (executedContext.Result is ObjectResult objectResult && objectResult.Value is not null)
            {
                if (objectResult.Value is SearchHintResult hintResult)
                {
                    objectResult.Value = await EnrichSearchHintsAsync(hintResult, searchResults, cancellationToken).ConfigureAwait(false);
                }
                else if (objectResult.Value is QueryResult<BaseItemDto> itemResult)
                {
                    objectResult.Value = await EnrichQueryResultAsync(itemResult, searchResults, cancellationToken).ConfigureAwait(false);
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
        IReadOnlyList<PremiumizeSearchItem> items,
        CancellationToken cancellationToken)
    {
        var existingHints = new List<SearchHint>(hintResult.SearchHints);

        foreach (var item in items)
        {
            if (string.Equals(item.Type, "folder", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var isTv = TvPattern.IsMatch(item.Name);
            var itemGuid = GenerateDeterministicGuid(item.Id);

            var hint = new SearchHint
            {
                Id = itemGuid,
                Name = $"[Premio] {item.Name}",
                Type = isTv ? BaseItemKind.Episode : BaseItemKind.Movie,
                MediaType = MediaType.Video
            };

            existingHints.Add(hint);

            // Write corresponding .strm file in background
            _ = Task.Run(async () =>
            {
                try
                {
                    var streamUrl = await _client.GetStreamUrlAsync(item.Id, cancellationToken).ConfigureAwait(false);
                    if (Uri.TryCreate(streamUrl, UriKind.Absolute, out var uri))
                    {
                        await _strmService.WriteMediaStrmFileAsync(item.Name, uri, isTv, cancellationToken).ConfigureAwait(false);
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
        IReadOnlyList<PremiumizeSearchItem> items,
        CancellationToken cancellationToken)
    {
        var existingItems = new List<BaseItemDto>(queryResult.Items);

        foreach (var item in items)
        {
            if (string.Equals(item.Type, "folder", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var isTv = TvPattern.IsMatch(item.Name);
            var itemGuid = GenerateDeterministicGuid(item.Id);

            var dto = new BaseItemDto
            {
                Id = itemGuid,
                Name = $"[Premio] {item.Name}",
                Type = isTv ? BaseItemKind.Episode : BaseItemKind.Movie,
                MediaType = MediaType.Video,
                IsFolder = false
            };

            existingItems.Add(dto);

            // Write corresponding .strm file in background
            _ = Task.Run(async () =>
            {
                try
                {
                    var streamUrl = await _client.GetStreamUrlAsync(item.Id, cancellationToken).ConfigureAwait(false);
                    if (Uri.TryCreate(streamUrl, UriKind.Absolute, out var uri))
                    {
                        await _strmService.WriteMediaStrmFileAsync(item.Name, uri, isTv, cancellationToken).ConfigureAwait(false);
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
