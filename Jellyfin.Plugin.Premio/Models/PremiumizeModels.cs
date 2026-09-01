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
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary><c>"file"</c> or <c>"folder"</c>.</summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("mime_type")]
    public string? MimeType { get; init; }

    [JsonPropertyName("size")]
    public long Size { get; init; }

    [JsonPropertyName("created_at")]
    public long CreatedAt { get; init; }

    [JsonPropertyName("folder_id")]
    public string? FolderId { get; init; }
}

/// <summary>Payload for the <c>/folder/search</c> endpoint.</summary>
public sealed class PremiumizeSearchContent
{
    [JsonPropertyName("files")]
    public IReadOnlyList<PremiumizeSearchItem> Files { get; init; } = [];
}

/// <summary>Payload for the <c>/item/details</c> endpoint.</summary>
public sealed class PremiumizeItemDetails
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; init; }

    [JsonPropertyName("mime_type")]
    public string? MimeType { get; init; }

    /// <summary>Direct streaming link, valid for a limited period.</summary>
    [JsonPropertyName("link")]
    public string? Link { get; init; }

    /// <summary>Permanent stream link (if available).</summary>
    [JsonPropertyName("stream_link")]
    public string? StreamLink { get; init; }
}
