using System.IO.Compression;
using System.Security.Cryptography;
using Dapper;
using Npgsql;
using Typebeat.Web.Data;
using Typebeat.Web.Storage;

namespace Typebeat.Web.Packages;

/// <summary>
/// Server half of the isolated-vocals-stem backfill (backlog 393). The editor's "Vocals Waveform"
/// toggle (backlog 392) reads a <c>vocals.ogg</c> (or <c>vocals.wav</c>) sitting beside a map's
/// audio, which the game writes locally when its importer runs Demucs. Maps imported before that
/// persistence existed carry no stem, so the toggle is inert on them. This exists to attach a stem
/// the LOCAL aligner produced to an already-stored set, so every player's ordinary UPDATE pulls it.
///
/// <para>
/// WHY IT IS RANK-SAFE, which is the whole point. A <c>set_versions</c> row is an immutable
/// snapshot and <c>version_files</c> its filename -&gt; sha256 manifest; the <c>files</c> table is
/// content-addressed and immutable. So attaching a stem is: store one new blob, cut a NEW version
/// whose manifest is the previous version's files PLUS <c>vocals.ogg</c>, and bump
/// <c>beatmapsets.current_version</c> and <c>updated_at</c>. Nothing the gameplay fingerprint reads
/// moves: <see cref="GameplayFingerprint"/> hashes exactly the SHA256 of the audio file each
/// difficulty points at (a blob this never rewrites) and the <c>[Lyrics]</c> section of the stored
/// .osu (bytes this never rewrites). A file no version referenced before is simply not in the
/// recipe. And because this path never calls
/// <see cref="PackageIngest"/>'s demotion check, a ranked set is not even a candidate for demotion.
/// Adding a <c>files</c> row changes nothing any ranking or scoring query reads.
/// </para>
///
/// <para>
/// WHY IT CUTS A NEW VERSION RATHER THAN EDITING THE LATEST. <c>set_versions</c> are immutable
/// snapshots everywhere else in the codebase (the BSS upload path only ever appends), and a
/// snapshot's <c>package_key</c> object must match its manifest or the download route serves a
/// package that disagrees with <c>version_files</c>. Re-snapshotting with every existing file plus
/// the stem keeps both true and is exactly the contract the client's updater reads.
/// </para>
///
/// <para>
/// THE PRECONDITION GUARD, and why it is here rather than assumed. The fingerprint is invariant
/// only if the audio a difficulty points at stays resolvable in the new manifest, which the ingest
/// already guarantees (the manifest is the whole package). Should a set's stored manifest ever
/// disagree with its .osu (hand-edited rows, a botched historical op), attaching a stem would
/// silently change what the fingerprint resolves, so this REFUSES rather than cut such a version.
/// </para>
/// </summary>
public static class StemBackfill
{
    /// <summary>The stem filename the game's <c>VocalsStem</c> looks for; the ogg is always used
    /// because the local producer here has a Vorbis encoder.</summary>
    public const string VocalsFilename = "vocals.ogg";

    /// <summary>The two names the game accepts, lowercase, for the "already has a stem" test.</summary>
    public static readonly string[] StemFilenames = ["vocals.ogg", "vocals.wav"];

    /// <summary>
    /// A hard ceiling on the stem this accepts (~16 MiB). A 16 kHz mono Vorbis render of a full
    /// song is a few hundred KB to a couple of MB, so anything past this is a wrong upload rather
    /// than a stem, and the endpoint reads the body into memory.
    /// </summary>
    public const long MaxStemBytes = 16L * 1024 * 1024;

    /// <summary>One set that has a current version, at least one live difficulty, and no stem file
    /// in that version's manifest: what the local pass has to produce a <c>vocals.ogg</c> for.</summary>
    public sealed record MissingStemRow(
        long SetId,
        string Artist,
        string Title,
        string Status,
        int CurrentVersion,
        int LiveDiffs,
        string AudioFilename,
        string AudioSha256Hex,
        long AudioSize);

    /// <summary>The outcome of an attach: the new (or existing) version and where the stem landed.</summary>
    public sealed record AttachResult(
        long SetId,
        int VersionNo,
        bool CutNewVersion,
        string Sha256Hex,
        string Filename);

    /// <summary>Raised when a set cannot carry a stem (missing, no version, or a manifest that does
    /// not resolve a difficulty's audio). The endpoint maps this to a 4xx, never a 500.</summary>
    public sealed class StemBackfillException(string message) : Exception(message);

    /// <summary>
    /// Enumerates the sets still lacking a stem: the current version's manifest has no
    /// <c>vocals.ogg</c>/<c>vocals.wav</c>. Only sets with a current version and at least one live
    /// difficulty are listed, because those are the only ones a client can UPDATE and the only ones
    /// whose stem the editor could ever read. The audio filename and its blob identity ride along so
    /// the local driver knows exactly what to feed Demucs (see
    /// <see cref="OpenAudioBlobAsync"/>). Sets whose manifest cannot be parsed are skipped and
    /// logged, so one broken row does not hide the rest.
    /// </summary>
    public static async Task<IReadOnlyList<MissingStemRow>> ListMissingStemAsync(
        Db db, IFileStore fileStore, ILogger logger, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);

        var candidates = (await conn.QueryAsync<(long SetId, string Artist, string Title, string Status, long VersionId, int CurrentVersion, long BeatmapId, string Filename)>(
                """
                SELECT s.id            AS SetId,
                       s.artist        AS Artist,
                       s.title         AS Title,
                       s.status        AS Status,
                       sv.id           AS VersionId,
                       s.current_version AS CurrentVersion,
                       b.id            AS BeatmapId,
                       b.filename      AS Filename
                FROM beatmapsets s
                JOIN set_versions sv ON sv.set_id = s.id AND sv.version_no = s.current_version
                JOIN beatmaps b ON b.set_id = s.id AND b.filename IS NOT NULL
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM set_versions sv2
                    JOIN version_files vf ON vf.version_id = sv2.id
                    WHERE sv2.set_id = s.id
                      AND sv2.version_no = s.current_version
                      AND lower(vf.filename) IN ('vocals.ogg', 'vocals.wav'))
                ORDER BY s.id, b.id
                """,
                new { }))
            .ToList();

        var manifests = new Dictionary<long, IReadOnlyList<PackageFileEntry>>();
        var result = new List<MissingStemRow>();

        foreach (var group in candidates.GroupBy(c => c.SetId))
        {
            try
            {
                var first = group.First();

                if (!manifests.TryGetValue(first.VersionId, out var manifest))
                {
                    manifest = await ReadManifestAsync(conn, first.VersionId, ct);
                    manifests[first.VersionId] = manifest;
                }

                var liveDiffs = group.Count();

                // The audio of the FIRST live difficulty speaks for the set, exactly as the primary
                // difficulty speaks for a set's metadata at ingest. A set whose .osu cannot be read
                // or whose audio is not in the manifest is skipped, not reported.
                var audio = await ResolveAudioAsync(fileStore, manifest, first.Filename, ct);

                if (audio == null)
                    continue;

                result.Add(new MissingStemRow(
                    first.SetId, first.Artist, first.Title, first.Status, first.CurrentVersion,
                    liveDiffs, audio.Filename, audio.Sha256Hex, audio.Size));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Stem backfill: set {SetId} could not be enumerated; skipping.", group.Key);
            }
        }

        return result;
    }

    /// <summary>
    /// Whether the set's CURRENT version carries a vocals stem file (a <c>version_files</c> row named
    /// <c>vocals.ogg</c> or <c>vocals.wav</c>). One indexed EXISTS scoped to the set, so the metadata
    /// lookup can answer it per set without touching blob storage. Follows
    /// <c>beatmapsets.current_version</c> (the same version the download route serves), not the
    /// highest version number, so a version cut that has not been published cannot report a stem the
    /// client would not actually download.
    /// </summary>
    public static async Task<bool> CurrentVersionHasStemAsync(Db db, long setId, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);

        // The two literals mirror StemFilenames (the download/attach contract); kept inline so the
        // name test is a plain indexed lookup the planner can use as-is.
        return await conn.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS (
                SELECT 1
                FROM beatmapsets s
                JOIN set_versions sv ON sv.set_id = s.id AND sv.version_no = s.current_version
                JOIN version_files vf ON vf.version_id = sv.id
                WHERE s.id = @setId
                  AND lower(vf.filename) IN ('vocals.ogg', 'vocals.wav'))
            """,
            new { setId });
    }

    /// <summary>
    /// Opens the current version's blob for the named file, so the ops endpoint can stream a set's
    /// audio to the local Demucs pass. Returns null when the file is not in the manifest.
    /// </summary>
    public static async Task<Stream?> OpenAudioBlobAsync(
        Db db, IFileStore fileStore, long setId, string sha256Hex, CancellationToken ct = default)
    {
        byte[] sha = Convert.FromHexString(sha256Hex);

        await using var conn = await db.OpenAsync(ct);

        bool inManifest = await conn.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS (
                SELECT 1
                FROM set_versions sv
                JOIN version_files vf ON vf.version_id = sv.id
                WHERE sv.set_id = @setId
                  AND sv.version_no = (SELECT MAX(version_no) FROM set_versions WHERE set_id = @setId)
                  AND vf.sha256 = @sha)
            """,
            new { setId, sha });

        if (!inManifest)
            return null;

        try
        {
            return await fileStore.OpenBlobReadAsync(sha, ct);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Attaches <paramref name="content"/> as <c>vocals.ogg</c> to the set's CURRENT version by
    /// cutting a fresh version that carries every existing file plus the stem, then bumping
    /// <c>current_version</c> and <c>updated_at</c>. Idempotent: a set whose current version already
    /// contains a stem returns <c>CutNewVersion = false</c> and writes nothing.
    ///
    /// <para>
    /// Runs under the same per-set advisory lock the ingest takes
    /// (<c>pg_advisory_xact_lock('bss-set-ingest:' || setId)</c>), so it serialises against a
    /// concurrent submission to the same set and cannot race a version cut. All rows join one
    /// transaction; the blob and the assembled download package are written before the commit, so a
    /// version row is never durable without its package object (the same invariant
    /// <see cref="PackageIngest"/> holds).
    /// </para>
    /// </summary>
    /// <exception cref="StemBackfillException">The set is missing, has no version, or its manifest
    /// does not resolve a live difficulty's audio.</exception>
    public static async Task<AttachResult> AttachVocalsStemAsync(
        Db db, IFileStore fileStore, long setId, byte[] content, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (content.Length == 0)
            throw new StemBackfillException("The vocals stem is empty.");

        if (content.LongLength > MaxStemBytes)
            throw new StemBackfillException($"The vocals stem is {content.LongLength} bytes, over the {MaxStemBytes} byte limit.");

        byte[] sha = SHA256.HashData(content);
        string filename = BeatmapPackageParser.NormalizeFilename(VocalsFilename);

        await using var conn = await db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // The ingest critical section: same key and shape as PackageIngest.BeginSetScopeAsync, so a
        // stem attach and a submission to this set fully serialise (xact-scoped, released at commit).
        await conn.ExecuteAsync(
            "SELECT pg_advisory_xact_lock(hashtextextended('bss-set-ingest:' || @setId::text, 0))",
            new { setId }, tx);

        var set = await conn.QuerySingleOrDefaultAsync<(int CurrentVersion, string Status)?>(
            "SELECT current_version AS CurrentVersion, status AS Status FROM beatmapsets WHERE id = @setId",
            new { setId }, tx);

        if (set is not { } row)
            throw new StemBackfillException($"Beatmap set {setId} does not exist.");

        var latest = await conn.QuerySingleOrDefaultAsync<(long Id, int VersionNo)?>(
            """
            SELECT id AS Id, version_no AS VersionNo
            FROM set_versions
            WHERE set_id = @setId
            ORDER BY version_no DESC
            LIMIT 1
            """,
            new { setId }, tx);

        if (latest is not { } latestVersion)
            throw new StemBackfillException($"Beatmap set {setId} has no uploaded version to attach a stem to.");

        var manifest = await ReadManifestAsync(conn, latestVersion.Id, ct, tx);

        // Already has a stem: nothing to do, and telling the caller the version it is on keeps a
        // re-run of the whole backfill a no-op rather than a version-cutting loop.
        if (manifest.Any(f => StemFilenames.Contains(f.Filename, StringComparer.OrdinalIgnoreCase)))
            return new AttachResult(setId, latestVersion.VersionNo, false, Convert.ToHexStringLower(sha), filename);

        // The precondition guard: the fingerprint is invariant only if every live difficulty's
        // audio stays resolvable, and it stays resolvable only if the manifest already names it (the
        // stem changes nothing about that). A manifest that disagrees with its .osu is refused here
        // rather than cut into a version whose fingerprint would resolve differently.
        var liveDiffs = (await conn.QueryAsync<(long Id, string Filename)>(
                "SELECT id AS Id, filename AS Filename FROM beatmaps WHERE set_id = @setId AND filename IS NOT NULL",
                new { setId }, tx))
            .ToList();

        foreach (var (diffId, diffFilename) in liveDiffs)
        {
            var audio = await ResolveAudioAsync(fileStore, manifest, diffFilename, ct);

            if (audio == null)
                throw new StemBackfillException(
                    $"Cannot attach a stem to set {setId}: difficulty {diffId} (\"{diffFilename}\") has an audio file the stored manifest does not resolve.");
        }

        // ---- store the blob + files row (content-addressed; an orphan from a later rollback is harmless). ----

        int inserted = await conn.ExecuteAsync(
            "INSERT INTO files (sha256, size) VALUES (@sha256, @size) ON CONFLICT (sha256) DO NOTHING",
            new { sha256 = sha, size = content.LongLength }, tx);

        if (inserted == 0)
        {
            long storedSize = await conn.ExecuteScalarAsync<long>(
                "SELECT size FROM files WHERE sha256 = @sha256", new { sha256 = sha }, tx);

            if (storedSize != content.LongLength)
                throw new StemBackfillException(
                    $"sha256 collision: stored size {storedSize} != incoming {content.LongLength} for {Convert.ToHexStringLower(sha)}.");
        }

        if (!await fileStore.BlobExistsAsync(sha, ct))
        {
            using var buffer = new MemoryStream(content, writable: false);
            await fileStore.WriteBlobIfAbsentAsync(sha, buffer, ct);
        }

        // ---- cut the new immutable version: the previous files PLUS the stem. ----

        int versionNo = latestVersion.VersionNo + 1;
        string packageKey = StoreKeys.Package(setId, versionNo);

        var newFiles = manifest.ToList();
        newFiles.Add(new PackageFileEntry(sha, content.LongLength, filename));

        long versionId = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO set_versions (set_id, version_no, uploader_id, package_key)
            VALUES (@setId, @versionNo, @uploaderId, @packageKey)
            RETURNING id
            """,
            new { setId, versionNo, uploaderId = (long?)null, packageKey }, tx);

        foreach (var file in newFiles)
        {
            await conn.ExecuteAsync(
                "INSERT INTO version_files (version_id, sha256, filename) VALUES (@versionId, @sha256, @filename)",
                new { versionId, sha256 = file.Sha256, filename = file.Filename }, tx);
        }

        // current_version drives the download route, the fingerprint/pace backfills and the version
        // rule, and updated_at is the update identity the client polls, so both move together.
        await conn.ExecuteAsync(
            "UPDATE beatmapsets SET current_version = @versionNo, updated_at = now() WHERE id = @setId",
            new { setId, versionNo }, tx);

        // ---- assemble the download package from the blobs BEFORE the commit, so the new version
        //      row is never durable with a package_key that has no object behind it. ----

        await assemblePackageAsync(fileStore, packageKey, newFiles, ct);

        await tx.CommitAsync(ct);

        return new AttachResult(setId, versionNo, true, Convert.ToHexStringLower(sha), filename);
    }

    /// <summary>
    /// The latest version's manifest as <see cref="PackageFileEntry"/>s, read on <paramref name="tx"/>
    /// when inside the critical section and on the bare connection otherwise.
    /// </summary>
    private static async Task<IReadOnlyList<PackageFileEntry>> ReadManifestAsync(
        NpgsqlConnection conn, long versionId, CancellationToken ct, NpgsqlTransaction? tx = null)
    {
        var rows = await conn.QueryAsync<(byte[] Sha256, long Size, string Filename)>(
            """
            SELECT vf.sha256 AS Sha256, f.size AS Size, vf.filename AS Filename
            FROM version_files vf
            JOIN files f ON f.sha256 = vf.sha256
            WHERE vf.version_id = @versionId
            ORDER BY vf.filename
            """,
            new { versionId }, tx);

        return rows.Select(r => new PackageFileEntry(r.Sha256, r.Size, r.Filename)).ToList();
    }

    /// <summary>The audio a difficulty points at, read from its stored .osu, or null when the .osu
    /// cannot be read or names no file the manifest contains.</summary>
    private static async Task<PackageFileEntry?> ResolveAudioAsync(
        IFileStore fileStore, IReadOnlyList<PackageFileEntry> manifest, string osuFilename, CancellationToken ct)
    {
        var osu = GameplayFingerprint.Resolve(manifest, osuFilename);

        if (osu == null)
            return null;

        ParsedDifficulty diff;

        try
        {
            byte[] bytes;

            await using (var blob = await fileStore.OpenBlobReadAsync(osu.Sha256, ct))
            using (var buffer = new MemoryStream())
            {
                await blob.CopyToAsync(buffer, ct);
                bytes = buffer.ToArray();
            }

            diff = BeatmapPackageParser.ParseDifficulty(osu.Filename, bytes);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }

        return GameplayFingerprint.Resolve(manifest, diff.AudioFilename);
    }

    /// <summary>
    /// Assembles the downloadable package zip for a version from the stored blobs (mirrors
    /// <see cref="PackageIngest"/>'s private assemble step: the manifest is the authority, and the
    /// download route opens <c>package_key</c>, so a version cut here needs its package object too).
    /// </summary>
    private static async Task assemblePackageAsync(
        IFileStore fileStore, string packageKey, IReadOnlyList<PackageFileEntry> files, CancellationToken ct)
    {
        using var buffer = new MemoryStream();

        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in files)
            {
                var entry = zip.CreateEntry(file.Filename, CompressionLevel.Optimal);

                await using var entryStream = entry.Open();
                await using var blob = await fileStore.OpenBlobReadAsync(file.Sha256, ct);
                await blob.CopyToAsync(entryStream, ct);
            }
        }

        buffer.Position = 0;
        await fileStore.WriteObjectAsync(packageKey, buffer, ct);
    }
}
