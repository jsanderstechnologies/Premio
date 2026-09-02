using System;
using System.Collections.Concurrent;
using Jellyfin.Plugin.Premio.Models;

namespace Jellyfin.Plugin.Premio.Services;

/// <summary>
/// Thread-safe in-memory cache for mapping temporary/virtual Premio item GUIDs to TMDB metadata and poster images.
/// </summary>
public static class PremioMetadataCache
{
    private static readonly ConcurrentDictionary<Guid, Uri> PosterMap = new();
    private static readonly ConcurrentDictionary<Guid, byte[]> ImageCache = new();
    private static readonly ConcurrentDictionary<Guid, TmdbItem> ItemMap = new();

    /// <summary>
    /// Registers a TMDB item and its poster URI under the specified unique item GUID.
    /// </summary>
    /// <param name="id">Deterministic item GUID.</param>
    /// <param name="item">TMDB item metadata.</param>
    public static void Register(Guid id, TmdbItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        ItemMap[id] = item;
        if (item.PosterUrl is not null)
        {
            PosterMap[id] = item.PosterUrl;
        }
    }

    /// <summary>
    /// Attempts to retrieve the poster URI associated with the specified item GUID.
    /// </summary>
    /// <param name="id">Item GUID.</param>
    /// <param name="posterUri">Output poster URI.</param>
    /// <returns><c>true</c> if found; otherwise, <c>false</c>.</returns>
    public static bool TryGetPosterUri(Guid id, out Uri? posterUri)
    {
        return PosterMap.TryGetValue(id, out posterUri);
    }

    /// <summary>
    /// Attempts to retrieve cached raw image bytes for an item.
    /// </summary>
    /// <param name="id">Item GUID.</param>
    /// <param name="bytes">Output byte array.</param>
    /// <returns><c>true</c> if cached in memory; otherwise, <c>false</c>.</returns>
    public static bool TryGetImageBytes(Guid id, out byte[]? bytes)
    {
        return ImageCache.TryGetValue(id, out bytes);
    }

    /// <summary>
    /// Stores downloaded image bytes into the memory cache.
    /// </summary>
    /// <param name="id">Item GUID.</param>
    /// <param name="bytes">Raw image bytes.</param>
    public static void SetImageBytes(Guid id, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ImageCache[id] = bytes;
    }

    /// <summary>
    /// Attempts to retrieve the cached TMDB item.
    /// </summary>
    /// <param name="id">Item GUID.</param>
    /// <param name="item">Output TMDB item.</param>
    /// <returns><c>true</c> if found; otherwise, <c>false</c>.</returns>
    public static bool TryGetItem(Guid id, out TmdbItem? item)
    {
        return ItemMap.TryGetValue(id, out item);
    }
}
