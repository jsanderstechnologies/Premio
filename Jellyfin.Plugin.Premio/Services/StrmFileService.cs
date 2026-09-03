using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Premio.Configuration;
using Jellyfin.Plugin.Premio.Models;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Premio.Services;

/// <summary>
/// Manages the creation, update, and deletion of <c>.strm</c> files on disk.
/// A <c>.strm</c> file is a plain-text file that Jellyfin treats as a media
/// item whose single line of content is a remote stream URL.
/// </summary>
public sealed partial class StrmFileService
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<StrmFileService> _logger;

    /// <summary>
    /// Initialises a new <see cref="StrmFileService"/>.
    /// </summary>
    /// <param name="libraryManager">Library manager to scan libraries after file creation.</param>
    /// <param name="logger">Logger injected by the host.</param>
    public StrmFileService(
        ILibraryManager libraryManager,
        ILogger<StrmFileService> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private static PluginConfiguration Config =>
        PremioPlugin.Instance?.Configuration
        ?? throw new InvalidOperationException("Premio: Plugin configuration is not available.");

    /// <summary>
    /// Removes invalid filesystem characters from a file or directory name.
    /// </summary>
    /// <param name="name">Raw filename.</param>
    /// <returns>Sanitized filename safe for disk storage.</returns>
    public static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Untitled";
        }

        var invalidChars = Regex.Escape(new string(Path.GetInvalidFileNameChars()));
        var invalidRegStr = $"[{invalidChars}]";
        return Regex.Replace(name, invalidRegStr, "_").Trim();
    }

    // -------------------------------------------------------------------------
    // Public methods
    // -------------------------------------------------------------------------

    /// <summary>
    /// Writes a <c>.strm</c> file into the appropriate Movie or TV Show directory based on media type.
    /// Movies: [MoviesDir]/[MovieTitle].[Year]/[MovieTitle].[Year].strm
    /// TV Shows: [TvDir]/[ShowTitle].[Year]/Season [SeasonNumber]/[ShowTitle].S##E##.strm
    /// </summary>
    /// <param name="title">Item title.</param>
    /// <param name="year">Release year (e.g. "1987" or "2008").</param>
    /// <param name="streamUri">Direct stream URI.</param>
    /// <param name="isTvShow">Indicates whether the item belongs to a TV show.</param>
    /// <param name="seasonNumber">Season number (default 1 for TV shows).</param>
    /// <param name="episodeNumber">Episode number (default 1 for TV shows).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The absolute path of the generated file, or null if no directory is configured.</returns>
    [SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Justification = "File name is sanitized with SanitizeFileName and combined with configured server admin directories.")]
    public async Task<string?> WriteMediaStrmFileAsync(
        string title,
        string? year,
        Uri streamUri,
        bool isTvShow = false,
        int seasonNumber = 1,
        int episodeNumber = 1,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(streamUri);

        var targetDir = isTvShow
            ? (string.IsNullOrWhiteSpace(Config.TvShowsStrmDirectory) ? Config.StrmOutputDirectory : Config.TvShowsStrmDirectory)
            : (string.IsNullOrWhiteSpace(Config.MoviesStrmDirectory) ? Config.StrmOutputDirectory : Config.MoviesStrmDirectory);

        if (string.IsNullOrWhiteSpace(targetDir))
        {
            return null;
        }

        var cleanTitle = SanitizeFileName(title);
        var cleanYear = !string.IsNullOrWhiteSpace(year) ? SanitizeFileName(year) : null;
        var folderWithYear = cleanYear is not null ? $"{cleanTitle}.{cleanYear}" : cleanTitle;

        string directoryPath;
        string fileName;

        if (isTvShow)
        {
            var seasonFolder = $"Season {seasonNumber}";
            var sNum = seasonNumber < 1 ? 1 : seasonNumber;
            var eNum = episodeNumber < 1 ? 1 : episodeNumber;
            fileName = $"{cleanTitle}.S{sNum:D2}E{eNum:D2}.strm";
            directoryPath = Path.Combine(targetDir, folderWithYear, seasonFolder);
        }
        else
        {
            fileName = $"{folderWithYear}.strm";
            directoryPath = Path.Combine(targetDir, folderWithYear);
        }

        var absolutePath = Path.Combine(directoryPath, fileName);

        if (File.Exists(absolutePath) && !Config.OverwriteExistingStrmFiles)
        {
            LogSkippingExistingFile(_logger, absolutePath);
            return absolutePath;
        }

        if (!Directory.Exists(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }

        await File.WriteAllTextAsync(absolutePath, streamUri.AbsoluteUri, cancellationToken)
                  .ConfigureAwait(false);

        LogWroteFile(_logger, absolutePath);
        TriggerLibraryRefresh();
        return absolutePath;
    }

    /// <summary>
    /// Creates the complete directory structure and .strm files for all seasons and episodes of a TV show.
    /// </summary>
    /// <param name="title">Show title.</param>
    /// <param name="year">Release year.</param>
    /// <param name="imdbId">IMDB identifier.</param>
    /// <param name="tvDetails">TMDB detailed item with season metadata.</param>
    /// <param name="posterBytes">Optional poster image bytes.</param>
    /// <param name="backdropBytes">Optional backdrop image bytes.</param>
    /// <param name="torrentioClient">Optional Torrentio client to discover additional or newly aired seasons.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The root directory path of the created show.</returns>
    [SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Justification = "File name is sanitized with SanitizeFileName and combined with configured server admin directories.")]
    public async Task<string?> CreateTvShowSeriesStructureAsync(
        string title,
        string? year,
        string imdbId,
        TmdbDetailedItem tvDetails,
        byte[]? posterBytes = null,
        byte[]? backdropBytes = null,
        TorrentioClient? torrentioClient = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(imdbId);
        ArgumentNullException.ThrowIfNull(tvDetails);

        var targetDir = string.IsNullOrWhiteSpace(Config.TvShowsStrmDirectory)
            ? Config.StrmOutputDirectory
            : Config.TvShowsStrmDirectory;

        if (string.IsNullOrWhiteSpace(targetDir))
        {
            return null;
        }

        var cleanTitle = SanitizeFileName(title);
        var cleanYear = !string.IsNullOrWhiteSpace(year) ? SanitizeFileName(year) : null;
        var folderWithYear = cleanYear is not null ? $"{cleanTitle}.{cleanYear}" : cleanTitle;
        var showRootPath = Path.Combine(targetDir, folderWithYear);

        if (!Directory.Exists(showRootPath))
        {
            Directory.CreateDirectory(showRootPath);
        }

        if (posterBytes is not null && posterBytes.Length > 0)
        {
            var posterPath = Path.Combine(showRootPath, "poster.jpg");
            if (!File.Exists(posterPath))
            {
                await File.WriteAllBytesAsync(posterPath, posterBytes, cancellationToken).ConfigureAwait(false);
            }
        }

        if (backdropBytes is not null && backdropBytes.Length > 0)
        {
            var backdropPath = Path.Combine(showRootPath, "fanart.jpg");
            if (!File.Exists(backdropPath))
            {
                await File.WriteAllBytesAsync(backdropPath, backdropBytes, cancellationToken).ConfigureAwait(false);
            }
        }

        var seasons = tvDetails.Seasons.Where(s => s.SeasonNumber >= 1).ToList();
        if (seasons.Count == 0)
        {
            var defaultCount = tvDetails.NumberOfEpisodes ?? 10;
            seasons.Add(new TmdbSeasonSummary { SeasonNumber = 1, EpisodeCount = Math.Max(defaultCount, 1) });
        }

        // 1. If TMDB indicates more seasons than listed in details.Seasons, add them
        if (tvDetails.NumberOfSeasons.HasValue && tvDetails.NumberOfSeasons.Value > seasons.Count)
        {
            var maxExisting = seasons.Count > 0 ? seasons.Max(s => s.SeasonNumber) : 0;
            var defaultEpisodes = seasons.Count > 0 ? seasons[^1].EpisodeCount : 8;
            for (var extra = maxExisting + 1; extra <= tvDetails.NumberOfSeasons.Value; extra++)
            {
                seasons.Add(new TmdbSeasonSummary
                {
                    SeasonNumber = extra,
                    EpisodeCount = defaultEpisodes > 0 ? defaultEpisodes : 8,
                    Name = $"Season {extra:D2}"
                });
            }
        }

        // 2. Probe Torrentio for any newer unannounced/released seasons (e.g. Season 4 when TMDB only has 1-3)
        if (torrentioClient is not null && !string.IsNullOrWhiteSpace(imdbId))
        {
            var probeSeason = seasons.Count > 0 ? seasons.Max(s => s.SeasonNumber) + 1 : 1;
            while (probeSeason <= 30)
            {
                var probeStreams = await torrentioClient.GetSeriesStreamsAsync(imdbId, probeSeason, 1, title, cleanYear, cancellationToken).ConfigureAwait(false);
                if (probeStreams.Count == 0)
                {
                    break;
                }

                var defaultEpisodes = seasons.Count > 0 ? seasons[^1].EpisodeCount : 8;
                var estimatedEpisodes = defaultEpisodes > 0 ? defaultEpisodes : 8;

                for (var i = 0; i < probeStreams.Count; i++)
                {
                    var streamTitle = probeStreams[i].Title ?? string.Empty;
                    var match = Regex.Match(streamTitle, @"(?:из|\/|-E|to\s*E)\s*(\d{1,2})", RegexOptions.IgnoreCase);
                    if (match.Success && int.TryParse(match.Groups[1].Value, out var parsedCount) && parsedCount is >= 1 and <= 50)
                    {
                        estimatedEpisodes = Math.Max(estimatedEpisodes, parsedCount);
                        break;
                    }
                }

                seasons.Add(new TmdbSeasonSummary
                {
                    SeasonNumber = probeSeason,
                    EpisodeCount = estimatedEpisodes,
                    Name = $"Season {probeSeason:D2}"
                });

                probeSeason++;
            }
        }

        foreach (var season in seasons)
        {
            var sNum = season.SeasonNumber;
            var seasonFolder = Path.Combine(showRootPath, $"Season {sNum:D2}");
            if (!Directory.Exists(seasonFolder))
            {
                Directory.CreateDirectory(seasonFolder);
            }

            var episodeCount = season.EpisodeCount > 0 ? season.EpisodeCount : 1;
            for (var ep = 1; ep <= episodeCount; ep++)
            {
                var strmFileName = $"{cleanTitle}.S{sNum:D2}E{ep:D2}.strm";
                var strmFilePath = Path.Combine(seasonFolder, strmFileName);

                if (!File.Exists(strmFilePath) || Config.OverwriteExistingStrmFiles)
                {
                    var streamUrl = $"/Premio/Stream?type=tv&imdbId={Uri.EscapeDataString(imdbId)}&season={sNum}&episode={ep}&title={Uri.EscapeDataString(title)}&year={cleanYear ?? string.Empty}";
                    await File.WriteAllTextAsync(strmFilePath, streamUrl, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        TriggerLibraryRefresh();
        return showRootPath;
    }

    /// <summary>
    /// Writes a <c>.strm</c> file into the appropriate Movie or TV Show directory based on media type.
    /// </summary>
    /// <param name="title">Item title or formatted filename.</param>
    /// <param name="streamUri">Direct stream URI.</param>
    /// <param name="isTvShow">Indicates whether the item belongs to a TV show.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The absolute path of the generated file, or null if no directory is configured.</returns>
    public Task<string?> WriteMediaStrmFileAsync(
        string title,
        Uri streamUri,
        bool isTvShow = false,
        CancellationToken cancellationToken = default)
    {
        return WriteMediaStrmFileAsync(title, null, streamUri, isTvShow, 1, 1, cancellationToken);
    }

    /// <summary>
    /// Saves a poster image alongside an existing .strm file or in the show root folder.
    /// </summary>
    /// <param name="strmPath">The absolute path to the generated .strm file.</param>
    /// <param name="posterBytes">Raw image bytes to save.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The path to the saved poster image, or null if invalid.</returns>
    [SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Justification = "File path is based on sanitized .strm target file within configured directory.")]
    public async Task<string?> SavePosterImageAsync(
        string strmPath,
        byte[] posterBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strmPath);
        ArgumentNullException.ThrowIfNull(posterBytes);

        if (posterBytes.Length == 0)
        {
            return null;
        }

        var directory = Path.GetDirectoryName(strmPath);
        if (string.IsNullOrEmpty(directory))
        {
            return null;
        }

        // If inside a Season folder (e.g. TV/Show.2008/Season 1), save poster to the show root folder
        var targetFolder = directory;
        if (Path.GetFileName(directory).StartsWith("Season ", StringComparison.OrdinalIgnoreCase))
        {
            var parent = Directory.GetParent(directory);
            if (parent is not null)
            {
                targetFolder = parent.FullName;
            }
        }

        var posterPath = Path.Combine(targetFolder, "poster.jpg");

        if (File.Exists(posterPath) && !Config.OverwriteExistingStrmFiles)
        {
            return posterPath;
        }

        await File.WriteAllBytesAsync(posterPath, posterBytes, cancellationToken).ConfigureAwait(false);
        LogSavedPoster(_logger, posterPath);
        return posterPath;
    }

    /// <summary>
    /// Writes (or overwrites) a <c>.strm</c> file containing <paramref name="streamUri"/>.
    /// The file is placed under <see cref="PluginConfiguration.StrmOutputDirectory"/>
    /// using <paramref name="relativePath"/> (which may include sub-directories).
    /// </summary>
    /// <param name="relativePath">
    /// Path relative to <see cref="PluginConfiguration.StrmOutputDirectory"/>.
    /// Do <em>not</em> include the <c>.strm</c> extension — it is appended automatically.
    /// Example: <c>Movies/The Matrix (1999)</c>
    /// </param>
    /// <param name="streamUri">The absolute HTTP(S) URI to write into the file.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>The absolute path of the written file.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="relativePath"/> is null or whitespace,
    /// or when <see cref="PluginConfiguration.StrmOutputDirectory"/> is not configured.
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="streamUri"/> is null.
    /// </exception>
    public async Task<string> WriteStrmFileAsync(
        string relativePath,
        Uri streamUri,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ArgumentNullException.ThrowIfNull(streamUri);

        var outputDir = Config.StrmOutputDirectory;
        if (string.IsNullOrWhiteSpace(outputDir))
        {
            throw new InvalidOperationException(
                "Premio: StrmOutputDirectory is not configured. " +
                "Set it in the plugin settings before writing .strm files.");
        }

        // Normalise path separators and append .strm extension.
        var safeRelative  = relativePath.Replace('/', Path.DirectorySeparatorChar)
                                        .Replace('\\', Path.DirectorySeparatorChar);
        var absolutePath  = Path.Combine(outputDir, safeRelative + ".strm");

        // Skip if the file already exists and overwrite is disabled.
        if (File.Exists(absolutePath) && !Config.OverwriteExistingStrmFiles)
        {
            LogSkippingExistingFile(_logger, absolutePath);
            return absolutePath;
        }

        // Ensure the directory tree exists.
        var directory = Path.GetDirectoryName(absolutePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(absolutePath, streamUri.AbsoluteUri, cancellationToken)
                  .ConfigureAwait(false);

        LogWroteFile(_logger, absolutePath);
        TriggerLibraryRefresh();
        return absolutePath;
    }

    /// <summary>
    /// Triggers an asynchronous Jellyfin library scan/refresh so newly created .strm files appear immediately.
    /// </summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Background library refresh is best-effort and must not fail file creation.")]
    public void TriggerLibraryRefresh()
    {
        try
        {
            _ = Task.Run(() => _libraryManager.ValidateMediaLibrary(new Progress<double>(), CancellationToken.None));
            LogTriggeredLibraryScan(_logger);
        }
        catch (Exception ex)
        {
            LogLibraryScanFailed(_logger, ex.Message);
        }
    }

    /// <summary>
    /// Deletes a previously written <c>.strm</c> file.
    /// </summary>
    /// <param name="relativePath">
    /// The same relative path used when calling <see cref="WriteStrmFileAsync"/>
    /// (without the <c>.strm</c> extension).
    /// </param>
    /// <returns>
    /// <see langword="true"/> if the file was deleted;
    /// <see langword="false"/> if it did not exist.
    /// </returns>
    public bool DeleteStrmFile(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        var outputDir = Config.StrmOutputDirectory;
        if (string.IsNullOrWhiteSpace(outputDir))
        {
            return false;
        }

        var safeRelative = relativePath.Replace('/', Path.DirectorySeparatorChar)
                                       .Replace('\\', Path.DirectorySeparatorChar);
        var absolutePath = Path.Combine(outputDir, safeRelative + ".strm");

        if (!File.Exists(absolutePath))
        {
            return false;
        }

        File.Delete(absolutePath);
        LogDeletedFile(_logger, absolutePath);
        return true;
    }

    /// <summary>
    /// Reads the stream URI stored inside an existing <c>.strm</c> file.
    /// </summary>
    /// <param name="relativePath">
    /// The relative path of the file (without the <c>.strm</c> extension).
    /// </param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>
    /// The URL stored in the file, or <see langword="null"/> if the file does not exist.
    /// </returns>
    public static async Task<string?> ReadStrmFileAsync(
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        var outputDir = Config.StrmOutputDirectory;
        if (string.IsNullOrWhiteSpace(outputDir))
        {
            return null;
        }

        var safeRelative = relativePath.Replace('/', Path.DirectorySeparatorChar)
                                       .Replace('\\', Path.DirectorySeparatorChar);
        var absolutePath = Path.Combine(outputDir, safeRelative + ".strm");

        if (!File.Exists(absolutePath))
        {
            return null;
        }

        return await File.ReadAllTextAsync(absolutePath, cancellationToken)
                         .ConfigureAwait(false);
    }

    // -------------------------------------------------------------------------
    // LoggerMessage delegates (CA1848)
    // -------------------------------------------------------------------------

    [LoggerMessage(Level = LogLevel.Debug, Message = "Premio: Skipping existing .strm file (overwrite disabled): {Path}")]
    private static partial void LogSkippingExistingFile(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Premio: Wrote .strm file: {Path}")]
    private static partial void LogWroteFile(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Premio: Saved poster image: {Path}")]
    private static partial void LogSavedPoster(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Premio: Deleted .strm file: {Path}")]
    private static partial void LogDeletedFile(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Premio: Triggered background library scan/refresh")]
    private static partial void LogTriggeredLibraryScan(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Premio: Failed to trigger library scan: {ErrorMessage}")]
    private static partial void LogLibraryScanFailed(ILogger logger, string errorMessage);
}
