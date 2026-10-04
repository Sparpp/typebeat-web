using System.Text.Json.Nodes;
using Dapper;
using Npgsql;
using Typebeat.Web.Caching;
using Typebeat.Web.Data;
using Typebeat.Web.Storage;

namespace Typebeat.Web.Scoring;

/// <summary>
/// Backlog 398: the DOWNWARD half of the version rule, as a CLASSIC MARK rather than a demotion.
/// Where <see cref="SetRankRefund"/> carries an honest pending-era play UP onto a board ranked
/// later, this marks a play that is on the board but whose played version is NOT the current
/// gameplay of that board's map. Both run <see cref="PlayedVersionRule"/>; this is the same
/// predicate pointed the other way.
///
/// <para>
/// THE MARK, and why it is not an unranking (owner decision 2026-10-04, which SUPERSEDES an earlier
/// plan to drop these scores). A board re-ranked after a re-upload used to mix plays from every
/// version with no record that they were played on a different map. Instead of throwing that
/// history away, the affected scores get the synthetic acronym "CL" appended to their stored
/// <c>scores.mods</c>, keep their <c>ranked</c> flag, and are paid 5 percent less: their stored
/// <c>total_score</c> is repriced to 0.95x once, and their pp recomputes at 0.95x because both
/// pricing tables now price "CL". Riding <c>scores.mods</c> is deliberate: every consumer that
/// already reads mods (the pp engine, <see cref="ModMultiplier"/>, the score card's badge,
/// <see cref="ModInfo"/>) then sees Classic uniformly, with no new column and no per-surface
/// special-casing.
/// </para>
///
/// <para>
/// IT IS A STANDING SWEEP, and re-runnable by construction: the marker's own presence in
/// <c>mods</c> is the idempotence key, so a row it marked stops being a candidate (see the
/// SectionSkip in the candidate query), and a second run in the same boot or on the next moves
/// nothing. The natural home is the boot chain, so deploying the website IS the backfill; the
/// rank-button transition runs the same pass scoped to its set.
/// </para>
///
/// <para>
/// IT EXAMINES EVERY SCORE, ranked and unranked, per the owner's scope note: an unranked play on a
/// stale version is marked too, so when it is later carried up (<see cref="SetRankRefund"/>, whose
/// own version rule would decline the stale ones anyway) or read on the unranked board, it already
/// carries the mark and the price. A score already carrying CL is skipped: nothing applies the
/// penalty twice.
/// </para>
///
/// <para>
/// THE RULE IS STRICT, per the owner's call: a score is MARKED unless its played version is
/// provably the ranked one. <see cref="PlayedVersionRule.IsCarried"/> is the keep test, so the two
/// arms are exact mirrors: <see cref="PlayedVersionRule.Verdict.SameBytes"/> and
/// <see cref="PlayedVersionRule.Verdict.SameGameplay"/> keep a row unmarked; a missing token
/// (<see cref="MarkReason.MissingToken"/>), a hash no stored version produces
/// (<see cref="MarkReason.NoMatchingVersion"/>) and a version whose fingerprint differs
/// (<see cref="MarkReason.ChangedGameplay"/>) all mark it. This is the opposite of the carry-up
/// sweep's precision-over-recall default, deliberately: there the costly direction is marking a
/// play on missing data, and the owner chose the boards-are-strictly-current-version arm.
/// </para>
///
/// <para>
/// WHAT IT WRITES, and why each piece. (1) The mark: "CL" appended to <c>scores.mods</c>, which is
/// what every mods consumer reads. (2) The score: <c>total_score</c> repriced to
/// <c>round(total_score * 0.95)</c> in the SAME statement, so the mark and the price commit
/// together and a rerun cannot see the middle state; the integer rounding is <c>Math.Round(...,
/// MidpointRounding.AwayFromZero)</c>, the client's own rule for a modded total, and the result is
/// checked against the row's ceiling (see below). (3) pp: the row is stamped <c>pp_version = 0</c>,
/// NOT repriced here. PpBackfill runs immediately after this in the boot chain and prices every row
/// below <see cref="PerformancePoints.VERSION"/>, so the 0.95x lands on the same boot with no
/// second pricing path. It is a TARGETED stamp rather than a <see cref="PerformancePoints.VERSION"/>
/// bump on purpose: a bump reprices the WHOLE catalogue (every score on every map, re-read and
/// rewritten at boot), where this touches only the rows that actually got a mark, which on a mature
/// board is a handful. A bump also drags in rows whose stored pp is correct precisely so nobody has
/// to prove it, and this deletes that work; the targeted stamp is the smaller, safer move and is
/// what the refunds already do for the same reason.
/// </para>
///
/// <para>
/// THE CEILING IS NOT RE-CHECKED, and the reason is a direction. The mark only ever LOWERS a total
/// (0.95x), and the submit path's bound is an UPPER one (<see cref="ModMultiplier.TotalScoreCeiling"/>):
/// a row in bounds at 1.0x is still in bounds, with room to spare, at 0.95x, so there is nothing a
/// re-check could catch that was not already true of the stored row. Rounding is the only thing that
/// moves at all, and <c>Math.Round</c> settles it to the nearest unit. Equally, the mark must not
/// become a second unranking path by accident, and this is what guarantees it cannot.
/// </para>
///
/// <para>
/// AGGREGATES, and what needs invalidating. Every scoring surface re-reads <c>scores.ranked</c> and
/// <c>scores.total_score</c> live (beatmap boards, <see cref="GlobalRanking"/>, <see cref="PpRanking"/>,
/// <see cref="ProfileScores"/>, the profile's first places), so the data change is the whole of it.
/// The CACHE does need telling: the game client's board memo (<see cref="CacheEviction.BoardKey"/>,
/// 5 s) and the beatmap lookup memo (60 s) would serve the un-marked board for up to a minute, so a
/// pass that marked a row in a given set asks <see cref="CacheEviction.AfterSetStatusAsync"/> to
/// forget it. The eviction is the CALLER'S: the boot sweep passes the singleton in; the
/// rank-button path already evicts the set as part of its transition and passes none.
/// </para>
/// </summary>
public static class SetRankClassicMark
{
    /// <summary>The synthetic acronym the mark appends. NOT player-selectable; see the class note.</summary>
    public const string ClassicAcronym = "CL";

    /// <summary>The score and pp penalty the mark carries, 0.95x on both (owner decision, backlog 398).</summary>
    public const double Penalty = 0.95;

    /// <summary>
    /// Why a score was judged to be on a version that is not the current gameplay, so it was
    /// marked. Reported per reason by the dry run so the owner can size each arm before the backfill.
    /// </summary>
    public enum MarkReason
    {
        /// <summary>The score has no <c>score_tokens</c> row: nothing names the version it was played on.</summary>
        MissingToken,

        /// <summary>A token hash no stored version's .osu produces (a manifest edited outside the ingest, a missing blob).</summary>
        NoMatchingVersion,

        /// <summary>The played version exists and its gameplay fingerprint differs from the current one.</summary>
        ChangedGameplay,
    }

    /// <summary>
    /// One score the rule examines. Carries the version-rule columns plus the two the mark writes.
    /// </summary>
    public sealed record Candidate(
        long ScoreId,
        long SetId,
        long BeatmapId,
        string CurrentChecksum,
        int CurrentVersion,
        string? PlayedHash,
        long TotalScore,
        string? ModsJson);

    /// <summary>
    /// The counts a dry run produces, WITHOUT touching a row: how many scores each reason would
    /// mark, and how many would be left alone.
    /// </summary>
    public sealed record Report(int MissingToken, int NoMatchingVersion, int ChangedGameplay, int Kept)
    {
        /// <summary>How many rows the sweep would mark in total.</summary>
        public int Marked => MissingToken + NoMatchingVersion + ChangedGameplay;

        /// <summary>How many rows were examined.</summary>
        public int Examined => Marked + Kept;
    }

    /// <summary>What a run did: how many rows were marked, and the reason breakdown.</summary>
    public sealed record Outcome(int Marked, Report Report);

    /// <summary>
    /// The boot sweep: every score, on every set. <paramref name="eviction"/> is the live board
    /// memo, forgotten per affected set.
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
    /// owner runs this before the prod backfill to see how many rows each reason would mark.
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
            "SELECT to_regclass('public.score_classic_marks') IS NOT NULL");

        if (!ready)
            return new Outcome(0, new Report(0, 0, 0, 0));

        // Every score, ranked or not (owner scope), that does not ALREADY carry the mark. The mark
        // is the idempotence key: a marked row stops being a candidate here, so a rerun is a no-op
        // independent of the audit table's guard.
        var candidates = (await conn.QueryAsync<Candidate>(
                """
                SELECT s.id               AS ScoreId,
                       b.set_id           AS SetId,
                       b.id               AS BeatmapId,
                       b.checksum_md5     AS CurrentChecksum,
                       bs.current_version AS CurrentVersion,
                       t.beatmap_hash     AS PlayedHash,
                       s.total_score      AS TotalScore,
                       s.mods::text       AS ModsJson
                FROM scores s
                JOIN beatmaps b ON b.id = s.beatmap_id
                JOIN beatmapsets bs ON bs.id = b.set_id
                LEFT JOIN (SELECT DISTINCT ON (score_id) score_id, beatmap_hash
                           FROM score_tokens
                           WHERE score_id IS NOT NULL
                           ORDER BY score_id, id) t ON t.score_id = s.id
                WHERE NOT EXISTS (
                          SELECT 1 FROM jsonb_array_elements(COALESCE(s.mods, '[]'::jsonb)) e
                          WHERE upper(e->>'acronym') = @classic)
                  AND (@setId::bigint IS NULL OR b.set_id = @setId)
                """,
                new { classic = ClassicAcronym, setId }))
            .ToList();

        int missing = 0, noMatch = 0, changed = 0, kept = 0, marked = 0;
        var rule = new PlayedVersionRule(fileStore, logger);

        // Per set, so the cache eviction at the end names exactly the boards that changed, and only
        // when something actually changed (a dry run and a no-op pass both evict nothing).
        var markedSets = new HashSet<long>();

        foreach (var row in candidates)
        {
            var verdict = await rule.JudgeAsync(
                conn, row.SetId, row.BeatmapId, row.CurrentChecksum, row.CurrentVersion, row.PlayedHash, ct);

            if (PlayedVersionRule.IsCarried(verdict))
            {
                kept++;
                continue;
            }

            // Unknown and Changed both mark here (the strict arm). Which one it was is only for the
            // report: a missing token and a hash no version produces are both Unknown to the rule,
            // and are told apart by whether the row names a hash at all.
            MarkReason reason = verdict == PlayedVersionRule.Verdict.Changed
                ? MarkReason.ChangedGameplay
                : row.PlayedHash is null
                    ? MarkReason.MissingToken
                    : MarkReason.NoMatchingVersion;

            switch (reason)
            {
                case MarkReason.MissingToken: missing++; break;
                case MarkReason.NoMatchingVersion: noMatch++; break;
                default: changed++; break;
            }

            if (dryRun)
                continue;

            long repriced = RepricedTotal(row);
            string markedMods = AppendClassic(row.ModsJson);

            await using var tx = await conn.BeginTransactionAsync(ct);

            // Guarded on the mark still being absent, so a row marked between the read above and
            // here is a no-op rather than a double penalty (the mark is the idempotence key, and
            // this makes the guard atomic with the write). pp_version is stamped to 0 so PpBackfill,
            // which runs immediately after this in the boot chain, reprices the row at 0.95x on the
            // same boot (a targeted stamp, not a VERSION bump; see the class note).
            int written = await conn.ExecuteAsync(
                """
                UPDATE scores
                SET mods = CAST(@mods AS jsonb),
                    total_score = @total,
                    pp_version = 0
                WHERE id = @scoreId
                  AND NOT EXISTS (
                          SELECT 1 FROM jsonb_array_elements(COALESCE(mods, '[]'::jsonb)) e
                          WHERE upper(e->>'acronym') = @classic)
                """,
                new
                {
                    scoreId = row.ScoreId,
                    mods = markedMods,
                    total = repriced,
                    classic = ClassicAcronym,
                },
                tx);

            if (written > 0)
            {
                await conn.ExecuteAsync(
                    """
                    INSERT INTO score_classic_marks (score_id, reason, set_id)
                    VALUES (@scoreId, @reason, @setId)
                    ON CONFLICT (score_id) DO NOTHING
                    """,
                    new { scoreId = row.ScoreId, reason = ReasonKey(reason), setId = row.SetId }, tx);

                markedSets.Add(row.SetId);
                marked++;
            }

            await tx.CommitAsync(ct);
        }

        if (eviction != null && marked > 0)
            foreach (long affected in markedSets)
                await eviction.AfterSetStatusAsync(affected);

        if (marked > 0 || kept > 0)
        {
            logger.LogInformation(
                "Classic marks{Scope}: {Marked} of {Examined} scores marked (missing token {Missing}, no matching version {NoMatch}, changed gameplay {Changed}); {Kept} left alone.",
                setId is { } scoped ? $" (set {scoped})" : "", marked, candidates.Count, missing, noMatch, changed, kept);
        }

        return new Outcome(marked, new Report(missing, noMatch, changed, kept));
    }

    /// <summary>
    /// The repriced stored total, 0.95x, rounded the way the client rounds a modded total
    /// (<c>Math.Round</c> with <see cref="MidpointRounding.AwayFromZero"/>, so a 0.5 rounds up rather
    /// than to even). No ceiling re-check is needed: the mark only lowers a total (see the ceiling
    /// paragraph on the class).
    /// </summary>
    private static long RepricedTotal(Candidate row)
        => (long)Math.Round(row.TotalScore * Penalty, MidpointRounding.AwayFromZero);

    /// <summary>
    /// The stored mods blob with "CL" appended: a JSON array of <c>{"acronym":"CL"}</c>, matching
    /// the wire shape <see cref="ScoreMods.Parse"/> reads. A routing/parse failure yields a fresh
    /// single-entry array rather than corrupting the blob, and a blob that is already an array is
    /// extended in place so the row's own mods (and any rate settings) survive.
    /// </summary>
    private static string AppendClassic(string? modsJson)
    {
        JsonArray array;

        try
        {
            array = JsonNode.Parse(string.IsNullOrWhiteSpace(modsJson) ? "[]" : modsJson) as JsonArray ?? new JsonArray();
        }
        catch (System.Text.Json.JsonException)
        {
            array = new JsonArray();
        }

        array.Add(new JsonObject { ["acronym"] = ClassicAcronym });

        return array.ToJsonString();
    }

    /// <summary>
    /// The stored spelling of a mark reason (<c>score_classic_marks.reason</c>): snake_case, stable,
    /// so a query or a later report can group on it.
    /// </summary>
    public static string ReasonKey(MarkReason reason) => reason switch
    {
        MarkReason.MissingToken => "missing_token",
        MarkReason.NoMatchingVersion => "no_matching_version",
        MarkReason.ChangedGameplay => "changed_gameplay",
        _ => "unknown",
    };
}
