using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Premio.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Premio.Services;

/// <summary>
/// Typed HTTP client for searching and retrieving torrent streams from Torrentio.
/// </summary>
public sealed partial class TorrentioClient
{
    private readonly HttpClient _http;
    private readonly ILogger<TorrentioClient> _logger;

    /// <summary>
    /// Initialises a new instance of <see cref="TorrentioClient"/>.
    /// </summary>
    /// <param name="httpClient">Injected HTTP client.</param>
    /// <param name="logger">Injected logger.</param>
    public TorrentioClient(HttpClient httpClient, ILogger<TorrentioClient> logger)
    {
        _http = httpClient;
        _logger = logger;
        _http.BaseAddress = new Uri("https://torrentio.strem.fun/");
        _http.Timeout = TimeSpan.FromSeconds(15);
    }

    private static readonly char[] ReleaseDelimiters = [' ', '.', '_', '-', '/', '\\', '[', ']', '(', ')', '{', '}', '+', ',', ':', ';'];

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "and", "or", "of", "in", "on", "at", "to", "for", "with", "by"
    };

    /// <summary>
    /// Retrieves available torrent streams for a movie given its IMDB ID, optionally filtered by title and year.
    /// </summary>
    /// <param name="imdbId">The IMDB identifier (e.g. "tt0093058").</param>
    /// <param name="expectedTitle">Optional expected movie title to match in release names.</param>
    /// <param name="expectedYear">Optional expected release year to match in release names.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A list of available torrent streams.</returns>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Torrentio lookup failures are non-critical and should not break the UI.")]
    public async Task<IReadOnlyList<TorrentioStreamResult>> GetMovieStreamsAsync(
        string imdbId,
        string? expectedTitle = null,
        string? expectedYear = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imdbId);

        try
        {
            var url = $"stream/movie/{Uri.EscapeDataString(imdbId)}.json";
            LogFetchingMovieStreams(_logger, imdbId);

            var response = await _http
                .GetFromJsonAsync<TorrentioResponse>(url, cancellationToken)
                .ConfigureAwait(false);

            var streams = response?.Streams ?? [];
            var onlyX264 = PremioPlugin.Instance?.Configuration?.OnlyX264Streams ?? true;
            return FilterStreams(streams, expectedTitle, expectedYear, onlyX264);
        }
        catch (Exception ex)
        {
            LogTorrentioError(_logger, imdbId, ex.Message);
            return [];
        }
    }

    /// <summary>
    /// Retrieves available torrent streams for a series episode given its IMDB ID, season, and episode number, optionally filtered by title and year.
    /// </summary>
    /// <param name="imdbId">The IMDB identifier (e.g. "tt0903747").</param>
    /// <param name="season">Season number (1-based).</param>
    /// <param name="episode">Episode number (1-based).</param>
    /// <param name="expectedTitle">Optional expected series title to match in release names.</param>
    /// <param name="expectedYear">Optional expected release year to match in release names.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A list of available torrent streams.</returns>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Torrentio lookup failures are non-critical and should not break the UI.")]
    public async Task<IReadOnlyList<TorrentioStreamResult>> GetSeriesStreamsAsync(
        string imdbId,
        int season,
        int episode,
        string? expectedTitle = null,
        string? expectedYear = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imdbId);

        try
        {
            var url = $"stream/series/{Uri.EscapeDataString(imdbId)}:{season}:{episode}.json";
            LogFetchingSeriesStreams(_logger, imdbId, season, episode);

            var response = await _http
                .GetFromJsonAsync<TorrentioResponse>(url, cancellationToken)
                .ConfigureAwait(false);

            var streams = response?.Streams ?? [];
            var onlyX264 = PremioPlugin.Instance?.Configuration?.OnlyX264Streams ?? true;
            return FilterStreams(streams, expectedTitle, null, onlyX264, season, episode);
        }
        catch (Exception ex)
        {
            LogTorrentioError(_logger, $"{imdbId}:{season}:{episode}", ex.Message);
            return [];
        }
    }

    /// <summary>
    /// Filters streams based on English language, title words matching, year matching, season/episode matching, and x264 preference.
    /// </summary>
    /// <param name="streams">Raw streams list.</param>
    /// <param name="expectedTitle">Expected item title.</param>
    /// <param name="expectedYear">Expected release year.</param>
    /// <param name="onlyX264">Whether to restrict results to x264/H.264.</param>
    /// <param name="expectedSeason">Optional expected season number for TV shows.</param>
    /// <param name="expectedEpisode">Optional expected episode number for TV shows.</param>
    /// <returns>Filtered streams.</returns>
    public static IReadOnlyList<TorrentioStreamResult> FilterStreams(
        IReadOnlyList<TorrentioStreamResult> streams,
        string? expectedTitle,
        string? expectedYear,
        bool onlyX264 = true,
        int? expectedSeason = null,
        int? expectedEpisode = null)
    {
        ArgumentNullException.ThrowIfNull(streams);

        if (streams.Count == 0)
        {
            return streams;
        }

        // 1. Initial filter: exclude foreign languages, enforce x264 if enabled, and match season/episode for TV
        var filtered = new List<TorrentioStreamResult>();
        for (var i = 0; i < streams.Count; i++)
        {
            var s = streams[i];
            var fullText = $"{s.Name} {s.Title}";

            if (onlyX264 && !s.IsX264)
            {
                continue;
            }

            if (ContainsForeignLanguage(fullText))
            {
                continue;
            }

            if (expectedSeason.HasValue && expectedEpisode.HasValue)
            {
                if (!MatchesSeasonAndEpisode(fullText, expectedSeason.Value, expectedEpisode.Value))
                {
                    continue;
                }
            }

            if (!string.IsNullOrWhiteSpace(expectedTitle) && !ContainsTitleWords(fullText, expectedTitle))
            {
                continue;
            }

            filtered.Add(s);
        }

        if (filtered.Count > 0)
        {
            return filtered;
        }

        // 2. Strict match: enforce Title and Year matching for movies
        var strictMatches = new List<TorrentioStreamResult>();
        for (var i = 0; i < filtered.Count; i++)
        {
            var s = filtered[i];
            var fullText = $"{s.Name} {s.Title}";

            var titleMatches = string.IsNullOrWhiteSpace(expectedTitle) || ContainsTitleWords(fullText, expectedTitle);
            var yearMatches = string.IsNullOrWhiteSpace(expectedYear) || ContainsYear(fullText, expectedYear);

            if (titleMatches && yearMatches)
            {
                strictMatches.Add(s);
            }
        }

        if (strictMatches.Count > 0)
        {
            return strictMatches;
        }

        // 3. Fallback: if x264 filter excluded all streams, find any non-foreign stream that matches show title and season/episode
        var nonForeign = new List<TorrentioStreamResult>();
        for (var i = 0; i < streams.Count; i++)
        {
            var s = streams[i];
            var fullText = $"{s.Name} {s.Title}";

            if (ContainsForeignLanguage(fullText))
            {
                continue;
            }

            if (expectedSeason.HasValue && expectedEpisode.HasValue)
            {
                if (!MatchesSeasonAndEpisode(fullText, expectedSeason.Value, expectedEpisode.Value))
                {
                    continue;
                }
            }

            if (!string.IsNullOrWhiteSpace(expectedTitle) && !ContainsTitleWords(fullText, expectedTitle))
            {
                continue;
            }

            nonForeign.Add(s);
        }

        return nonForeign;
    }

    [SuppressMessage("Security", "CA3012:Do not use untrusted input to form regular expressions", Justification = "Season and episode numbers are integers sanitized by integer formatting.")]
    private static bool MatchesSeasonAndEpisode(string text, int season, int episode)
    {
        // 1. Check standard S01E01, S1E1, S01.E01, S01_E01, S01 - E01
        if (Regex.IsMatch(text, $@"\bS0?{season}[\.\s\-_]*E0?{episode}\b", RegexOptions.IgnoreCase))
        {
            return true;
        }

        // 2. Check 1x01, 1x1
        if (Regex.IsMatch(text, $@"\b0?{season}x0?{episode}\b", RegexOptions.IgnoreCase))
        {
            return true;
        }

        // 3. Check "Season 1 Episode 1", "Season.01.Episode.01"
        if (Regex.IsMatch(text, $@"\bSeason[\.\s\-_]*0?{season}[\.\s\-_]+Episode[\.\s\-_]*0?{episode}\b", RegexOptions.IgnoreCase))
        {
            return true;
        }

        // 4. Check multi-episode range, e.g. S01E01-E04, S01E01-04
        var rangeMatch = Regex.Match(text, $@"\bS0?{season}[\.\s\-_]*E(\d{{1,2}})[-\sEe]+(\d{{1,2}})\b", RegexOptions.IgnoreCase);
        if (rangeMatch.Success && int.TryParse(rangeMatch.Groups[1].Value, out var startEp) && int.TryParse(rangeMatch.Groups[2].Value, out var endEp))
        {
            if (episode >= startEp && episode <= endEp)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Determines whether the given text contains characters from a foreign alphabet
    /// (e.g. Cyrillic, Chinese, Japanese, Korean, Arabic, Hebrew, Greek, Thai, or Indic scripts).
    /// </summary>
    /// <param name="text">Text to inspect.</param>
    /// <returns><c>true</c> if foreign alphabet characters are present; otherwise, <c>false</c>.</returns>
    public static bool ContainsForeignAlphabet(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];

            // Basic Latin, Latin-1 Supplement, Latin Extended (English, standard Latin accents)
            if (ch <= 0x024F)
            {
                continue;
            }

            // Foreign non-Latin alphabet Unicode ranges:
            // 0x0370 - 0x052F: Greek, Coptic, and Cyrillic (Russian, Ukrainian, etc.)
            // 0x0590 - 0x05FF: Hebrew
            // 0x0600 - 0x08FF: Arabic
            // 0x0900 - 0x0DFF: Indic scripts (Devanagari/Hindi, Bengali, Tamil, Telugu, etc.)
            // 0x0E00 - 0x0E7F: Thai
            // 0x1100 - 0x11FF: Hangul Jamo
            // 0x2E80 - 0x2EFF: CJK Radicals
            // 0x3040 - 0x30FF: Hiragana and Katakana (Japanese)
            // 0x3130 - 0x318F: Hangul Compatibility Jamo
            // 0x3400 - 0x4DBF: CJK Extension A
            // 0x4E00 - 0x9FFF: CJK Unified Ideographs (Chinese, Japanese Kanji, Korean Hanja)
            // 0xAC00 - 0xD7AF: Hangul Syllables (Korean)
            if ((ch >= 0x0370 && ch <= 0x052F) ||
                (ch >= 0x0590 && ch <= 0x05FF) ||
                (ch >= 0x0600 && ch <= 0x08FF) ||
                (ch >= 0x0900 && ch <= 0x0DFF) ||
                (ch >= 0x0E00 && ch <= 0x0E7F) ||
                (ch >= 0x1100 && ch <= 0x11FF) ||
                (ch >= 0x2E80 && ch <= 0x2EFF) ||
                (ch >= 0x3040 && ch <= 0x30FF) ||
                (ch >= 0x3130 && ch <= 0x318F) ||
                (ch >= 0x3400 && ch <= 0x4DBF) ||
                (ch >= 0x4E00 && ch <= 0x9FFF) ||
                (ch >= 0xAC00 && ch <= 0xD7AF))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsForeignLanguage(string text) => ContainsForeignAlphabet(text);

    private static bool ContainsYear(string text, string year)
    {
        if (string.IsNullOrWhiteSpace(year) || year.Length < 4)
        {
            return true;
        }

        var fourDigitYear = year.Length == 4 ? year : year[..4];
        var tokens = text.Split(ReleaseDelimiters, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < tokens.Length; i++)
        {
            if (string.Equals(tokens[i], fourDigitYear, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsTitleWords(string text, string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return true;
        }

        var rawTitleWords = title.Split(ReleaseDelimiters, StringSplitOptions.RemoveEmptyEntries);
        var significantWords = new List<string>();
        for (var i = 0; i < rawTitleWords.Length; i++)
        {
            var word = rawTitleWords[i];
            if (!StopWords.Contains(word) || rawTitleWords.Length <= 2)
            {
                significantWords.Add(word);
            }
        }

        if (significantWords.Count == 0)
        {
            significantWords.AddRange(rawTitleWords);
        }

        var releaseTokens = new HashSet<string>(text.Split(ReleaseDelimiters, StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < significantWords.Count; i++)
        {
            var word = significantWords[i];
            if (!releaseTokens.Contains(word) && !text.Contains(word, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    // -------------------------------------------------------------------------
    // LoggerMessage delegates (CA1848)
    // -------------------------------------------------------------------------

    [LoggerMessage(Level = LogLevel.Debug, Message = "Premio: Fetching Torrentio streams for movie '{ImdbId}'")]
    private static partial void LogFetchingMovieStreams(ILogger logger, string imdbId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Premio: Fetching Torrentio streams for series '{ImdbId}' S{Season}E{Episode}")]
    private static partial void LogFetchingSeriesStreams(ILogger logger, string imdbId, int season, int episode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Premio: Torrentio stream fetch failed for '{Query}': {ErrorMessage}")]
    private static partial void LogTorrentioError(ILogger logger, string query, string errorMessage);
}
