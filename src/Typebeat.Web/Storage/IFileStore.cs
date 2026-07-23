namespace Typebeat.Web.Storage;

/// <summary>
/// Blob storage behind the upload/download pipeline. Two kinds of object live here:
///
///  - <b>content-addressed blobs</b>: immutable, write-once, keyed by the SHA-256 of their bytes
///    (canonical key <c>files/{sha256hex}</c>). One blob per unique file across all beatmap sets
///    and versions; the R2-shaped layout from osu-server-beatmap-submission.
///  - <b>named objects</b>: mutable, keyed by a caller-chosen path (covers, previews, assembled
///    download packages). Use <see cref="StoreKeys"/> for the canonical key shapes.
///
/// The interface deals only in keys and streams so an R2/S3 implementation can slot in without
/// touching any caller (M3 ships <see cref="LocalFileStore"/>; R2 is a later milestone).
/// </summary>
public interface IFileStore
{
    /// <summary>
    /// Stores a content-addressed blob unless it already exists. The caller vouches that
    /// <paramref name="sha256"/> is the hash of <paramref name="content"/>'s bytes.
    /// </summary>
    /// <returns>true if the blob was written; false if it already existed (content skipped).</returns>
    Task<bool> WriteBlobIfAbsentAsync(byte[] sha256, Stream content, CancellationToken ct = default);

    Task<bool> BlobExistsAsync(byte[] sha256, CancellationToken ct = default);

    /// <summary>Opens a content-addressed blob for sequential reading.</summary>
    /// <exception cref="FileNotFoundException">No blob with this hash is stored.</exception>
    Task<Stream> OpenBlobReadAsync(byte[] sha256, CancellationToken ct = default);

    /// <summary>Creates or replaces a named object.</summary>
    Task WriteObjectAsync(string key, Stream content, CancellationToken ct = default);

    Task<bool> ObjectExistsAsync(string key, CancellationToken ct = default);

    /// <summary>Opens a named object for sequential reading, or null when it does not exist.</summary>
    Task<Stream?> OpenObjectReadAsync(string key, CancellationToken ct = default);

    /// <summary>Removes a named object; no-op when absent. Blobs are write-once and never deleted here.</summary>
    Task DeleteObjectAsync(string key, CancellationToken ct = default);
}

/// <summary>
/// Canonical object-key shapes (single authority: endpoints, ingest and the website must all
/// build keys through these so a future R2 bucket has one coherent layout).
/// </summary>
public static class StoreKeys
{
    /// <summary>Content-addressed blob key: <c>files/{sha256hex}</c> (lowercase hex).</summary>
    public static string Blob(byte[] sha256) => $"files/{Convert.ToHexStringLower(sha256)}";

    /// <summary>One cover bucket per set version: <c>covers/{setId}/{versionNo}/{name}.jpg</c>.</summary>
    public static string Cover(long setId, int versionNo, string name) => $"covers/{setId}/{versionNo}/{name}.jpg";

    /// <summary>The prefix stored in <c>beatmapsets.cover_key</c>; append <c>/{name}.jpg</c>.</summary>
    public static string CoverPrefix(long setId, int versionNo) => $"covers/{setId}/{versionNo}";

    public static string Preview(long setId) => $"previews/{setId}.mp3";

    /// <summary>Profile avatar, version-stamped so a replacement gets a fresh immutable URL:
    /// <c>avatars/{userId}/{version}.jpg</c>. Stored verbatim in <c>users.avatar_key</c>.</summary>
    public static string Avatar(long userId, long version) => $"avatars/{userId}/{version}.jpg";

    /// <summary>Profile banner/cover, version-stamped like the avatar:
    /// <c>user-covers/{userId}/{version}.jpg</c>. Stored verbatim in <c>users.cover_key</c>.</summary>
    public static string UserCover(long userId, long version) => $"user-covers/{userId}/{version}.jpg";

    /// <summary>Assembled full package for a set version (stored in <c>set_versions.package_key</c>);
    /// <c>.typb</c> is the game's native package extension.</summary>
    public static string Package(long setId, int versionNo) => $"packages/{setId}/{versionNo}.typb";

    /// <summary>Game-client release artifacts live under <c>downloads/{fileName}</c> (TYPEBEAT_GAME_DOWNLOAD).</summary>
    public static string Download(string fileName) => $"downloads/{fileName}";

    /// <summary>Velopack update-feed assets (manifest + packages): <c>downloads/releases/{fileName}</c>.</summary>
    public static string Release(string fileName) => $"downloads/releases/{fileName}";
}
