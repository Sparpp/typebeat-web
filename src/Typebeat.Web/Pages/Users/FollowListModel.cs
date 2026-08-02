using System.Globalization;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Typebeat.Web.Data;
using Typebeat.Web.Social;

namespace Typebeat.Web.Pages.Users;

/// <summary>
/// Shared body of the two follow list pages hanging off a profile: /users/{id}/followers (people
/// who follow this user) and /users/{id}/following (people this user follows). Both are the same
/// query with the join direction flipped, so the direction is the only thing the subclasses
/// choose; everything else (owner lookup, delisting, ordering, the cap) lives here once.
///
/// Public pages, like the profile itself: signing in is needed to follow, not to look. Only the
/// 'user' relation is listed; mapper watches are the viewer's own business and surface on
/// /watching instead, never as a public "watched by" list.
///
/// Id-only routes (no name form). The profile page owns the name → id canonical redirect, and
/// every link into these lists is built from the resolved id.
/// </summary>
public abstract class FollowListModel(Db db) : TypebeatPageModel
{
    /// <summary>
    /// Rows shown before the page stops. Following is a flat list with no paging (tens of users
    /// site-wide, and a follower list is not a feed you scroll); the cap exists so a future
    /// popular account cannot turn one page view into an unbounded render.
    /// </summary>
    private const int max_rows = 200;

    /// <summary>The profile the list belongs to.</summary>
    public ProfileRef Owner { get; private set; } = null!;

    public IReadOnlyList<UserRowModel> Users { get; private set; } = [];

    /// <summary>True when the cap trimmed the list (the page says so rather than lying).</summary>
    public bool Truncated { get; private set; }

    /// <summary>Which direction this page reads: followers of the owner, or the owner's followees.</summary>
    protected abstract bool Incoming { get; }

    protected async Task<IActionResult> LoadAsync(long id)
    {
        await using var conn = await db.OpenAsync(HttpContext.RequestAborted);

        var owner = await conn.QuerySingleOrDefaultAsync<ProfileRef>(
            "SELECT id AS Id, username::text AS Username FROM users WHERE id = @id AND NOT restricted",
            new { id });

        if (owner is null)
            return NotFound();

        Owner = owner;

        // Incoming: u is the follower, the edge points AT the owner. Outgoing: u is the followee.
        // Restricted accounts are excluded on the listed side (they are delisted everywhere, and
        // their profile 404s, so a row here would be a dead link); Follows.ProfileStateAsync
        // filters the counts the same way, which is what keeps count and list agreeing.
        string joinColumn = Incoming ? "follower_id" : "followee_id";
        string matchColumn = Incoming ? "followee_id" : "follower_id";

        var rows = (await conn.QueryAsync<UserRowModel>(
            $"""
             {UserRowSql.Select}
             JOIN user_follows f ON f.{joinColumn} = u.id
             WHERE f.{matchColumn} = @id AND f.kind = @kind AND NOT u.restricted
             ORDER BY f.created_at DESC, u.id DESC
             LIMIT {max_rows + 1}
             """,
            new { id, kind = Follows.UserKind })).ToList();

        Truncated = rows.Count > max_rows;
        if (Truncated)
            rows.RemoveAt(max_rows);
        Users = rows;

        string what = Incoming ? "Followers" : "Following";
        ViewData["Title"] = $"{what} of {Owner.Username}";
        ViewData["MetaDescription"] = Incoming
            ? $"People following {Owner.Username} on type!beat."
            : $"People {Owner.Username} follows on type!beat.";

        return Page();
    }

    /// <summary>"3 followers" / "1 follower", with the count formatted like every other counter.</summary>
    public string CountLabel(string singular, string plural)
        => $"{Users.Count.ToString("N0", CultureInfo.InvariantCulture)} {(Users.Count == 1 ? singular : plural)}";

    public sealed record ProfileRef(long Id, string Username);
}
