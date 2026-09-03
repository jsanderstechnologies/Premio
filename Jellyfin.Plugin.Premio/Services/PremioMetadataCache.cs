using System;
using System.Collections.Concurrent;
using Jellyfin.Plugin.Premio.Models;

namespace Jellyfin.Plugin.Premio.Services;

/// <summary>
/// Thread-safe in-memory cache for mapping temporary/virtual Premio item GUIDs to TMDB metadata, posters, and backdrops.
/// </summary>
public static class PremioMetadataCache
{
    private static readonly ConcurrentDictionary<Guid, Uri> PosterMap = new();
    private static readonly ConcurrentDictionary<Guid, Uri> BackdropMap = new();
    private static readonly ConcurrentDictionary<Guid, byte[]> ImageCache = new();
    private static readonly ConcurrentDictionary<Guid, byte[]> BackdropCache = new();
    private static readonly ConcurrentDictionary<Guid, TmdbItem> ItemMap = new();

    /// <summary>
    /// Registers a TMDB item, its poster URI, and backdrop URI under the specified unique item GUID.
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

        if (item.BackdropUrl is not null)
        {
            BackdropMap[id] = item.BackdropUrl;
        }
    }

    /// <summary>
    /// Registers a backdrop URI for an item.
    /// </summary>
    /// <param name="id">Item GUID.</param>
    /// <param name="backdropUri">Backdrop image URI.</param>
    public static void RegisterBackdrop(Guid id, Uri backdropUri)
    {
        ArgumentNullException.ThrowIfNull(backdropUri);
        BackdropMap[id] = backdropUri;
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
    /// Attempts to retrieve the backdrop URI associated with the specified item GUID.
    /// </summary>
    /// <param name="id">Item GUID.</param>
    /// <param name="backdropUri">Output backdrop URI.</param>
    /// <returns><c>true</c> if found; otherwise, <c>false</c>.</returns>
    public static bool TryGetBackdropUri(Guid id, out Uri? backdropUri)
    {
        return BackdropMap.TryGetValue(id, out backdropUri);
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
    /// Attempts to retrieve cached raw backdrop bytes for an item.
    /// </summary>
    /// <param name="id">Item GUID.</param>
    /// <param name="bytes">Output byte array.</param>
    /// <returns><c>true</c> if cached in memory; otherwise, <c>false</c>.</returns>
    public static bool TryGetBackdropBytes(Guid id, out byte[]? bytes)
    {
        return BackdropCache.TryGetValue(id, out bytes);
    }

    /// <summary>
    /// Stores downloaded backdrop bytes into the memory cache.
    /// </summary>
    /// <param name="id">Item GUID.</param>
    /// <param name="bytes">Raw backdrop bytes.</param>
    public static void SetBackdropBytes(Guid id, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        BackdropCache[id] = bytes;
    }

    private static readonly ConcurrentDictionary<Guid, string> StreamHashMap = new();

    /// <summary>
    /// Registers a torrent infohash under a deterministic stream GUID.
    /// </summary>
    /// <param name="id">Stream GUID.</param>
    /// <param name="infoHash">Torrent infohash.</param>
    public static void RegisterStreamHash(Guid id, string infoHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(infoHash);
        StreamHashMap[id] = infoHash;
    }

    /// <summary>
    /// Attempts to retrieve the torrent infohash associated with the stream GUID.
    /// </summary>
    /// <param name="id">Stream GUID.</param>
    /// <param name="infoHash">Output torrent infohash.</param>
    /// <returns><c>true</c> if found; otherwise, <c>false</c>.</returns>
    public static bool TryGetStreamHash(Guid id, out string? infoHash)
    {
        return StreamHashMap.TryGetValue(id, out infoHash);
    }

    /// <summary>
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

    private static readonly ConcurrentDictionary<string, string> EpisodeChosenHashMap = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registers the chosen torrent infohash for a specific TV episode.
    /// </summary>
    /// <param name="showTitle">Series title.</param>
    /// <param name="season">Season number.</param>
    /// <param name="episode">Episode number.</param>
    /// <param name="infoHash">Torrent infohash.</param>
    public static void RegisterChosenEpisodeStream(string showTitle, int season, int episode, string infoHash)
    {
        if (string.IsNullOrWhiteSpace(showTitle) || string.IsNullOrWhiteSpace(infoHash))
        {
            return;
        }

        var key = $"{showTitle.Trim()}:{season}:{episode}";
        EpisodeChosenHashMap[key] = infoHash;
    }

    /// <summary>
    /// Attempts to retrieve the chosen torrent infohash for a specific TV episode.
    /// </summary>
    /// <param name="showTitle">Series title.</param>
    /// <param name="season">Season number.</param>
    /// <param name="episode">Episode number.</param>
    /// <param name="infoHash">Output torrent infohash.</param>
    /// <returns><c>true</c> if found; otherwise, <c>false</c>.</returns>
    public static bool TryGetChosenEpisodeStream(string showTitle, int season, int episode, out string? infoHash)
    {
        if (string.IsNullOrWhiteSpace(showTitle))
        {
            infoHash = null;
            return false;
        }

        var key = $"{showTitle.Trim()}:{season}:{episode}";
        return EpisodeChosenHashMap.TryGetValue(key, out infoHash);
    }
}
