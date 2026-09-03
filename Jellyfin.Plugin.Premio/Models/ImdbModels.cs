using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Premio.Models;

/// <summary>
/// Top-level response from the Cinemeta metadata service for an IMDb entity.
/// </summary>
public sealed class CinemetaSeriesResponse
{
    /// <summary>Gets the meta payload.</summary>
    [JsonPropertyName("meta")]
    public CinemetaMeta? Meta { get; init; }
}

/// <summary>
/// Series metadata from Cinemeta/IMDb.
/// </summary>
public sealed class CinemetaMeta
{
    /// <summary>Gets the IMDb ID.</summary>
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    /// <summary>Gets the series title.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>Gets the TheTVDB ID if present.</summary>
    [JsonPropertyName("tvdb_id")]
    public int? TvdbId { get; init; }

    /// <summary>Gets the list of episode videos.</summary>
    [JsonPropertyName("videos")]
    public IReadOnlyList<CinemetaVideo> Videos { get; init; } = [];
}

/// <summary>
/// Episode video entry from Cinemeta/IMDb.
/// </summary>
public sealed class CinemetaVideo
{
    /// <summary>Gets the video ID (e.g. tt9288030:1:1).</summary>
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    /// <summary>Gets the episode title.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>Gets the season number.</summary>
    [JsonPropertyName("season")]
    public int Season { get; init; }

    /// <summary>Gets the episode number within the season.</summary>
    [JsonPropertyName("number")]
    public int Number { get; init; }

    /// <summary>Gets the episode number alias.</summary>
    [JsonPropertyName("episode")]
    public int Episode { get; init; }
}
