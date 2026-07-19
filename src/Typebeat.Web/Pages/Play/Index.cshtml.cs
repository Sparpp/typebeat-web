using Dapper;
using Microsoft.AspNetCore.Antiforgery;
using Typebeat.Web.Data;

namespace Typebeat.Web.Pages.Play;

/// <summary>
/// /play — the in-browser web player's map picker. Lists currently-ranked maps that have a
/// playable .osu difficulty and an assembled package, rendered with the shared beatmapset card
/// (in "play mode" — the download rail becomes a play button), and mints an antiforgery request
/// token for the cookie-authed /play/token + /play/submit fetches (see play.js). The gameplay
/// itself is a JS reimplementation (typebeat-core.js) — this page only picks the map and carries
/// auth/CSRF state.
/// </summary>
public sealed class IndexModel(Db db, IAntiforgery antiforgery) : TypebeatPageModel
{
    public IReadOnlyList<BeatmapsetCardModel> Maps { get; private set; } = [];
    public string CsrfToken { get; private set; } = string.Empty;

    public async Task OnGetAsync()
    {
        CsrfToken = antiforgery.GetAndStoreTokens(HttpContext).RequestToken ?? string.Empty;

        await using var conn = await db.OpenAsync(HttpContext.RequestAborted);

        // Reuse the canonical card SQL, then keep only ranked sets that are actually playable in
        // the browser: a live .osu difficulty (for /play/map/{id}/osu) and an assembled package.
        Maps = (await conn.QueryAsync<BeatmapsetCardModel>(
            $"""
            SELECT card.* FROM (
                {BeatmapsetCardSql.Select}
            ) card
            WHERE card.status = 'ranked'
              AND card.haspackage = true
              AND EXISTS (SELECT 1 FROM beatmaps b WHERE b.set_id = card.id AND b.filename LIKE '%.osu')
            ORDER BY card.playcount DESC, card.id DESC
            LIMIT 60
            """,
            new { viewerId = CurrentUser?.Id ?? 0 })).ToList();
    }
}
