using System.Collections.Concurrent;

namespace Typebeat.Web.Storage;

/// <summary>
/// Where each installer (downloads/{TYPEBEAT_GAME_DOWNLOAD*}) can be had, and how big it is: the
/// one answer the /download/game* endpoints, the /download page and the landing CTA share.
///
/// <para>With the public store disabled this is exactly the local store, the behaviour before
/// backlog 364. Enabled, the bucket's copy is preferred (that is what a Cloudflare-host request is
/// redirected to) and the local file still answers when the bucket has none. The bucket's answer
/// is a network HEAD, and the landing page asks on every request, so a stat is cached for
/// <see cref="StatCacheDuration"/>; a ship replaces an installer under the same name, so the
/// cached answer is at worst a minute stale on the size caption, never a dead link.</para>
/// </summary>
public sealed class GameInstallers(IFileStore store, IPublicObjectStore publicStore, ILogger<GameInstallers> logger)
{
    public static readonly TimeSpan StatCacheDuration = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<string, (long? Size, long ExpiresAt)> statCache = new(StringComparer.Ordinal);

    /// <summary>
    /// The bucket copy's size, or null when the store is disabled, the object is absent, or the
    /// stat failed (logged, not cached: the local file is the fallback either way).
    /// </summary>
    public async Task<long?> PublicSizeAsync(string fileName, CancellationToken ct = default)
    {
        if (!publicStore.Enabled)
            return null;

        string key = StoreKeys.Download(fileName);
        long now = Environment.TickCount64;

        if (statCache.TryGetValue(key, out var cached) && cached.ExpiresAt > now)
            return cached.Size;

        long? size;

        try
        {
            size = await publicStore.StatAsync(key, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "Public store stat failed for {Key}; falling back to the local installer.", key);
            return null;
        }

        statCache[key] = (size, now + (long)StatCacheDuration.TotalMilliseconds);
        return size;
    }

    /// <summary>The installer's size (bucket copy first when enabled, then the local file), or null when neither has it.</summary>
    public async Task<long?> SizeAsync(string fileName, CancellationToken ct = default)
    {
        if (await PublicSizeAsync(fileName, ct) is { } publicSize)
            return publicSize;

        // A seekable read stream is the cheapest way to both confirm the object exists and size it.
        await using var stream = await store.OpenObjectReadAsync(StoreKeys.Download(fileName), ct);
        return stream?.Length;
    }

    /// <summary>True when either the bucket or the local store holds the installer.</summary>
    public async Task<bool> ExistsAsync(string fileName, CancellationToken ct = default)
        => await PublicSizeAsync(fileName, ct) is not null
           || await store.ObjectExistsAsync(StoreKeys.Download(fileName), ct);

    /// <summary>Forgets every cached stat (tests, and nothing else, need this).</summary>
    public void ClearCache() => statCache.Clear();
}
