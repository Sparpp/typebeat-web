using System.IO.Compression;
using Dapper;
using Npgsql;
using Typebeat.Web.Data;
using Typebeat.Web.Packages.Lyrics;
using Typebeat.Web.Social;
using Typebeat.Web.Storage;

namespace Typebeat.Web.Packages;

/// <summary>
/// Writes a validated package into the versioned upload store, the server half of the BSS
/// versioning contract (osu-server-beatmap-submission's updateBeatmapSetFromArchiveAsync shape,
/// per recon result.bss.versioning):
///
///  - <c>files</c> is a global content-addressed dedup table (insert-if-absent by sha256);
///  - <c>set_versions</c> are immutable snapshots; <c>version_files</c> is the per-version
///    filename -> hash manifest;
///  - if the incoming (sha256, size, filename) set equals the latest version's, NO new version is
///    cut, only <c>beatmapsets.updated_at</c> is touched;
///  - otherwise a new version is cut, blobs stored, set metadata + search vector refreshed,
///    beatmap rows upserted by their allocated ids, and the full package zip assembled for the
///    website download path;
///  - and a RANKED set whose gameplay moved is demoted back to 'pending' for re-review
///    (030_gameplay_fingerprint.sql), which is the one status write here that is not the
///    once-per-set publish latch.
///
/// Concurrency + crash-safety doctrine (the two invariants everything below hangs off):
///
///  1. <b>The whole per-set ingest path is one critical section.</b> Callers enter through
///     <see cref="BeginSetScopeAsync"/>, which takes <c>pg_advisory_xact_lock</c> on the set id
///     inside a single transaction. Base-manifest reads (the PATCH rebuild base), the version
///     cut, beatmap upserts, the diff-liveness refresh, the hidden→public publish flip and all
///     artifact publication (package/covers/preview + their key updates) happen under that one
///     lock/transaction, so two submissions to the same set can never interleave; the second
///     fully observes the first or fully precedes it.
///  2. <b>A set_versions row is only ever committed with its download package already durable
///     in the store.</b> The package zip (and covers/preview) are written BEFORE the commit; a
///     crash or client abort anywhere rolls the version back, leaving only orphaned
///     content-addressed blobs / versioned objects, which are harmless and simply overwritten
///     by the next successful attempt at the same version number. The identical-content
///     fast path additionally re-verifies the package object and reassembles it from
///     <c>version_files</c> if missing, healing any row created before this invariant existed.
///
/// Cover/preview generation failures (ffmpeg missing, bad image, ...) can only degrade the
/// result (statuses in <see cref="IngestResult"/>), never fail the upload.
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
    /// The per-set ingest critical section: one connection + transaction holding
    /// <c>pg_advisory_xact_lock</c> on the set id, so everything a submission does, reading the
    /// rebuild-base manifest, cutting the version, publishing artifacts, is serialized against
    /// every other submission to the same set. Disposal without <see cref="IngestAsync"/> having
    /// committed rolls everything back (transaction disposal aborts it), which also releases the
    /// advisory lock.
    /// </summary>
    public sealed class SetScope : IAsyncDisposable
    {
        /// <summary>
        /// The scope's connection, for callers that need additional reads inside the same
        /// critical section (e.g. the allocated-id snapshot validation runs against). Any
        /// statement executed here joins the scope's open transaction.
        /// </summary>
        public NpgsqlConnection Connection { get; }

        internal NpgsqlTransaction Transaction { get; }

        internal SetScope(NpgsqlConnection connection, NpgsqlTransaction transaction)
        {
            Connection = connection;
            Transaction = transaction;
        }

        internal async Task CommitAsync(CancellationToken ct) => await Transaction.CommitAsync(ct);

        public async ValueTask DisposeAsync()
        {
            await Transaction.DisposeAsync(); // rolls back unless committed
            await Connection.DisposeAsync();
        }
    }

    /// <summary>
    /// Opens the ingest critical section for one set. Blocks until any in-flight submission to
    /// the same set fully finishes (commit or rollback); different sets don't contend.
    /// </summary>
    public async Task<SetScope> BeginSetScopeAsync(long setId, CancellationToken ct = default)
    {
        var conn = await db.OpenAsync(ct);

        try
        {
            var tx = await conn.BeginTransactionAsync(ct);

            // Namespaced advisory lock (xact-scoped: released at commit/rollback, so a crashed
            // request can never leak it). hashtextextended keeps the 64-bit key space distinct
            // from any other advisory-lock user of this database.
            await conn.ExecuteAsync(
                "SELECT pg_advisory_xact_lock(hashtextextended('bss-set-ingest:' || @setId::text, 0))",
                new { setId });

            return new SetScope(conn, tx);
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Ingests a parsed + validated package inside <paramref name="scope"/> and commits it. The
    /// caller must have run <see cref="PackageValidator.Validate"/> first (this method trusts
    /// embedded ids) and must pass the same seekable zip stream that was parsed.
    /// </summary>
    public async Task<IngestResult> IngestAsync(
        SetScope scope,
        Stream packageStream,
        ParsedPackage package,
        long setId,
        long uploaderId,
        CancellationToken ct = default)
    {
        var conn = scope.Connection;

        var set = await conn.QuerySingleOrDefaultAsync<(long OwnerId, string Status)?>(
            "SELECT owner_id AS OwnerId, status AS Status FROM beatmapsets WHERE id = @setId", new { setId });

        if (set == null)
            throw new InvalidOperationException($"Beatmap set {setId} does not exist.");

        long ownerId = set.Value.OwnerId;

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
            var currentFiles = (await conn.QueryAsync<(byte[] Sha256, long Size, string Filename)>(
                    """
                    SELECT vf.sha256 AS Sha256, f.size AS Size, vf.filename AS Filename
                    FROM version_files vf
                    JOIN files f ON f.sha256 = vf.sha256
                    WHERE vf.version_id = @versionId
                    """,
                    new { versionId = latestVersion.VersionId }))
                .Select(f => new PackageFileEntry(f.Sha256, f.Size, f.Filename))
                .ToList();

            var current = currentFiles.Select(f => (f.Sha256Hex, f.Size, f.Filename)).ToHashSet();

            if (current.SetEquals(incoming))
            {
                // Identical content: no new version, just record that the set was touched.
                await conn.ExecuteAsync(
                    "UPDATE beatmapsets SET updated_at = now() WHERE id = @setId",
                    new { setId });

                // Still refresh liveness + publish: a retry after an interrupted first upload
                // lands here and must finish the job (publish the set, settle diff liveness,
                // notify watchers). An identical resubmission of an ALREADY published set flips
                // nothing, so it notifies nobody, which is the whole point of the latch.
                await refreshLivenessAndPublishAsync(conn, package, setId, ownerId, ct);

                // Repair path: versions recorded before the assemble-before-commit invariant
                // existed (or damaged by operator error) can have a package_key with no object
                // behind it. An identical resubmission is the mapper's natural "it's broken,
                // re-upload" action, so verify and reassemble from the stored manifest here
                // rather than letting the download 404 forever.
                string packageKeyForLatest = StoreKeys.Package(setId, latestVersion.VersionNo);

                if (!await fileStore.ObjectExistsAsync(packageKeyForLatest, ct))
                    await assemblePackageAsync(packageKeyForLatest, currentFiles, ct);

                await scope.CommitAsync(ct);

                return new IngestResult(setId, latestVersion.VersionNo, false, package.Files, "unchanged", "unchanged");
            }
        }

        // ---- the gameplay-fingerprint snapshot, taken BEFORE anything below overwrites it
        //      (030_gameplay_fingerprint.sql, backlog 173). Only this branch can reach it: an
        //      identical-content resubmission returned above, and identical content cannot have
        //      moved a fingerprint.
        //
        //      The predicate is "has a fingerprint", NOT "is live", and the difference is the
        //      whole reason a REMOVED difficulty is detectable here. The BSS metadata PUT nulls
        //      beatmaps.filename for dropped diffs in an EARLIER request than this upload, so by
        //      now filename can no longer say what the previous version contained. The fingerprint
        //      can: it is cleared in the same statement that clears filename during THIS ingest
        //      (refreshLivenessAndPublishAsync), so a non-NULL value means exactly "live in the
        //      version this upload is about to replace". ----

        var previousFingerprints = (await conn.QueryAsync<(long Id, string Fingerprint)>(
                """
                SELECT id AS Id, gameplay_fingerprint AS Fingerprint
                FROM beatmaps
                WHERE set_id = @setId AND gameplay_fingerprint IS NOT NULL
                """,
                new { setId }))
            .ToDictionary(r => r.Id, r => r.Fingerprint);

        var incomingFingerprints = package.Difficulties.ToDictionary(
            d => d.BeatmapId!.Value,
            d => GameplayFingerprint.Compute(d, package.Files));

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
                -- Song language (019_language.sql). A package that reports NO language (every
                -- pre-task-58 client, which never wrote the [Metadata] Language: line) must LEAVE
                -- the stored value alone rather than clearing it: an old client re-submitting a
                -- map would otherwise wipe whatever the mapper or the backfill had established.
                -- SET expressions read the row's pre-update value, so this reads the old language.
                language = coalesce(nullif(@language, ''), language),
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
                // Folded onto the canonical vocabulary here, so an unrecognised or "unspecified"
                // value from any client lands as '' (= leave whatever is stored) rather than in
                // the column. The primary difficulty speaks for the set, exactly as it does for
                // title/artist/source/tags.
                language = BeatmapLanguages.Normalize(primary.Language),
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
                     difficulty_rating, filename, word_count, char_count, wpm, pace_version, skippable_s, lyrics,
                     sr_dt, sr_ht, sr_literate, sr_literate_dt, sr_literate_ht,
                     peak_wpm, peak_cpm, target_wpm, wpm_curve, gameplay_fingerprint, freestyle_cell_count,
                     ratings)
                VALUES
                    (@id, @setId, @versionName, 0, @checksumMd5, @totalLengthS, @drainLengthS,
                     @difficultyRating, @filename, @wordCount, @charCount, @wpm, @paceVersion, @skippableS, @lyrics,
                     @srDt, @srHt, @srLiterate, @srLiterateDt, @srLiterateHt,
                     @peakWpm, @peakCpm, @targetWpm, @wpmCurve, @gameplayFingerprint, @freestyleCellCount,
                     @ratings::jsonb)
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
                    wpm = EXCLUDED.wpm,
                    pace_version = EXCLUDED.pace_version,
                    skippable_s = EXCLUDED.skippable_s,
                    lyrics = EXCLUDED.lyrics,
                    sr_dt = EXCLUDED.sr_dt,
                    sr_ht = EXCLUDED.sr_ht,
                    sr_literate = EXCLUDED.sr_literate,
                    sr_literate_dt = EXCLUDED.sr_literate_dt,
                    sr_literate_ht = EXCLUDED.sr_literate_ht,
                    peak_wpm = EXCLUDED.peak_wpm,
                    peak_cpm = EXCLUDED.peak_cpm,
                    target_wpm = EXCLUDED.target_wpm,
                    wpm_curve = EXCLUDED.wpm_curve,
                    gameplay_fingerprint = EXCLUDED.gameplay_fingerprint,
                    freestyle_cell_count = EXCLUDED.freestyle_cell_count,
                    ratings = EXCLUDED.ratings;

                -- A re-upload can move this difficulty's star ratings, and the stored per-score pp
                -- is a function of them, so hand every score set on this map back to PpBackfill
                -- (020_performance_points.sql). Same invalidation PaceBackfill performs; without it
                -- a re-uploaded map would keep pricing its old plays at the previous difficulty.
                UPDATE scores SET pp_version = 0 WHERE beatmap_id = @id AND pp_version <> 0;
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
                    paceVersion = LyricPace.VERSION,
                    // What the in-game skip button may legally remove from this map; the
                    // play-time gate's allowance (Scoring/PlayTimeGate).
                    skippableS = diff.SkippableS,
                    // The lyrics: search operator's haystack, also rendered by the set page's
                    // lyrics section (018_lyrics_search.sql).
                    lyrics = diff.LyricsText,
                    // The star ratings at Double Time's and Half Time's BASE clock rates, the only
                    // rate ratings pp ever needs (020_performance_points.sql, docs/pp.md). Written
                    // here so no rate maths ever happens at query time.
                    srDt = diff.SrDoubleTime,
                    srHt = diff.SrHalfTime,
                    // The same three for the LITERATE-CONVERTED map (029_literate_stars.sql).
                    // Literate is a conversion mod and is priced through its rating rather than a
                    // flat multiplier (backlog 144); it is orthogonal to the rate, so the ratings
                    // are a cross product and each combination is stored.
                    srLiterate = diff.SrLiterate,
                    srLiterateDt = diff.SrLiterateDoubleTime,
                    srLiterateHt = diff.SrLiterateHalfTime,
                    // The rolling-window pace the set page's WPM tab graphs (028_wpm_curve.sql).
                    // All three are NULL for a map too short to measure, which renders as no graph.
                    peakWpm = diff.PeakWpm,
                    peakCpm = diff.PeakCpm,
                    // The target pace the pages headline (033_target_wpm.sql). NOT on the curve's
                    // contract: it is a per-line figure, so it survives on maps far too short to
                    // sweep and is NULL only where beatmaps.wpm itself would be meaningless.
                    targetWpm = diff.TargetWpm,
                    wpmCurve = diff.WpmCurvePoints,
                    // The identity of this difficulty's GAMEPLAY (030_gameplay_fingerprint.sql):
                    // the [Lyrics] timing payload minus the menu-only beatdrop, plus the hash of
                    // the audio it points at. Compared against the snapshot above to decide
                    // whether this upload demotes a ranked set.
                    gameplayFingerprint = incomingFingerprints[diff.BeatmapId!.Value],
                    // How many freestyle slots the map carries (031_freestyle_cell_count.sql).
                    // Since LyricPace v21 an ADDITION to charCount rather than a subset of it, the
                    // pace having stopped counting any-key cells, and always written (0 for a map
                    // with no flagged freestyle line); NULL there means only that the pace backfill
                    // has not reached the row yet.
                    freestyleCellCount = diff.Pace.FreestyleCellCount,
                    // THE RATING MATRIX (034_ratings_matrix.sql): the eighteen (judgement arm,
                    // stream, rate) readings, each carrying its star rating AND the map's difficult
                    // characters, which is what pp prices a play from since PerformancePoints v22.
                    // The six rating columns above are its arm-none stars and stay written for the
                    // pages, the client lookup and the search filters; neither is derived from the
                    // other, both come off the same parse. EIGHTEEN FULL PASSES over the map's
                    // words, so it is the most expensive thing this upsert asks for, and it is
                    // asked for once per difficulty (BeatmapPackage.Ratings caches).
                    ratings = diff.RatingsJson,
                });
        }

        // NOTE: deliberately NO sequence realignment here. The explicit-id upserts above can
        // only ever reuse ids that PUT /bss/beatmapsets previously allocated via nextval
        // (PackageValidator pins embedded ids to allocated rows), so the serial sequence is
        // always already at or past every id written in this transaction. The historical
        // setval(MAX(id)) "realignment" was removed as actively harmful: setval is
        // non-transactional and a READ COMMITTED MAX(id) cannot see other transactions'
        // uncommitted allocations, so under concurrency it could REWIND the sequence and make
        // later allocations 500 on primary-key collisions.

        // ---- the ranked demotion, still inside the transaction so a rolled-back upload can
        //      never leave a set demoted for content that was never stored. Runs BEFORE the
        //      liveness refresh only for readability; the comparison itself reads the snapshot
        //      taken above, so the order of these two is not load-bearing. ----

        await demoteIfGameplayChangedAsync(
            conn, setId, set.Value.Status, uploaderId, versionNo, previousFingerprints, incomingFingerprints);

        // ---- diff liveness + publish, still inside the transaction (crossing submissions must
        //      never NULL a diff the newer current version contains). ----

        await refreshLivenessAndPublishAsync(conn, package, setId, ownerId, ct);

        // ---- artifacts: download package, covers, preview, BEFORE the commit, so a version
        //      row is only ever durable with its package object already in the store (a crash
        //      or client abort here rolls the version back; orphaned blobs/objects are harmless
        //      and overwritten by the next attempt at this version number). Cover/preview
        //      failures are caught and degrade the result; their key updates join this
        //      transaction, so cover_key/preview_key can never point at artifacts of a version
        //      that did not commit. ----

        await assemblePackageAsync(packageKey, package.Files, ct);

        string coverStatus = await generateCoversAsync(conn, package, primary, setId, versionNo, ct);
        string previewStatus = await generatePreviewAsync(conn, package, primary, setId, ct);

        await scope.CommitAsync(ct);

        // ---- disk hygiene (after the commit; best-effort): assembled per-version packages are
        //      pure derivatives (reassemblable from the content-addressed blobs at any time)
        //      and downloads only ever serve the LATEST version, so beyond a one-version safety
        //      margin they are dead weight on the small shared prod disk (~2x amplification of
        //      every upload, never reclaimed). Pruned HERE rather than in the backup cron
        //      because the server is the packages' single writer: this path knows the version
        //      numbers without scraping the store, runs serialized per set (each ingest deletes
        //      only versions two behind the version it just committed, which no concurrent
        //      request can be serving as "latest"), and bounds growth at creation time instead
        //      of once a day. Failures only log; a leftover package is a disk-usage nit, never
        //      a correctness problem. ----

        await prunePackagesBeyondLatestTwoAsync(setId, versionNo, ct);

        return new IngestResult(setId, versionNo, true, package.Files, coverStatus, previewStatus);
    }

    /// <summary>
    /// Deletes assembled package objects older than (latest, latest-1). Walks downward and
    /// stops at the first absent object: earlier ingests already pruned everything below it.
    /// </summary>
    private async Task prunePackagesBeyondLatestTwoAsync(long setId, int latestVersionNo, CancellationToken ct)
    {
        try
        {
            for (int versionNo = latestVersionNo - 2; versionNo >= 1; versionNo--)
            {
                string key = StoreKeys.Package(setId, versionNo);

                if (!await fileStore.ObjectExistsAsync(key, ct))
                    break;

                await fileStore.DeleteObjectAsync(key, ct);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "Package pruning failed for set {SetId} (latest v{VersionNo}).", setId, latestVersionNo);
        }
    }

    /// <summary>
    /// The moderation_actions.action value an automatic demotion writes, deliberately distinct
    /// from the reviewer's manual 'unrank' so the two are separable in the audit trail. Safe by
    /// construction against the table's only reader: the Discord ranked-map feed
    /// (Endpoints/BuddyEndpoints.cs) filters on <c>ma.action = 'rank'</c>.
    /// </summary>
    public const string AutoUnrankAction = "auto_unrank";

    /// <summary>
    /// BACKLOG 173: a mapper who re-uploads a ranked map with GAMEPLAY-AFFECTING changes sends it
    /// back to 'pending' for re-review. Anything else, a background swap, a tag fix, a metadata
    /// edit, a preview-time change, a beatdrop tweak, keeps the rank, because none of those change
    /// what the player types against. That line is drawn by
    /// <see cref="GameplayFingerprint"/>, and it is the same line the game already draws locally:
    /// <c>TypeBeatRuleset.NativeEncodingsEquivalentForStatus</c> normalises the beatdrop out of a
    /// save precisely so a cosmetic edit cannot demote a ranked map to LocallyModified.
    ///
    /// <para>
    /// A DIFFICULTY ADDED OR REMOVED COUNTS AS A GAMEPLAY CHANGE, both directions, deliberately.
    /// An added difficulty is content no reviewer has ever seen, arriving pre-ranked with a live
    /// leaderboard, which is the exact hole this whole check exists to close. A removed difficulty
    /// takes a ranked leaderboard offline while the set keeps its rank, so the set's ranked claim
    /// no longer describes what it ships. Neither is representable as a moved fingerprint on a
    /// surviving row, so both are tested for by key rather than by value.
    /// </para>
    ///
    /// <para>
    /// AN UNKNOWN (missing) STORED FINGERPRINT READS AS A CHANGE, which is the fail-safe
    /// direction and falls out of the "added" rule for free. It should not be reachable in
    /// practice: <see cref="GameplayFingerprintBackfill"/> fills every live difficulty at startup
    /// before the app serves a request. If it ever is, a metadata-only edit on that one map
    /// demotes it, which a reviewer undoes in one click, and the alternative (assume unchanged)
    /// would silently reproduce the bug this closes.
    /// </para>
    ///
    /// <para>
    /// SCORES ARE NOT TOUCHED. No <c>scores.ranked</c> write, no recalc: the rows stay attached to
    /// their beatmaps and come back with the set when a reviewer re-ranks. The schema cannot do
    /// better, <c>scores.beatmap_id</c> carries no version link, so there is no way to tell a score
    /// set on the old content from one set on the new; see 030_gameplay_fingerprint.sql.
    /// </para>
    /// </summary>
    private static async Task demoteIfGameplayChangedAsync(
        NpgsqlConnection conn,
        long setId,
        string status,
        long uploaderId,
        int versionNo,
        IReadOnlyDictionary<long, string> previous,
        IReadOnlyDictionary<long, string> incoming)
    {
        // Only a RANKED set can be demoted. 'pending' and 'unranked' have nothing to lose,
        // 'hidden' is a set that has never published (the publish latch below is what handles it),
        // and 'removed' is a takedown, which is final and never re-enters circulation.
        if (status != "ranked")
            return;

        int added = incoming.Keys.Count(id => !previous.ContainsKey(id));
        int removed = previous.Keys.Count(id => !incoming.ContainsKey(id));
        int changed = incoming.Count(kv => previous.TryGetValue(kv.Key, out string? was) && was != kv.Value);

        if (added + removed + changed == 0)
            return;

        await conn.ExecuteAsync(
            "UPDATE beatmapsets SET status = 'pending', updated_at = now() WHERE id = @setId AND status = 'ranked'",
            new { setId });

        // Audited, and NOT best-effort. The set-page reviewer flip swallows a failed audit insert
        // because its status change had already committed and a 500 there would lie about a rank
        // that really happened; here both statements are in the ingest transaction, so a failure
        // rolls the demotion back with it and there is nothing to be inconsistent with.
        await conn.ExecuteAsync(
            """
            INSERT INTO moderation_actions (actor_id, set_id, action, note)
            VALUES (@actorId, @setId, @action, @note)
            """,
            new
            {
                // NOT NULL REFERENCES users(id), so somebody has to own this row. The uploading
                // mapper is the honest choice: they caused it, and it is their map.
                actorId = uploaderId,
                setId,
                action = AutoUnrankAction,
                note = $"ranked -> pending (gameplay change in v{versionNo}: {changed} changed, {added} added, {removed} removed)",
            });
    }

    /// <summary>
    /// The uploaded package IS the current version: any diff row not in it stops being live
    /// (<c>filename = NULL</c> is the repo-wide marker; validated ids ⊆ allocated, so this only
    /// ever clears rows of this set), and a set still 'hidden' is published by its first
    /// successful upload ('removed' is deliberately never touched: takedowns are final).
    /// Runs inside the ingest transaction so crossing submissions serialize with the version cut.
    ///
    /// <para>
    /// THE PUBLISH LATCH, and why the watcher notification hangs off it. The status UPDATE is
    /// guarded by <c>status = 'hidden'</c>, the pre-publish shell a set is created in
    /// (BssEndpoints' INSERT), and NO code path anywhere puts a set back into it: the only other
    /// status writes in the repo are the BSS PUT's pending/unranked intent switch (which cannot
    /// produce 'hidden'), the reviewer's pending &lt;-&gt; ranked transitions, the automatic
    /// ranked -&gt; pending demotion above (<see cref="demoteIfGameplayChangedAsync"/>, which
    /// likewise only ever writes 'pending'), and takedowns to 'removed', which are an admin-SQL
    /// lever and deliberately final. So this statement's row
    /// count is exactly "this set became publicly visible for the first time", once per set for
    /// the life of the set, and it is therefore the honest trigger for "a mapper you watch
    /// uploaded something". Hanging the fan-out off <c>CutNewVersion</c> instead would notify
    /// every watcher on every re-upload of an existing map; hanging it off the PUT that creates
    /// the set would notify them about a draft nobody can open yet.
    /// </para>
    ///
    /// <para>
    /// The one thing the latch cannot defend against is somebody hand-running SQL to put a
    /// published set back to 'hidden' and letting it republish. 027's partial unique index covers
    /// that case: the second fan-out inserts nothing rather than re-notifying.
    /// </para>
    ///
    /// <para>
    /// The fan-out runs INSIDE this transaction, so notifications and the publish they announce
    /// commit together: a rolled-back upload cannot leave a badge pointing at a set that never
    /// went live, and a crash between them is not representable.
    /// </para>
    /// </summary>
    private static async Task refreshLivenessAndPublishAsync(
        NpgsqlConnection conn, ParsedPackage package, long setId, long ownerId, CancellationToken ct)
    {
        long[] liveIds = package.Difficulties.Select(d => d.BeatmapId!.Value).ToArray();

        // gameplay_fingerprint rides along with the filename liveness marker on purpose
        // (030_gameplay_fingerprint.sql): "has a fingerprint" is what the next upload's demotion
        // check reads as "was in the previous current version", and a diff that just stopped being
        // live must not still answer yes to that on the upload after this one.
        await conn.ExecuteAsync(
            "UPDATE beatmaps SET filename = NULL, gameplay_fingerprint = NULL WHERE set_id = @setId AND id <> ALL(@liveIds)",
            new { setId, liveIds });

        // Publication is never ranked: leaderboards stay locked until a map reviewer (or admin)
        // promotes the set from the website (migration 005). The published status is the creator's
        // wizard choice: 'pending' (awaiting review) or 'unranked' (not intended for ranking),
        // recorded on the set at PUT time (migration 012).
        int published = await conn.ExecuteAsync(
            "UPDATE beatmapsets SET status = intended_status WHERE id = @setId AND status = 'hidden'",
            new { setId });

        if (published > 0)
            await Notifications.FanOutMapperUploadAsync(conn, setId, ownerId, ct);
    }

    /// <summary>
    /// The latest version's manifest for a set, the <c>files[]</c> list the BSS PUT response
    /// carries (empty for a set with no versions, which drives the client's replace-vs-patch
    /// branch). Exposed here so the endpoints module reuses one definition. This overload is a
    /// point-in-time read on its own connection; do NOT use it as a rebuild base, that's what
    /// the <see cref="SetScope"/> overload is for.
    /// </summary>
    public async Task<IReadOnlyList<PackageFileEntry>> GetLatestVersionFilesAsync(long setId, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        return await queryLatestVersionFilesAsync(conn, setId);
    }

    /// <summary>
    /// The latest version's manifest read INSIDE the ingest critical section: the only safe
    /// rebuild base for the PATCH overlay: nothing can cut another version between this read
    /// and the version this scope goes on to commit.
    /// </summary>
    public async Task<IReadOnlyList<PackageFileEntry>> GetLatestVersionFilesAsync(SetScope scope, long setId)
        => await queryLatestVersionFilesAsync(scope.Connection, setId);

    private static async Task<IReadOnlyList<PackageFileEntry>> queryLatestVersionFilesAsync(NpgsqlConnection conn, long setId)
    {
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
    /// works, and it proves the blobs round-trip).
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
