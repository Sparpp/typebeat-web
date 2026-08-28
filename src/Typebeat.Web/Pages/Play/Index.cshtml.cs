using Dapper;
using Microsoft.AspNetCore.Antiforgery;
using Typebeat.Web.Data;

namespace Typebeat.Web.Pages.Play;

/// <summary>
/// /play: the in-browser web player's map picker. Lists PUBLISHED maps that have a playable .osu
/// difficulty and an assembled package, rendered with the shared beatmapset card (in "play mode",
/// the download rail becomes a play button), and mints an antiforgery request token for the
/// cookie-authed /play/token + /play/submit fetches (see play.js). The gameplay itself is a JS
/// reimplementation (typebeat-core.js); this page only picks the map and carries auth/CSRF state.
///
/// <para>Published, not ranked-only, since backlog 230: a pending or unranked set is world-readable
/// everywhere else on the site and /play/submit already stores its plays <c>ranked = false</c>, so
/// there was nothing left for the picker to protect. The card's own status pill is what tells the
/// visitor which kind of map they are about to play.</para>
///
/// <para><c>/play?set={id}</c> is the deep link the shared beatmapset card's webplay rail points at:
/// the picker still renders, but the named set is handed to play.js as <c>autoPlay</c> so it loads
/// straight onto the difficulty step (or, for a one-difficulty set, the start gate). An id that is
/// missing, unpublished or not playable is simply ignored (the visitor lands on the picker), never
/// an error page. <c>&amp;diff={beatmapId}</c> additionally preselects one difficulty of that set,
/// on the set page's own terms: honoured when it belongs to the set and is live, ignored otherwise.</para>
/// </summary>
public sealed class IndexModel(Db db, IAntiforgery antiforgery) : TypebeatPageModel
{
    /// <summary>
    /// Only sets satisfying this are playable: exactly what the /play/map endpoints need. The
    /// status list is <see cref="Typebeat.Web.Endpoints.BeatmapsetEndpoints.IsPublished"/> in SQL,
    /// and it must stay equal to <see cref="BeatmapsetCardModel.CanWebplay"/>, or the picker and the
    /// rail on every other listing would disagree about what is playable.
    /// </summary>
    private const string playable_filter =
        "card.status IN ('pending', 'unranked', 'ranked') AND card.haspackage AND card.hasplayablediff";

    public IReadOnlyList<BeatmapsetCardModel> Maps { get; private set; } = [];
    public string CsrfToken { get; private set; } = string.Empty;

    /// <summary>The card named by <c>?set={id}</c>, or null when there is nothing to auto-open.</summary>
    public BeatmapsetCardModel? AutoPlay { get; private set; }

    /// <summary>The difficulty named by <c>&amp;diff={beatmapId}</c> when it is a live difficulty of
    /// <see cref="AutoPlay"/>, else 0 (the player then shows its difficulty step as usual).</summary>
    public long AutoPlayDiffId { get; private set; }

    public async Task OnGetAsync(long set = 0, long diff = 0)
    {
        CsrfToken = antiforgery.GetAndStoreTokens(HttpContext).RequestToken ?? string.Empty;

        await using var conn = await db.OpenAsync(HttpContext.RequestAborted);

        long viewerId = CurrentUser?.Id ?? 0;

        // Reuse the canonical card SQL, then keep only published sets that are actually playable in
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

        if (AutoPlay is null || diff <= 0)
            return;

        // Same membership + liveness predicate the media routes and /play/token resolve a named
        // difficulty through, so a deep link can never preselect something they would refuse.
        AutoPlayDiffId = await conn.ExecuteScalarAsync<long>(
            """
            SELECT COALESCE((
                SELECT b.id FROM beatmaps b
                WHERE b.id = @diff AND b.set_id = @set
                  AND b.filename IS NOT NULL AND b.filename LIKE '%.osu'
            ), 0)
            """,
            new { set, diff });
    }
}
