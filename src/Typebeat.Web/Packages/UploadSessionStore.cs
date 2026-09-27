using Newtonsoft.Json;
using Typebeat.Web.Storage;

namespace Typebeat.Web.Packages;

/// <summary>
/// Backing store for the BSS chunked upload sessions (POST /bss/beatmapsets/{id}/upload-sessions
/// and friends), rooted at {TYPEBEAT_FILE_ROOT}/upload-sessions (the /data volume in prod).
///
/// Why chunking exists at all: some users sit behind a middlebox that black-holes any single
/// request to this host once its body passes roughly 20 KB. Every attempt read exactly the same
/// prefix and then died on a read timeout, on BOTH the Cloudflare-proxied and the direct-origin
/// path, so no monolithic upload above that ceiling could ever complete for them. The session
/// protocol slices the SAME multipart body the direct routes take into small requests, each of
/// which stays well under the ceiling, and reassembles it server-side.
///
/// Layout, one directory per session:
///
///   {root}/upload-sessions/{sessionId}/meta.json      the session manifest (snake_case JSON)
///                                      chunk-000000   fixed-size payload slices, index-named
///                                      tmp-{guid}     in-flight chunk write, moved into place
///
/// Deliberately NOT on <see cref="IFileStore"/>: that interface has no enumeration, and both the
/// received-chunk list and the expiry sweep need to enumerate. Plain Directory/File APIs instead.
/// The root rides the persistent volume, so a
/// half-uploaded session survives an app restart or a deploy and the client can resume it rather
/// than starting the whole payload again.
/// </summary>
public sealed class UploadSessionStore
{
    /// <summary>
    /// Bytes per chunk. Comfortably under the observed ~20 KB per-connection ceiling with request
    /// headers, the multipart-free raw body framing and TLS overhead all counted, and small enough
    /// that a chunk that does die costs one cheap retry rather than a whole upload.
    /// </summary>
    public const int ChunkBytes = 8192;

    /// <summary>
    /// Live sessions one user may hold at once. A disk bound, not a refusal: creating past it
    /// evicts the user's oldest live session rather than failing, because a session can leak
    /// client-side (a flow that dies between its last chunk and complete) and a hard cap turned
    /// three such leaks into a 24 hour lockout of EVERY set the user owns.
    /// </summary>
    public const int MaxLiveSessionsPerUser = 3;

    /// <summary>How long a session stays resumable after its creation.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    private const string meta_file = "meta.json";
    private const string chunk_prefix = "chunk-";
    private const string temp_prefix = "tmp-";

    private readonly string root;

    public UploadSessionStore(IConfiguration config)
    {
        string? configured = config[LocalFileStore.RootConfigKey];

        root = Path.Combine(
            Path.GetFullPath(string.IsNullOrEmpty(configured) ? LocalFileStore.DefaultRoot : configured),
            "upload-sessions");
    }

    /// <summary>One upload session: the declared payload plus who may drive it.</summary>
    public sealed record Session(
        string SessionId,
        long UserId,
        long SetId,
        string Kind,
        string ContentType,
        long TotalBytes,
        string Sha256,
        DateTimeOffset CreatedAtUtc)
    {
        public int TotalChunks => (int)((TotalBytes + ChunkBytes - 1) / ChunkBytes);

        public DateTimeOffset ExpiresAt => CreatedAtUtc + Lifetime;

        public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt;

        /// <summary>
        /// The exact byte length the chunk at this index must carry: a full chunk everywhere but
        /// the last, which carries the remainder (and is a full chunk when the payload divides).
        /// </summary>
        public int ChunkLength(int index)
            => index == TotalChunks - 1 ? (int)(TotalBytes - (long)index * ChunkBytes) : ChunkBytes;
    }

    /// <summary>True for a well-formed session id (32 lowercase hex); everything else is a 404 at the routes.</summary>
    public static bool IsSessionId(string? value)
        => value is { Length: 32 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>
    /// Returns the caller's existing unexpired session for the same payload (same set, kind,
    /// sha256 and total_bytes) so a client that lost its session id can pick up where it left off,
    /// otherwise creates one. Creation is self-healing on two fronts, because a session can leak
    /// when a client flow dies between its last chunk and complete: a fresh declaration for the
    /// same set and kind SUPERSEDES any stale session for them (the client rebuilds its multipart
    /// payload per attempt, so the old payload can never complete once a new declaration exists),
    /// and a caller already holding <see cref="MaxLiveSessionsPerUser"/> live sessions has their
    /// oldest one evicted rather than being refused, so leaked sessions on one set can never lock
    /// the user out of another.
    /// </summary>
    public async Task<Session> CreateOrResumeAsync(
        long userId, long setId, string kind, string contentType, long totalBytes, string sha256, CancellationToken ct)
    {
        await SweepExpiredAsync(ct);

        var mine = (await liveSessionsAsync(ct))
                   .Where(s => s.UserId == userId)
                   .OrderBy(s => s.CreatedAtUtc)
                   .ThenBy(s => s.SessionId, StringComparer.Ordinal)
                   .ToList();

        foreach (var existing in mine)
        {
            if (existing.SetId == setId
                && string.Equals(existing.Kind, kind, StringComparison.Ordinal)
                && existing.TotalBytes == totalBytes
                && string.Equals(existing.Sha256, sha256, StringComparison.Ordinal))
            {
                return existing;
            }
        }

        // No resume, so this declaration replaces whatever it made stale, then whatever the disk
        // bound demands, oldest first.
        mine.RemoveAll(s =>
        {
            if (s.SetId != setId || !string.Equals(s.Kind, kind, StringComparison.Ordinal))
                return false;

            Delete(s);
            return true;
        });

        while (mine.Count >= MaxLiveSessionsPerUser)
        {
            Delete(mine[0]);
            mine.RemoveAt(0);
        }

        var session = new Session(
            Guid.NewGuid().ToString("N"), userId, setId, kind, contentType, totalBytes, sha256, DateTimeOffset.UtcNow);

        string dir = Path.Combine(root, session.SessionId);
        Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(Path.Combine(dir, meta_file), JsonConvert.SerializeObject(toMeta(session)), ct);

        return session;
    }

    /// <summary>The session with this id, or null when it is absent, expired or the id is malformed.</summary>
    public async Task<Session?> TryGetAsync(string? sessionId, CancellationToken ct)
    {
        if (!IsSessionId(sessionId))
            return null;

        var session = await readSessionAsync(Path.Combine(root, sessionId!), ct);

        return session is { IsExpired: false } && string.Equals(session.SessionId, sessionId, StringComparison.Ordinal)
            ? session
            : null;
    }

    /// <summary>The stored chunk indexes, ascending. Drives both the resume list and the completeness check.</summary>
    public IReadOnlyList<int> ReceivedIndexes(Session session)
    {
        var received = new List<int>();
        string dir = Path.Combine(root, session.SessionId);

        if (!Directory.Exists(dir))
            return received;

        foreach (string path in Directory.EnumerateFiles(dir, chunk_prefix + "*"))
        {
            string name = Path.GetFileName(path);

            if (int.TryParse(name.AsSpan(chunk_prefix.Length), out int index) && index >= 0 && index < session.TotalChunks)
                received.Add(index);
        }

        received.Sort();
        return received;
    }

    /// <summary>
    /// Stores one already-validated chunk. Written to a temp file and moved into place, so a
    /// chunk file is either absent or complete; a re-PUT of an index is an idempotent overwrite.
    /// </summary>
    public async Task WriteChunkAsync(Session session, int index, byte[] content, CancellationToken ct)
    {
        string dir = Path.Combine(root, session.SessionId);
        Directory.CreateDirectory(dir);

        string temp = Path.Combine(dir, temp_prefix + Guid.NewGuid().ToString("N"));

        try
        {
            await File.WriteAllBytesAsync(temp, content, ct);
            File.Move(temp, Path.Combine(dir, chunkName(index)), overwrite: true);
        }
        catch
        {
            File.Delete(temp);
            throw;
        }
    }

    /// <summary>Concatenates every chunk in index order into the caller's stream (the verbatim payload).</summary>
    public async Task AssembleAsync(Session session, Stream destination, CancellationToken ct)
    {
        string dir = Path.Combine(root, session.SessionId);

        for (int index = 0; index < session.TotalChunks; index++)
        {
            await using var chunk = new FileStream(
                Path.Combine(dir, chunkName(index)), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);

            await chunk.CopyToAsync(destination, ct);
        }
    }

    /// <summary>Drops a session and everything it buffered. No-op when it is already gone.</summary>
    public void Delete(Session session)
        => deleteDirectory(Path.Combine(root, session.SessionId));

    /// <summary>
    /// Deletes every session whose manifest says it has expired. Runs at the top of every create,
    /// which is the only moment anyone is guaranteed to be looking. Directories with no readable
    /// manifest are LEFT ALONE: one of them is a session being created right now, between its
    /// directory and its meta.json.
    /// </summary>
    public async Task SweepExpiredAsync(CancellationToken ct)
    {
        if (!Directory.Exists(root))
            return;

        foreach (string dir in Directory.EnumerateDirectories(root))
        {
            if (await readSessionAsync(dir, ct) is { IsExpired: true })
                deleteDirectory(dir);
        }
    }

    private async Task<List<Session>> liveSessionsAsync(CancellationToken ct)
    {
        var live = new List<Session>();

        if (!Directory.Exists(root))
            return live;

        foreach (string dir in Directory.EnumerateDirectories(root))
        {
            if (await readSessionAsync(dir, ct) is { IsExpired: false } session)
                live.Add(session);
        }

        return live;
    }

    private static async Task<Session?> readSessionAsync(string dir, CancellationToken ct)
    {
        string path = Path.Combine(dir, meta_file);

        try
        {
            if (!File.Exists(path))
                return null;

            var meta = JsonConvert.DeserializeObject<SessionMeta>(await File.ReadAllTextAsync(path, ct));

            if (meta?.SessionId == null || meta.Kind == null || meta.ContentType == null || meta.Sha256 == null)
                return null;

            return new Session(
                meta.SessionId, meta.UserId, meta.SetId, meta.Kind, meta.ContentType,
                meta.TotalBytes, meta.Sha256, meta.CreatedAtUtc);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // A torn or half-written manifest reads as "not a session"; the sweep leaves it alone
            // (it may be one mid-creation) and it expires with its own directory.
            return null;
        }
    }

    private static void deleteDirectory(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Raced another sweep, or a file is momentarily locked. Either way it is not this
            // caller's problem: the next sweep picks it up again.
        }
    }

    private static string chunkName(int index) => chunk_prefix + index.ToString("D6");

    private static SessionMeta toMeta(Session session) => new SessionMeta
    {
        SessionId = session.SessionId,
        UserId = session.UserId,
        SetId = session.SetId,
        Kind = session.Kind,
        ContentType = session.ContentType,
        TotalBytes = session.TotalBytes,
        Sha256 = session.Sha256,
        CreatedAtUtc = session.CreatedAtUtc,
    };

    /// <summary>The on-disk manifest shape. Snake_case, like every other file protocol here.</summary>
    private sealed class SessionMeta
    {
        [JsonProperty("session_id")]
        public string? SessionId { get; set; }

        [JsonProperty("user_id")]
        public long UserId { get; set; }

        [JsonProperty("set_id")]
        public long SetId { get; set; }

        [JsonProperty("kind")]
        public string? Kind { get; set; }

        [JsonProperty("content_type")]
        public string? ContentType { get; set; }

        [JsonProperty("total_bytes")]
        public long TotalBytes { get; set; }

        [JsonProperty("sha256")]
        public string? Sha256 { get; set; }

        [JsonProperty("created_at_utc")]
        public DateTimeOffset CreatedAtUtc { get; set; }
    }
}
