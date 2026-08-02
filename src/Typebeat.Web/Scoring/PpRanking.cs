using System.Globalization;
using Dapper;
using Npgsql;

namespace Typebeat.Web.Scoring;

/// <summary>
/// The pp ranking: the MAIN global ranking (<c>/rankings</c>), the metric that replaced cumulative
/// score. <see cref="GlobalRanking"/> still defines the cumulative-score metric, which survives as
/// the score-farming board's second tab.
///
/// <para>
/// A player's total is osu's shape, per <c>docs/pp.md</c>: keep only the BEST-pp play per ranked
/// map (without the dedup, replays of one hard map would fill the whole list), sort those
/// descending and sum them with a decay, <c>Σ pp_i · DECAY^i</c>, over ALL of them. There is no
/// hard top-N truncation, so there is no cliff where the 11th-best play contributes exactly
/// nothing; <see cref="PerformancePoints.DECAY"/> makes the tail vanish on its own.
/// </para>
///
/// <para>
/// COMPUTED ON READ, never stored. At this scale (tens of users, low thousands of scores) the
/// aggregate is a single indexed query, and keeping it out of the schema is what makes raising the
/// decay later a one-line change with no migration and no recompute job. The per-play values it
/// sums ARE stored (<c>scores.pp</c>, written at submission and by
/// <see cref="Packages.PpBackfill"/>), because those depend on the map's star ratings and on the
/// statistics blob, which is exactly the work worth doing once.
/// </para>
///
/// <para>
/// ELIGIBILITY IS RE-CHECKED HERE, not baked into the stored value: the same
/// ranked-score-on-a-ranked-set join the cumulative metric uses. That is what makes an admin
/// un-ranking a set (or a single score) take effect on the board immediately, with no recompute.
/// The stored per-play pp only carries what is intrinsic to the PLAY (its difficulty, cleanliness,
/// mods and rate eligibility).
/// </para>
/// </summary>
public static class PpRanking
{
    private static readonly string decay_literal =
        PerformancePoints.DECAY.ToString("0.############", CultureInfo.InvariantCulture);

    /// <summary>
    /// Per-user total pp. Yields columns <c>user_id</c>, <c>total_pp</c> (double precision),
    /// <c>pp_play_count</c> (bigint, the number of deduped plays that contributed); only users with
    /// at least one pp-earning play appear. Embed as a subquery: it is the single source of truth
    /// for the metric, shared by the rankings board and any per-user lookup.
    /// </summary>
    public static readonly string PerUserTotalSql =
        $"""
         SELECT weighted.user_id,
                SUM(weighted.pp * power({decay_literal}, weighted.rn - 1)) AS total_pp,
                COUNT(*)::bigint                                           AS pp_play_count
         FROM (
             SELECT best.user_id,
                    best.pp,
                    row_number() OVER (PARTITION BY best.user_id ORDER BY best.pp DESC, best.id ASC) AS rn
             FROM (
                 SELECT DISTINCT ON (s.user_id, s.beatmap_id) s.user_id, s.beatmap_id, s.pp, s.id
                 FROM scores s
                 JOIN beatmaps b ON b.id = s.beatmap_id
                 JOIN beatmapsets bs ON bs.id = b.set_id
                 WHERE s.ranked AND s.passed AND bs.status = 'ranked' AND s.pp > 0
                 ORDER BY s.user_id, s.beatmap_id, s.pp DESC, s.id ASC
             ) best
         ) weighted
         JOIN users u ON u.id = weighted.user_id
         WHERE NOT u.restricted AND u.deleted_at IS NULL
         GROUP BY weighted.user_id
         """;

    public readonly record struct UserPp(double TotalPp, long? GlobalRank, long PpPlayCount)
    {
        public static readonly UserPp Unranked = new(0, null, 0);
    }

    /// <summary>
    /// One user's total pp + global pp rank. The dense_rank window is evaluated over every
    /// pp-earning user before the row is filtered, so the rank is global; ties share a rank, and a
    /// player with no pp-earning play is unranked (null), matching
    /// <see cref="GlobalRanking.ForUserAsync"/>.
    /// </summary>
    public static async Task<UserPp> ForUserAsync(NpgsqlConnection conn, long userId, CancellationToken ct = default)
    {
        var row = await conn.QuerySingleOrDefaultAsync<(double TotalPp, long GlobalRank, long PpPlayCount)?>(
            new CommandDefinition(
                $"""
                 SELECT total_pp AS TotalPp, global_rank AS GlobalRank, pp_play_count AS PpPlayCount
                 FROM (
                     SELECT user_id, total_pp, pp_play_count,
                            dense_rank() OVER (ORDER BY total_pp DESC) AS global_rank
                     FROM ({PerUserTotalSql}) totals
                 ) ranked
                 WHERE user_id = @userId
                 """,
                new { userId },
                cancellationToken: ct));

        return row is { } r ? new UserPp(r.TotalPp, r.GlobalRank, r.PpPlayCount) : UserPp.Unranked;
    }
}
