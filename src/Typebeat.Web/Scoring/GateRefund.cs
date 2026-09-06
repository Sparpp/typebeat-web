using Dapper;
using Newtonsoft.Json;
using Typebeat.Web.Data;

namespace Typebeat.Web.Scoring;

/// <summary>
/// The shared machinery behind every startup pass that restores the <c>ranked</c> flag on a score
/// stored unranked for a reason that was never the player's. Two of them correct a since-fixed bug
/// in <see cref="PlayTimeGate"/>, one per correction, and differ only in a handful of values:
/// <list type="bullet">
/// <item><see cref="SkipGateRefund"/> (016): the gate ignored the in-game skip button.</item>
/// <item><see cref="RateGateRefund"/> (017): the gate ignored the play's rate mod.</item>
/// </list>
///
/// <para>
/// SINCE BACKLOG 270 IT ALSO CARRIES A THIRD KIND OF PASS, <see cref="SetRankRefund"/>, which is
/// not a correction to the gate at all: it re-ranks a play that was honest in every way and was
/// stored unranked only because its SET was still pending at submit time. Everything in this file
/// except the two gate bounds is exactly what such a pass needs (the candidate query, the
/// re-derivation of every submit-time condition, the audit trail), so it is a <see cref="Plan"/>
/// like the others, flagged <see cref="Plan.Standing"/>. See that property for what the flag
/// changes and why.
/// </para>
///
/// <para>
/// Everything else is identical between all three and lives here exactly once: which rows are even
/// candidates, the re-derivation of every submit-time condition the pass itself did not decide, the
/// <c>score_refunds</c> row, and the audit trail. A <see cref="Plan"/> supplies the handful of
/// things that genuinely vary (the key, an extra SQL candidate filter, the two bounds, the per-row
/// test for "this correction is the one that applies here", and whether the pass is
/// <see cref="Plan.Standing"/>).
/// </para>
///
/// <para>
/// It lives in C#, not in the migration files, for the reason 016's header spells out: the skip
/// allowance is derived from a blob in the file store (no query can see it), and deciding whether
/// the GATE is the reason a row is unranked needs the submit path's own recompute, which already
/// exists here (<see cref="ScoringContract"/>, <see cref="ModMultiplier"/>) and must not be mirrored
/// a third time in SQL. The audit trail is the <c>score_refunds</c> table, in the same shape 015
/// uses <c>score_rescales</c>, keyed per pass; for a one-shot pass that row is also the guard that
/// makes a rerun a no-op, and for a standing one idempotency is the candidate query itself.
/// </para>
///
/// <para>
/// PRECISION OVER RECALL. A score is refunded only when every other submit-time condition can be
/// re-derived from stored data and holds. Everything the gate itself did not decide is re-checked:
/// the set is ranked, the play passed and was fully judged, the statistics are valid, no
/// always-unranked mod was used, the build is not blocked, and the stored total is inside the
/// ceiling its own statistics justify. A row failing any of those was unranked for a DIFFERENT
/// reason (a mod-multiplier violation from tasks 36/44, a blocked build, a fail, an unranked set)
/// and is left exactly as it is.
/// </para>
///
/// <para>
/// WHAT IT REFUSES TO GUESS.
///  * <c>total_score_without_mods</c> is not persisted, so the submit path's exact
///    <c>withinBounds</c> test cannot be reproduced. The strongest available necessary condition is
///    used instead (stored total inside the mod-priced ceiling of the statistics), which catches
///    gross tampering but cannot prove the original submission was in bounds.
///  * Set status is read as it is NOW. A play made while its set was still <c>pending</c>, that ALSO
///    failed the gate, on a set promoted to <c>ranked</c> since, is indistinguishable from a genuine
///    victim; there is no <c>ranked_at</c> column and no per-score reason column to separate them.
///  * Drain, skippable and the stored mod stack are read as they are NOW. If the map was re-uploaded
///    with different timing between the play and the refund, the bound re-evaluated here is the new
///    map's.
/// </para>
///
/// <para>
/// AGGREGATES. Nothing to do, deliberately, and this holds for every pass built on it. Both
/// submission paths accrue <c>user_stats</c> (play_count / total_score / play_time_s / hit_counts)
/// on <c>StatisticsValid &amp;&amp; withinBounds</c> alone; the play-time gate has never been part of
/// that condition, so a gate-unranked score ALREADY contributed its full share and re-ranking it
/// must not add anything a second time. Every other scoring surface (beatmap leaderboards,
/// <see cref="GlobalRanking.PerUserCumulativeSql"/>, the profile's best scores, the set page) is a
/// live query over <c>scores.ranked</c>, so flipping the flag is the whole of the refund. There is
/// no leaderboard or pace cache to invalidate.
/// </para>
/// </summary>
public static class GateRefund
{
    /// <summary>
    /// One refund pass, as its runner configures it.
    /// </summary>
    /// <param name="MigrationKey">
    /// The <c>score_refunds.migration</c> value that guards and audits this pass. Its own key, so a
    /// later correction reconsiders rows an earlier one declined without undoing anything.
    /// </param>
    /// <param name="CandidateFilterSql">
    /// Extra SQL AND-ed into the candidate query, narrowing it to rows this correction could
    /// possibly move (<c>s</c> is scores, <c>b</c> beatmaps). A constant fragment, never anything
    /// derived from a request.
    /// </param>
    /// <param name="OldRequiredSeconds">
    /// The bound the gate applied WHEN THE ROW WAS SUBMITTED. The refund only touches a row this
    /// fired on, so a score that already cleared the gate of its day is left alone: whatever
    /// unranked it, it was not the gate.
    /// </param>
    /// <param name="NewRequiredSeconds">The corrected bound, which the row must clear.</param>
    /// <param name="AppliesTo">
    /// The per-row precondition for this correction being the relevant one (e.g. 017: the play
    /// carries an up-rate). Anything expressible in SQL belongs in
    /// <paramref name="CandidateFilterSql"/> instead.
    /// </param>
    /// <param name="Summary">What the log line says the refunded rows now clear.</param>
    /// <param name="Standing">
    /// FALSE (the default) for a one-shot MIGRATION pass: it corrects a bug in the gate, so a row
    /// it declines can only ever be reconsidered by a LATER correction, its <c>score_refunds</c>
    /// row is the guard that makes a rerun a no-op, and a reviewer who unranks a refunded score by
    /// hand must have that stick.
    ///
    /// <para>TRUE for a pass whose condition is INTRINSICALLY RE-CHECKABLE, i.e. one that asks a
    /// question about the world as it is now rather than about a bug that has been fixed. Two
    /// things follow, and both follow from that one fact:</para>
    ///
    /// <list type="bullet">
    /// <item>THE GUARD IS DROPPED. The pass reconsiders every unranked row at every boot, and the
    /// <c>score_refunds</c> insert becomes an audit record rather than the thing that decides. It
    /// stays idempotent because a row it re-ranks stops being a candidate (the query takes
    /// unranked rows only), so a second run in the same boot moves nothing.</item>
    /// <item>THE OLD BOUND IS NOT TESTED. <paramref name="OldRequiredSeconds"/> exists so a gate
    /// correction can prove the gate FIRED on this row; a standing pass is not claiming the gate
    /// was ever wrong, so it requires only that <paramref name="NewRequiredSeconds"/> is cleared
    /// today, alongside every other submit-time condition.</item>
    /// </list>
    /// </param>
    public sealed record Plan(
        string MigrationKey,
        string CandidateFilterSql,
        Func<CandidateRow, double> OldRequiredSeconds,
        Func<CandidateRow, double> NewRequiredSeconds,
        Func<CandidateRow, bool> AppliesTo,
        string Summary,
        bool Standing = false);

    public static async Task<int> RunAsync(Db db, ILogger logger, Plan plan, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);

        // The audit table only exists once 016 has been applied; on a database mid-migration
        // (or a test harness stopped earlier) there is simply nothing to do.
        bool ready = await conn.ExecuteScalarAsync<bool>(
            "SELECT to_regclass('public.score_refunds') IS NOT NULL");

        if (!ready)
            return 0;

        // A STANDING pass reconsiders every unranked row at every boot, so it has no
        // already-refunded clause at all; a one-shot pass is guarded by its own key. Built as a
        // string rather than inlined, because a nested raw literal inside the interpolation hole
        // of the query below would be a good deal harder to read than the query it filters.
        string alreadyRefunded = plan.Standing
            ? ""
            : "AND NOT EXISTS (SELECT 1 FROM score_refunds r WHERE r.migration = @migration AND r.score_id = s.id)";

        // Candidates: unranked, passed, on a currently-ranked set, with an anchored elapsed time,
        // not already refunded by THIS pass, plus whatever the plan narrows to. Everything else is
        // decided per row below.
        var candidates = (await conn.QueryAsync<CandidateRow>(
            $"""
            SELECT s.id                                              AS scoreId,
                   s.user_id                                         AS userId,
                   s.total_score                                     AS totalScore,
                   s.max_combo                                       AS maxCombo,
                   s.statistics::text                                AS statisticsJson,
                   s.maximum_statistics::text                        AS maximumStatisticsJson,
                   s.mods::text                                      AS modsJson,
                   -- EXTRACT yields numeric; Dapper needs the double the record declares.
                   EXTRACT(EPOCH FROM (s.ended_at - s.started_at))::double precision AS elapsedS,
                   b.drain_length_s                                  AS drainLengthS,
                   b.skippable_s                                     AS skippableS,
                   COALESCE(bd.blocked, false)                       AS buildBlocked
            FROM scores s
            JOIN beatmaps b ON b.id = s.beatmap_id
            JOIN beatmapsets bs ON bs.id = b.set_id
            LEFT JOIN builds bd ON bd.id = s.build_id
            WHERE NOT s.ranked
              AND s.passed
              AND s.started_at IS NOT NULL
              AND bs.status = 'ranked'
              AND {plan.CandidateFilterSql}
              {alreadyRefunded}
            """,
            new { migration = plan.MigrationKey })).ToList();

        if (candidates.Count == 0)
            return 0;

        int refunded = 0;

        foreach (var row in candidates)
        {
            if (!Qualifies(plan, row))
                continue;

            await using var tx = await conn.BeginTransactionAsync(ct);

            // The insert is the guard: a duplicate key means a concurrent boot got there first, so
            // this one must not flip the flag again.
            int logged = await conn.ExecuteAsync(
                """
                INSERT INTO score_refunds (migration, score_id, elapsed_s, old_required_s, new_required_s)
                VALUES (@migration, @scoreId, @elapsed, @oldRequired, @newRequired)
                ON CONFLICT (migration, score_id) DO NOTHING
                """,
                new
                {
                    migration = plan.MigrationKey,
                    scoreId = row.ScoreId,
                    elapsed = row.ElapsedS,
                    oldRequired = plan.OldRequiredSeconds(row),
                    newRequired = plan.NewRequiredSeconds(row),
                }, tx);

            // For a one-shot pass the insert IS the guard: a duplicate key means a concurrent boot
            // got there first, so this one must not flip the flag again. A STANDING pass has no
            // guard row to lose the race on (see Plan.Standing), and a second flip of an already
            // flipped row is a no-op anyway, so it proceeds on its own candidate reading.
            if (plan.Standing || logged > 0)
            {
                // pp_version = 0 ALONGSIDE THE FLAG, and forgetting it is the bug backlog 270
                // found. PpBackfill's sweep predicate is `pp_version < VERSION`, so a refunded row
                // already stamped at the current version is SKIPPED: it stays at pp 0 while
                // reading as priced. It only ever worked because both existing passes shipped
                // beside a VERSION bump, which dragged every row in regardless. Every refund pass
                // stamps it now, so a refund landing on its own still reprices.
                await conn.ExecuteAsync(
                    "UPDATE scores SET ranked = true, pp_version = 0 WHERE id = @scoreId AND NOT ranked",
                    new { scoreId = row.ScoreId }, tx);

                refunded++;
            }

            await tx.CommitAsync(ct);
        }

        if (refunded > 0)
        {
            logger.LogInformation(
                "{Migration}: {Refunded}/{Candidates} unranked scores re-ranked ({Summary}).",
                plan.MigrationKey, refunded, candidates.Count, plan.Summary);
        }

        return refunded;
    }

    /// <summary>
    /// Whether this row was unranked by the play-time gate ALONE and clears the corrected bound.
    /// Public so a migration test can assert the decision directly, row by row.
    /// </summary>
    public static bool Qualifies(Plan plan, CandidateRow row)
    {
        // The correction has to have something to correct here at all.
        if (!plan.AppliesTo(row))
            return false;

        // The gate must actually have been the thing that fired: a play that already cleared the
        // bound of its day was unranked by something else entirely. A STANDING pass makes no such
        // claim (see Plan.Standing) and asks only that the current bound is cleared, below.
        if (!plan.Standing && row.ElapsedS >= plan.OldRequiredSeconds(row))
            return false;

        // ... and the corrected bound must be cleared.
        if (row.ElapsedS < plan.NewRequiredSeconds(row))
            return false;

        if (row.BuildBlocked)
            return false;

        // Read from the ONE list (backlog 270). This used to be a private copy that claimed to
        // mirror ScoreEndpoints' and was three acronyms behind it, so a Conductor, Dyslexia or
        // Puppeteer play the submit path refused could be re-ranked here.
        if (UnrankedMods.StackIsUnranked(row.Mods.Select(m => m.Acronym)))
            return false;

        var statistics = ParseCounts(row.StatisticsJson);
        var maximumStatistics = ParseCounts(row.MaximumStatisticsJson);
        var recomputed = ScoringContract.Recompute(statistics, maximumStatistics, row.MaxCombo);

        if (!recomputed.StatisticsValid)
            return false;

        // A "passed" row whose judged cells fall short of the map's total was untrustworthy at
        // submit time and stays that way.
        if (recomputed.AccuracyProgress < 1)
            return false;

        // The best available stand-in for the submit path's withinBounds: total_score_without_mods
        // is not persisted, but a legitimate base can never exceed the ceiling its own statistics
        // justify, so neither can the mod-priced total. (Migration 015 only ever shrank stored
        // totals, so a rescaled Half Time row still satisfies this.)
        double multiplier = ModMultiplier.MaxForStack(row.Mods.Select(m => (m.Acronym, m.SpeedChange)));

        if (row.TotalScore < 0 || row.TotalScore > ModMultiplier.TotalScoreCeiling(recomputed.TotalScoreCeiling, multiplier))
            return false;

        return true;
    }

    /// <summary>One refund candidate, as stored. Times in seconds, both lengths in map time.</summary>
    public sealed record CandidateRow(
        long ScoreId,
        long UserId,
        long TotalScore,
        int MaxCombo,
        string? StatisticsJson,
        string? MaximumStatisticsJson,
        string? ModsJson,
        double ElapsedS,
        double DrainLengthS,
        double SkippableS,
        bool BuildBlocked)
    {
        private IReadOnlyList<StoredMod>? parsedMods;

        /// <summary>The stored stack, parsed once per row (never part of the record's equality).</summary>
        public IReadOnlyList<StoredMod> Mods => parsedMods ??= ParseMods(ModsJson);

        /// <summary>
        /// The rate the play ran at, read exactly as the submit path reads a submitted stack:
        /// 1.0 with no rate mod, the slowest submitted speed_change otherwise.
        /// </summary>
        public double Rate => RateMods.EffectiveRate(Mods.Select(m => (m.Acronym, m.SpeedChange)));
    }

    private static Dictionary<string, int> ParseCounts(string? json)
        => JsonConvert.DeserializeObject<Dictionary<string, int>>(json ?? "{}") ?? new Dictionary<string, int>();

    /// <summary>The stored mods jsonb, read exactly as the submit path reads a submitted stack.</summary>
    private static List<StoredMod> ParseMods(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new List<StoredMod>();

        try
        {
            return JsonConvert.DeserializeObject<List<StoredMod>>(json) ?? new List<StoredMod>();
        }
        catch (JsonException)
        {
            // A row whose mods blob is not a mod list describes no stack; treat it as no mods,
            // exactly as 015 does for a non-array blob.
            return new List<StoredMod>();
        }
    }

    public sealed class StoredMod
    {
        [JsonProperty("acronym")]
        public string? Acronym { get; set; }

        [JsonProperty("settings")]
        public Dictionary<string, object>? Settings { get; set; }

        /// <summary>The clamped rate this stack member is priced at, or null when it is not a rate mod.</summary>
        public double? SpeedChange => RateMods.ReadSpeedChange(Acronym, Settings);
    }
}
