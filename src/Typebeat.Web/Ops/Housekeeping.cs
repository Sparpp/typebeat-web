using Dapper;
using Npgsql;
using Typebeat.Web.Data;
using Typebeat.Web.Packages;
using Typebeat.Web.Storage;

namespace Typebeat.Web.Ops;

/// <summary>What the replay retention pass does with the replays it finds prunable.</summary>
public enum ReplaySweepMode
{
    /// <summary>The pass does not even look.</summary>
    Off,

    /// <summary>Counts and logs "would prune N replays, B bytes" and deletes nothing. The default.</summary>
    DryRun,

    /// <summary>Deletes the replay bytes and stamps <c>scores.replay_pruned_at</c>.</summary>
    On,
}

/// <summary>The housekeeping knobs, env-configured with tolerant parsing (an empty string is the default).</summary>
public sealed record HousekeepingOptions
{
    public const string EnabledKey = "TYPEBEAT_HOUSEKEEPING";
    public const string ReplaySweepKey = "TYPEBEAT_REPLAY_SWEEP";
    public const string KeepTopNKey = "TYPEBEAT_REPLAY_KEEP_TOP_N";
    public const string KeepDaysKey = "TYPEBEAT_REPLAY_KEEP_DAYS";
    public const string KeepViewedDaysKey = "TYPEBEAT_REPLAY_KEEP_VIEWED_DAYS";
    public const string SoftCapKey = "TYPEBEAT_REPLAY_SOFT_CAP_GB";

    public ReplaySweepMode ReplaySweep { get; init; } = ReplaySweepMode.DryRun;

    /// <summary>A replay among the player's top N plays on that map, per board, is always kept.</summary>
    public int KeepTopN { get; init; } = 3;

    /// <summary>A replay uploaded within this many days is always kept.</summary>
    public int KeepDays { get; init; } = 30;

    /// <summary>A replay someone ELSE watched within this many days is always kept.</summary>
    public int KeepViewedDays { get; init; } = 90;

    /// <summary>Stored replay bytes above this only WARN. A keeper is never deleted to meet it.</summary>
    public long SoftCapBytes { get; init; } = DiskGuardOptions.GigabytesToBytes(10);

    /// <summary>Expired or revoked tokens are kept this long past the fact, for forensics.</summary>
    public int TokenMarginDays { get; init; } = 7;

    /// <summary>DECIDED (owner, 2026-10-01): the download log keeps one year.</summary>
    public int DownloadLogDays { get; init; } = 365;

    /// <summary>Rows per DELETE statement, so no pass holds a long lock or bloats one transaction.</summary>
    public int BatchSize { get; init; } = 5000;

    public static HousekeepingOptions FromConfiguration(IConfiguration config) => new()
    {
        ReplaySweep = ParseMode(config[ReplaySweepKey]),
        KeepTopN = (int)DiskGuardOptions.ParseNonNegative(config[KeepTopNKey], 3),
        KeepDays = (int)DiskGuardOptions.ParseNonNegative(config[KeepDaysKey], 30),
        KeepViewedDays = (int)DiskGuardOptions.ParseNonNegative(config[KeepViewedDaysKey], 90),
        SoftCapBytes = DiskGuardOptions.GigabytesToBytes(DiskGuardOptions.ParseNonNegative(config[SoftCapKey], 10)),
    };

    /// <summary>off | dryrun | on, case-insensitive; anything else (empty included) is dryrun, the safe middle.</summary>
    public static ReplaySweepMode ParseMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "off" => ReplaySweepMode.Off,
        "on" => ReplaySweepMode.On,
        _ => ReplaySweepMode.DryRun,
    };
}

/// <summary>What one pass did. Every count is rows (or objects) actually removed, or for a dry run, that would be.</summary>
public sealed record HousekeepingReport
{
    /// <summary>Another instance held the advisory lock, so this pass did nothing.</summary>
    public bool SkippedLocked { get; init; }

    public int OAuthTokens { get; init; }
    public int EmailTokens { get; init; }
    public int ScoreTokens { get; init; }
    public int DownloadLogRows { get; init; }
    public int CoverObjects { get; init; }

    public ReplaySweepMode ReplayMode { get; init; }

    /// <summary>Replays pruned, or in a dry run the replays that would be.</summary>
    public int Replays { get; init; }

    public long ReplayBytes { get; init; }

    /// <summary>Every stored replay's bytes after the pass, for the soft cap and the ops endpoint.</summary>
    public long StoredReplayBytes { get; init; }

    /// <summary>The ids pruned (or that would be), so a test can name them.</summary>
    public IReadOnlyList<long> ReplayScoreIds { get; init; } = [];
}

/// <summary>Last completed pass, for GET /api/v2/ops/disk. Registered whether or not the service runs.</summary>
public sealed class HousekeepingStatus
{
    private long lastSweepTicks;

    public DateTimeOffset? LastSweepAt
    {
        get
        {
            long ticks = Interlocked.Read(ref lastSweepTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    public void Record(DateTimeOffset at) => Interlocked.Exchange(ref lastSweepTicks, at.UtcTicks);
}

/// <summary>
/// The app's housekeeping pass (backlog 365), and its FIRST hosted service: hourly, it deletes what
/// nothing reads any more and nothing else ever deletes.
///
/// <list type="bullet">
/// <item><c>oauth_tokens</c> revoked, or with both halves expired, more than seven days ago (every
/// refresh inserts a row and website sessions are the same rows, so this table only grew).</item>
/// <item><c>email_tokens</c> expired more than seven days ago.</item>
/// <item><c>score_tokens</c> NEVER CONSUMED and older than seven days. A consumed token
/// (<c>score_id IS NOT NULL</c>) is load-bearing (GateRefund and PlayedVersionRule read its
/// beatmap_hash) and is never touched.</item>
/// <item><c>beatmapset_downloads</c> older than 365 days (DECIDED by the owner). The per-set
/// <c>download_count</c> is a separate denormalised column, so counts do not move.</item>
/// <item>Expired chunked upload sessions (otherwise swept only when a new session is created).</item>
/// <item>Cover jpegs of set versions below latest - 1, except the version cover_key names
/// (<see cref="CoverPrune"/>), a catch-up for the ingest's own per-version prune.</item>
/// <item>Replay retention, under <see cref="ReplaySweepMode"/>, DRY RUN by default: see
/// <see cref="sweepReplaysAsync"/>.</item>
/// </list>
///
/// <para>Content-addressed blobs are left alone (they are shared across versions and sets). No
/// VACUUM FULL here: that takes an exclusive lock and belongs in a maintenance window.</para>
///
/// <para>Off unless <c>TYPEBEAT_HOUSEKEEPING=true</c>, so the test hosts never run it; each pass is
/// the public static <see cref="RunOnceAsync"/> so tests drive it directly against the shared
/// database rather than through a host. A pass takes a session advisory lock, so two app instances
/// (a deploy overlap) never run one at the same time.</para>
/// </summary>
public sealed class Housekeeping(
    Db db, IFileStore store, UploadSessionStore sessions, HousekeepingStatus status, IConfiguration config, ILogger<Housekeeping> logger) : BackgroundService
{
    /// <summary>How often a pass runs.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    /// <summary>The first pass waits this long after boot, so it never competes with the startup sweeps' tail or a deploy's health check.</summary>
    public static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);

    /// <summary>Session advisory lock key, a constant so every instance contends on the same one.</summary>
    public const long AdvisoryLockKey = 0x7479_7065_0365;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = HousekeepingOptions.FromConfiguration(config);

        logger.LogInformation("Housekeeping: enabled, hourly, replay sweep {Mode} (keep top {TopN} per board, uploaded within {KeepDays} days, viewed within {ViewedDays} days, pinned).",
            options.ReplaySweep, options.KeepTopN, options.KeepDays, options.KeepViewedDays);

        try
        {
            await Task.Delay(StartupDelay, stoppingToken);

            using var timer = new PeriodicTimer(Interval);

            do
            {
                try
                {
                    await using var conn = await db.OpenAsync(stoppingToken);
                    var report = await RunOnceAsync(conn, store, options, DateTimeOffset.UtcNow, sessions, logger, stoppingToken);

                    if (!report.SkippedLocked)
                        status.Record(DateTimeOffset.UtcNow);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // A failed pass must never take the app down; the next tick tries again.
                    logger.LogWarning(e, "Housekeeping: pass failed.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// One full pass. Everything is relative to <paramref name="now"/> (bound as a parameter, never
    /// the database's now()), so a test states the instant it means.
    /// </summary>
    public static async Task<HousekeepingReport> RunOnceAsync(
        NpgsqlConnection conn,
        IFileStore store,
        HousekeepingOptions options,
        DateTimeOffset now,
        UploadSessionStore? sessions = null,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        var utcNow = now.ToUniversalTime();

        if (!await conn.ExecuteScalarAsync<bool>("SELECT pg_try_advisory_lock(@key)", new { key = AdvisoryLockKey }))
        {
            logger?.LogInformation("Housekeeping: another instance holds the lock, skipping this pass.");
            return new HousekeepingReport { SkippedLocked = true, ReplayMode = options.ReplaySweep };
        }

        try
        {
            var tokenCutoff = utcNow.AddDays(-options.TokenMarginDays);

            int oauth = await deleteBatchedAsync(conn, "oauth_tokens", "id",
                "(revoked_at IS NOT NULL AND revoked_at < @cutoff) OR (access_expires_at < @cutoff AND refresh_expires_at < @cutoff)",
                tokenCutoff, options.BatchSize, ct);

            int email = await deleteBatchedAsync(conn, "email_tokens", "id",
                "expires_at < @cutoff", tokenCutoff, options.BatchSize, ct);

            // NEVER a consumed token: score_id IS NULL is the whole safety of this statement.
            int scoreTokens = await deleteBatchedAsync(conn, "score_tokens", "id",
                "score_id IS NULL AND created_at < @cutoff", tokenCutoff, options.BatchSize, ct);

            // No primary key on the download log, so the batch is chosen by ctid.
            int downloads = await deleteBatchedAsync(conn, "beatmapset_downloads", "ctid",
                "at < @cutoff", utcNow.AddDays(-options.DownloadLogDays), options.BatchSize, ct);

            if (sessions != null)
                await sessions.SweepExpiredAsync(ct);

            int covers = await sweepCoversAsync(conn, store, ct);

            var (replayIds, replayBytes) = await sweepReplaysAsync(conn, store, options, utcNow, logger, ct);

            long storedReplayBytes = await StoredReplayBytesAsync(conn, ct);

            var report = new HousekeepingReport
            {
                OAuthTokens = oauth,
                EmailTokens = email,
                ScoreTokens = scoreTokens,
                DownloadLogRows = downloads,
                CoverObjects = covers,
                ReplayMode = options.ReplaySweep,
                Replays = replayIds.Count,
                ReplayBytes = replayBytes,
                ReplayScoreIds = replayIds,
                StoredReplayBytes = storedReplayBytes,
            };

            logger?.LogInformation(
                "Housekeeping: deleted {OAuth} oauth tokens, {Email} email tokens, {ScoreTokens} unconsumed score tokens, {Downloads} download log rows, {Covers} stale cover objects.",
                oauth, email, scoreTokens, downloads, covers);

            switch (options.ReplaySweep)
            {
                case ReplaySweepMode.DryRun:
                    logger?.LogInformation("Housekeeping: replay sweep dryrun, would prune {Count} replays, {Bytes} bytes ({Gib} GiB); {Stored} GiB of replays stored.",
                        replayIds.Count, replayBytes, DiskGuard.Gib(replayBytes), DiskGuard.Gib(storedReplayBytes));
                    break;

                case ReplaySweepMode.On:
                    logger?.LogInformation("Housekeeping: replay sweep pruned {Count} replays, {Bytes} bytes ({Gib} GiB); {Stored} GiB of replays stored.",
                        replayIds.Count, replayBytes, DiskGuard.Gib(replayBytes), DiskGuard.Gib(storedReplayBytes));
                    break;
            }

            if (storedReplayBytes > options.SoftCapBytes)
            {
                logger?.LogWarning("Housekeeping: stored replays ({Stored} GiB) are over the {Cap} GiB soft cap. Nothing extra is deleted to meet it: every remaining replay is a keeper.",
                    DiskGuard.Gib(storedReplayBytes), DiskGuard.Gib(options.SoftCapBytes));
            }

            return report;
        }
        finally
        {
            await conn.ExecuteAsync("SELECT pg_advisory_unlock(@key)", new { key = AdvisoryLockKey });
        }
    }

    /// <summary>Every stored replay's bytes (the replay_* index, not the files themselves).</summary>
    public static async Task<long> StoredReplayBytesAsync(NpgsqlConnection conn, CancellationToken ct = default)
        => await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COALESCE(SUM(replay_bytes), 0)::bigint FROM scores WHERE replay_key IS NOT NULL", cancellationToken: ct));

    private static async Task<int> deleteBatchedAsync(
        NpgsqlConnection conn, string table, string keyColumn, string predicate, DateTimeOffset cutoff, int batch, CancellationToken ct)
    {
        // Table, key and predicate are fixed literals from this file, never input.
        string sql = $"DELETE FROM {table} WHERE {keyColumn} IN (SELECT {keyColumn} FROM {table} WHERE {predicate} LIMIT @batch)";

        int total = 0;

        while (true)
        {
            int deleted = await conn.ExecuteAsync(new CommandDefinition(sql, new { cutoff, batch }, cancellationToken: ct));
            total += deleted;

            if (deleted < batch)
                return total;
        }
    }

    private static async Task<int> sweepCoversAsync(NpgsqlConnection conn, IFileStore store, CancellationToken ct)
    {
        var sets = await conn.QueryAsync<(long SetId, string? CoverKey, int Latest)>(new CommandDefinition(
            """
            SELECT bs.id AS SetId, bs.cover_key AS CoverKey, MAX(v.version_no) AS Latest
            FROM beatmapsets bs
            JOIN set_versions v ON v.set_id = bs.id
            GROUP BY bs.id, bs.cover_key
            HAVING MAX(v.version_no) >= 3
            """, cancellationToken: ct));

        int deleted = 0;

        foreach (var (setId, coverKey, latest) in sets)
        {
            foreach (int versionNo in CoverPrune.PrunableVersions(setId, latest, coverKey))
                deleted += await CoverPrune.PruneVersionAsync(store, setId, versionNo, coverKey, ct);
        }

        return deleted;
    }

    /// <summary>
    /// The candidate rule, as SQL. A stored replay is KEPT when any one of these holds:
    /// (a) its play is among the player's top <c>@keepTopN</c> on that beatmap on its board (ranked
    /// and unranked separately, passed plays first, then the board's own order
    /// <c>total_score DESC, id ASC</c>); (b) it is pinned; (c) it was uploaded within
    /// <c>@keepDays</c>; (d) someone other than its owner watched it within <c>@viewedDays</c> of <c>@now</c>'s UTC day
    /// (replay_views only ever records other viewers). Everything else is a candidate.
    /// Keyset-paged on id so a dry run walks the whole table instead of re-reading one batch.
    /// </summary>
    public const string ReplayCandidatesSql =
        """
        WITH owners AS (
            SELECT DISTINCT user_id, beatmap_id
            FROM scores
            WHERE replay_key IS NOT NULL AND replay_uploaded_at < @uploadedBefore),
        r AS (
            SELECT s.id,
                   row_number() OVER (PARTITION BY s.user_id, s.beatmap_id, s.ranked
                                      ORDER BY s.passed DESC, s.total_score DESC, s.id ASC) AS rn
            FROM scores s
            JOIN owners o ON o.user_id = s.user_id AND o.beatmap_id = s.beatmap_id)
        SELECT s.id AS Id, s.replay_key AS Key, s.replay_uploaded_at AS UploadedAt, COALESCE(s.replay_bytes, 0)::bigint AS Bytes
        FROM scores s
        JOIN r ON r.id = s.id
        WHERE s.replay_key IS NOT NULL
          AND r.rn > @keepTopN
          AND s.replay_uploaded_at < @uploadedBefore
          AND NOT EXISTS (SELECT 1 FROM score_pins p WHERE p.score_id = s.id)
          AND NOT EXISTS (SELECT 1 FROM replay_views v WHERE v.score_id = s.id AND v.viewed_on > (@now AT TIME ZONE 'UTC')::date - @viewedDays)
          AND s.id > @after
        ORDER BY s.id
        LIMIT @batch
        """;

    private sealed record ReplayCandidate(long Id, string Key, DateTime UploadedAt, long Bytes);

    /// <summary>
    /// Replay retention. Per pruned row, in its own transaction: re-read the row FOR UPDATE with the
    /// upload time the scan saw (a re-upload since then means it is a fresh replay and is skipped),
    /// null replay_key and stamp replay_pruned_at, delete the object, commit. The download route
    /// already answers 404 for a row that says stored while the object is gone, so the order is
    /// safe for readers, and a re-upload clears replay_pruned_at again.
    ///
    /// <para>Recalc coordination: tools/score-recalc reads a pruned row as
    /// <c>UnreplayableCase.Pruned</c>, which demands its own explicit policy, so turning this on
    /// can never silently change what a supersede sweep does.</para>
    /// </summary>
    private static async Task<(List<long> Ids, long Bytes)> sweepReplaysAsync(
        NpgsqlConnection conn, IFileStore store, HousekeepingOptions options, DateTimeOffset utcNow, ILogger? logger, CancellationToken ct)
    {
        var ids = new List<long>();
        long bytes = 0;

        if (options.ReplaySweep == ReplaySweepMode.Off)
            return (ids, bytes);

        var parameters = new
        {
            uploadedBefore = utcNow.AddDays(-options.KeepDays),
            now = utcNow,
            viewedDays = options.KeepViewedDays,
            keepTopN = options.KeepTopN,
            batch = options.BatchSize,
        };

        long after = 0;

        while (true)
        {
            var page = (await conn.QueryAsync<ReplayCandidate>(new CommandDefinition(
                ReplayCandidatesSql,
                new { parameters.uploadedBefore, parameters.now, parameters.viewedDays, parameters.keepTopN, parameters.batch, after },
                cancellationToken: ct))).ToList();

            foreach (var candidate in page)
            {
                if (options.ReplaySweep == ReplaySweepMode.DryRun || await pruneOneAsync(conn, store, candidate, utcNow, logger, ct))
                {
                    ids.Add(candidate.Id);
                    bytes += candidate.Bytes;
                }
            }

            if (page.Count < options.BatchSize)
                return (ids, bytes);

            after = page[^1].Id;
        }
    }

    private static async Task<bool> pruneOneAsync(NpgsqlConnection conn, IFileStore store, ReplayCandidate candidate, DateTimeOffset utcNow, ILogger? logger, CancellationToken ct)
    {
        await using var tx = await conn.BeginTransactionAsync(ct);

        string? key = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            """
            SELECT replay_key FROM scores
            WHERE id = @id AND replay_key IS NOT NULL AND replay_uploaded_at = @seen
            FOR UPDATE
            """,
            new { id = candidate.Id, seen = candidate.UploadedAt }, tx, cancellationToken: ct));

        // Re-uploaded (or already gone) since the scan: not the replay that qualified.
        if (key is null)
            return false;

        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE scores SET replay_key = NULL, replay_pruned_at = @now WHERE id = @id",
            new { id = candidate.Id, now = utcNow }, tx, cancellationToken: ct));

        try
        {
            await store.DeleteObjectAsync(key, ct);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning(e, "Housekeeping: could not delete replay object {Key} for score {ScoreId}; leaving the row as stored.", key, candidate.Id);
            return false;
        }

        await tx.CommitAsync(ct);
        return true;
    }
}
