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
/// Represents a movie or TV show item returned by TMDB search.
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
    /// Gets the absolute full-resolution poster URI.
    /// </summary>
    [JsonIgnore]
    public Uri? PosterUrl => !string.IsNullOrWhiteSpace(PosterPath)
        ? new Uri($"https://image.tmdb.org/t/p/w500{PosterPath}")
        : null;

    /// <summary>
    /// Gets the absolute backdrop URI.
    /// </summary>
    [JsonIgnore]
    public Uri? BackdropUrl => !string.IsNullOrWhiteSpace(BackdropPath)
        ? new Uri($"https://image.tmdb.org/t/p/w1280{BackdropPath}")
        : null;
}

/// <summary>
/// External IDs associated with a TMDB media item.
/// </summary>
public sealed class TmdbExternalIds
{
    /// <summary>Gets the IMDB ID (e.g. "tt0093058").</summary>
    [JsonPropertyName("imdb_id")]
    public string? ImdbId { get; init; }
}

/// <summary>
/// Detailed metadata for a single movie or TV show.
/// </summary>
public sealed class TmdbDetailedItem
{
    /// <summary>Gets the TMDB ID.</summary>
    [JsonPropertyName("id")]
    public int Id { get; init; }

    /// <summary>Gets or sets the IMDB ID.</summary>
    [JsonPropertyName("imdb_id")]
    public string? ImdbId { get; set; }

    /// <summary>Gets the movie title.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>Gets the TV show name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>Gets the overview description.</summary>
    [JsonPropertyName("overview")]
    public string? Overview { get; init; }

    /// <summary>Gets the poster path.</summary>
    [JsonPropertyName("poster_path")]
    public string? PosterPath { get; init; }

    /// <summary>Gets the backdrop path.</summary>
    [JsonPropertyName("backdrop_path")]
    public string? BackdropPath { get; init; }

    /// <summary>Gets the release date string.</summary>
    [JsonPropertyName("release_date")]
    public string? ReleaseDate { get; init; }

    /// <summary>Gets the first air date string.</summary>
    [JsonPropertyName("first_air_date")]
    public string? FirstAirDate { get; init; }

    /// <summary>Gets the runtime in minutes.</summary>
    [JsonPropertyName("runtime")]
    public int? Runtime { get; init; }

    /// <summary>Gets the number of seasons (for TV shows).</summary>
    [JsonPropertyName("number_of_seasons")]
    public int? NumberOfSeasons { get; init; }

    /// <summary>Gets the number of episodes (for TV shows).</summary>
    [JsonPropertyName("number_of_episodes")]
    public int? NumberOfEpisodes { get; init; }

    /// <summary>Gets the external IDs container if embedded in response.</summary>
    [JsonPropertyName("external_ids")]
    public TmdbExternalIds? ExternalIds { get; init; }

    /// <summary>Gets the resolved display title.</summary>
    [JsonIgnore]
    public string DisplayTitle => !string.IsNullOrWhiteSpace(Title) ? Title : (Name ?? string.Empty);

    /// <summary>Gets the release year.</summary>
    [JsonIgnore]
    public string? Year
    {
        get
        {
            var date = !string.IsNullOrWhiteSpace(ReleaseDate) ? ReleaseDate : FirstAirDate;
            return !string.IsNullOrWhiteSpace(date) && date.Length >= 4 ? date[..4] : null;
        }
    }

    /// <summary>Gets the full poster URI.</summary>
    [JsonIgnore]
    public Uri? PosterUrl => !string.IsNullOrWhiteSpace(PosterPath)
        ? new Uri($"https://image.tmdb.org/t/p/w500{PosterPath}")
        : null;

    /// <summary>Gets the full backdrop URI.</summary>
    [JsonIgnore]
    public Uri? BackdropUrl => !string.IsNullOrWhiteSpace(BackdropPath)
        ? new Uri($"https://image.tmdb.org/t/p/w1280{BackdropPath}")
        : null;
}
