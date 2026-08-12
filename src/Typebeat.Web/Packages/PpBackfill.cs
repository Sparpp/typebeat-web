using Dapper;
using Typebeat.Web.Data;
using Typebeat.Web.Scoring;

namespace Typebeat.Web.Packages;

/// <summary>
/// Startup recompute of <c>scores.pp</c> for every score row whose stamped
/// <see cref="PerformancePoints.VERSION"/> is behind the current one (020_performance_points.sql).
/// Same shape as <see cref="PaceBackfill"/>: one pass at boot, a per-row try/catch so a bad row
/// cannot abort the sweep, one summary log line, and no-op once everything is current.
///
/// <para>
/// IT READS ONLY COLUMNS, no blobs and no network: the score's own statistics/mods jsonb plus the
/// six star ratings on its beatmap (three for the map, three for the map the client's Literate mod
/// converts it into; 029_literate_stars.sql). That is why it can run unconditionally on every boot,
/// and it is the constraint that decides the storage shape: a mod combination whose rating is not
/// in a column cannot be priced here at all.
/// </para>
///
/// <para>
/// IT MUST RUN AFTER <see cref="PaceBackfill"/> (Program.cs). Two reasons, and they are the whole
/// reason this class exists rather than pp being computed purely at submission time:
/// <list type="number">
/// <item>PaceBackfill is what FILLS <c>beatmaps.sr_dt</c> / <c>sr_ht</c>. A DT/HT play whose rate
/// rating is still NULL cannot be priced, so it is deliberately left stale (pp 0, version 0) and
/// retried on the next boot rather than being stamped at zero forever. Since backlog 90 a HALF TIME
/// play needs BOTH columns, <c>sr_ht</c> to price it and <c>sr_dt</c> to mirror against; a map
/// carrying only one of the two therefore leaves its HT plays pending. Since backlog 144 the same
/// holds one level up: a LITERATE play needs the converted map's ratings, so a map the sweep has
/// not reached leaves even a plain no-rate Literate play pending.</item>
/// <item>PaceBackfill is also what INVALIDATES rows: when it rewrites a beatmap's ratings it stamps
/// every score on that map back to version 0, so a stored pp can never outlive the star rating it
/// was computed from.</item>
/// </list>
/// It should also run after the score-refund sweeps (SkipGateRefund / RateGateRefund), which flip
/// <c>scores.ranked</c> on: a row those re-rank in this same boot is picked up here in the same
/// pass, so a refunded play earns its pp immediately instead of on the following boot.
/// </para>
///
/// <para>
/// UNRANKED ROWS ARE STILL PROCESSED, and settle at pp 0 stamped with the current version, so they
/// drop out of the stale set instead of being rescanned forever. Their eligibility is not frozen by
/// that: <see cref="PpRanking"/> re-checks ranked/passed/set-status at read time anyway, and the
/// only thing a stored 0 asserts is "this play, as played, is worth nothing".
/// </para>
/// </summary>
public static class PpBackfill
{
    public static async Task RunAsync(Db db, ILogger logger, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);

        var stale = (await conn.QueryAsync<StaleScore>(
                """
                SELECT s.id                 AS ScoreId,
                       s.ranked             AS Ranked,
                       s.accuracy           AS Accuracy,
                       s.max_combo          AS MaxCombo,
                       s.mods::text         AS ModsJson,
                       s.statistics::text   AS StatisticsJson,
                       b.difficulty_rating  AS BaseStars,
                       b.sr_dt              AS SrDt,
                       b.sr_ht              AS SrHt,
                       b.sr_literate        AS SrLiterate,
                       b.sr_literate_dt     AS SrLiterateDt,
                       b.sr_literate_ht     AS SrLiterateHt
                FROM scores s
                JOIN beatmaps b ON b.id = s.beatmap_id
                WHERE s.pp_version < @version
                """,
                new { version = PerformancePoints.VERSION }))
            .ToList();

        if (stale.Count == 0)
            return;

        int written = 0, pending = 0, failed = 0;

        foreach (var row in stale)
        {
            try
            {
                var (pp, settled) = PerformancePoints.ForScore(
                    row.Ranked,
                    ScoreMods.Parse(row.ModsJson),
                    PerformancePoints.CountNotes(row.StatisticsJson),
                    row.Accuracy,
                    row.MaxCombo,
                    row.BaseStars,
                    row.SrDt,
                    row.SrHt,
                    new PerformancePoints.LiterateStars(row.SrLiterate, row.SrLiterateDt, row.SrLiterateHt));

                if (!settled)
                {
                    // The map's rate rating is not stored yet. Leave the row at version 0 (and at
                    // whatever pp it holds, which for an unpriced row is 0) so the next boot, after
                    // PaceBackfill has reached that map, computes it properly.
                    pending++;
                    continue;
                }

                await conn.ExecuteAsync(
                    "UPDATE scores SET pp = @pp, pp_version = @version WHERE id = @id",
                    // The column is NOT NULL. A settled null is a play the formula refused to run
                    // for (unranked, or a custom rate), which stores 0 and stamps the current
                    // version so it drops out of the stale set for good.
                    new { id = row.ScoreId, pp = pp ?? 0, version = PerformancePoints.VERSION });

                written++;
            }
            catch (Exception ex)
            {
                failed++;
                logger.LogWarning(ex, "pp backfill failed for score {ScoreId}; will retry next startup.", row.ScoreId);
            }
        }

        logger.LogInformation(
            "pp backfill: {Written}/{Stale} scores recomputed to pp v{Version} ({Pending} awaiting a rate star rating, {Failed} errored).",
            written, stale.Count, PerformancePoints.VERSION, pending, failed);
    }

    private sealed record StaleScore(
        long ScoreId,
        bool Ranked,
        double Accuracy,
        int MaxCombo,
        string? ModsJson,
        string? StatisticsJson,
        double BaseStars,
        double? SrDt,
        double? SrHt,
        double? SrLiterate,
        double? SrLiterateDt,
        double? SrLiterateHt);
}
