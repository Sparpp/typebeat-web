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
/// the redirect that the destination would refuse to show.
/// </summary>
public sealed class RedirectModel(Db db) : TypebeatPageModel
{
    public async Task<IActionResult> OnGetAsync(long id)
    {
        await using var conn = await db.OpenAsync(HttpContext.RequestAborted);

        var row = await conn.QuerySingleOrDefaultAsync<(long SetId, string Status, long OwnerId)?>(
            """
            SELECT s.id AS SetId, s.status AS Status, s.owner_id AS OwnerId
            FROM scores sc
            JOIN beatmaps b ON b.id = sc.beatmap_id
            JOIN beatmapsets s ON s.id = b.set_id
            WHERE sc.id = @id
            """,
            new { id });

        if (row is not { } target)
            return NotFound();

        bool isOwner = CurrentUser?.Id == target.OwnerId;
        bool isAdmin = CurrentUser?.IsAdmin == true;

        if ((target.Status == "removed" && !isOwner && !isAdmin) || (target.Status == "hidden" && !isOwner))
            return NotFound();

        return RedirectPermanent($"/beatmapsets/{target.SetId}");
    }
}
