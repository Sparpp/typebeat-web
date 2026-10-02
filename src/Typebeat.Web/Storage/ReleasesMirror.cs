using System.Security.Cryptography;
using Typebeat.Web.Endpoints;

namespace Typebeat.Web.Storage;

/// <summary>
/// The box mirrors its own release downloads to the public bucket (backlog 380). Before this the
/// SHIP SCRIPTS uploaded every nupkg and installer to R2 beside the box copy, and a ship from a
/// shell without the four R2 variables silently landed on the box only, so every updater on a
/// Cloudflare host 404'd at the edge. Now the box is the single source of truth and this service
/// owns the bucket's <c>downloads/</c> prefix:
///
/// <list type="bullet">
/// <item>UPLOADS every file under <c>{root}/downloads/releases/</c> except the manifests
/// (<see cref="IsManifest"/>: RELEASES, releases.*.json, assets.*.json stay origin-served with
/// no-cache and are never mirrored) and every configured installer
/// (<see cref="GameDownloadKeys"/>) whose bucket object is missing or differs. "Differs" is the
/// size, then the content: a single-part PUT's ETag is the MD5 of its bytes, so a same-size
/// rewrite is caught by comparing it with the box file's MD5 (hashed once per file version, see
/// <see cref="ReleasesMirror.hashCache"/>). An object uploaded multipart (the AWS CLI and rclone
/// legs of backlog 364 did that above their part thresholds) carries an ETag that says nothing
/// about its content, so a size match is accepted for it and counted as unverified; that keeps the
/// first sweep idempotent against 364's backfill instead of re-uploading every package.</item>
/// <item>DELETES every object under <c>downloads/</c> whose box file no longer exists, so the
/// box's release prune (Invoke-BoxReleasePrune) propagates. Never when the box holds nothing
/// mirrorable at all: an empty or unmounted file root is not evidence that the bucket should be
/// emptied.</item>
/// <item>Never touches anything outside <c>downloads/</c> (packages/ is PackageIngest's).</item>
/// </list>
///
/// <para>Same HTTP metadata 364 chose: application/octet-stream everywhere, feed files
/// <see cref="PublicObjectMetadata.PackageCacheControl"/>, installers
/// <see cref="PublicObjectMetadata.InstallerCacheControl"/> with an attachment disposition.</para>
///
/// <para>WHEN: a sweep at start, then on every change under <c>{root}/downloads</c> (a
/// FileSystemWatcher, debounced, since a ship lands each file with docker cp to a dot-named temp
/// and an mv, and the temps are skipped) and every <see cref="ReleasesMirrorOptions.SweepInterval"/>
/// regardless, as the safety net for a missed event. Each operation is retried
/// <see cref="ReleasesMirrorOptions.Attempts"/> times; a file that still fails is reported on
/// GET /api/v2/ops/mirror (<see cref="ReleasesMirrorStatus"/>) and tried again on the next sweep.
/// One summary log line per sweep.</para>
///
/// <para>Off (returns at once) when the public store is disabled, like
/// <see cref="Packages.PublicPackageBackfill"/>. The /releases/*.nupkg redirect only fires once the
/// bucket holds the file (<see cref="GameInstallers.PublicReleaseSizeAsync"/>), so a mirror that is
/// behind or down costs box bandwidth, never a broken update.</para>
/// </summary>
public sealed class ReleasesMirror(
    IPublicObjectStore publicStore,
    ReleasesMirrorOptions options,
    ReleasesMirrorStatus status,
    ILogger<ReleasesMirror> logger,
    GameInstallers? installers = null) : BackgroundService
{
    /// <summary>The bucket (and box) prefix this service owns.</summary>
    public const string Prefix = "downloads/";

    /// <summary>
    /// The box file's MD5 by path, valid while its length and write time are unchanged (and
    /// dropped on any watcher event for the path), so a 200 MB package is hashed once per version
    /// rather than once per sweep.
    /// </summary>
    private readonly Dictionary<string, (long Length, long WriteTicks, string Md5)> hashCache = new(StringComparer.Ordinal);

    private readonly SemaphoreSlim changed = new(0, 1);

    /// <summary>
    /// A feed manifest: extensionless (RELEASES) or *.json (releases.*.json, assets.*.json). The
    /// same rule decides the box's no-cache header in MediaEndpoints, so what is never mirrored and
    /// what is never cached at the edge cannot drift apart.
    /// </summary>
    public static bool IsManifest(string file)
        => !file.Contains('.') || file.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

    /// <summary>A file still being landed: a dot-named temp (the ship's .x.tmp, CI's .incoming-x) or LocalFileStore's *.tmp.</summary>
    public static bool IsTemporary(string file)
        => file.StartsWith('.') || file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!publicStore.Enabled)
            return;

        // Let the host finish starting before any network traffic begins.
        await Task.Yield();

        using var watcher = startWatcher();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);

                // Wait for a change (then let the burst a ship makes settle) or the interval.
                if (await changed.WaitAsync(options.SweepInterval, stoppingToken))
                {
                    await Task.Delay(options.Debounce, stoppingToken);
                    changed.Wait(0);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                // SweepAsync records its own failures; this is the belt for anything it did not.
                logger.LogWarning(e, "Releases mirror: the sweep loop failed; retrying after the interval.");

                try
                {
                    await Task.Delay(options.SweepInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>
    /// One full pass: list the bucket's downloads/, upload what is missing or differs, delete what
    /// the box no longer holds. Public so tests (and nothing else) can drive a pass directly.
    /// Never throws for a bucket or file failure: they land in the result and in
    /// <see cref="ReleasesMirrorStatus"/>.
    /// </summary>
    public async Task<ReleasesMirrorSweep> SweepAsync(CancellationToken ct = default)
    {
        var startedAt = DateTimeOffset.UtcNow;
        long started = Environment.TickCount64;
        var failures = new List<ReleasesMirrorFailure>();

        if (!publicStore.Enabled)
            return status.Record(new ReleasesMirrorSweep(startedAt, 0, 0, 0, 0, 0, 0, 0, false, "the public store is disabled"), failures);

        string downloads = Path.Combine(options.FileRoot, "downloads");
        var desired = boxFiles(downloads);

        IReadOnlyList<PublicObjectInfo> listed;

        try
        {
            listed = await withRetryAsync(() => publicStore.ListAsync(Prefix, ct), ct);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var failed = new ReleasesMirrorSweep(startedAt, elapsed(started), desired.Count, 0, 0, 0, 0, 0, false,
                "could not list the bucket: " + e.Message);
            logger.LogWarning("Releases mirror: sweep FAILED, {Error}; the next sweep retries.", failed.Error);
            return status.Record(failed, status.Failing);
        }

        var bucket = listed.ToDictionary(o => o.Key, StringComparer.Ordinal);
        int uploaded = 0, unchanged = 0, unverified = 0, deleted = 0;

        foreach (var file in desired)
        {
            ct.ThrowIfCancellationRequested();

            Verdict verdict;

            try
            {
                verdict = await compareAsync(file, bucket.GetValueOrDefault(file.Key), ct);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The file went away or is locked mid-sweep: the next sweep sees the settled state.
                logger.LogDebug(e, "Releases mirror: could not read {Path}; skipped this sweep.", file.Path);
                continue;
            }

            if (verdict == Verdict.Same)
            {
                unchanged++;
                continue;
            }

            if (verdict == Verdict.SameSizeUnverifiable)
            {
                unchanged++;
                unverified++;
                continue;
            }

            if (await attemptAsync("upload", file.Key, failures, async () =>
                {
                    await using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                    await publicStore.PutAsync(file.Key, stream, "application/octet-stream", file.CacheControl, file.ContentDisposition, ct);
                }, ct))
                uploaded++;
        }

        // Propagate the box's deletions, unless the box looks empty: an unmounted or misconfigured
        // file root would otherwise empty the bucket.
        bool deletionsSkipped = desired.Count == 0;

        if (!deletionsSkipped)
        {
            foreach (var obj in listed)
            {
                ct.ThrowIfCancellationRequested();

                if (boxPathFor(downloads, obj.Key) is not { } path || File.Exists(path))
                    continue;

                if (await attemptAsync("delete", obj.Key, failures, () => publicStore.DeleteAsync(obj.Key, ct), ct))
                    deleted++;
            }
        }

        var sweep = new ReleasesMirrorSweep(startedAt, elapsed(started), desired.Count, uploaded, unchanged, unverified, deleted,
            failures.Count, deletionsSkipped, null);

        logger.LogInformation(
            "Releases mirror: {Files} box file(s), {Uploaded} uploaded, {Deleted} deleted, {Unchanged} unchanged ({Unverified} size-only), {Failed} failed{Skipped} in {Ms} ms.",
            sweep.Files, sweep.Uploaded, sweep.Deleted, sweep.Unchanged, sweep.Unverified, sweep.Failed,
            deletionsSkipped ? ", deletions skipped (the box holds nothing to mirror)" : "", sweep.DurationMs);

        return status.Record(sweep, failures);
    }

    // ---------------------------------------------------------------------------------------------

    private sealed record BoxFile(string Key, string Path, string CacheControl, string? ContentDisposition);

    private enum Verdict { Same, SameSizeUnverifiable, Differs }

    /// <summary>What the bucket should hold: every non-manifest feed file, and each configured installer that exists.</summary>
    private List<BoxFile> boxFiles(string downloads)
    {
        var files = new List<BoxFile>();
        string releases = Path.Combine(downloads, "releases");

        if (Directory.Exists(releases))
        {
            foreach (string path in Directory.EnumerateFiles(releases).Order(StringComparer.Ordinal))
            {
                string name = Path.GetFileName(path);

                if (IsManifest(name) || IsTemporary(name))
                    continue;

                files.Add(new BoxFile(StoreKeys.Release(name), path, PublicObjectMetadata.PackageCacheControl, null));
            }
        }

        foreach (string name in options.InstallerFileNames)
        {
            string path = Path.Combine(downloads, name);

            if (File.Exists(path))
                files.Add(new BoxFile(StoreKeys.Download(name), path, PublicObjectMetadata.InstallerCacheControl,
                    PublicObjectMetadata.AttachmentDisposition(name)));
        }

        return files;
    }

    /// <summary>The box path a bucket key under downloads/ mirrors, or null for a key that is not a plain relative path.</summary>
    private static string? boxPathFor(string downloads, string key)
    {
        if (!key.StartsWith(Prefix, StringComparison.Ordinal))
            return null;

        string relative = key[Prefix.Length..];

        if (relative.Length == 0 || relative.EndsWith('/') || relative.Contains('\\')
            || relative.Split('/').Any(s => s is "" or "." or ".."))
            return null;

        return Path.Combine(downloads, relative.Replace('/', Path.DirectorySeparatorChar));
    }

    private async Task<Verdict> compareAsync(BoxFile file, PublicObjectInfo? remote, CancellationToken ct)
    {
        if (remote == null)
            return Verdict.Differs;

        var info = new FileInfo(file.Path);

        if (remote.Size != info.Length)
            return Verdict.Differs;

        if (!isContentEtag(remote.ETag))
            return Verdict.SameSizeUnverifiable;

        string md5 = await md5Async(info, ct);
        return string.Equals(md5, remote.ETag, StringComparison.OrdinalIgnoreCase) ? Verdict.Same : Verdict.Differs;
    }

    /// <summary>A single-part ETag: exactly 32 hex digits, the MD5 of the object.</summary>
    private static bool isContentEtag(string? etag)
        => etag is { Length: 32 } && etag.All(Uri.IsHexDigit);

    private async Task<string> md5Async(FileInfo info, CancellationToken ct)
    {
        long ticks = info.LastWriteTimeUtc.Ticks;

        lock (hashCache)
        {
            if (hashCache.TryGetValue(info.FullName, out var cached) && cached.Length == info.Length && cached.WriteTicks == ticks)
                return cached.Md5;
        }

        await using var stream = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        string md5 = Convert.ToHexStringLower(await MD5.HashDataAsync(stream, ct));

        lock (hashCache)
            hashCache[info.FullName] = (info.Length, ticks, md5);

        return md5;
    }

    /// <summary>Runs one operation with retries; on the last failure records it and returns false.</summary>
    private async Task<bool> attemptAsync(string operation, string key, List<ReleasesMirrorFailure> failures, Func<Task> action, CancellationToken ct)
    {
        try
        {
            await withRetryAsync(async () =>
            {
                await action();
                return true;
            }, ct);

            installers?.Forget(key);
            return true;
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(e, "Releases mirror: {Operation} of {Key} failed {Attempts} time(s); the next sweep retries.",
                operation, key, options.Attempts);
            failures.Add(new ReleasesMirrorFailure(key, operation, e.Message, options.Attempts, DateTimeOffset.UtcNow));
            return false;
        }
    }

    private async Task<T> withRetryAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await action();
            }
            catch (Exception) when (attempt < options.Attempts && !ct.IsCancellationRequested)
            {
                if (options.RetryDelay > TimeSpan.Zero)
                    await Task.Delay(options.RetryDelay * attempt, ct);
            }
        }
    }

    private static long elapsed(long started) => Environment.TickCount64 - started;

    private FileSystemWatcher? startWatcher()
    {
        string downloads = Path.Combine(options.FileRoot, "downloads");

        try
        {
            Directory.CreateDirectory(downloads);

            var watcher = new FileSystemWatcher(downloads)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
            };

            watcher.Created += onChange;
            watcher.Changed += onChange;
            watcher.Deleted += onChange;
            watcher.Renamed += (_, e) =>
            {
                forget(e.OldFullPath);
                onChange(null, e);
            };
            // An overflowed buffer lost events: a sweep sees everything anyway.
            watcher.Error += (_, _) => signal();
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Releases mirror: could not watch {Path}; relying on the periodic sweep alone.", downloads);
            return null;
        }
    }

    private void onChange(object? sender, FileSystemEventArgs e)
    {
        forget(e.FullPath);
        signal();
    }

    private void forget(string path)
    {
        lock (hashCache)
            hashCache.Remove(Path.GetFullPath(path));
    }

    private void signal()
    {
        try
        {
            if (changed.CurrentCount == 0)
                changed.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already signalled: one pending sweep covers every change.
        }
    }

    public override void Dispose()
    {
        changed.Dispose();
        base.Dispose();
    }
}

/// <summary>The mirror's settings. <see cref="FromConfiguration"/> is the app's; tests build their own.</summary>
public sealed record ReleasesMirrorOptions(string FileRoot, IReadOnlyList<string> InstallerFileNames)
{
    /// <summary>The safety-net sweep, for a change the watcher missed.</summary>
    public TimeSpan SweepInterval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How long after a change the sweep waits, so a ship's burst of files is one sweep.</summary>
    public TimeSpan Debounce { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Tries per operation within one sweep (the first included).</summary>
    public int Attempts { get; init; } = 3;

    /// <summary>The pause before the second try; each later try waits one more multiple of it.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The app's file root (resolved as <see cref="LocalFileStore"/> resolves it) and the
    /// configured installer names: plain file names only, so a config value can never point the
    /// mirror outside downloads/.
    /// </summary>
    public static ReleasesMirrorOptions FromConfiguration(IConfiguration config)
        => new(OpsEndpoints.FileRoot(config), GameDownloadKeys.All
            .Select(k => config[k]?.Trim())
            .Where(n => !string.IsNullOrEmpty(n) && n == Path.GetFileName(n) && !n.Contains('/') && !n.Contains('\\') && n != "..")
            .Select(n => n!)
            .Distinct(StringComparer.Ordinal)
            .ToArray());
}

/// <summary>One sweep's outcome. <see cref="Error"/> is set only when the sweep could not run at all.</summary>
public sealed record ReleasesMirrorSweep(
    DateTimeOffset StartedAt,
    long DurationMs,
    int Files,
    int Uploaded,
    int Unchanged,
    int Unverified,
    int Deleted,
    int Failed,
    bool DeletionsSkipped,
    string? Error);

/// <summary>A key the last sweep could not upload or delete after every retry. <see cref="Since"/> is when it first failed.</summary>
public sealed record ReleasesMirrorFailure(string Key, string Operation, string Error, int Attempts, DateTimeOffset Since);

/// <summary>The last sweep and what is failing, for GET /api/v2/ops/mirror. Registered whether or not the mirror runs.</summary>
public sealed class ReleasesMirrorStatus
{
    private readonly object gate = new();
    private ReleasesMirrorSweep? last;
    private IReadOnlyList<ReleasesMirrorFailure> failing = [];

    public ReleasesMirrorSweep? LastSweep
    {
        get
        {
            lock (gate)
                return last;
        }
    }

    public IReadOnlyList<ReleasesMirrorFailure> Failing
    {
        get
        {
            lock (gate)
                return failing;
        }
    }

    /// <summary>
    /// Records a sweep. A key that was already failing keeps its first-failure time, so the ops
    /// readout says how long it has been stuck; a key that succeeded or went away drops out.
    /// </summary>
    public ReleasesMirrorSweep Record(ReleasesMirrorSweep sweep, IReadOnlyList<ReleasesMirrorFailure> failures)
    {
        lock (gate)
        {
            var previous = failing.ToDictionary(f => (f.Key, f.Operation));
            failing = failures
                .Select(f => previous.TryGetValue((f.Key, f.Operation), out var p) ? f with { Since = p.Since } : f)
                .ToArray();
            last = sweep;
        }

        return sweep;
    }
}
