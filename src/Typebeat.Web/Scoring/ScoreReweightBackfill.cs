using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using Typebeat.Web.Caching;
using Typebeat.Web.Data;

namespace Typebeat.Web.Scoring;

/// <summary>
/// The SCORE REWEIGHT backfill (owner, 2026-10-04; migration 046).
///
/// <para>
/// total_score's two halves were osu's 500000/500000: the first scaled by cumulative COMBO POSITION,
/// the second by map PROGRESS at accuracy to the fifth power. The owner moved the weight onto the
/// accuracy half, 300000/700000, so a score leans on how well the player typed rather than how long
/// their streak ran (a perfect play still totals exactly 1000000). Every stored row was priced on
/// the old split and must be re-based, exactly as 015's Half Time rescale re-based the rate curve.
/// </para>
///
/// <para>
/// THE TRANSFORM, and why it needs no replay. A stored total is round(base x multiplier) where base
/// is the two-term sum. Writing the OLD second term as t2 = 500000 x acc^5 x accuracyProgress, the
/// old first term is old_base - t2, so
///     new_base = 0.6 x old_base + 0.8 x t2
/// (0.3/0.5 = 0.6, 0.7/0.5 = 1.4, and 1.4 - 0.6 = 0.8). Every other mod factor in the stack cancels
/// out of the ratio, and t2 is recoverable from the stored accuracy and statistics: accuracyProgress
/// is judged cells over the map's cells, and judge/cell counts are in the stored dictionaries. So the
/// row is re-based from what is stored, never re-derived from a replay.
/// </para>
///
/// <para>
/// THE MOD MULTIPLIER IS READ FROM THE ONE DEFINITION. <see cref="ModMultiplier.MaxForStack"/> is the
/// same call the submit path bounds a total with, so a SQL copy that could drift is avoided; that is
/// the reason this is a C# sweep rather than a pure-SQL migration, the same choice
/// <see cref="SetRankClassicMark"/> (043) made for its own reprice.
/// </para>
///
/// <para>
/// IT IS NOT IDEMPOTENT BY ARITHMETIC, so the audit table is the guard. The transform shrinks one
/// term and grows the other; applying it twice would shrink an already-shrunk total. Every row it
/// moves is recorded in <c>score_reweights</c> (migration 046), and the candidate query EXCLUDES
/// recorded rows, so a second boot in the same process or after a restart selects nothing. A row
/// whose write raced another boot is protected by the ON CONFLICT DO NOTHING on the audit insert
/// plus the re-read guard below.
/// </para>
///
/// <para>
/// pp IS NOT TOUCHED HERE. pp prices from accuracy, misses and the star rating, none of which the
/// reweight moves, so no pp changes. rank is not touched either: grades are accuracy-based (PR 17)
/// and do not read total_score.
/// </para>
/// </summary>
public static class ScoreReweightBackfill
{
    /// <summary>How many scores this pass moved.</summary>
    public sealed record Outcome(int Reweighted, long Examined);

    /// <summary>The whole catalogue. The natural home is the boot chain, so deploying IS the backfill.</summary>
    public static async Task<Outcome> RunAsync(Db db, ILogger logger, CacheEviction? eviction = null, CancellationToken ct = default)
        => await runAsync(db, logger, setId: null, eviction, ct);

    /// <summary>The same pass, scoped to one set (the re-rank transition), for completeness.</summary>
    public static async Task<Outcome> RunAsync(Db db, ILogger logger, long setId, CancellationToken ct = default)
        => await runAsync(db, logger, setId, eviction: null, ct);

    private static async Task<Outcome> runAsync(Db db, ILogger logger, long? setId, CacheEviction? eviction, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);

        // The audit table only exists once 046 is applied; on a database mid-migration there is
        // nothing to do (the same guard SetRankClassicMark uses for 043).
        bool ready = await conn.ExecuteScalarAsync<bool>(
            "SELECT to_regclass('public.score_reweights') IS NOT NULL");

        if (!ready)
            return new Outcome(0, 0);

        // Every score not already recorded. The audit table is the idempotence guard here (the
        // transform is not its own fixed point), so a recorded row stops being a candidate.
        var candidates = (await conn.QueryAsync<Candidate>(
                """
                SELECT s.id            AS ScoreId,
                       b.set_id        AS SetId,
                       s.total_score   AS TotalScore,
                       s.accuracy      AS Accuracy,
                       s.mods::text    AS ModsJson,
                       s.statistics::text        AS StatisticsJson,
                       s.maximum_statistics::text AS MaximumStatisticsJson
                FROM scores s
                JOIN beatmaps b ON b.id = s.beatmap_id
                WHERE NOT EXISTS (SELECT 1 FROM score_reweights r WHERE r.score_id = s.id)
                  AND (@setId::bigint IS NULL OR b.set_id = @setId)
                """,
                new { setId }))
            .ToList();

        if (candidates.Count == 0)
            return new Outcome(0, 0);

        int moved = 0;
        var touchedSets = new HashSet<long>();

        foreach (var row in candidates)
        {
            long newTotal = ReweightedTotal(row);

            // A row already at the new value (a re-run where the write landed but the audit insert
            // did not) is recorded without a second write; the audit insert is then the guard.
            if (newTotal == row.TotalScore)
            {
                await conn.ExecuteAsync(
                    "INSERT INTO score_reweights (score_id, old_total, new_total) VALUES (@id, @old, @new) ON CONFLICT (score_id) DO NOTHING",
                    new { id = row.ScoreId, old = row.TotalScore, @new = newTotal });
                continue;
            }

            await using var tx = await conn.BeginTransactionAsync(ct);

            // Guarded on the row not being recorded yet, so a row moved between the read and here is
            // a no-op rather than a double shrink.
            int written = await conn.ExecuteAsync(
                """
                UPDATE scores SET total_score = @total
                WHERE id = @scoreId
                  AND NOT EXISTS (SELECT 1 FROM score_reweights r WHERE r.score_id = scores.id)
                """,
                new { scoreId = row.ScoreId, total = newTotal }, tx);

            if (written > 0)
            {
                await conn.ExecuteAsync(
                    "INSERT INTO score_reweights (score_id, old_total, new_total) VALUES (@id, @old, @new) ON CONFLICT (score_id) DO NOTHING",
                    new { id = row.ScoreId, old = row.TotalScore, @new = newTotal }, tx);

                touchedSets.Add(row.SetId);
                moved++;
            }

            await tx.CommitAsync(ct);
        }

        if (eviction != null && moved > 0)
            foreach (long affected in touchedSets)
                await eviction.AfterSetStatusAsync(affected);

        if (moved > 0)
        {
            logger.LogInformation(
                "Score reweight{Scope}: {Moved} of {Examined} scores re-based onto the 300000/700000 split.",
                setId is { } scoped ? $" (set {scoped})" : "", moved, candidates.Count);
        }

        return new Outcome(moved, candidates.Count);
    }

    /// <summary>
    /// The stored total re-based onto the 0.3/0.7 split. Writing the OLD second term as
    /// <c>t2 = 500000 x acc^5 x accuracyProgress</c> (see <see cref="ScoringContract.OldAccuracyTerm"/>),
    /// the new base is <c>0.6 x old_base + 0.8 x t2</c>, and the stored total is its base times the
    /// exact mod multiplier the submit path bounds with (so every other mod factor cancels).
    /// </summary>
    public static long ReweightedTotal(Candidate row)
    {
        var statistics = ParseCounts(row.StatisticsJson);
        var maximum = ParseCounts(row.MaximumStatisticsJson);
        var recomputed = ScoringContract.Recompute(statistics, maximum, maxCombo: 0);

        double multiplier = ModMultiplier.MaxForStack(ScoreMods.Parse(row.ModsJson).Select(m => ((string?)m.Acronym, m.Rate)));

        // new_base = 0.6 x old_base + 0.8 x t2, with old_base = old_total / multiplier.
        double oldBase = row.TotalScore / multiplier;
        double newBase = 0.6 * oldBase + 0.8 * ScoringContract.OldAccuracyTerm(row.Accuracy, recomputed.AccuracyProgress);

        return (long)Math.Round(newBase * multiplier, MidpointRounding.AwayFromZero);
    }

    /// <summary>The stored dictionary as the server reads it elsewhere (GateRefund's idiom).</summary>
    private static Dictionary<string, int> ParseCounts(string? json)
        => Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, int>>(json ?? "{}")
           ?? new Dictionary<string, int>();

    public sealed record Candidate(
        long ScoreId,
        long SetId,
        long TotalScore,
        double Accuracy,
        string? ModsJson,
        string? StatisticsJson,
        string? MaximumStatisticsJson);
}
