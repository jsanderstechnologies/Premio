using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Premio.Models;

/// <summary>
/// Login request payload for TheTVDB v4 API.
/// </summary>
public sealed class TvdbLoginRequest
{
    /// <summary>Gets the API key.</summary>
    [JsonPropertyName("apikey")]
    public string ApiKey { get; init; } = string.Empty;
}

/// <summary>
/// Login response from TheTVDB v4 API.
/// </summary>
public sealed class TvdbLoginResponse
{
    /// <summary>Gets the status.</summary>
    [JsonPropertyName("status")]
    public string? Status { get; init; }

    /// <summary>Gets the authorization data.</summary>
    [JsonPropertyName("data")]
    public TvdbAuthData? Data { get; init; }
}

/// <summary>
/// Token authorization data from TheTVDB.
/// </summary>
public sealed class TvdbAuthData
{
    /// <summary>Gets the bearer token.</summary>
    [JsonPropertyName("token")]
    public string? Token { get; init; }
}

/// <summary>
/// Response from TheTVDB remote ID search.
/// </summary>
public sealed class TvdbRemoteIdResponse
{
    /// <summary>Gets the status.</summary>
    [JsonPropertyName("status")]
    public string? Status { get; init; }

    /// <summary>Gets the list of matching records.</summary>
    [JsonPropertyName("data")]
    public IReadOnlyList<TvdbRemoteIdRecord> Data { get; init; } = [];
}

/// <summary>
/// Record in TheTVDB remote ID search response.
/// </summary>
public sealed class TvdbRemoteIdRecord
{
    /// <summary>Gets the series details.</summary>
    [JsonPropertyName("series")]
    public TvdbSeriesRecord? Series { get; init; }
}

/// <summary>
/// Summary record for a TV series in TheTVDB.
/// </summary>
public sealed class TvdbSeriesRecord
{
    /// <summary>Gets the TVDB series ID.</summary>
    [JsonPropertyName("id")]
    public int Id { get; init; }

    /// <summary>Gets the series name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

/// <summary>
/// Response from TheTVDB series search endpoint.
/// </summary>
public sealed class TvdbSearchResponse
{
    /// <summary>Gets the status.</summary>
    [JsonPropertyName("status")]
    public string? Status { get; init; }

    /// <summary>Gets the search results.</summary>
    [JsonPropertyName("data")]
    public IReadOnlyList<TvdbSearchResult> Data { get; init; } = [];
}

/// <summary>
/// Search result from TheTVDB search endpoint.
/// </summary>
public sealed class TvdbSearchResult
{
    /// <summary>Gets the TVDB ID (can be string or int in JSON).</summary>
    [JsonPropertyName("tvdb_id")]
    public string? TvdbId { get; init; }

    /// <summary>Gets the series name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

/// <summary>
/// Extended series details response from TheTVDB.
/// </summary>
public sealed class TvdbSeriesExtendedResponse
{
    /// <summary>Gets the status.</summary>
    [JsonPropertyName("status")]
    public string? Status { get; init; }

    /// <summary>Gets the extended series payload.</summary>
    [JsonPropertyName("data")]
    public TvdbSeriesExtendedData? Data { get; init; }
}

/// <summary>
/// Extended series data containing episodes from TheTVDB.
/// </summary>
public sealed class TvdbSeriesExtendedData
{
    /// <summary>Gets the TVDB series ID.</summary>
    [JsonPropertyName("id")]
    public int Id { get; init; }

    /// <summary>Gets the series name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>Gets the episodes list.</summary>
    [JsonPropertyName("episodes")]
    public IReadOnlyList<TvdbEpisodeRecord> Episodes { get; init; } = [];
}

/// <summary>
/// Episode record from TheTVDB extended series response.
/// </summary>
public sealed class TvdbEpisodeRecord
{
    /// <summary>Gets the episode ID.</summary>
    [JsonPropertyName("id")]
    public int Id { get; init; }

    /// <summary>Gets the season number.</summary>
    [JsonPropertyName("seasonNumber")]
    public int SeasonNumber { get; init; }

    /// <summary>Gets the episode number within the season.</summary>
    [JsonPropertyName("number")]
    public int Number { get; init; }

    /// <summary>Gets the episode name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }
}
