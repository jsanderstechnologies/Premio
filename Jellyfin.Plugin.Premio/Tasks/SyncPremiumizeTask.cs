using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Premio.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Premio.Tasks;

/// <summary>
/// Scheduled task that synchronises Premiumize cloud files into local .strm files.
/// </summary>
public sealed partial class SyncPremiumizeTask : IScheduledTask
{
    private static readonly Regex TvPattern = new(
        @"[sS]\d{1,2}[eE]\d{1,2}|[sS]eason\s*\d+|[eE]pisode\s*\d+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly PremiumizeClient _client;
    private readonly StrmFileService _strmService;
    private readonly ILogger<SyncPremiumizeTask> _logger;

    /// <summary>
    /// Initialises a new instance of <see cref="SyncPremiumizeTask"/>.
    /// </summary>
    /// <param name="client">Injected Premiumize client.</param>
    /// <param name="strmService">Injected STRM service.</param>
    /// <param name="logger">Injected logger.</param>
    public SyncPremiumizeTask(
        PremiumizeClient client,
        StrmFileService strmService,
        ILogger<SyncPremiumizeTask> logger)
    {
        _client = client;
        _strmService = strmService;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Sync Premiumize Cloud Storage";

    /// <inheritdoc />
    public string Key => "PremioSyncTask";

    /// <inheritdoc />
    public string Description => "Scans Premiumize cloud storage and creates local .strm files for Jellyfin libraries.";

    /// <inheritdoc />
    public string Category => "Premio";

    /// <inheritdoc />
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Scheduled tasks must capture all exceptions and report failure gracefully.")]
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = PremioPlugin.Instance?.Configuration;
        if (string.IsNullOrWhiteSpace(config?.ApiKey))
        {
            LogApiKeyMissing(_logger);
            return;
        }

        LogStartingSync(_logger);
        progress?.Report(0);

        try
        {
            // Search all items in cloud root
            var items = await _client.SearchAsync(string.Empty, cancellationToken).ConfigureAwait(false);
            if (items.Count == 0)
            {
                LogNoItemsFound(_logger);
                progress?.Report(100);
                return;
            }

            var total = items.Count;
            var processed = 0;

            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (string.Equals(item.Type, "folder", StringComparison.OrdinalIgnoreCase))
                {
                    processed++;
                    progress?.Report((double)processed / total * 100);
                    continue;
                }

                try
                {
                    var isTv = TvPattern.IsMatch(item.Name);
                    var streamUrl = await _client.GetStreamUrlAsync(item.Id, cancellationToken).ConfigureAwait(false);

                    if (Uri.TryCreate(streamUrl, UriKind.Absolute, out var uri))
                    {
                        await _strmService.WriteMediaStrmFileAsync(item.Name, uri, isTv, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    LogItemSyncFailed(_logger, item.Name, ex.Message);
                }

                processed++;
                progress?.Report((double)processed / total * 100);
            }

            LogSyncCompleted(_logger, processed);
        }
        catch (Exception ex)
        {
            LogSyncFailed(_logger, ex.Message);
        }
        finally
        {
            progress?.Report(100);
        }
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(6).Ticks
            }
        ];
    }

    // -------------------------------------------------------------------------
    // LoggerMessage delegates (CA1848)
    // -------------------------------------------------------------------------

    [LoggerMessage(Level = LogLevel.Warning, Message = "Premio: API key is not configured. Skipping cloud sync.")]
    private static partial void LogApiKeyMissing(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Premio: Starting Premiumize cloud storage sync...")]
    private static partial void LogStartingSync(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Premio: No cloud items found to sync.")]
    private static partial void LogNoItemsFound(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Premio: Cloud sync completed. Processed {Count} items.")]
    private static partial void LogSyncCompleted(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Premio: Failed to sync item '{ItemName}': {ErrorMessage}")]
    private static partial void LogItemSyncFailed(ILogger logger, string itemName, string errorMessage);

    [LoggerMessage(Level = LogLevel.Error, Message = "Premio: Cloud sync task failed: {ErrorMessage}")]
    private static partial void LogSyncFailed(ILogger logger, string errorMessage);
}
