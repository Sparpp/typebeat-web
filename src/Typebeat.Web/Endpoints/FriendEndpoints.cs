using Dapper;
using Newtonsoft.Json;
using Npgsql;
using Typebeat.Web.Auth;
using Typebeat.Web.Data;
using Typebeat.Web.Social;
using Typebeat.Web.Wire;

namespace Typebeat.Web.Endpoints;

/// <summary>
/// The game's friends list, which on this server IS the website's player follows (<c>user_follows</c>, kind
/// <c>'user'</c>): following someone on the site makes them a friend in game and the reverse.
///
///  - <c>GET /api/v2/friends</c> (GetFriendsRequest, fetched at login): everyone the caller follows.
///  - <c>POST /api/v2/friends?target={id}</c> (AddFriendRequest, the profile header's follow button).
///  - <c>DELETE /api/v2/friends/{id}</c> (DeleteFriendRequest).
///
/// <para>
/// A relation is <c>mutual</c> when the target follows the caller back. Restricted accounts are delisted on both
/// sides, as on the website: they are not listed, and cannot be followed.
/// </para>
/// </summary>
public static class FriendEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v2/friends", ListAsync).RequireBearer();
        app.MapPost("/api/v2/friends", AddAsync).RequireBearer();
        app.MapDelete("/api/v2/friends/{targetId:long}", RemoveAsync).RequireBearer();
    }

    private static async Task<IResult> ListAsync(HttpContext ctx, Db db)
    {
        await using var conn = await db.OpenAsync(ctx.RequestAborted);

        var rows = await relationsAsync(conn, ctx.AuthedUser().Id, targetId: null, ctx.RequestAborted);
        return WireJson.Ok(rows.Select(r => toWire(r, ctx)).ToList());
    }

    private static async Task<IResult> AddAsync(HttpContext ctx, Db db)
    {
        var user = ctx.AuthedUser();

        if (!long.TryParse(ctx.Request.Query["target"], out long targetId))
            return WireJson.Error(StatusCodes.Status422UnprocessableEntity, "target is required");

        // The table's CHECK would otherwise turn this into a 500.
        if (targetId == user.Id)
            return WireJson.Error(StatusCodes.Status422UnprocessableEntity, "cannot follow yourself");

        await using var conn = await db.OpenAsync(ctx.RequestAborted);

        bool followable = await conn.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM users WHERE id = @targetId AND NOT restricted AND deleted_at IS NULL)",
            new { targetId });

        if (!followable)
            return WireJson.Error(StatusCodes.Status404NotFound, "user not found");

        await Follows.SetAsync(conn, user.Id, targetId, Follows.UserKind, on: true, ctx.RequestAborted);

        var relation = (await relationsAsync(conn, user.Id, targetId, ctx.RequestAborted)).Single();
        return WireJson.Ok(new { user_relation = toWire(relation, ctx) });
    }

    private static async Task<IResult> RemoveAsync(long targetId, HttpContext ctx, Db db)
    {
        await using var conn = await db.OpenAsync(ctx.RequestAborted);

        await Follows.SetAsync(conn, ctx.AuthedUser().Id, targetId, Follows.UserKind, on: false, ctx.RequestAborted);
        return WireJson.Ok(new { });
    }

    /// <summary>The caller's follows (or the one to <paramref name="targetId"/>), with whether each follows back.</summary>
    private static async Task<List<RelationRow>> relationsAsync(NpgsqlConnection conn, long userId, long? targetId, CancellationToken ct)
        => (await conn.QueryAsync<RelationRow>(new CommandDefinition(
            """
            SELECT u.id AS TargetId, u.username::text AS Username, u.country_code::text AS CountryCode, u.avatar_key AS AvatarKey,
                   EXISTS (SELECT 1 FROM user_follows back
                           WHERE back.follower_id = u.id AND back.followee_id = @userId AND back.kind = 'user') AS Mutual
            FROM user_follows f
            JOIN users u ON u.id = f.followee_id
            WHERE f.follower_id = @userId AND f.kind = 'user' AND NOT u.restricted AND u.deleted_at IS NULL
              AND (@targetId::bigint IS NULL OR u.id = @targetId)
            ORDER BY f.created_at DESC, u.id
            """,
            new { userId, targetId }, cancellationToken: ct))).ToList();

    private static RelationWire toWire(RelationRow r, HttpContext ctx) => new()
    {
        TargetId = r.TargetId,
        Mutual = r.Mutual,
        Target = new ScoreUserWire
        {
            Id = r.TargetId,
            Username = r.Username,
            CountryCode = r.CountryCode,
            AvatarUrl = UserWire.AvatarUrl(ctx.Request.Scheme, ctx.Request.Host.Value ?? string.Empty, r.AvatarKey),
        },
    };

    private sealed class RelationRow
    {
        public long TargetId { get; set; }
        public string Username { get; set; } = string.Empty;
        public string CountryCode { get; set; } = "XX";
        public string? AvatarKey { get; set; }
        public bool Mutual { get; set; }
    }
}

/// <summary>The client's APIRelation.</summary>
public sealed class RelationWire
{
    [JsonProperty("target_id")]
    public long TargetId { get; init; }

    /// <summary>Binds to the client's RelationType by member name.</summary>
    [JsonProperty("relation_type")]
    public string RelationType { get; init; } = "friend";

    [JsonProperty("mutual")]
    public bool Mutual { get; init; }

    [JsonProperty("target")]
    public ScoreUserWire? Target { get; init; }
}
