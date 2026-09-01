using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Premio.Models;

/// <summary>
/// Envelope returned by TMDB search endpoints.
/// </summary>
public sealed class TmdbSearchResponse
{
    /// <summary>Gets the current page number.</summary>
    [JsonPropertyName("page")]
    public int Page { get; init; }

    /// <summary>Gets the list of matching media items.</summary>
    [JsonPropertyName("results")]
    public IReadOnlyList<TmdbItem> Results { get; init; } = [];

    /// <summary>Gets the total number of pages.</summary>
    [JsonPropertyName("total_pages")]
    public int TotalPages { get; init; }

    /// <summary>Gets the total number of results.</summary>
    [JsonPropertyName("total_results")]
    public int TotalResults { get; init; }
}

/// <summary>
/// Represents a movie or TV show item returned by TMDB.
/// </summary>
public sealed class TmdbItem
{
    /// <summary>Gets the TMDB ID.</summary>
    [JsonPropertyName("id")]
    public int Id { get; init; }

    /// <summary>Gets the movie title (if movie).</summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>Gets the TV show title (if TV show).</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>Gets the media type (<c>"movie"</c>, <c>"tv"</c>, or <c>"person"</c>).</summary>
    [JsonPropertyName("media_type")]
    public string? MediaType { get; init; }

    /// <summary>Gets the poster path on TMDB CDN.</summary>
    [JsonPropertyName("poster_path")]
    public string? PosterPath { get; init; }

    /// <summary>Gets the backdrop path on TMDB CDN.</summary>
    [JsonPropertyName("backdrop_path")]
    public string? BackdropPath { get; init; }

    /// <summary>Gets the plot overview.</summary>
    [JsonPropertyName("overview")]
    public string? Overview { get; init; }

    /// <summary>Gets the release date string for movies (e.g. "1987-06-26").</summary>
    [JsonPropertyName("release_date")]
    public string? ReleaseDate { get; init; }

    /// <summary>Gets the first air date string for TV shows.</summary>
    [JsonPropertyName("first_air_date")]
    public string? FirstAirDate { get; init; }

    /// <summary>Gets the average vote score.</summary>
    [JsonPropertyName("vote_average")]
    public double VoteAverage { get; init; }

    /// <summary>
    /// Gets the resolved display title of the movie or TV show.
    /// </summary>
    [JsonIgnore]
    public string DisplayTitle => !string.IsNullOrWhiteSpace(Title) ? Title : (Name ?? string.Empty);

    /// <summary>
    /// Gets the release or first air year if available.
    /// </summary>
    [JsonIgnore]
    public string? Year
    {
        get
        {
            var date = !string.IsNullOrWhiteSpace(ReleaseDate) ? ReleaseDate : FirstAirDate;
            return !string.IsNullOrWhiteSpace(date) && date.Length >= 4 ? date[..4] : null;
        }
    }

    /// <summary>
    /// Gets the absolute full-resolution poster URL.
    /// </summary>
    [JsonIgnore]
    public string? PosterUrl => !string.IsNullOrWhiteSpace(PosterPath)
        ? $"https://image.tmdb.org/t/p/w500{PosterPath}"
        : null;
}
