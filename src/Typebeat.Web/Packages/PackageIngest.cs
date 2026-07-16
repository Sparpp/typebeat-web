using System.IO.Compression;
using Dapper;
using Npgsql;
using Typebeat.Web.Data;
using Typebeat.Web.Storage;

namespace Typebeat.Web.Packages;

/// <summary>
/// Writes a validated package into the versioned upload store — the server half of the BSS
/// versioning contract (osu-server-beatmap-submission's updateBeatmapSetFromArchiveAsync shape,
/// per recon result.bss.versioning):
///
///  - <c>files</c> is a global content-addressed dedup table (insert-if-absent by sha256);
///  - <c>set_versions</c> are immutable snapshots; <c>version_files</c> is the per-version
///    filename -> hash manifest;
///  - if the incoming (sha256, size, filename) set equals the latest version's, NO new version is
///    cut — only <c>beatmapsets.updated_at</c> is touched;
///  - otherwise a new version is cut, blobs stored, set metadata + search vector refreshed,
///    beatmap rows upserted by their allocated ids, and the full package zip assembled for the
///    website download path.
///
/// Cover/preview generation runs after the version commit and can only degrade the result
/// (statuses in <see cref="IngestResult"/>), never fail the upload.
/// </summary>
public sealed class PackageIngest(
    Db db,
    IFileStore fileStore,
    CoverGenerator coverGenerator,
    PreviewGenerator previewGenerator,
    ILogger<PackageIngest> logger)
{
    /// <summary>
    /// The weighted search-vector expression. MUST stay in sync with the backfill UPDATE in
    /// Data/Migrations/002_website_uploads.sql. Parameters: none beyond the row aliases
    /// (beatmapsets s, users u).
    /// </summary>
    public const string SearchVectorSql =
        """
        setweight(to_tsvector('simple', coalesce(s.title, '')), 'A')
        || setweight(to_tsvector('simple', coalesce(s.title_unicode, '')), 'A')
        || setweight(to_tsvector('simple', coalesce(s.artist, '')), 'B')
        || setweight(to_tsvector('simple', coalesce(s.artist_unicode, '')), 'B')
        || setweight(to_tsvector('simple', coalesce(u.username::text, '')), 'C')
        || setweight(to_tsvector('simple', coalesce(s.tags, '')), 'D')
        || setweight(to_tsvector('simple', coalesce(s.source, '')), 'D')
        """;

    public sealed record IngestResult(
        long SetId,
        int VersionNo,
        bool CutNewVersion,
        IReadOnlyList<PackageFileEntry> Files,
        string CoverStatus,
        string PreviewStatus);

    /// <summary>
    /// Ingests a parsed + validated package. The caller must have run
    /// <see cref="PackageValidator.Validate"/> first (this method trusts embedded ids) and must
    /// pass the same seekable zip stream that was parsed.
    /// </summary>
    public async Task<IngestResult> IngestAsync(
        Stream packageStream,
        ParsedPackage package,
        long setId,
        long uploaderId,
        CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Serialize concurrent submissions to one set: version_no assignment and the
        // latest-version comparison below both race without this lock.
        var owner = await conn.QuerySingleOrDefaultAsync<(long OwnerId, string Username)>(
            """
            SELECT s.owner_id AS OwnerId, u.username::text AS Username
            FROM beatmapsets s
            JOIN users u ON u.id = s.owner_id
            WHERE s.id = @setId
            FOR UPDATE OF s
            """,
            new { setId });

        if (owner == default)
            throw new InvalidOperationException($"Beatmap set {setId} does not exist.");

        var latest = await conn.QuerySingleOrDefaultAsync<(long VersionId, int VersionNo)?>(
            """
            SELECT id AS VersionId, version_no AS VersionNo
            FROM set_versions
            WHERE set_id = @setId
            ORDER BY version_no DESC
            LIMIT 1
            """,
            new { setId });

        // The identity a version snapshot is defined by: (sha256, size, filename), all of them
        // (osu-server-beatmap-submission PackageFileEqualityComparer).
        var incoming = package.Files
                              .Select(f => (f.Sha256Hex, f.Size, f.Filename))
                              .ToHashSet();

        if (latest is { } latestVersion)
        {
            var current = (await conn.QueryAsync<(byte[] Sha256, long Size, string Filename)>(
                    """
                    SELECT vf.sha256 AS Sha256, f.size AS Size, vf.filename AS Filename
                    FROM version_files vf
                    JOIN files f ON f.sha256 = vf.sha256
                    WHERE vf.version_id = @versionId
                    """,
                    new { versionId = latestVersion.VersionId }))
                .Select(f => (Convert.ToHexStringLower(f.Sha256), f.Size, f.Filename))
                .ToHashSet();

            if (current.SetEquals(incoming))
            {
                // Identical content: no new version — just record that the set was touched.
                await conn.ExecuteAsync(
                    "UPDATE beatmapsets SET updated_at = now() WHERE id = @setId",
                    new { setId });
                await tx.CommitAsync(ct);

                return new IngestResult(setId, latestVersion.VersionNo, false, package.Files, "unchanged", "unchanged");
            }
        }

        // ---- dedup-insert files rows + store blobs (content-addressed; orphans are harmless
        //      if the transaction later rolls back, so blob writes need no compensation). ----

        using var archive = openArchive(packageStream);

        foreach (var file in package.Files)
        {
            int inserted = await conn.ExecuteAsync(
                "INSERT INTO files (sha256, size) VALUES (@sha256, @size) ON CONFLICT (sha256) DO NOTHING",
                new { sha256 = file.Sha256, size = file.Size });

            if (inserted == 0)
            {
                long storedSize = await conn.ExecuteScalarAsync<long>(
                    "SELECT size FROM files WHERE sha256 = @sha256",
                    new { sha256 = file.Sha256 });

                if (storedSize != file.Size)
                    throw new InvalidOperationException($"sha256 collision: stored size {storedSize} != incoming {file.Size} for {file.Sha256Hex}.");
            }

            if (!await fileStore.BlobExistsAsync(file.Sha256, ct))
            {
                await using var entryStream = openEntry(archive, file.Filename);
                await fileStore.WriteBlobIfAbsentAsync(file.Sha256, entryStream, ct);
            }
        }

        // ---- cut the new immutable version + manifest. ----

        int versionNo = (latest?.VersionNo ?? 0) + 1;
        string packageKey = StoreKeys.Package(setId, versionNo);

        long versionId = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO set_versions (set_id, version_no, uploader_id, package_key)
            VALUES (@setId, @versionNo, @uploaderId, @packageKey)
            RETURNING id
            """,
            new { setId, versionNo, uploaderId, packageKey });

        foreach (var file in package.Files)
        {
            await conn.ExecuteAsync(
                "INSERT INTO version_files (version_id, sha256, filename) VALUES (@versionId, @sha256, @filename)",
                new { versionId, sha256 = file.Sha256, filename = file.Filename });
        }

        // ---- set metadata (package-driven; recon result.bss.metadata_rules) + search vector. ----

        var primary = package.Difficulties[0];

        await conn.ExecuteAsync(
            """
            UPDATE beatmapsets
            SET title = @title,
                title_unicode = @titleUnicode,
                artist = @artist,
                artist_unicode = @artistUnicode,
                source = @source,
                tags = @tags,
                has_video = @hasVideo,
                bpm = @bpm,
                current_version = @versionNo,
                updated_at = now()
            WHERE id = @setId
            """,
            new
            {
                setId,
                title = primary.Title,
                titleUnicode = primary.TitleUnicode,
                artist = primary.Artist,
                artistUnicode = primary.ArtistUnicode,
                source = primary.Source,
                tags = primary.Tags,
                hasVideo = package.HasVideo,
                bpm = primary.Bpm,
                versionNo,
            });

        // Search vector in a SECOND statement: SET expressions read the row's PRE-update values,
        // so folding this into the UPDATE above would index the previous metadata.
        await conn.ExecuteAsync(
            $"""
             UPDATE beatmapsets s
             SET search = {SearchVectorSql}
             FROM users u
             WHERE u.id = s.owner_id AND s.id = @setId
             """,
            new { setId });

        // ---- upsert beatmap rows by their allocated ids. ----

        foreach (var diff in package.Difficulties)
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO beatmaps
                    (id, set_id, version_name, ruleset_id, checksum_md5, total_length_s, drain_length_s,
                     difficulty_rating, filename, word_count, char_count, wpm)
                VALUES
                    (@id, @setId, @versionName, 0, @checksumMd5, @totalLengthS, @drainLengthS,
                     @difficultyRating, @filename, @wordCount, @charCount, @wpm)
                ON CONFLICT (id) DO UPDATE
                SET set_id = EXCLUDED.set_id,
                    version_name = EXCLUDED.version_name,
                    checksum_md5 = EXCLUDED.checksum_md5,
                    total_length_s = EXCLUDED.total_length_s,
                    drain_length_s = EXCLUDED.drain_length_s,
                    difficulty_rating = EXCLUDED.difficulty_rating,
                    filename = EXCLUDED.filename,
                    word_count = EXCLUDED.word_count,
                    char_count = EXCLUDED.char_count,
                    wpm = EXCLUDED.wpm
                """,
                new
                {
                    id = diff.BeatmapId!.Value,
                    setId,
                    versionName = diff.VersionName,
                    checksumMd5 = diff.ChecksumMd5,
                    totalLengthS = diff.TotalLengthS,
                    drainLengthS = diff.DrainLengthS,
                    difficultyRating = diff.Pace.DifficultyRating,
                    filename = diff.Filename,
                    wordCount = diff.Pace.WordCount,
                    charCount = diff.Pace.TypeableCellCount,
                    wpm = diff.Pace.AverageWpm,
                });
        }

        // Explicit-id inserts bypass the serial sequence; realign it so future allocations
        // (nextval) can never collide with an id written here.
        await conn.ExecuteAsync(
            "SELECT setval(pg_get_serial_sequence('beatmaps', 'id'), (SELECT COALESCE(MAX(id), 1) FROM beatmaps))");

        await tx.CommitAsync(ct);

        // ---- post-commit artifacts: download package, covers, preview. Failures here degrade
        //      the result but the version is already durable. ----

        await assemblePackageAsync(packageKey, package.Files, ct);

        string coverStatus = await generateCoversAsync(conn, package, primary, setId, versionNo, ct);
        string previewStatus = await generatePreviewAsync(conn, package, primary, setId, ct);

        return new IngestResult(setId, versionNo, true, package.Files, coverStatus, previewStatus);
    }

    /// <summary>
    /// The latest version's manifest for a set — the <c>files[]</c> list the BSS PUT response
    /// carries (empty for a set with no versions, which drives the client's replace-vs-patch
    /// branch). Exposed here so the endpoints module reuses one definition.
    /// </summary>
    public async Task<IReadOnlyList<PackageFileEntry>> GetLatestVersionFilesAsync(long setId, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);

        var rows = await conn.QueryAsync<(byte[] Sha256, long Size, string Filename)>(
            """
            SELECT vf.sha256 AS Sha256, f.size AS Size, vf.filename AS Filename
            FROM set_versions sv
            JOIN version_files vf ON vf.version_id = sv.id
            JOIN files f ON f.sha256 = vf.sha256
            WHERE sv.set_id = @setId
              AND sv.version_no = (SELECT MAX(version_no) FROM set_versions WHERE set_id = @setId)
            ORDER BY vf.filename
            """,
            new { setId });

        return rows.Select(r => new PackageFileEntry(r.Sha256, r.Size, r.Filename)).ToList();
    }

    /// <summary>
    /// Assembles the downloadable package zip from the stored blobs (NOT the upload bytes: the
    /// PATCH flow has no full incoming archive, so blob reassembly is the one path that always
    /// works — and it proves the blobs round-trip).
    /// </summary>
    private async Task assemblePackageAsync(string packageKey, IReadOnlyList<PackageFileEntry> files, CancellationToken ct)
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

    private async Task<string> generateCoversAsync(
        NpgsqlConnection conn, ParsedPackage package, ParsedDifficulty primary, long setId, int versionNo, CancellationToken ct)
    {
        var background = findFile(package, primary.BackgroundFilename)
                         ?? package.Difficulties.Select(d => findFile(package, d.BackgroundFilename)).FirstOrDefault(f => f != null);

        if (background == null)
            return "no_background";

        try
        {
            await using var blob = await fileStore.OpenBlobReadAsync(background.Sha256, ct);
            string coverKey = await coverGenerator.GenerateAsync(blob, setId, versionNo, fileStore, ct);

            await conn.ExecuteAsync(
                "UPDATE beatmapsets SET cover_key = @coverKey WHERE id = @setId",
                new { coverKey, setId });

            return "generated";
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "Cover generation failed for set {SetId} v{VersionNo}.", setId, versionNo);
            return "failed";
        }
    }

    private async Task<string> generatePreviewAsync(
        NpgsqlConnection conn, ParsedPackage package, ParsedDifficulty primary, long setId, CancellationToken ct)
    {
        var audio = findFile(package, primary.AudioFilename);

        if (audio == null)
            return "no_audio"; // Unreachable after validation; kept for PATCH-era safety.

        try
        {
            await using var blob = await fileStore.OpenBlobReadAsync(audio.Sha256, ct);
            var status = await previewGenerator.GenerateAsync(blob, primary.PreviewTime, setId, fileStore, ct);

            if (status == PreviewGenerator.Status.Generated)
            {
                await conn.ExecuteAsync(
                    "UPDATE beatmapsets SET preview_key = @previewKey WHERE id = @setId",
                    new { previewKey = StoreKeys.Preview(setId), setId });

                return "generated";
            }

            if (status == PreviewGenerator.Status.FfmpegMissing)
                logger.LogWarning("ffmpeg not available; no preview for set {SetId}.", setId);

            return status == PreviewGenerator.Status.FfmpegMissing ? "ffmpeg_missing" : "failed";
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "Preview generation failed for set {SetId}.", setId);
            return "failed";
        }
    }

    private static PackageFileEntry? findFile(ParsedPackage package, string? filename)
    {
        if (string.IsNullOrEmpty(filename))
            return null;

        string normalized = BeatmapPackageParser.NormalizeFilename(filename);
        return package.Files.FirstOrDefault(f => string.Equals(f.Filename, normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static ZipArchive openArchive(Stream packageStream)
    {
        packageStream.Position = 0;
        return new ZipArchive(packageStream, ZipArchiveMode.Read, leaveOpen: true);
    }

    private static Stream openEntry(ZipArchive archive, string filename)
    {
        var entry = archive.Entries.FirstOrDefault(e => BeatmapPackageParser.NormalizeFilename(e.FullName) == filename)
                    ?? throw new InvalidOperationException($"Entry \"{filename}\" vanished from the archive between parse and ingest.");

        return entry.Open();
    }
}
