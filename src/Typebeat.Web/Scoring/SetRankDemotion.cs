using Dapper;
using Npgsql;
using Typebeat.Web.Caching;
using Typebeat.Web.Data;
using Typebeat.Web.Storage;

namespace Typebeat.Web.Scoring;

/// <summary>
/// The DOWNWARD half of the version rule (backlog 398). Where <see cref="SetRankRefund"/> carries an
/// honest pending-era play UP onto a board ranked later, this carries a stale play DOWN off a board
/// whose map has since been re-uploaded with different gameplay. Both run
/// <see cref="PlayedVersionRule"/>; this is the same predicate pointed the other way, so a row is
/// ever moved one direction on a given transition, never both.
///
/// <para>
/// THE BUG THIS CLOSES. 030_gameplay_fingerprint.sql demotes a RANKED SET back to 'pending' when a
/// mapper re-uploads it with gameplay-affecting changes, and deliberately leaves
/// <c>scores.ranked</c> alone: at the time there was no way to tell a play on the old content from
/// one on the new. Backlog 352 recovered the played version from the score's own token
/// (<c>score_tokens.beatmap_hash</c>) and used it up; nothing used it down. So a set a reviewer
/// re-ranks after a re-upload keeps scoring plays made on the version that used to be ranked, and
/// its board mixes every version it has ever shipped.
/// </para>
///
/// <para>
/// IT IS A STANDING SWEEP, and re-runnable by construction for the reason
/// <see cref="SetRankRefund"/> documents: a row it demotes stops matching the candidate query
/// (which takes RANKED rows only), so a second run in the same boot, or on the next, moves nothing.
/// The natural home is the boot chain, so deploying the website IS the backfill; the rank-button
/// transition runs the same pass scoped to its set, alongside its carry in the other direction.
/// </para>
///
/// <para>
/// THE RULE IS STRICT, per the owner's 2026-10-04 decision: a score is DROPPED unless its played
/// version is provably the ranked one. <see cref="PlayedVersionRule.IsCarried"/> is the keep test,
/// so the two arms are exact mirrors: <see cref="PlayedVersionRule.Verdict.SameBytes"/> and
/// <see cref="PlayedVersionRule.Verdict.SameGameplay"/> keep a row; a missing token
/// (<see cref="DropReason.MissingToken"/>), a hash no stored version produces
/// (<see cref="DropReason.NoMatchingVersion"/>) and a version whose fingerprint differs
/// (<see cref="DropReason.ChangedGameplay"/>) all demote. This is the opposite of the carry-up
/// sweep's precision-over-recall default, deliberately: there the costly direction is dropping a
/// live score on missing data, and here it is leaving a stale one on the board, and the owner chose
/// the boards-strictly-current-version arm.
/// </para>
///
/// <para>
/// WHAT IT WRITES. <c>ranked = false</c> and <c>pp = 0</c>, plus a <c>score_demotions</c> audit row
/// (043_rank_version_demotion.sql). pp is zeroed in the same statement rather than left for
/// <see cref="Packages.PpBackfill"/>: the pricing is a live function of <c>ranked</c>, so leaving the
/// old number would briefly show a demoted play with pp on any surface that read <c>pp</c> without
/// re-checking <c>ranked</c>, and zeroing it is what makes the drop land on the pp surfaces at once.
/// The row's <c>pp_version</c> is left alone: it is not repriced (see below), and touching it would
/// be a claim about a value that no longer matters.
/// </para>
///
/// <para>
/// AGGREGATES, and what actually needs invalidating. The cumulative-score ranking
/// (<see cref="GlobalRanking"/>), the pp ranking (<see cref="PpRanking"/>) and the profile's score
/// sections (<see cref="ProfileScores"/>) all re-check <c>scores.ranked</c> at read time, so
/// flipping the flag is the whole of the data change: unlike a re-rank, there is no stored pp value
/// they consult that must be recomputed, because the row earns nothing now. <c>user_stats</c>
/// accrual never depended on <c>ranked</c> (a demoted play keeps its play_count and total_score), so
/// nothing there moves either. The one thing that does need telling is the CACHE: the game client's
/// board memo (<see cref="CacheEviction.BoardKey"/>, 5 s) and the beatmap lookup memo (60 s) would
/// serve the stale board for up to a minute, so a pass that actually flipped a row in a given set
/// asks <see cref="CacheEviction.AfterSetStatusAsync"/> to forget it, exactly as the reviewer's own
/// status flip does. The eviction is the CALLER'S: the boot sweep passes the singleton in, and the
/// rank-button path already evicts the set as part of its transition, so it passes none.
/// </para>
/// </summary>
public static class SetRankDemotion
{
    /// <summary>
    /// Why a currently-ranked score was judged not to be on the ranked version. Reported per reason
    /// by the dry run so the owner can size each arm before the backfill.
    /// </summary>
    public enum DropReason
    {
        /// <summary>The score has no <c>score_tokens</c> row: nothing names the version it was played on.</summary>
        MissingToken,

        /// <summary>A token hash no stored version's .osu produces (a manifest edited outside the ingest, a missing blob).</summary>
        NoMatchingVersion,

        /// <summary>The played version exists and its gameplay fingerprint differs from the current one.</summary>
        ChangedGameplay,
    }

    /// <summary>
    /// One currently-ranked score the rule examines. Carries the same five version-rule columns as
    /// <see cref="GateRefund.CandidateRow"/> plus nothing else: every column here is read, either to
    /// judge the version or to write the demotion.
    /// </summary>
    public sealed record Candidate(
        long ScoreId,
        long SetId,
        long BeatmapId,
        string CurrentChecksum,
        int CurrentVersion,
        string? PlayedHash);

    /// <summary>
    /// The counts a dry run produces, WITHOUT touching a row: how many currently-ranked scores each
    /// drop reason would remove, and how many would be kept.
    /// </summary>
    public sealed record Report(int MissingToken, int NoMatchingVersion, int ChangedGameplay, int Kept)
    {
        /// <summary>How many rows the sweep would demote in total.</summary>
        public int Dropped => MissingToken + NoMatchingVersion + ChangedGameplay;

        /// <summary>How many currently-ranked rows were examined.</summary>
        public int Examined => Dropped + Kept;
    }

    /// <summary>What a per-set demotion run did: how many rows moved, and the reason breakdown.</summary>
    public sealed record Outcome(int Demoted, Report Report);

    /// <summary>
    /// The boot sweep: every currently-ranked score on every ranked set.
    /// <paramref name="eviction"/> is the live board memo, forgotten per affected set.
    /// </summary>
    public static Task<Outcome> RunAsync(
        Db db, IFileStore fileStore, ILogger logger, CacheEviction? eviction = null, CancellationToken ct = default)
        => runAsync(db, fileStore, logger, setId: null, dryRun: false, eviction, ct);

    /// <summary>
    /// The identical pass over ONE set, for the rank-button transition: same predicate, only the
    /// candidate query narrowed to <paramref name="setId"/>. Its caller has already evicted the set.
    /// </summary>
    public static Task<Outcome> RunForSetAsync(
        Db db, IFileStore fileStore, ILogger logger, long setId, CancellationToken ct = default)
        => runAsync(db, fileStore, logger, setId, dryRun: false, eviction: null, ct);

    /// <summary>
    /// The DRY RUN: the same candidate query and the same predicate, with every write skipped. The
    /// owner runs this before the prod backfill to see how many rows each drop reason would remove.
    /// </summary>
    public static async Task<Report> ReportAsync(
        Db db, IFileStore fileStore, ILogger logger, long? setId = null, CancellationToken ct = default)
        => (await runAsync(db, fileStore, logger, setId, dryRun: true, eviction: null, ct)).Report;

    private static async Task<Outcome> runAsync(
        Db db,
        IFileStore fileStore,
        ILogger logger,
        long? setId,
        bool dryRun,
        CacheEviction? eviction,
        CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);

        // The audit table only exists once 043 has been applied; on a database mid-migration there
        // is nothing to do (the same guard GateRefund uses).
        bool ready = await conn.ExecuteScalarAsync<bool>(
            "SELECT to_regclass('public.score_demotions') IS NOT NULL");

        if (!ready)
            return new Outcome(0, new Report(0, 0, 0, 0));

        var candidates = (await conn.QueryAsync<Candidate>(
                """
                SELECT s.id               AS ScoreId,
                       b.set_id           AS SetId,
                       b.id               AS BeatmapId,
                       b.checksum_md5     AS CurrentChecksum,
                       bs.current_version AS CurrentVersion,
                       t.beatmap_hash     AS PlayedHash
                FROM scores s
                JOIN beatmaps b ON b.id = s.beatmap_id
                JOIN beatmapsets bs ON bs.id = b.set_id
                LEFT JOIN (SELECT DISTINCT ON (score_id) score_id, beatmap_hash
                           FROM score_tokens
                           WHERE score_id IS NOT NULL
                           ORDER BY score_id, id) t ON t.score_id = s.id
                WHERE s.ranked
                  AND s.passed
                  AND bs.status = 'ranked'
                  AND (@setId::bigint IS NULL OR b.set_id = @setId)
                """,
                new { setId }))
            .ToList();

        int missing = 0, noMatch = 0, changed = 0, kept = 0, demoted = 0;
        var rule = new PlayedVersionRule(fileStore, logger);

        // Per set, so the cache eviction at the end can name exactly the boards that moved, and
        // only when something actually moved (a dry run and a no-op pass both evict nothing).
        var demotedSets = new HashSet<long>();

        foreach (var row in candidates)
        {
            var verdict = await rule.JudgeAsync(
                conn, row.SetId, row.BeatmapId, row.CurrentChecksum, row.CurrentVersion, row.PlayedHash, ct);

            if (PlayedVersionRule.IsCarried(verdict))
            {
                kept++;
                continue;
            }

            // Unknown and Changed both drop here (the strict arm). Which one it was is only for the
            // report: a missing token and a hash no version produces are both Unknown to the rule,
            // and are told apart by whether the row names a hash at all.
            DropReason reason = verdict == PlayedVersionRule.Verdict.Changed
                ? DropReason.ChangedGameplay
                : row.PlayedHash is null
                    ? DropReason.MissingToken
                    : DropReason.NoMatchingVersion;

            switch (reason)
            {
                case DropReason.MissingToken: missing++; break;
                case DropReason.NoMatchingVersion: noMatch++; break;
                default: changed++; break;
            }

            if (dryRun)
                continue;

            await using var tx = await conn.BeginTransactionAsync(ct);

            // The audit row, then the flip. Guarded on ranked, so a row demoted between the read
            // above and here is a no-op rather than a double write (the flag is the whole state;
            // pp is zeroed with it).
            int flipped = await conn.ExecuteAsync(
                "UPDATE scores SET ranked = false, pp = 0 WHERE id = @scoreId AND ranked",
                new { scoreId = row.ScoreId }, tx);

            if (flipped > 0)
            {
                await conn.ExecuteAsync(
                    """
                    INSERT INTO score_demotions (score_id, reason, set_id)
                    VALUES (@scoreId, @reason, @setId)
                    ON CONFLICT (score_id) DO NOTHING
                    """,
                    new { scoreId = row.ScoreId, reason = ReasonKey(reason), setId = row.SetId }, tx);

                demotedSets.Add(row.SetId);
                demoted++;
            }

            await tx.CommitAsync(ct);
        }

        if (eviction != null && demoted > 0)
            foreach (long affected in demotedSets)
                await eviction.AfterSetStatusAsync(affected);

        if (demoted > 0 || kept > 0)
        {
            logger.LogInformation(
                "Version demotion{Scope}: {Demoted} of {Examined} ranked scores dropped (missing token {Missing}, no matching version {NoMatch}, changed gameplay {Changed}); {Kept} kept.",
                setId is { } scoped ? $" (set {scoped})" : "", demoted, candidates.Count, missing, noMatch, changed, kept);
        }

        return new Outcome(demoted, new Report(missing, noMatch, changed, kept));
    }

    /// <summary>
    /// The stored spelling of a drop reason (<c>score_demotions.reason</c>): snake_case, stable, so
    /// a query or a later report can group on it.
    /// </summary>
    public static string ReasonKey(DropReason reason) => reason switch
    {
        DropReason.MissingToken => "missing_token",
        DropReason.NoMatchingVersion => "no_matching_version",
        DropReason.ChangedGameplay => "changed_gameplay",
        _ => "unknown",
    };
}
