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
/// SONG, the <c>beatmapsets</c> row rather than the single difficulty (without the dedup, replays
/// of one hard map, or a clear of every difficulty of one song, would fill the whole list), sort
/// those descending and sum them with a decay, <c>Σ pp_i · DECAY^i</c>, over ALL of them. There is
/// no hard top-N truncation, so there is no cliff where the 11th-best play contributes exactly
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
    /// EVERY PLAY THAT EARNS pp, one row per play. The single definition of pp eligibility, so a
    /// set an admin un-ranks, a score an admin un-ranks, a fail, a custom-rate (unpriced) play and
    /// a restricted or deleted account all drop out of every pp surface at once:
    ///
    /// <list type="bullet">
    /// <item>the play is on its map's RANKED board (<see cref="BeatmapLeaderboard.OnBoard"/>: it
    /// passed and its stored <c>ranked</c> flag is set);</item>
    /// <item>its set is CURRENTLY <c>'ranked'</c>, re-read per request rather than trusted from the
    /// stored flag, so un-ranking a set takes effect with no recompute;</item>
    /// <item>it was priced above zero. Per docs/pp.md a custom-rate play is pp-INELIGIBLE (it still
    /// ranks on the score boards), and a not-yet-priced row is stored 0, so both are excluded by
    /// the same predicate;</item>
    /// <item>the account is LISTED: not restricted, not deleted. This is the global-ranking
    /// delisting rule, deliberately absent from <see cref="BeatmapLeaderboard"/> (per-map boards
    /// keep restricted players' rows; global rankings do not).</item>
    /// </list>
    ///
    /// <para>
    /// Columns: <c>id</c>, <c>user_id</c>, <c>beatmap_id</c>, <c>set_id</c> (the song the map is a
    /// difficulty of, which <see cref="BestPerSetSql"/> folds on), <c>pp</c> (double precision), and
    /// nothing else. <c>set_id</c> is free: the set is already joined for its status. Embed as a
    /// subquery and join <c>scores</c> back on <c>id</c> for display columns AFTER the caller's
    /// <c>LIMIT</c>, never before: the partial index
    /// <c>ix_scores_pp (user_id, beatmap_id, pp DESC) WHERE ranked AND passed AND pp &gt; 0</c>
    /// (020_performance_points.sql) covers exactly this row set.
    /// </para>
    /// </summary>
    public static readonly string EligiblePlaysSql =
        $"""
         SELECT s.id, s.user_id, s.beatmap_id, b.set_id, s.pp
         FROM scores s
         JOIN beatmaps b ON b.id = s.beatmap_id
         JOIN beatmapsets bs ON bs.id = b.set_id
         JOIN users u ON u.id = s.user_id
         WHERE {BeatmapLeaderboard.OnBoard("s", "true")}
           AND bs.status = 'ranked'
           AND s.pp > 0
           AND NOT u.restricted AND u.deleted_at IS NULL
         """;

    /// <summary>
    /// <see cref="EligiblePlaysSql"/> folded to ONE play per (user, map): the player's BEST-pp play
    /// on each ranked map, ties broken by the earlier submission (lower id), so the fold is total
    /// and picks exactly one row.
    ///
    /// <para>
    /// AN INTERMEDIATE STAGE, not the unit any pp surface counts in: that is
    /// <see cref="BestPerSetSql"/>, which folds this again onto the song. This stage exists because
    /// it is the one the partial index <c>ix_scores_pp (user_id, beatmap_id, pp DESC)</c> can serve,
    /// its leading columns being exactly this fold's key.
    /// </para>
    ///
    /// <para>Columns are <see cref="EligiblePlaysSql"/>'s.</para>
    /// </summary>
    public static readonly string BestPerMapSql =
        $"""
         SELECT DISTINCT ON (e.user_id, e.beatmap_id) e.id, e.user_id, e.beatmap_id, e.set_id, e.pp
         FROM ({EligiblePlaysSql}) e
         ORDER BY e.user_id, e.beatmap_id, e.pp DESC, e.id ASC
         """;

    /// <summary>
    /// <see cref="BestPerMapSql"/> folded again to ONE play per (user, SONG): the player's best-pp
    /// play anywhere in a ranked set, whichever difficulty of it they set that play on. This is the
    /// unit every pp surface counts in, per docs/pp.md: without it, replays of one hard map, or a
    /// clear of the Easy, Normal and Insane of one song, would fill a player's whole list.
    ///
    /// <para>
    /// LAYERED over the per-map fold rather than keyed on <c>set_id</c> directly, which would give
    /// the identical answer: <c>DISTINCT ON (user_id, set_id)</c> straight over
    /// <see cref="EligiblePlaysSql"/> could not use <c>ix_scores_pp</c>, whose leading columns are
    /// <c>(user_id, beatmap_id)</c>. Folding twice keeps the expensive stage indexed and leaves the
    /// second one a sort over an already tiny row set.
    /// </para>
    ///
    /// <para>
    /// Ties are broken by pp then the earlier submission (lower id), the same total order the inner
    /// fold uses, so exactly one row survives per song. Columns are
    /// <see cref="EligiblePlaysSql"/>'s. Shared by <see cref="PerUserTotalSql"/> (which weights and
    /// sums it per user) and by the /rankings top-plays board (which sorts it globally), so the two
    /// cannot disagree about which play represents a player on a song.
    /// </para>
    /// </summary>
    public static readonly string BestPerSetSql =
        $"""
         SELECT DISTINCT ON (best.user_id, best.set_id) best.id, best.user_id, best.beatmap_id, best.set_id, best.pp
         FROM ({BestPerMapSql}) best
         ORDER BY best.user_id, best.set_id, best.pp DESC, best.id ASC
         """;

    /// <summary>
    /// The TOP-PLAYS board's ordering over <see cref="BestPerSetSql"/>: biggest pp first, a tie
    /// broken by the EARLIER submission (lower score id), the same tie-break
    /// <see cref="BeatmapLeaderboard.Order"/> uses. Score ids are unique, so this order is TOTAL:
    /// the board reads the same on every render and a <c>LIMIT</c> always cuts in the same place.
    /// </summary>
    public static string TopPlaysOrder(string play) => $"{play}.pp DESC, {play}.id ASC";

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
             FROM ({BestPerSetSql}) best
         ) weighted
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
