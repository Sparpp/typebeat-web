using Dapper;
using Microsoft.AspNetCore.Mvc;
using Typebeat.Web.Data;

namespace Typebeat.Web.Pages.Scores;

/// <summary>
/// Canonical redirect: /scores/{scoreId} → 301 /beatmapsets/{setId}. The game's leaderboard
/// "Copy Link" context-menu item copies {WebsiteUrl}/scores/{OnlineID}; the website has no
/// per-score page, so shared score links land on the score's set page (where the leaderboard
/// lives). Unknown scores 404; scores on hidden/removed sets follow the set page's own
/// visibility rules (hidden: owner only; removed: owner or admin) so nothing leaks through
/// the redirect that the destination would refuse to show. A RESTRICTED player's score 404s
/// as well, whoever asks: it is on no board (<c>BeatmapLeaderboard.PlayerListed</c>) and its
/// player's profile 404s, so the link has nothing left to point at. Read per request, so
/// unrestricting the player brings the link back.
/// </summary>
public sealed class RedirectModel(Db db) : TypebeatPageModel
{
    public async Task<IActionResult> OnGetAsync(long id)
    {
        await using var conn = await db.OpenAsync(HttpContext.RequestAborted);

        var row = await conn.QuerySingleOrDefaultAsync<(long SetId, string Status, long OwnerId, bool OwnerRestricted, bool PlayerRestricted)?>(
            """
            SELECT s.id AS SetId, s.status AS Status, s.owner_id AS OwnerId, u.restricted AS OwnerRestricted,
                   player.restricted AS PlayerRestricted
            FROM scores sc
            JOIN beatmaps b ON b.id = sc.beatmap_id
            JOIN beatmapsets s ON s.id = b.set_id
            JOIN users u ON u.id = s.owner_id
            JOIN users player ON player.id = sc.user_id
            WHERE sc.id = @id
            """,
            new { id });

        if (row is not { } target || target.PlayerRestricted)
            return NotFound();

        bool isOwner = CurrentUser?.Id == target.OwnerId;
        bool isAdmin = CurrentUser?.IsAdmin == true;

        if ((target.OwnerRestricted && !isOwner)
            || (target.Status == "removed" && !isOwner && !isAdmin)
            || (target.Status == "hidden" && !isOwner))
        {
            return NotFound();
        }

        return RedirectPermanent($"/beatmapsets/{target.SetId}");
    }
}
