using Dapper;
using Microsoft.AspNetCore.Mvc;
using Typebeat.Web.Data;
using Typebeat.Web.Social;

namespace Typebeat.Web.Pages;

/// <summary>
/// /watching: the notifications home, and the write endpoints behind the header bell.
///
/// <para>
/// It was born (task 64) as the ONLY payoff for the mapper bell, back when the site had no
/// notification system and "tell me when they upload" had to be answered by a page you
/// remembered to visit. Task 70 gave it the badge that finds you, and this page kept its URL and
/// its two lower sections (who you watch, and their uploads) while gaining the notification feed
/// at the top. So it now serves three jobs:
/// </para>
///
/// <list type="bullet">
///   <item>the "see all" target of the header dropdown, showing the full notification list;</item>
///   <item>the watched-mapper management surface (the strip of who you watch);</item>
///   <item>the historical upload feed, which is the ONLY place pre-notification uploads appear
///         (027 backfills nothing, deliberately).</item>
/// </list>
///
/// <para>
/// It also owns every notification handler, because they are all "act on MY notifications" and
/// this is the page that is about exactly that: <c>?handler=Panel</c> renders the dropdown's
/// contents for the bell's fetch, <c>?handler=Read</c> marks one row read and forwards to its
/// target, <c>?handler=ReadAll</c> clears the badge.
/// </para>
///
/// <para>
/// Signed-in only (it is a view of YOUR watchlist, so there is nothing to render for a stranger);
/// an anonymous hit bounces to /login the same way a signed-out favourite post does.
/// </para>
///
/// <para>
/// Paging is plain <c>?page=</c> offset paging, not the keyset cursor the /beatmapsets listing
/// uses. The listing is the site's one deep, hot, sorted-many-ways surface; this one is a handful
/// of mappers' uploads, where an OFFSET over an index scan costs nothing and a cursor would be
/// ceremony. Revisit if watchlists ever get long enough to page past a few hundred rows.
/// </para>
/// </summary>
public sealed class WatchingModel(Db db) : TypebeatPageModel
{
    private const int page_size = 24;

    /// <summary>Cap on the "who you watch" strip, so one enthusiast cannot make this page render
    /// an unbounded list. The feed below is unaffected: it pages independently.</summary>
    private const int max_mappers = 200;

    /// <summary>This user's recent notifications, newest first (the page's lead section).</summary>
    public IReadOnlyList<NotificationRowModel> Notes { get; private set; } = [];

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

        // Only the first page carries the notification list: page 2+ is somebody walking BACK
        // through the upload history, and repeating the same newest-first notifications above it
        // would be noise (and a second query per page for a list nobody is paging).
        if (PageNumber == 1)
            Notes = await Notifications.RecentAsync(conn, viewerId, Notifications.PageSize, HttpContext.RequestAborted);

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
             WHERE s.status IN ('pending', 'unranked', 'ranked', 'loved')
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

        ViewData["Title"] = "Notifications";
        ViewData["MetaDescription"] = "New maps from the mappers you watch on type!beat.";

        return Page();
    }

    /// <summary>
    /// The header dropdown's contents, as HTML, for the bell's fetch. A partial rather than JSON
    /// so the panel and this page render notifications through the SAME template
    /// (_NotificationRow), including the mark-read form each row posts: a JSON endpoint would
    /// mean a second, hand-built copy of that markup living in JavaScript, and the two would
    /// drift the first time a row gained a field.
    ///
    /// Anonymous gets the /login redirect every other handler here gives, not an empty panel:
    /// the bell is never rendered signed-out, so an anonymous hit is a stale tab or a hand-made
    /// request, and both are answered honestly by sending them to sign in.
    /// </summary>
    public async Task<IActionResult> OnGetPanelAsync()
    {
        if (CurrentUser is null)
            return Redirect("/login");

        await using var conn = await db.OpenAsync(HttpContext.RequestAborted);

        var rows = await Notifications.RecentAsync(
            conn, CurrentUser.Id, Notifications.PanelSize, HttpContext.RequestAborted);

        return Partial("_NotificationPanel", rows);
    }

    /// <summary>
    /// Clicking a notification: mark THAT row read, then forward to what it points at.
    ///
    /// This is a POST that redirects rather than a link, because reading and navigating are one
    /// user action and splitting them would need JavaScript to keep them together. Nothing
    /// intercepts it: with scripting off it behaves identically, which is why the row markup has
    /// no fetch path at all (unlike the follow toggles, whose whole point is to NOT navigate).
    ///
    /// Ownership is <see cref="Notifications.MarkReadAsync"/>'s user_id predicate: another user's
    /// notification id matches no row, so a forged POST reads nothing and gets the same 404 a
    /// deleted notification gets. Indistinguishable on purpose (the pin handlers' rule).
    /// </summary>
    public async Task<IActionResult> OnPostReadAsync(long id)
    {
        if (CurrentUser is null)
            return Redirect("/login");

        await using var conn = await db.OpenAsync(HttpContext.RequestAborted);

        var target = await Notifications.MarkReadAsync(conn, CurrentUser.Id, id, HttpContext.RequestAborted);

        if (target is null)
            return NotFound();

        return Redirect(target.Url);
    }

    /// <summary>
    /// "Mark all read" (the panel's header control, and the page's). Clears the badge in one
    /// statement.
    ///
    /// The fetch path gets the new unread count back so the panel can drop the badge without a
    /// reload, matching the follow toggles' JSON convention; a plain submit lands back on this
    /// page, re-rendered with everything read.
    /// </summary>
    public async Task<IActionResult> OnPostReadAllAsync()
    {
        if (CurrentUser is null)
            return Redirect("/login");

        await using var conn = await db.OpenAsync(HttpContext.RequestAborted);

        await Notifications.MarkAllReadAsync(conn, CurrentUser.Id, HttpContext.RequestAborted);

        if (string.Equals(Request.Headers["X-Requested-With"], "fetch", StringComparison.Ordinal))
            return new JsonResult(new { unread = 0 });

        return Redirect("/watching");
    }
}
