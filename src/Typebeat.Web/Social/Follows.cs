using Dapper;
using Npgsql;

namespace Typebeat.Web.Social;

/// <summary>
/// The two social relations stored in <c>user_follows</c> (023_follows.sql): following a player
/// and watching a mapper. One place owns the kind strings, the toggle write and the count reads
/// so the profile header, the two list pages and the /watching feed cannot drift apart.
/// </summary>
public static class Follows
{
    /// <summary>Following a player (the profile Follow button; drives follower/following lists).</summary>
    public const string UserKind = "user";

    /// <summary>Watching a mapper (the profile bell; drives the /watching upload feed).</summary>
    public const string MapperKind = "mapper";

    public static bool IsKnownKind(string kind) => kind is UserKind or MapperKind;

    /// <summary>
    /// Flips one edge and reports the state it landed in. Toggle semantics, matching the
    /// favourite button (the only other per-user toggle on the site): the caller posts "act on
    /// this relation" rather than a desired state, so a second post undoes the first.
    ///
    /// Both halves are idempotent at the storage layer, which is what makes a double-submit (two
    /// clicks racing, a retried request) safe rather than a duplicate row or a crash: DELETE of a
    /// missing row affects nothing, and the INSERT rides the primary key with ON CONFLICT DO
    /// NOTHING. The two statements run in one transaction so a concurrent reader never observes
    /// the intermediate no-row state of an "unfollow then re-follow" race.
    /// </summary>
    /// <returns>True when the relation now exists (followed / watched), false when it was removed.</returns>
    public static async Task<bool> ToggleAsync(
        NpgsqlConnection conn, long followerId, long followeeId, string kind, CancellationToken ct = default)
    {
        await using var tx = await conn.BeginTransactionAsync(ct);

        int deleted = await conn.ExecuteAsync(
            """
            DELETE FROM user_follows
            WHERE follower_id = @followerId AND followee_id = @followeeId AND kind = @kind
            """,
            new { followerId, followeeId, kind }, tx);

        if (deleted == 0)
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO user_follows (follower_id, followee_id, kind)
                VALUES (@followerId, @followeeId, @kind)
                ON CONFLICT DO NOTHING
                """,
                new { followerId, followeeId, kind }, tx);
        }

        await tx.CommitAsync(ct);

        return deleted == 0;
    }

    /// <summary>
    /// Follower/following counts for one profile, plus whether the viewer already follows and/or
    /// watches it. One round trip: the profile header needs all four on every view, and each is a
    /// small index probe (the PK for the viewer's own edges and the outgoing count, the
    /// followee index for the incoming count).
    ///
    /// Restricted accounts are excluded on BOTH sides, the same delisting every other surface
    /// applies: a restricted follower must not inflate a count whose list page will not show them.
    /// </summary>
    public static async Task<ProfileFollowState> ProfileStateAsync(
        NpgsqlConnection conn, long profileId, long viewerId, CancellationToken ct = default)
        => await conn.QuerySingleAsync<ProfileFollowState>(
            new CommandDefinition(
                """
                SELECT
                    (SELECT count(*)
                     FROM user_follows f
                     JOIN users fu ON fu.id = f.follower_id
                     WHERE f.followee_id = @profileId AND f.kind = 'user' AND NOT fu.restricted) AS Followers,
                    (SELECT count(*)
                     FROM user_follows f
                     JOIN users fu ON fu.id = f.followee_id
                     WHERE f.follower_id = @profileId AND f.kind = 'user' AND NOT fu.restricted) AS Following,
                    EXISTS (SELECT 1 FROM user_follows f
                            WHERE f.follower_id = @viewerId AND f.followee_id = @profileId
                              AND f.kind = 'user')   AS ViewerFollows,
                    EXISTS (SELECT 1 FROM user_follows f
                            WHERE f.follower_id = @viewerId AND f.followee_id = @profileId
                              AND f.kind = 'mapper') AS ViewerWatches
                """,
                new { profileId, viewerId }, cancellationToken: ct));

    /// <summary>How many mappers this user watches (the /watching page's subtitle).</summary>
    public static async Task<int> WatchedMapperCountAsync(
        NpgsqlConnection conn, long userId, CancellationToken ct = default)
        => await conn.ExecuteScalarAsync<int>(
            new CommandDefinition(
                """
                SELECT count(*)
                FROM user_follows f
                JOIN users u ON u.id = f.followee_id
                WHERE f.follower_id = @userId AND f.kind = 'mapper' AND NOT u.restricted
                """,
                new { userId }, cancellationToken: ct));
}

/// <summary>
/// The follow numbers + viewer relation one profile view needs.
/// </summary>
/// <param name="Followers">Users following this profile ('user' kind), restricted excluded.</param>
/// <param name="Following">Users this profile follows ('user' kind), restricted excluded.</param>
/// <param name="ViewerFollows">The signed-in viewer follows this profile (Follow button is on).</param>
/// <param name="ViewerWatches">The signed-in viewer watches this profile as a mapper (bell is on).</param>
public sealed record ProfileFollowState(long Followers, long Following, bool ViewerFollows, bool ViewerWatches)
{
    /// <summary>Anonymous / not-yet-loaded default: no counts, no viewer relation.</summary>
    public static readonly ProfileFollowState Empty = new(0, 0, false, false);
}
