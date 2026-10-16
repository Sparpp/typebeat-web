using System.Globalization;
using Dapper;
using Typebeat.Web.Auth;
using Typebeat.Web.Data;
using Typebeat.Web.Scoring;
using Typebeat.Web.Social;
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
///
/// <para>
/// THE <c>{ruleset}</c> SEGMENT IS VALIDATED, and that is a fix rather than pedantry. This server
/// has exactly one ruleset, so the handler has nothing to do with the value; the route however is
/// two segments wide and therefore swallows every other two-segment <c>users/{id}/...</c> path the
/// client can construct. Task 81 found the consequence: <c>users/{id}/kudosu</c> and
/// <c>users/{id}/recent_activity</c> did not 404, they matched HERE and answered a user object,
/// which is worse than a 404 because the caller is expecting a list and gets a deserialisation
/// failure instead of a plain "no such route". Anything that is not this server's ruleset now 404s.
/// </para>
/// </summary>
public static class UserEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        // Anonymous since backlog 406: the payload reads no viewer and is the public profile page's
        // data, so a guest client's profile overlay can open it too.
        app.MapGet("/api/v2/users/{lookup}", Handle);
        app.MapGet("/api/v2/users/{lookup}/{ruleset}", HandleWithRuleset);
    }

    /// <summary>
    /// The two-segment form. <c>GetUserRequest</c> builds its target as
    /// <c>users/{lookup}/{ruleset?.ShortName}</c>, so the segment is either this server's ruleset
    /// or empty (which routes to the one-segment overload instead); anything else is not a profile
    /// fetch at all and must not be answered as one.
    /// </summary>
    private static Task<IResult> HandleWithRuleset(string lookup, string ruleset, HttpContext ctx, Db db)
        => string.Equals(ruleset, UserWire.PlayMode, StringComparison.OrdinalIgnoreCase)
            ? Handle(lookup, ctx, db)
            : Task.FromResult(WireJson.Error(StatusCodes.Status404NotFound, "not found"));

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

        // The profile overlay's five subsection counters, from the same fragments the section
        // endpoints page over (ProfileScoreEndpoints). Landed WITH those endpoints, never before
        // them: a section whose list works and whose count does not is a wrong number on screen.
        var sections = await ProfileScores.CountsForUserAsync(conn, row.Id, ctx.RequestAborted);

        // The two graph subsections read their series off the user object rather than fetching.
        var playMonths = await PlayHistory.ForUserAsync(conn, row.Id, ct: ctx.RequestAborted);
        var viewMonths = await ReplayViews.ForUserAsync(conn, row.Id, ct: ctx.RequestAborted);

        // The Beatmaps section's headings, from the predicates its list endpoint pages over.
        var beatmapsets = await ProfileBeatmapsets.CountsForUserAsync(conn, row.Id, ctx.RequestAborted);

        // The header's two follow buttons. The viewer half of the follow state is not needed here.
        var follows = await Follows.ProfileStateAsync(conn, row.Id, viewerId: 0, ctx.RequestAborted);
        int mapperFollowers = await Follows.MapperFollowerCountAsync(conn, row.Id, ctx.RequestAborted);

        var profile = new UserWire.UserProfile(
            row.Id, row.Username, row.CountryCode, row.AvatarKey, row.CoverKey, row.IsAdmin, row.CreatedAt, row.LastVisit, statistics,
            sections,
            playMonths.Select(m => history(m.Month, m.Plays)).ToList(),
            viewMonths.Select(m => history(m.Month, m.Views)).ToList(),
            beatmapsets,
            (int)follows.Followers,
            mapperFollowers);

        return WireJson.Ok(UserWire.User(profile, ctx.Request.Scheme, ctx.Request.Host.Value ?? string.Empty));
    }

    /// <summary>
    /// One point of a profile history graph. The client binds <c>start_date</c> to a plain
    /// <c>DateTime</c> and only ever reads its month, so the month's first day is sent with no time
    /// and no offset; an instant with a zone would invite a timezone shift across the boundary the
    /// rollups were deliberately bucketed in UTC to avoid (024_play_history.sql).
    /// </summary>
    private static UserHistoryCountWire history(DateOnly month, long count) => new()
    {
        StartDate = month.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        Count = count,
    };

    // The play/score aggregates that used to be joined in here now come from
    // UserStatisticsWire.ForUserAsync, which reads user_stats itself so the login payload gets the
    // same numbers from the same place.
    private const string user_select =
        """
        SELECT u.id AS Id, u.username::text AS Username, u.country_code AS CountryCode,
               u.avatar_key AS AvatarKey, u.is_admin AS IsAdmin, u.created_at AS CreatedAt,
               u.last_visit AS LastVisit, u.cover_key AS CoverKey
        FROM users u
        """;

    // created_at/last_visit are timestamptz; Npgsql materializes them as DateTime (Kind=Utc).
    // Positional: Dapper matches by column order, so new columns are appended.
    private sealed record UserRow(
        long Id, string Username, string CountryCode, string? AvatarKey, bool IsAdmin,
        DateTime CreatedAt, DateTime? LastVisit, string? CoverKey);
}
