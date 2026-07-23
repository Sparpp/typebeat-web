using Dapper;
using Npgsql;

namespace Typebeat.Web.Scoring;

/// <summary>
/// The one global-ranking definition, shared by the /rankings board, the website profile stats
/// card, and the game client's user endpoint, so all three agree on a player's rank. There is no
/// pp yet: a player's "ranked score" is the sum of their best score per RANKED map (a
/// best-per-(user, beatmap) fold over ranked+passed scores on sets in status 'ranked'), and their
/// global rank is the dense_rank by that score among non-restricted, non-deleted users. Ties share
/// a rank; a player with no qualifying score is unranked (null).
/// </summary>
public static class GlobalRanking
{
    /// <summary>
    /// Per-user cumulative ranked score. Yields columns <c>user_id</c>, <c>ranked_score</c>
    /// (bigint), <c>ranked_map_count</c> (bigint); only users with at least one qualifying score
    /// appear. Embed as a subquery: it is the single source of truth for the metric.
    /// </summary>
    public const string PerUserCumulativeSql =
        """
        SELECT best.user_id,
               SUM(best.total_score)::bigint AS ranked_score,
               COUNT(*)::bigint              AS ranked_map_count
        FROM (
            SELECT DISTINCT ON (s.user_id, s.beatmap_id) s.user_id, s.total_score
            FROM scores s
            JOIN beatmaps b ON b.id = s.beatmap_id
            JOIN beatmapsets bs ON bs.id = b.set_id
            WHERE s.ranked AND s.passed AND bs.status = 'ranked'
            ORDER BY s.user_id, s.beatmap_id, s.total_score DESC, s.id ASC
        ) best
        JOIN users u ON u.id = best.user_id
        WHERE NOT u.restricted AND u.deleted_at IS NULL
        GROUP BY best.user_id
        """;

    public readonly record struct UserRanking(long RankedScore, long? GlobalRank, long RankedMapCount)
    {
        public static readonly UserRanking Unranked = new(0, null, 0);
    }

    /// <summary>
    /// One user's ranked score + global rank. The dense_rank window is evaluated over every ranked
    /// user before the row is filtered to <paramref name="userId"/>, so the rank is global. Returns
    /// <see cref="UserRanking.Unranked"/> when the user has no qualifying score.
    /// </summary>
    public static async Task<UserRanking> ForUserAsync(NpgsqlConnection conn, long userId, CancellationToken ct = default)
    {
        var row = await conn.QuerySingleOrDefaultAsync<(long RankedScore, long GlobalRank, long RankedMapCount)?>(
            new CommandDefinition(
                $"""
                 SELECT ranked_score AS RankedScore, global_rank AS GlobalRank, ranked_map_count AS RankedMapCount
                 FROM (
                     SELECT user_id, ranked_score, ranked_map_count,
                            dense_rank() OVER (ORDER BY ranked_score DESC) AS global_rank
                     FROM ({PerUserCumulativeSql}) totals
                 ) ranked
                 WHERE user_id = @userId
                 """,
                new { userId },
                cancellationToken: ct));

        return row is { } r ? new UserRanking(r.RankedScore, r.GlobalRank, r.RankedMapCount) : UserRanking.Unranked;
    }
}
