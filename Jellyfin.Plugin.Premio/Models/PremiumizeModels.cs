using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Premio.Models;

// ---------------------------------------------------------------------------
// Premiumize v2 API response models
// All property names match the exact JSON keys returned by the API.
// ---------------------------------------------------------------------------

/// <summary>Envelope returned by every Premiumize v2 API call.</summary>
/// <typeparam name="T">The type of the <see cref="Content"/> payload.</typeparam>
public sealed class PremiumizeResponse<T>
{
    /// <summary>
    /// <c>"success"</c> when the request succeeded, <c>"error"</c> otherwise.
    /// </summary>
    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    /// <summary>Human-readable error message when <see cref="Status"/> is <c>"error"</c>.</summary>
    [JsonPropertyName("message")]
    public string? Message { get; init; }

    /// <summary>The response payload.</summary>
    [JsonPropertyName("content")]
    public T? Content { get; init; }
}

/// <summary>Represents a single item in a Premiumize cloud search result.</summary>
public sealed class PremiumizeSearchItem
{
    /// <summary>Gets the unique Premiumize item identifier.</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    /// <summary>Gets the display name of the item.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary><c>"file"</c> or <c>"folder"</c>.</summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    /// <summary>Gets the MIME type of the item, if available.</summary>
    [JsonPropertyName("mime_type")]
    public string? MimeType { get; init; }

    /// <summary>Gets the size of the item in bytes.</summary>
    [JsonPropertyName("size")]
    public long Size { get; init; }

    /// <summary>Gets the Unix timestamp (seconds) when the item was created.</summary>
    [JsonPropertyName("created_at")]
    public long CreatedAt { get; init; }

    /// <summary>Gets the ID of the parent folder, if applicable.</summary>
    [JsonPropertyName("folder_id")]
    public string? FolderId { get; init; }
}

/// <summary>Payload for the <c>/item/details</c> endpoint.</summary>
public sealed class PremiumizeItemDetails
{
    /// <summary>Gets the unique Premiumize item identifier.</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    /// <summary>Gets the display name of the item.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>Gets the item type, e.g. <c>"file"</c> or <c>"folder"</c>.</summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    /// <summary>Gets the size of the item in bytes.</summary>
    [JsonPropertyName("size")]
    public long Size { get; init; }

    /// <summary>Gets the MIME type of the item, if available.</summary>
    [JsonPropertyName("mime_type")]
    public string? MimeType { get; init; }

    /// <summary>Direct streaming link, valid for a limited period.</summary>
    [JsonPropertyName("link")]
    public string? Link { get; init; }

    /// <summary>Permanent stream link (if available).</summary>
    [JsonPropertyName("stream_link")]
    public string? StreamLink { get; init; }
}

/// <summary>Response returned by the <c>/directdl/create</c> endpoint.</summary>
public sealed class PremiumizeDirectDlResponse
{
    /// <summary>Gets the status of the directdl request.</summary>
    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    /// <summary>Gets the error message if status is not success.</summary>
    [JsonPropertyName("message")]
    public string? Message { get; init; }

    /// <summary>Gets the direct download/stream link when resolving a single file.</summary>
    [JsonPropertyName("location")]
    public string? Location { get; init; }

    /// <summary>Gets the filename of the single file resolved.</summary>
    [JsonPropertyName("filename")]
    public string? Filename { get; init; }

    /// <summary>Gets the total size in bytes.</summary>
    [JsonPropertyName("filesize")]
    public long? Filesize { get; init; }

    /// <summary>Gets the list of contained files for multi-file torrents.</summary>
    [JsonPropertyName("content")]
    public IReadOnlyList<PremiumizeDirectDlFile>? Content { get; init; }
}

/// <summary>Represents a single file within a DirectDL response.</summary>
public sealed class PremiumizeDirectDlFile
{
    /// <summary>Gets the relative path of the file inside the torrent.</summary>
    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;

    /// <summary>Gets the size of the file in bytes.</summary>
    [JsonPropertyName("size")]
    public long Size { get; init; }

    /// <summary>Gets the direct download link for this file.</summary>
    [JsonPropertyName("link")]
    public string? Link { get; init; }

    /// <summary>Gets the transcoded/direct stream link for this file.</summary>
    [JsonPropertyName("stream_link")]
    public string? StreamLink { get; init; }
}

/// <summary>Response from the Premiumize <c>/cache/check</c> endpoint.</summary>
public sealed class PremiumizeCacheCheckResponse
{
    /// <summary>Gets the status of the cache check.</summary>
    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    /// <summary>Gets boolean results corresponding to the queried hashes.</summary>
    [JsonPropertyName("response")]
    public IReadOnlyList<bool>? Response { get; init; }
}
