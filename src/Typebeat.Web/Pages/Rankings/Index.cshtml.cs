using Dapper;
using Typebeat.Web.Data;

namespace Typebeat.Web.Pages.Rankings;

/// <summary>
/// Global leaderboard. No pp yet — the metric is a naive cumulative score: each player's best
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
        long RankedScoreCount);

    public IReadOnlyList<Row> Rows { get; private set; } = [];

    private const int page_size = 50;

    public async Task OnGetAsync()
    {
        await using var conn = await db.OpenAsync(HttpContext.RequestAborted);

        Rows = (await conn.QueryAsync<Row>(
                """
                SELECT u.id AS UserId,
                       u.username AS Username,
                       u.avatar_key AS AvatarKey,
                       u.country_code AS CountryCode,
                       SUM(best.total_score)::bigint AS CumulativeScore,
                       COUNT(*)::bigint AS RankedScoreCount
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
                GROUP BY u.id, u.username, u.avatar_key, u.country_code
                ORDER BY SUM(best.total_score) DESC, u.id ASC
                LIMIT @limit
                """,
                new { limit = page_size }))
            .ToList();
    }
}
