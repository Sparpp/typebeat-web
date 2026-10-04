using System.Globalization;
using Dapper;
using Typebeat.Web.Auth;
using Typebeat.Web.Data;
using Typebeat.Web.Scoring;
using Typebeat.Web.Wire;

namespace Typebeat.Web.Endpoints;

/// <summary>
/// <c>GET /api/v2/users/{id}/beatmapsets/{favourite|ranked|pending|graveyard}</c>: the game
/// profile's BEATMAPS section (GetUserBeatmapsRequest), answering a JSON array of set cards. Which
/// sets each type is comes from <see cref="ProfileBeatmapsets"/>, which also computes the heading
/// counts on the user payload. <c>most_played</c> is a literal route in
/// <see cref="ProfileScoreEndpoints"/> and wins over the <c>{type}</c> template here.
///
/// <para>Pagination and the restricted-profile 404 follow <see cref="ProfileScoreEndpoints"/>.</para>
/// </summary>
public static class ProfileBeatmapsetEndpoints
{
    private const int default_limit = 6;
    private const int max_limit = 100;

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v2/users/{userId:long}/beatmapsets/{type}", BeatmapsetsAsync).RequireBearer();
    }

    private static async Task<IResult> BeatmapsetsAsync(long userId, string type, HttpContext ctx, Db db)
    {
        type = type.ToLowerInvariant();

        if (ProfileBeatmapsets.WhereFor(type) is not { } where)
            return WireJson.Error(StatusCodes.Status404NotFound, "unknown beatmapset type");

        await using var conn = await db.OpenAsync(ctx.RequestAborted);

        bool visible = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM users WHERE id = @userId AND NOT restricted AND deleted_at IS NULL)",
            new { userId }, cancellationToken: ctx.RequestAborted));

        if (!visible)
            return WireJson.Error(StatusCodes.Status404NotFound, "user not found");

        var q = ctx.Request.Query;
        int offset = int.TryParse(q["offset"], NumberStyles.Integer, CultureInfo.InvariantCulture, out int o) ? Math.Max(0, o) : 0;
        int limit = int.TryParse(q["limit"], NumberStyles.Integer, CultureInfo.InvariantCulture, out int l) ? Math.Clamp(l, 1, max_limit) : default_limit;

        var sets = await BeatmapsetCards.LoadAsync(conn,
            $"""
             FROM beatmapsets s JOIN users u ON u.id = s.owner_id
             WHERE {where}
             {ProfileBeatmapsets.OrderFor(type)}
             LIMIT @limit OFFSET @offset
             """,
            new { id = userId, viewerId = ctx.AuthedUser().Id, limit, offset },
            $"{ctx.Request.Scheme}://{ctx.Request.Host}", ctx.RequestAborted);

        return WireJson.Ok(sets);
    }
}
