using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Premio.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Premio.Services;

/// <summary>
/// Typed HTTP client for the Premiumize v2 REST API.
/// Injected via <see cref="IHttpClientFactory"/> (registered in
/// <see cref="Jellyfin.Plugin.Premio.ServiceRegistrator"/>).
/// </summary>
public sealed partial class PremiumizeClient
{
    private readonly HttpClient _http;
    private readonly ILogger<PremiumizeClient> _logger;

    /// <summary>
    /// Initialises a new <see cref="PremiumizeClient"/>.
    /// </summary>
    /// <param name="httpClient">
    /// The <see cref="HttpClient"/> provided by <see cref="IHttpClientFactory"/>.
    /// The base address and timeout are configured here.
    /// </param>
    /// <param name="logger">Logger injected by the host.</param>
    public PremiumizeClient(HttpClient httpClient, ILogger<PremiumizeClient> logger)
    {
        _http   = httpClient;
        _logger = logger;

        var config = PremioPlugin.Instance?.Configuration;
        var baseUrl = config?.ApiBaseUrl ?? "https://www.premiumize.me/api";
        var timeoutSecs = config?.RequestTimeoutSeconds ?? 30;

        _http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + '/');
        _http.Timeout     = TimeSpan.FromSeconds(timeoutSecs);
        _http.DefaultRequestHeaders.Add("Accept", "application/json");
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private static string ApiKey =>
        PremioPlugin.Instance?.Configuration?.ApiKey
        ?? throw new InvalidOperationException("Premio: API key is not configured.");

    /// <summary>Appends the API key to any query string.</summary>
    private static string WithKey(string relativeUrl) =>
        relativeUrl.Contains('?', StringComparison.Ordinal)
            ? $"{relativeUrl}&apikey={Uri.EscapeDataString(ApiKey)}"
            : $"{relativeUrl}?apikey={Uri.EscapeDataString(ApiKey)}";

    // -------------------------------------------------------------------------
    // Public API methods
    // -------------------------------------------------------------------------

    /// <summary>
    /// Searches the authenticated user's Premiumize cloud storage.
    /// Maps to <c>GET /folder/search?q=…</c>.
    /// </summary>
    /// <param name="query">Search term.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>
    /// A read-only list of matching <see cref="PremiumizeSearchItem"/> objects,
    /// or an empty list when no results are found.
    /// </returns>
    /// <exception cref="HttpRequestException">Thrown when the HTTP call fails.</exception>
    /// <exception cref="PremiumizeApiException">Thrown when the API returns a non-success status.</exception>
    public async Task<IReadOnlyList<PremiumizeSearchItem>> SearchAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        var url = WithKey($"folder/search?q={Uri.EscapeDataString(query)}");
        LogSearching(_logger, query);

        var response = await _http
            .GetFromJsonAsync<PremiumizeResponse<PremiumizeSearchContent>>(url, cancellationToken)
            .ConfigureAwait(false);

        EnsureSuccess(response, "folder/search");

        return response!.Content?.Files ?? [];
    }

    /// <summary>
    /// Retrieves detailed metadata (including a streaming link) for a single item.
    /// Maps to <c>GET /item/details?id=…</c>.
    /// </summary>
    /// <param name="itemId">The Premiumize item ID.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>Detailed item information including a time-limited stream URL.</returns>
    /// <exception cref="HttpRequestException">Thrown when the HTTP call fails.</exception>
    /// <exception cref="PremiumizeApiException">Thrown when the API returns a non-success status.</exception>
    public async Task<PremiumizeItemDetails> GetItemDetailsAsync(
        string itemId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);

        var url = WithKey($"item/details?id={Uri.EscapeDataString(itemId)}");
        LogFetchingItemDetails(_logger, itemId);

        var response = await _http
            .GetFromJsonAsync<PremiumizeResponse<PremiumizeItemDetails>>(url, cancellationToken)
            .ConfigureAwait(false);

        EnsureSuccess(response, "item/details");

        return response!.Content
            ?? throw new PremiumizeApiException("item/details", "Response content was null.");
    }

    /// <summary>
    /// Resolves a direct streaming URL for a Premiumize item.
    /// Prefers <see cref="PremiumizeItemDetails.StreamLink"/> when available,
    /// then falls back to <see cref="PremiumizeItemDetails.Link"/>.
    /// </summary>
    /// <param name="itemId">The Premiumize item ID.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>An absolute stream URL string.</returns>
    /// <exception cref="PremiumizeApiException">Thrown when no stream URL is available for the item.</exception>
    public async Task<string> GetStreamUrlAsync(
        string itemId,
        CancellationToken cancellationToken = default)
    {
        var details = await GetItemDetailsAsync(itemId, cancellationToken).ConfigureAwait(false);

        var url = details.StreamLink ?? details.Link;
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new PremiumizeApiException(
                "item/details",
                $"No stream URL available for item '{itemId}'.");
        }

        LogResolvedStreamUrl(_logger, itemId);
        return url;
    }

    // -------------------------------------------------------------------------
    // Guard
    // -------------------------------------------------------------------------

    private static void EnsureSuccess<T>(PremiumizeResponse<T>? response, string endpoint)
    {
        if (response is null)
        {
            throw new PremiumizeApiException(endpoint, "Deserialised response was null.");
        }

        if (!string.Equals(response.Status, "success", StringComparison.OrdinalIgnoreCase))
        {
            throw new PremiumizeApiException(
                endpoint,
                response.Message ?? "Unknown API error.");
        }
    }

    // -------------------------------------------------------------------------
    // LoggerMessage delegates (CA1848)
    // -------------------------------------------------------------------------

    [LoggerMessage(Level = LogLevel.Debug, Message = "Premio: Searching for '{Query}'")]
    private static partial void LogSearching(ILogger logger, string query);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Premio: Fetching item details for '{ItemId}'")]
    private static partial void LogFetchingItemDetails(ILogger logger, string itemId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Premio: Resolved stream URL for item '{ItemId}'")]
    private static partial void LogResolvedStreamUrl(ILogger logger, string itemId);
}

/// <summary>
/// Thrown when the Premiumize API returns a non-success status.
/// </summary>
public sealed class PremiumizeApiException : Exception
{
    /// <summary>The API endpoint that returned the error.</summary>
    public string Endpoint { get; }

    /// <summary>
    /// Initialises a new default instance of <see cref="PremiumizeApiException"/>.
    /// </summary>
    public PremiumizeApiException()
        : base("An error occurred communicating with the Premiumize API.")
    {
        Endpoint = string.Empty;
    }

    /// <summary>
    /// Initialises a new instance of <see cref="PremiumizeApiException"/> with a message.
    /// </summary>
    /// <param name="message">The error message.</param>
    public PremiumizeApiException(string message)
        : base(message)
    {
        Endpoint = string.Empty;
    }

    /// <summary>
    /// Initialises a new instance of <see cref="PremiumizeApiException"/> with a message and inner exception.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The exception that caused this exception.</param>
    public PremiumizeApiException(string message, Exception innerException)
        : base(message, innerException)
    {
        Endpoint = string.Empty;
    }

    /// <summary>
    /// Initialises a new instance of <see cref="PremiumizeApiException"/>.
    /// </summary>
    /// <param name="endpoint">The API endpoint that returned the error.</param>
    /// <param name="message">The error message returned by the API.</param>
    public PremiumizeApiException(string endpoint, string message)
        : base($"Premiumize API error at '{endpoint}': {message}")
    {
        Endpoint = endpoint;
    }
}
