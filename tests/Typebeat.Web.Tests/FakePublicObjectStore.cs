using System.Collections.Concurrent;
using System.Security.Cryptography;
using Typebeat.Web.Storage;

namespace Typebeat.Web.Tests;

/// <summary>
/// An in-memory <see cref="IPublicObjectStore"/> for the backlog 364 tests. It starts DISABLED, and
/// while disabled it answers exactly as <see cref="DisabledPublicObjectStore"/> does, so a host that
/// carries it behaves as an unconfigured deployment until a test switches it on (and back off in
/// its teardown).
/// </summary>
public sealed class FakePublicObjectStore : IPublicObjectStore
{
    public const string PublicBaseUrl = "https://dl.example.test";

    /// <summary>
    /// One object. <paramref name="ETag"/> is what a listing reports: the hex MD5 of the bytes, as
    /// R2 answers for a single-part PUT, unless a test seeds a multipart-shaped one.
    /// </summary>
    public sealed record StoredObject(byte[] Bytes, string ContentType, string CacheControl, string? ContentDisposition, string ETag);

    public bool Enabled { get; set; }

    public string Description => Enabled ? "fake bucket" : "disabled (fake)";

    /// <summary>Every live object by key.</summary>
    public ConcurrentDictionary<string, StoredObject> Objects { get; } = new(StringComparer.Ordinal);

    /// <summary>Every PUT and DELETE in order, as "PUT key" / "DELETE key".</summary>
    public ConcurrentQueue<string> Log { get; } = new();

    /// <summary>When set, every PUT throws (the "bucket unreachable" case).</summary>
    public bool FailPuts { get; set; }

    /// <summary>Runs inside each PUT before it lands, so a test can look at the world at that moment.</summary>
    public Func<string, Task>? OnPut { get; set; }

    public IReadOnlySet<string> DirectHosts { get; } =
        new HashSet<string>(PublicObjectStoreOptions.DefaultDirectHosts, StringComparer.OrdinalIgnoreCase);

    public async Task PutAsync(string key, Stream content, string contentType, string cacheControl, string? contentDisposition, CancellationToken ct = default)
    {
        if (!Enabled)
            throw new InvalidOperationException("The public object store is disabled.");

        if (OnPut != null)
            await OnPut(key);

        if (FailPuts)
            throw new IOException("fake bucket unreachable");

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);

        byte[] bytes = buffer.ToArray();
        Objects[key] = new StoredObject(bytes, contentType, cacheControl, contentDisposition, md5(bytes));
        Log.Enqueue("PUT " + key);
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        if (!Enabled)
            throw new InvalidOperationException("The public object store is disabled.");

        Objects.TryRemove(key, out _);
        Log.Enqueue("DELETE " + key);
        return Task.CompletedTask;
    }

    public Task<long?> StatAsync(string key, CancellationToken ct = default)
        => Task.FromResult(Enabled && Objects.TryGetValue(key, out var o) ? (long?)o.Bytes.LongLength : null);

    public Task<IReadOnlyList<PublicObjectInfo>> ListAsync(string prefix, CancellationToken ct = default)
    {
        if (!Enabled)
            return Task.FromResult<IReadOnlyList<PublicObjectInfo>>([]);

        if (FailLists)
            throw new IOException("fake bucket unreachable");

        return Task.FromResult<IReadOnlyList<PublicObjectInfo>>(Objects
            .Where(o => o.Key.StartsWith(prefix, StringComparison.Ordinal))
            .OrderBy(o => o.Key, StringComparer.Ordinal)
            .Select(o => new PublicObjectInfo(o.Key, o.Value.Bytes.LongLength, o.Value.ETag))
            .ToArray());
    }

    /// <summary>When set, every listing throws.</summary>
    public bool FailLists { get; set; }

    public string PublicUrl(string key)
        => Enabled
            ? PublicObjectStoreOptions.BuildPublicUrl(PublicBaseUrl, key)
            : throw new InvalidOperationException("The public object store is disabled.");

    public bool IsDirectHost(string host) => !Enabled || DirectHosts.Contains(host);

    /// <summary>Back to the unconfigured state.</summary>
    public void Reset()
    {
        Enabled = false;
        FailPuts = false;
        FailLists = false;
        OnPut = null;
        Objects.Clear();
        Log.Clear();
    }

    /// <summary>
    /// Stores an object directly, as a ship script would (no metadata checks). <paramref name="etag"/>
    /// defaults to the bytes' MD5; pass a "{hash}-{parts}" one for a multipart upload.
    /// </summary>
    public void Seed(string key, byte[] bytes, string cacheControl = "no-cache", string? etag = null)
        => Objects[key] = new StoredObject(bytes, "application/octet-stream", cacheControl, null, etag ?? md5(bytes));

    private static string md5(byte[] bytes) => Convert.ToHexStringLower(MD5.HashData(bytes));
}
