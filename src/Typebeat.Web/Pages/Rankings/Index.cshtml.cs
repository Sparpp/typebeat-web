using Dapper;
using Typebeat.Web.Data;
using Typebeat.Web.Scoring;

namespace Typebeat.Web.Pages.Rankings;

/// <summary>
/// Global leaderboard, two boards behind one page.
///
/// <list type="bullet">
/// <item><b>performance</b> (the default, <c>?board=performance</c>): total pp. The MAIN global
/// ranking. Each player's best-pp play per ranked map, sorted, summed with a decay
/// (<see cref="PpRanking"/>, docs/pp.md). It rewards clearing the hardest maps with the fewest
/// misses, not volume.</item>
/// <item><b>score</b> (<c>?board=score</c>): the original cumulative-score board, unchanged. Each
/// player's best score per ranked map, added up (<see cref="GlobalRanking"/>). It is now explicitly
/// the score-farming board rather than the global ranking.</item>
/// </list>
///
/// Both share the same eligibility rules, because both metrics are defined by one SQL constant
/// each: pending/hidden/removed sets contribute nothing, unranked and failed scores never count,
/// and restricted or deleted accounts are delisted like everywhere else.
/// </summary>
public sealed class IndexModel(Db db) : TypebeatPageModel
{
    public const string PerformanceBoard = "performance";
    public const string ScoreBoard = "score";

    public sealed record PerformanceRow(
        long UserId,
        string Username,
        string? AvatarKey,
        string CountryCode,
        double TotalPp,
        long PpPlayCount,
        long CumulativeScore);

    public sealed record ScoreRow(
        long UserId,
        string Username,
        string? AvatarKey,
        string CountryCode,
        long CumulativeScore,
        long TotalCumulativeScore,
        long RankedScoreCount);

    /// <summary>Which board is showing: <see cref="PerformanceBoard"/> or <see cref="ScoreBoard"/>.</summary>
    public string Board { get; private set; } = PerformanceBoard;

    public IReadOnlyList<PerformanceRow> PerformanceRows { get; private set; } = [];
    public IReadOnlyList<ScoreRow> ScoreRows { get; private set; } = [];

    /// <summary>Whether the showing board has any rows at all (drives the empty state).</summary>
    public bool IsEmpty => Board == PerformanceBoard ? PerformanceRows.Count == 0 : ScoreRows.Count == 0;

    private const int page_size = 50;

    public async Task OnGetAsync(string? board)
    {
        // Anything unrecognised falls back to the main board rather than 404ing: /rankings is a
        // linked-to, shareable URL and a stale query string must still render something.
        Board = board == ScoreBoard ? ScoreBoard : PerformanceBoard;

        await using var conn = await db.OpenAsync(HttpContext.RequestAborted);

        if (Board == PerformanceBoard)
        {
            // Cumulative score rides along as a secondary column so the two boards can be compared
            // at a glance; it is a LEFT JOIN because a pp-earning player always has a cumulative
            // total, but the reverse framing keeps the query honest if that ever stops holding.
            PerformanceRows = (await conn.QueryAsync<PerformanceRow>(
                    $"""
                     SELECT u.id AS UserId,
                            u.username AS Username,
                            u.avatar_key AS AvatarKey,
                            u.country_code AS CountryCode,
                            p.total_pp AS TotalPp,
                            p.pp_play_count AS PpPlayCount,
                            COALESCE(t.ranked_score, 0) AS CumulativeScore
                     FROM ({PpRanking.PerUserTotalSql}) p
                     JOIN users u ON u.id = p.user_id
                     LEFT JOIN ({GlobalRanking.PerUserCumulativeSql}) t ON t.user_id = p.user_id
                     ORDER BY p.total_pp DESC, u.id ASC
                     LIMIT @limit
                     """,
                    new { limit = page_size }))
                .ToList();

            return;
        }

        // Same cumulative-ranked-score metric as the profile stats card and the client user
        // endpoint (GlobalRanking); one definition so every score-ranking surface agrees.
        ScoreRows = (await conn.QueryAsync<ScoreRow>(
                $"""
                 SELECT u.id AS UserId,
                        u.username AS Username,
                        u.avatar_key AS AvatarKey,
                        u.country_code AS CountryCode,
                        t.ranked_score AS CumulativeScore,
                        COALESCE(us.total_score, 0) AS TotalCumulativeScore,
                        t.ranked_map_count AS RankedScoreCount
                 FROM ({GlobalRanking.PerUserCumulativeSql}) t
                 JOIN users u ON u.id = t.user_id
                 LEFT JOIN user_stats us ON us.user_id = u.id
                 ORDER BY t.ranked_score DESC, u.id ASC
                 LIMIT @limit
                 """,
                new { limit = page_size }))
            .ToList();
    }
}
