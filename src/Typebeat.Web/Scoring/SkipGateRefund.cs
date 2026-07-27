using Dapper;
using Newtonsoft.Json;
using Typebeat.Web.Data;

namespace Typebeat.Web.Scoring;

/// <summary>
/// The refund half of 016_refund_skip_gate.sql: restores the <c>ranked</c> flag on scores that the
/// pre-task-47 play-time gate unranked purely because the player used the game's instrumental-skip
/// button. Runs at startup after <c>PaceBackfill</c> has filled <c>beatmaps.skippable_s</c> from the
/// stored .osu blobs, and is a no-op from then on.
///
/// <para>
/// It lives in C#, not in the migration file, for two reasons the migration's header spells out: the
/// skip allowance is derived from a blob in the file store (no query can see it), and deciding
/// whether the GATE is the reason a row is unranked needs the submit path's own recompute, which
/// already exists here (<see cref="ScoringContract"/>, <see cref="ModMultiplier"/>) and must not be
/// mirrored a third time in SQL. Idempotency and the audit trail are the migration's
/// <c>score_refunds</c> table, in the same shape 015 uses <c>score_rescales</c>.
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
///  * Drain and skippable are read as they are NOW. If the map was re-uploaded with different timing
///    between the play and the refund, the bound re-evaluated here is the new map's.
///  * Nothing is refunded on a map whose skip allowance is zero or unknown, which is exactly the
///    fail-toward-the-old-behaviour rule the gate itself follows.
/// </para>
///
/// <para>
/// AGGREGATES. Nothing to do, deliberately. Both submission paths accrue <c>user_stats</c>
/// (play_count / total_score / play_time_s / hit_counts) on <c>StatisticsValid &amp;&amp; withinBounds</c>
/// alone; the play-time gate was never part of that condition, so a gate-unranked score ALREADY
/// contributed its full share and re-ranking it must not add anything a second time. Every other
/// scoring surface (beatmap leaderboards, <see cref="GlobalRanking.PerUserCumulativeSql"/>, the
/// profile's best scores, the set page) is a live query over <c>scores.ranked</c>, so flipping the
/// flag is the whole of the refund. There is no leaderboard or pace cache to invalidate.
/// </para>
/// </summary>
public static class SkipGateRefund
{
    /// <summary>The score_refunds key; a future refund gets its own and its own guard.</summary>
    public const string MIGRATION_KEY = "016_refund_skip_gate";

    /// <summary>Mods that are unranked at any configuration (mirrors ScoreEndpoints' own set).</summary>
    private static readonly HashSet<string> always_unranked_mod_acronyms =
        new(StringComparer.OrdinalIgnoreCase) { "RX", "WU", "WD" };

    public static async Task<int> RunAsync(Db db, ILogger logger, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);

        // The audit table only exists once 016 has been applied; on a database mid-migration
        // (or a test harness stopped earlier) there is simply nothing to do.
        bool ready = await conn.ExecuteScalarAsync<bool>(
            "SELECT to_regclass('public.score_refunds') IS NOT NULL");

        if (!ready)
            return 0;

        // Candidates: unranked, passed, on a currently-ranked set, with a known skip allowance, an
        // anchored elapsed time, and not already refunded. Everything else is decided per row below.
        var candidates = (await conn.QueryAsync<CandidateRow>(
            """
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
              AND b.skippable_s > 0
              AND NOT EXISTS (SELECT 1 FROM score_refunds r
                              WHERE r.migration = @migration AND r.score_id = s.id)
            """,
            new { migration = MIGRATION_KEY })).ToList();

        if (candidates.Count == 0)
            return 0;

        int refunded = 0;

        foreach (var row in candidates)
        {
            if (!QualifiesForRefund(row))
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
                    migration = MIGRATION_KEY,
                    scoreId = row.ScoreId,
                    elapsed = row.ElapsedS,
                    oldRequired = PlayTimeGate.RequiredSeconds(row.DrainLengthS, 0),
                    newRequired = PlayTimeGate.RequiredSeconds(row.DrainLengthS, row.SkippableS),
                }, tx);

            if (logged > 0)
            {
                await conn.ExecuteAsync(
                    "UPDATE scores SET ranked = true WHERE id = @scoreId AND NOT ranked",
                    new { scoreId = row.ScoreId }, tx);

                refunded++;
            }

            await tx.CommitAsync(ct);
        }

        if (refunded > 0)
        {
            logger.LogInformation(
                "Skip-gate refund: {Refunded}/{Candidates} unranked scores re-ranked (elapsed cleared 0.9 x (drain - skippable)).",
                refunded, candidates.Count);
        }

        return refunded;
    }

    /// <summary>
    /// Whether this row was unranked by the play-time gate ALONE and clears the corrected bound.
    /// Public so the migration test can assert the decision directly, row by row.
    /// </summary>
    public static bool QualifiesForRefund(CandidateRow row)
    {
        // The gate must actually have been the thing that fired: a play that already cleared
        // 0.9 x drain was unranked by something else entirely.
        if (PlayTimeGate.Passes(row.ElapsedS, row.DrainLengthS, 0))
            return false;

        // ... and the corrected bound must be cleared.
        if (!PlayTimeGate.Passes(row.ElapsedS, row.DrainLengthS, row.SkippableS))
            return false;

        if (row.BuildBlocked)
            return false;

        var mods = ParseMods(row.ModsJson);

        if (mods.Any(m => m.Acronym != null && always_unranked_mod_acronyms.Contains(m.Acronym.Trim())))
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
        double multiplier = ModMultiplier.MaxForStack(mods.Select(m => (m.Acronym, m.SpeedChange)));

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
        bool BuildBlocked);

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

    private sealed class StoredMod
    {
        [JsonProperty("acronym")]
        public string? Acronym { get; set; }

        [JsonProperty("settings")]
        public Dictionary<string, object>? Settings { get; set; }

        /// <summary>The clamped rate this stack member is priced at, or null when it is not a rate mod.</summary>
        public double? SpeedChange => RateMods.ReadSpeedChange(Acronym, Settings);
    }
}
