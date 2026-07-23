using System.Globalization;
using Dapper;
using Typebeat.Web.Auth;
using Typebeat.Web.Data;
using Typebeat.Web.Scoring;
using Typebeat.Web.Wire;

namespace Typebeat.Web.Endpoints;

/// <summary>
/// GET /api/v2/users/{lookup}/{ruleset?}: the client's GetUserRequest, fired by the profile
/// overlay (UserProfileOverlay) whenever a user is shown. Without this route the request never
/// completes and the overlay spins forever. Returns the APIUser payload UserWire.User builds, with
/// real statistics: global rank + ranked score from the shared <see cref="GlobalRanking"/> metric
/// (so the in-game profile, the website profile, and /rankings all agree), plus play totals from
/// user_stats and SS/S/A grade counts from the best-per-map fold.
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

        var ranking = await GlobalRanking.ForUserAsync(conn, row.Id, ctx.RequestAborted);
        var (ss, s, a, accuracyPercent) = await GradeCountsAsync(conn, row.Id, ctx.RequestAborted);

        var statistics = UserWire.ProfileStatistics(
            ranking.GlobalRank, ranking.RankedScore, row.TotalScore, row.PlayCount, row.PlayTimeS,
            accuracyPercent, ss, s, a);

        var profile = new UserWire.UserProfile(
            row.Id, row.Username, row.CountryCode, row.AvatarKey, row.IsAdmin, row.CreatedAt, row.LastVisit, statistics);

        return WireJson.Ok(UserWire.User(profile, ctx.Request.Scheme, ctx.Request.Host.Value ?? string.Empty));
    }

    private const string user_select =
        """
        SELECT u.id AS Id, u.username::text AS Username, u.country_code AS CountryCode,
               u.avatar_key AS AvatarKey, u.is_admin AS IsAdmin, u.created_at AS CreatedAt,
               u.last_visit AS LastVisit,
               COALESCE(st.play_count, 0)  AS PlayCount,
               COALESCE(st.total_score, 0) AS TotalScore,
               COALESCE(st.play_time_s, 0) AS PlayTimeS
        FROM users u
        LEFT JOIN user_stats st ON st.user_id = u.id
        """;

    /// <summary>
    /// SS/S/A counts + mean accuracy (0–100) over the user's best ranked+passed score per map; the
    /// same per-map-best fold the website profile uses. B/C/D exist in our grading but have no slot
    /// in the client grade_counts DTO, so they are folded away here.
    /// </summary>
    private static async Task<(int Ss, int S, int A, double AccuracyPercent)> GradeCountsAsync(
        Npgsql.NpgsqlConnection conn, long userId, CancellationToken ct)
    {
        var rows = await conn.QueryAsync<(string Rank, long Count, double AccSum)>(
            new CommandDefinition(
                """
                SELECT best.rank AS Rank, count(*) AS Count, sum(best.accuracy) AS AccSum
                FROM (
                    SELECT DISTINCT ON (sc.beatmap_id) sc.rank, sc.accuracy
                    FROM scores sc
                    WHERE sc.user_id = @userId AND sc.ranked AND sc.passed
                    ORDER BY sc.beatmap_id, sc.total_score DESC, sc.id ASC
                ) best
                GROUP BY best.rank
                """,
                new { userId },
                cancellationToken: ct));

        int ss = 0, s = 0, a = 0;
        long total = 0;
        double accSum = 0;

        foreach (var r in rows)
        {
            total += r.Count;
            accSum += r.AccSum;

            switch (r.Rank)
            {
                case "X" or "XH": ss += (int)r.Count; break;
                case "S" or "SH": s += (int)r.Count; break;
                case "A": a += (int)r.Count; break;
            }
        }

        double accuracyPercent = total > 0 ? accSum / total * 100.0 : 0.0;
        return (ss, s, a, accuracyPercent);
    }

    // created_at/last_visit are timestamptz; Npgsql materializes them as DateTime (Kind=Utc).
    private sealed record UserRow(
        long Id, string Username, string CountryCode, string? AvatarKey, bool IsAdmin,
        DateTime CreatedAt, DateTime? LastVisit, int PlayCount, long TotalScore, long PlayTimeS);
}
