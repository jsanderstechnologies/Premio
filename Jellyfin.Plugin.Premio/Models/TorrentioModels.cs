using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Premio.Models;

/// <summary>
/// Envelope returned by the Torrentio streams endpoint.
/// </summary>
public sealed class TorrentioResponse
{
    /// <summary>Gets the list of available torrent streams.</summary>
    [JsonPropertyName("streams")]
    public IReadOnlyList<TorrentioStream> Streams { get; init; } = [];
}

/// <summary>
/// Represents a single torrent stream returned by Torrentio.
/// </summary>
public sealed partial class TorrentioStream
{
    private static readonly Regex ResolutionRegex = new(
        @"(4[kK]|2160[pP]|1080[pP]|720[pP]|480[pP]|HD|CAM)",
        RegexOptions.Compiled);

    private static readonly Regex SizeRegex = new(
        @"💾\s*([\d\.]+\s*(?:GB|MB|KB))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SeedersRegex = new(
        @"👤\s*(\d+)",
        RegexOptions.Compiled);

    /// <summary>Gets the provider name and quality tag (e.g. "Torrentio\n1080p").</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>Gets the stream title, release name, and metadata.</summary>
    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    /// <summary>Gets the torrent SHA-1 infohash.</summary>
    [JsonPropertyName("infoHash")]
    public string InfoHash { get; init; } = string.Empty;

    /// <summary>Gets the index of the file within the multi-file torrent, if applicable.</summary>
    [JsonPropertyName("fileIdx")]
    public int? FileIdx { get; init; }

    /// <summary>Gets or sets a value indicating whether this stream is cached in Premiumize.</summary>
    [JsonIgnore]
    public bool IsCached { get; set; }

    /// <summary>Gets the parsed resolution (e.g. "4K", "1080p", "720p").</summary>
    [JsonIgnore]
    public string Quality
    {
        get
        {
            var match = ResolutionRegex.Match($"{Name} {Title}");
            return match.Success ? match.Value.ToUpperInvariant() : "HD";
        }
    }

    /// <summary>Gets the formatted file size string if present.</summary>
    [JsonIgnore]
    public string FileSize
    {
        get
        {
            var match = SizeRegex.Match(Title);
            return match.Success ? match.Groups[1].Value : string.Empty;
        }
    }

    /// <summary>Gets the seeders count if present.</summary>
    [JsonIgnore]
    public int Seeders
    {
        get
        {
            var match = SeedersRegex.Match(Title);
            return match.Success && int.TryParse(match.Groups[1].Value, out var s) ? s : 0;
        }
    }

    /// <summary>Gets the clean release name without emoji metadata.</summary>
    [JsonIgnore]
    public string CleanReleaseName
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Title))
            {
                return Name;
            }

            var lines = Title.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            return lines.Length > 0 ? lines[0].Trim() : Title;
        }
    }
}
