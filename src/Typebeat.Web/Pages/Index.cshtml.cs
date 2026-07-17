using Dapper;
using Typebeat.Web.Data;

namespace Typebeat.Web.Pages;

/// <summary>
/// Landing page: neon-karaoke hero (slogan pair, live stats line, Download/Sign up CTAs)
/// plus a "newest maps" strip of up to 8 public sets rendered with the shared card partial.
/// </summary>
public sealed class IndexModel(Db db) : TypebeatPageModel
{
    public long Players { get; private set; }
    public long ScoresToday { get; private set; }
    public long Maps { get; private set; }

    public IReadOnlyList<BeatmapsetCardModel> NewestSets { get; private set; } = [];

    public async Task OnGetAsync()
    {
        await using var conn = await db.OpenAsync(HttpContext.RequestAborted);

        // One cheap round trip for the whole stats line. Restricted mappers' sets are invisible
        // site-wide (mirroring their 404ing profiles), so they don't count either.
        (Players, ScoresToday, Maps) = await conn.QuerySingleAsync<(long, long, long)>(
            """
            SELECT (SELECT count(*) FROM users)                                          AS players,
                   (SELECT count(*) FROM scores WHERE ended_at >= date_trunc('day', now())) AS scoresToday,
                   (SELECT count(*)
                    FROM beatmapsets s
                    JOIN users u ON u.id = s.owner_id
                    WHERE s.status = 'public' AND NOT u.restricted)                      AS maps
            """);

        NewestSets = (await conn.QueryAsync<BeatmapsetCardModel>(
            BeatmapsetCardSql.Select +
            """

            WHERE s.status = 'public' AND (NOT u.restricted OR s.owner_id = @viewerId)
            ORDER BY s.submitted_at DESC, s.id DESC
            LIMIT 8
            """,
            new { viewerId = CurrentUser?.Id ?? 0 })).ToList();
    }
}
