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
    public Task<long?> PublicSizeAsync(string fileName, CancellationToken ct = default)
        => PublicKeySizeAsync(StoreKeys.Download(fileName), ct);

    /// <summary>
    /// The same answer for one Velopack feed file (downloads/releases/{file}), which is what the
    /// /releases/*.nupkg redirect asks since backlog 380: a nupkg redirects only once the bucket
    /// holds it, so the window between a ship landing on the box and the mirror uploading it (or a
    /// mirror that is down) streams from the box instead of 404ing at the edge.
    /// </summary>
    public Task<long?> PublicReleaseSizeAsync(string file, CancellationToken ct = default)
        => PublicKeySizeAsync(StoreKeys.Release(file), ct);

    /// <summary>The cached stat of any public key; see <see cref="PublicSizeAsync"/>.</summary>
    public async Task<long?> PublicKeySizeAsync(string key, CancellationToken ct = default)
    {
        if (!publicStore.Enabled)
            return null;

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
            logger.LogWarning(e, "Public store stat failed for {Key}; falling back to the local copy.", key);
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

    /// <summary>
    /// Forgets one key's cached stat. The releases mirror calls it after every PUT and DELETE it
    /// makes, so a redirect never outlives the object it points at by a cache period.
    /// </summary>
    public void Forget(string key) => statCache.TryRemove(key, out _);
}
