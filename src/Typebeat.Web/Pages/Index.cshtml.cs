using Dapper;
using Microsoft.AspNetCore.OutputCaching;
using Typebeat.Web.Caching;
using Typebeat.Web.Data;
using Typebeat.Web.Storage;

namespace Typebeat.Web.Pages;

/// <summary>
/// Landing page: hero (slogan, live stats line, download/sign-up CTAs) plus a "newest maps" strip
/// of up to 8 published sets rendered with the shared card partial. The download button links to
/// the /download page, which picks the platform; it only shows when at least one platform's build
/// is actually stored, otherwise a signed-out visitor gets the sign-up CTA in its place.
///
/// <para>Output-cached for anonymous visitors for 60 s (backlog 366, Caching/CachePolicies.cs):
/// the stats line counts every score row on each render.</para>
/// </summary>
[OutputCache(PolicyName = CachePolicies.Landing)]
public sealed class IndexModel(Db db, IFileStore store, IConfiguration config) : TypebeatPageModel
{
    public long Players { get; private set; }
    public long ScoresTotal { get; private set; }
    public long Maps { get; private set; }

    /// <summary>True when SOME platform's game build is configured and present; gates the hero download button.</summary>
    public bool GameDownloadAvailable { get; private set; }

    /// <summary>Set after an account-deletion redirect (?deleted=1) to show a farewell note.</summary>
    public bool AccountDeleted { get; private set; }

    public IReadOnlyList<BeatmapsetCardModel> NewestSets { get; private set; } = [];

    public async Task OnGetAsync()
    {
        AccountDeleted = HttpContext.Request.Query.ContainsKey("deleted");

        GameDownloadAvailable = await AnyGameDownloadAvailableAsync(config, store, HttpContext.RequestAborted);

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
                    WHERE s.status IN ('pending', 'unranked', 'ranked') AND NOT u.restricted)        AS maps
            """);

        NewestSets = (await conn.QueryAsync<BeatmapsetCardModel>(
            BeatmapsetCardSql.Select +
            """

            WHERE s.status IN ('pending', 'unranked', 'ranked') AND (NOT u.restricted OR s.owner_id = @viewerId)
            ORDER BY s.submitted_at DESC, s.id DESC
            LIMIT 8
            """,
            new { viewerId = CurrentUser?.Id ?? 0 })).ToList();
    }

    /// <summary>
    /// True as soon as ANY platform's build is both configured and stored. The hero CTA just sends
    /// the visitor to /download, which does the per-platform work, so asking about Windows alone
    /// hid the front-page entry point whenever only a Linux or macOS build was published.
    ///
    /// Deliberately not <see cref="DownloadModel"/>'s resolver: that one opens each object to get a
    /// byte length for its size caption, and the hero has no size to show. Existence is the cheaper
    /// question, and the loop stops at the first platform that answers yes rather than probing all
    /// three on every request. An unset key is skipped, never treated as an answer.
    ///
    /// Public and static purely so it can be pinned without booting a second test host.
    /// </summary>
    public static async Task<bool> AnyGameDownloadAvailableAsync(IConfiguration config, IFileStore store, CancellationToken ct = default)
    {
        foreach (string configKey in GameDownloadKeys.All)
        {
            string? fileName = config[configKey];

            if (!string.IsNullOrEmpty(fileName) && await store.ObjectExistsAsync(StoreKeys.Download(fileName), ct))
                return true;
        }

        return false;
    }
}
