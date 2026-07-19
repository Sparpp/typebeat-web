using Dapper;
using Microsoft.AspNetCore.Antiforgery;
using Typebeat.Web.Data;

namespace Typebeat.Web.Pages.Play;

/// <summary>
/// /play — the in-browser web player's map picker. Lists currently-ranked maps that
/// have a playable .osu difficulty and an assembled package, and mints an antiforgery
/// request token for the cookie-authed /play/token + /play/submit fetches (see play.js).
/// The gameplay itself is a JS reimplementation (typebeat-core.js) — this page only
/// picks the map and carries auth/CSRF state.
/// </summary>
public sealed class IndexModel(Db db, IAntiforgery antiforgery) : TypebeatPageModel
{
    public IReadOnlyList<PlayMap> Maps { get; private set; } = [];
    public string CsrfToken { get; private set; } = string.Empty;

    public async Task OnGetAsync()
    {
        CsrfToken = antiforgery.GetAndStoreTokens(HttpContext).RequestToken ?? string.Empty;

        await using var conn = await db.OpenAsync(HttpContext.RequestAborted);

        // One playable diff per ranked set: the first .osu difficulty of the set's latest
        // published version. DISTINCT ON dedupes multi-diff sets; the outer sort surfaces
        // the most-played first.
        Maps = (await conn.QueryAsync<PlayMap>(
            """
            SELECT q.* FROM (
                SELECT DISTINCT ON (s.id)
                       s.id                AS SetId,
                       b.id                AS BeatmapId,
                       s.title             AS Title,
                       s.artist            AS Artist,
                       CASE WHEN s.cover_key IS NOT NULL THEN '/' || s.cover_key || '/list.jpg' END AS CoverUrl,
                       b.wpm               AS Wpm,
                       b.difficulty_rating AS Stars,
                       s.play_count        AS PlayCount
                FROM beatmapsets s
                JOIN users u ON u.id = s.owner_id
                JOIN beatmaps b ON b.set_id = s.id AND b.filename LIKE '%.osu'
                JOIN set_versions v ON v.set_id = s.id
                     AND v.version_no = (SELECT MAX(version_no) FROM set_versions WHERE set_id = s.id)
                WHERE s.status = 'ranked' AND v.package_key IS NOT NULL AND NOT u.restricted
                ORDER BY s.id, b.id
            ) q
            ORDER BY q.PlayCount DESC, q.SetId DESC
            LIMIT 60
            """)).ToList();
    }

    // Plain settable properties (not a positional record) so Dapper uses tolerant
    // name-based mapping — the columns are int/numeric/nullable and won't line up with
    // a record's strict positional constructor.
    public sealed class PlayMap
    {
        public long SetId { get; set; }
        public long BeatmapId { get; set; }
        public string Title { get; set; } = "";
        public string Artist { get; set; } = "";
        public string? CoverUrl { get; set; }
        public decimal? Wpm { get; set; }
        public double Stars { get; set; }
        public long PlayCount { get; set; }
    }
}
