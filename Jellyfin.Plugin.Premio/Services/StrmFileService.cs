using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Premio.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Premio.Services;

/// <summary>
/// Manages the creation, update, and deletion of <c>.strm</c> files on disk.
/// A <c>.strm</c> file is a plain-text file that Jellyfin treats as a media
/// item whose single line of content is a remote stream URL.
/// </summary>
public sealed partial class StrmFileService
{
    private readonly ILogger<StrmFileService> _logger;

    /// <summary>
    /// Initialises a new <see cref="StrmFileService"/>.
    /// </summary>
    /// <param name="logger">Logger injected by the host.</param>
    public StrmFileService(ILogger<StrmFileService> logger)
    {
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
    /// </summary>
    /// <param name="title">Item title or filename.</param>
    /// <param name="streamUri">Direct stream URI.</param>
    /// <param name="isTvShow">Indicates whether the item belongs to a TV show.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The absolute path of the generated file, or null if no directory is configured.</returns>
    public async Task<string?> WriteMediaStrmFileAsync(
        string title,
        Uri streamUri,
        bool isTvShow = false,
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

        var safeTitle = SanitizeFileName(title);
        var absolutePath = Path.Combine(targetDir, safeTitle + ".strm");

        if (File.Exists(absolutePath) && !Config.OverwriteExistingStrmFiles)
        {
            LogSkippingExistingFile(_logger, absolutePath);
            return absolutePath;
        }

        var directory = Path.GetDirectoryName(absolutePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(absolutePath, streamUri.AbsoluteUri, cancellationToken)
                  .ConfigureAwait(false);

        LogWroteFile(_logger, absolutePath);
        return absolutePath;
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
        return absolutePath;
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Premio: Deleted .strm file: {Path}")]
    private static partial void LogDeletedFile(ILogger logger, string path);
}
