using Dapper;
using Microsoft.AspNetCore.Mvc;
using Typebeat.Web.Data;

namespace Typebeat.Web.Pages.Beatmaps;

/// <summary>
/// Canonical redirect: /beatmaps/{beatmapId} → 301 /beatmapsets/{setId}. The game's link-out
/// (OpenBeatmap) uses beatmap-id URLs; the website's canonical page is the set.
/// </summary>
public sealed class RedirectModel(Db db) : TypebeatPageModel
{
    public async Task<IActionResult> OnGetAsync(long id)
    {
        await using var conn = await db.OpenAsync(HttpContext.RequestAborted);

        long? setId = await conn.ExecuteScalarAsync<long?>(
            "SELECT set_id FROM beatmaps WHERE id = @id", new { id });

        return setId is null ? NotFound() : RedirectPermanent($"/beatmapsets/{setId}");
    }
}
