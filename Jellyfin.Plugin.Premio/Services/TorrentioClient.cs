using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
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

    private static readonly HashSet<string> ForeignLanguageTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "french", "truefrench", "vostfr", "subfrench", "vf", "vff", "vfq",
        "german", "deutsch", "ger",
        "italian", "ita",
        "spanish", "espanol", "castellano", "latino", "spa", "esp",
        "russian", "rus",
        "hindi", "tamil", "telugu", "malayalam", "kannada", "marathi", "bengali", "punjabi", "hin",
        "korean", "kor", "japanese", "jap", "chinese", "chi", "mandarin", "cantonese",
        "portuguese", "portugues", "ptbr", "pt-br", "dublado", "legendado",
        "polish", "polski", "pol", "lektor",
        "turkish", "turkce", "tur",
        "thai", "vietnamese", "arabic", "hebrew", "czech", "cz", "dutch", "nl", "swedish", "norwegian", "danish", "finnish", "hungarian", "hun", "greek", "ukrainian", "ukr", "persian", "farsi",
        "multi", "dual", "dubbed", "dub"
    };

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
            return FilterStreams(streams, expectedTitle, null, onlyX264);
        }
        catch (Exception ex)
        {
            LogTorrentioError(_logger, $"{imdbId}:{season}:{episode}", ex.Message);
            return [];
        }
    }

    /// <summary>
    /// Filters streams based on English language, title words matching, year matching, and x264 preference.
    /// </summary>
    /// <param name="streams">Raw streams list.</param>
    /// <param name="expectedTitle">Expected item title.</param>
    /// <param name="expectedYear">Expected release year.</param>
    /// <param name="onlyX264">Whether to restrict results to x264/H.264.</param>
    /// <returns>Filtered streams.</returns>
    public static IReadOnlyList<TorrentioStreamResult> FilterStreams(
        IReadOnlyList<TorrentioStreamResult> streams,
        string? expectedTitle,
        string? expectedYear,
        bool onlyX264 = true)
    {
        ArgumentNullException.ThrowIfNull(streams);

        if (streams.Count == 0)
        {
            return streams;
        }

        // 1. Initial filter: exclude foreign languages and enforce x264 if enabled
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

            filtered.Add(s);
        }

        // 2. Strict match: enforce Title and Year matching
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

        // 3. Fallback to Title match only if year was omitted or absent in release name
        if (!string.IsNullOrWhiteSpace(expectedTitle))
        {
            var titleOnlyMatches = new List<TorrentioStreamResult>();
            for (var i = 0; i < filtered.Count; i++)
            {
                var s = filtered[i];
                var fullText = $"{s.Name} {s.Title}";
                if (ContainsTitleWords(fullText, expectedTitle))
                {
                    titleOnlyMatches.Add(s);
                }
            }

            if (titleOnlyMatches.Count > 0)
            {
                return titleOnlyMatches;
            }
        }

        // 4. Fallback to filtered non-foreign streams
        if (filtered.Count > 0)
        {
            return filtered;
        }

        var nonForeign = new List<TorrentioStreamResult>();
        for (var i = 0; i < streams.Count; i++)
        {
            if (!ContainsForeignLanguage($"{streams[i].Name} {streams[i].Title}"))
            {
                nonForeign.Add(streams[i]);
            }
        }

        return nonForeign;
    }

    private static bool ContainsForeignLanguage(string text)
    {
        var tokens = text.Split(ReleaseDelimiters, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < tokens.Length; i++)
        {
            if (ForeignLanguageTokens.Contains(tokens[i]))
            {
                return true;
            }
        }

        return false;
    }

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
