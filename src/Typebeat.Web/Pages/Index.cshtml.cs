using Dapper;
using Typebeat.Web.Data;
using Typebeat.Web.Storage;

namespace Typebeat.Web.Pages;

/// <summary>
/// Landing page: hero (slogan, live stats line, download/sign-up CTAs) plus a "newest maps" strip
/// of up to 8 published sets rendered with the shared card partial. The download button links
/// straight to /download/game (there is no dedicated download page) and only shows when a build
/// is actually stored.
/// </summary>
public sealed class IndexModel(Db db, IFileStore store, IConfiguration config) : TypebeatPageModel
{
    public long Players { get; private set; }
    public long ScoresTotal { get; private set; }
    public long Maps { get; private set; }

    /// <summary>True when a game build is configured and present — gates the hero download button.</summary>
    public bool GameDownloadAvailable { get; private set; }

    /// <summary>Set after an account-deletion redirect (?deleted=1) to show a farewell note.</summary>
    public bool AccountDeleted { get; private set; }

    public IReadOnlyList<BeatmapsetCardModel> NewestSets { get; private set; } = [];

    public async Task OnGetAsync()
    {
        AccountDeleted = HttpContext.Request.Query.ContainsKey("deleted");

        string? gameFile = config["TYPEBEAT_GAME_DOWNLOAD"];
        GameDownloadAvailable = !string.IsNullOrEmpty(gameFile)
            && await store.ObjectExistsAsync(StoreKeys.Download(gameFile), HttpContext.RequestAborted);

        await using var conn = await db.OpenAsync(HttpContext.RequestAborted);

        // One cheap round trip for the whole stats line. Restricted mappers' sets are invisible
        // site-wide (mirroring their 404ing profiles), so they don't count either.
        (Players, ScoresTotal, Maps) = await conn.QuerySingleAsync<(long, long, long)>(
            """
            SELECT (SELECT count(*) FROM users)                                          AS players,
                   (SELECT count(*) FROM scores)                                        AS scoresTotal,
                   (SELECT count(*)
                    FROM beatmapsets s
                    JOIN users u ON u.id = s.owner_id
                    WHERE s.status IN ('pending', 'ranked') AND NOT u.restricted)        AS maps
            """);

        NewestSets = (await conn.QueryAsync<BeatmapsetCardModel>(
            BeatmapsetCardSql.Select +
            """

            WHERE s.status IN ('pending', 'ranked') AND (NOT u.restricted OR s.owner_id = @viewerId)
            ORDER BY s.submitted_at DESC, s.id DESC
            LIMIT 8
            """,
            new { viewerId = CurrentUser?.Id ?? 0 })).ToList();
    }
}
