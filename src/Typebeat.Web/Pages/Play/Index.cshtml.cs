using Dapper;
using Microsoft.AspNetCore.Antiforgery;
using Typebeat.Web.Data;

namespace Typebeat.Web.Pages.Play;

/// <summary>
/// /play: the in-browser web player's map picker. Lists currently-ranked maps that have a
/// playable .osu difficulty and an assembled package, rendered with the shared beatmapset card
/// (in "play mode", the download rail becomes a play button), and mints an antiforgery request
/// token for the cookie-authed /play/token + /play/submit fetches (see play.js). The gameplay
/// itself is a JS reimplementation (typebeat-core.js); this page only picks the map and carries
/// auth/CSRF state.
///
/// <para><c>/play?set={id}</c> is the deep link the shared beatmapset card's webplay rail points at:
/// the picker still renders, but the named set is handed to play.js as <c>autoPlay</c> so it loads
/// straight onto the player's start gate. An id that is missing, unranked or not playable is simply
/// ignored (the visitor lands on the picker), never an error page.</para>
/// </summary>
public sealed class IndexModel(Db db, IAntiforgery antiforgery) : TypebeatPageModel
{
    /// <summary>Only sets satisfying this are playable: exactly what the /play/map endpoints need.</summary>
    private const string playable_filter =
        "card.status = 'ranked' AND card.haspackage AND card.hasplayablediff";

    public IReadOnlyList<BeatmapsetCardModel> Maps { get; private set; } = [];
    public string CsrfToken { get; private set; } = string.Empty;

    /// <summary>The card named by <c>?set={id}</c>, or null when there is nothing to auto-open.</summary>
    public BeatmapsetCardModel? AutoPlay { get; private set; }

    public async Task OnGetAsync(long set = 0)
    {
        CsrfToken = antiforgery.GetAndStoreTokens(HttpContext).RequestToken ?? string.Empty;

        await using var conn = await db.OpenAsync(HttpContext.RequestAborted);

        long viewerId = CurrentUser?.Id ?? 0;

        // Reuse the canonical card SQL, then keep only ranked sets that are actually playable in
        // the browser: a live .osu difficulty (for /play/map/{id}/osu) and an assembled package.
        Maps = (await conn.QueryAsync<BeatmapsetCardModel>(
            $"""
            SELECT card.* FROM (
                {BeatmapsetCardSql.Select}
            ) card
            WHERE {playable_filter}
            ORDER BY card.playcount DESC, card.id DESC
            LIMIT 60
            """,
            new { viewerId })).ToList();

        if (set <= 0)
            return;

        // Resolved separately rather than searched in Maps: the picker is capped at 60, so a
        // long-tail set's deep link must still open.
        AutoPlay = await conn.QuerySingleOrDefaultAsync<BeatmapsetCardModel>(
            $"""
            SELECT card.* FROM (
                {BeatmapsetCardSql.Select}
            ) card
            WHERE card.id = @set AND {playable_filter}
            """,
            new { viewerId, set });
    }
}
