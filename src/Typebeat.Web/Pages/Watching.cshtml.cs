using Dapper;
using Microsoft.AspNetCore.Mvc;
using Typebeat.Web.Data;
using Typebeat.Web.Social;

namespace Typebeat.Web.Pages;

/// <summary>
/// /watching: the payoff for the bell on a profile. Recent uploads by the mappers the signed-in
/// user watches, newest first, rendered with the same set cards the listing and the profile use.
///
/// This page IS the notification: the site has no notification system, and this feature
/// deliberately did not grow one, so "tell me when they upload" is answered by a page you visit
/// rather than a badge that finds you.
///
/// Signed-in only (it is a view of YOUR watchlist, so there is nothing to render for a stranger);
/// an anonymous hit bounces to /login the same way a signed-out favourite post does.
///
/// Paging is plain <c>?page=</c> offset paging, not the keyset cursor the /beatmapsets listing
/// uses. The listing is the site's one deep, hot, sorted-many-ways surface; this one is a handful
/// of mappers' uploads, where an OFFSET over an index scan costs nothing and a cursor would be
/// ceremony. Revisit if watchlists ever get long enough to page past a few hundred rows.
/// </summary>
public sealed class WatchingModel(Db db) : TypebeatPageModel
{
    private const int page_size = 24;

    /// <summary>Cap on the "who you watch" strip, so one enthusiast cannot make this page render
    /// an unbounded list. The feed below is unaffected: it pages independently.</summary>
    private const int max_mappers = 200;

    /// <summary>The mappers being watched, most recently watched first (the "who" strip).</summary>
    public IReadOnlyList<UserRowModel> Mappers { get; private set; } = [];

    /// <summary>How many mappers are watched in total, which is the strip's count even in the
    /// (currently impossible) case where <see cref="max_mappers"/> trimmed the strip itself.</summary>
    public int WatchedCount { get; private set; }

    /// <summary>This page of their uploads, newest submission first.</summary>
    public IReadOnlyList<BeatmapsetCardModel> Sets { get; private set; } = [];

    public int PageNumber { get; private set; } = 1;
    public bool HasPrevious => PageNumber > 1;
    public bool HasNext { get; private set; }

    public async Task<IActionResult> OnGetAsync(int page = 1)
    {
        if (CurrentUser is null)
            return Redirect("/login");

        PageNumber = page < 1 ? 1 : page;

        long viewerId = CurrentUser.Id;

        await using var conn = await db.OpenAsync(HttpContext.RequestAborted);

        WatchedCount = await Follows.WatchedMapperCountAsync(conn, viewerId, HttpContext.RequestAborted);

        Mappers = (await conn.QueryAsync<UserRowModel>(
            $"""
             {UserRowSql.Select}
             JOIN user_follows f ON f.followee_id = u.id
             WHERE f.follower_id = @viewerId AND f.kind = @kind AND NOT u.restricted
             ORDER BY f.created_at DESC, u.id DESC
             LIMIT {max_mappers}
             """,
            new { viewerId, kind = Follows.MapperKind })).ToList();

        // Published statuses only, exactly what the /beatmapsets listing shows: a watched
        // mapper's hidden drafts and removed sets are not "an upload you can go play", and the
        // set page would 404 or 403 for the watcher anyway. Ordered by submitted_at (when the set
        // first appeared) rather than updated_at, so a mapper re-uploading an old map does not
        // shove it back to the top of everyone's feed.
        var sets = (await conn.QueryAsync<BeatmapsetCardModel>(
            $"""
             {BeatmapsetCardSql.Select}
             WHERE s.status IN ('pending', 'unranked', 'ranked')
               AND NOT u.restricted
               AND EXISTS (SELECT 1 FROM user_follows f
                           WHERE f.follower_id = @viewerId AND f.followee_id = s.owner_id
                             AND f.kind = @kind)
             ORDER BY s.submitted_at DESC, s.id DESC
             LIMIT {page_size + 1} OFFSET @offset
             """,
            new { viewerId, kind = Follows.MapperKind, offset = (PageNumber - 1) * page_size })).ToList();

        HasNext = sets.Count > page_size;
        if (HasNext)
            sets.RemoveAt(page_size);
        Sets = sets;

        ViewData["Title"] = "Watched mappers";
        ViewData["MetaDescription"] = "New maps from the mappers you watch on type!beat.";

        return Page();
    }
}
