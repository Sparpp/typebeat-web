using System.Globalization;
using Dapper;
using Typebeat.Web.Auth;
using Typebeat.Web.Data;
using Typebeat.Web.Wire;

namespace Typebeat.Web.Endpoints;

/// <summary>
/// GET /api/v2/users/{lookup}/{ruleset?}: the client's GetUserRequest, fired by the profile
/// overlay (UserProfileOverlay) whenever a user is shown, and by LocalUserStatisticsProvider for
/// the local player's own statistics. Without this route the request never completes and the
/// overlay spins forever. Returns the APIUser payload UserWire.User builds; the statistics block is
/// assembled by the shared <see cref="UserStatisticsWire"/> so the in-game profile, the login
/// payload, the website profile and /rankings all agree.
///
/// <para>The <c>?key=id|username</c> hint (GetUserRequest) disambiguates a numeric username from an
/// id; absent, an all-digit lookup is treated as an id. Restricted/deleted users 404 (the overlay
/// shows "user not found" rather than hanging).</para>
/// </summary>
public static class UserEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v2/users/{lookup}", Handle).RequireBearer();
        app.MapGet("/api/v2/users/{lookup}/{ruleset}", Handle).RequireBearer();
    }

    private static async Task<IResult> Handle(string lookup, HttpContext ctx, Db db)
    {
        await using var conn = await db.OpenAsync(ctx.RequestAborted);

        string key = ctx.Request.Query["key"].ToString();
        bool byId = key == "id" || (key.Length == 0 && long.TryParse(lookup, NumberStyles.None, CultureInfo.InvariantCulture, out _));

        // Restricted/deleted users are invisible everywhere else; the profile fetch is no exception.
        var row = byId && long.TryParse(lookup, NumberStyles.None, CultureInfo.InvariantCulture, out long id)
            ? await conn.QuerySingleOrDefaultAsync<UserRow>(user_select + " WHERE u.id = @id AND NOT u.restricted AND u.deleted_at IS NULL", new { id })
            : await conn.QuerySingleOrDefaultAsync<UserRow>(user_select + " WHERE u.username = @lookup::citext AND NOT u.restricted AND u.deleted_at IS NULL", new { lookup });

        if (row is null)
            return WireJson.Error(StatusCodes.Status404NotFound, "user not found");

        object statistics = await UserStatisticsWire.ForUserAsync(conn, row.Id, ctx.RequestAborted);

        var profile = new UserWire.UserProfile(
            row.Id, row.Username, row.CountryCode, row.AvatarKey, row.IsAdmin, row.CreatedAt, row.LastVisit, statistics);

        return WireJson.Ok(UserWire.User(profile, ctx.Request.Scheme, ctx.Request.Host.Value ?? string.Empty));
    }

    // The play/score aggregates that used to be joined in here now come from
    // UserStatisticsWire.ForUserAsync, which reads user_stats itself so the login payload gets the
    // same numbers from the same place.
    private const string user_select =
        """
        SELECT u.id AS Id, u.username::text AS Username, u.country_code AS CountryCode,
               u.avatar_key AS AvatarKey, u.is_admin AS IsAdmin, u.created_at AS CreatedAt,
               u.last_visit AS LastVisit
        FROM users u
        """;

    // created_at/last_visit are timestamptz; Npgsql materializes them as DateTime (Kind=Utc).
    private sealed record UserRow(
        long Id, string Username, string CountryCode, string? AvatarKey, bool IsAdmin,
        DateTime CreatedAt, DateTime? LastVisit);
}
