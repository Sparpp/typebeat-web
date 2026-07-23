using Dapper;
using Typebeat.Web.Data;
using Typebeat.Web.Scoring;

namespace Typebeat.Web.Pages.Rankings;

/// <summary>
/// Global leaderboard. No pp yet; the metric is a naive cumulative score: each player's best
/// score per beatmap (the same best-per-user fold the per-map leaderboards use), summed across
/// RANKED maps only. Pending/hidden/removed sets contribute nothing, unranked/failed scores
/// never count, and restricted or deleted accounts are delisted like everywhere else.
/// </summary>
public sealed class IndexModel(Db db) : TypebeatPageModel
{
    public sealed record Row(
        long UserId,
        string Username,
        string? AvatarKey,
        string CountryCode,
        long CumulativeScore,
        long TotalCumulativeScore,
        long RankedScoreCount);

    public IReadOnlyList<Row> Rows { get; private set; } = [];

    private const int page_size = 50;

    public async Task OnGetAsync()
    {
        await using var conn = await db.OpenAsync(HttpContext.RequestAborted);

        // Same cumulative-ranked-score metric as the profile stats card and the client user
        // endpoint (GlobalRanking); one definition so every ranking surface agrees.
        Rows = (await conn.QueryAsync<Row>(
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
