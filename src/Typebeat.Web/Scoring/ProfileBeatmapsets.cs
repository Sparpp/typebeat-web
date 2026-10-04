using Dapper;
using Npgsql;

namespace Typebeat.Web.Scoring;

/// <summary>
/// Which sets each subsection of the game profile's BEATMAPS section lists
/// (<c>GET /api/v2/users/{id}/beatmapsets/{type}</c>), and the count each heading prints (the
/// <c>*_beatmapset_count</c> keys on the user payload). One place for both, the
/// <see cref="ProfileScores"/> rule: a heading that disagrees with the list under it is a wrong number.
///
/// <para>
/// Four of the client's seven subsections have data here: favourites (the favourites table) and the
/// owner's own sets by status. The client's "Graveyarded" subsection is this server's 'unranked'
/// status, the same mapping the listing search's graveyard category uses. Loved, guest and
/// nominated have nothing behind them (no loved status, one owner per set, no nomination step), so
/// they are not served and their counts stay absent (0).
/// </para>
///
/// <para>
/// Fragments are WHERE clauses over <c>beatmapsets s JOIN users u ON u.id = s.owner_id</c>, binding
/// <c>@id</c> (the profile owner). They interpolate only C# literals.
/// </para>
/// </summary>
public static class ProfileBeatmapsets
{
    private const string favourite_where =
        "EXISTS (SELECT 1 FROM favourites f WHERE f.set_id = s.id AND f.user_id = @id)"
        + " AND s.status IN ('pending', 'unranked', 'ranked') AND NOT u.restricted";

    private static string ownedWhere(string status) => $"s.owner_id = @id AND s.status = '{status}'";

    /// <summary>The WHERE for one subsection, or null for a type this server has no data for.</summary>
    public static string? WhereFor(string type) => type switch
    {
        "favourite" => favourite_where,
        "ranked" => ownedWhere("ranked"),
        "pending" => ownedWhere("pending"),
        "graveyard" => ownedWhere("unranked"),
        _ => null,
    };

    /// <summary>
    /// Newest first: favourites by when they were favourited, the owner's sets by submission. Each is
    /// tie-broken by set id, so paging is stable.
    /// </summary>
    public static string OrderFor(string type) => type == "favourite"
        ? "ORDER BY (SELECT f.created_at FROM favourites f WHERE f.set_id = s.id AND f.user_id = @id) DESC, s.id DESC"
        : "ORDER BY s.submitted_at DESC, s.id DESC";

    public static async Task<BeatmapsetCounts> CountsForUserAsync(NpgsqlConnection conn, long userId, CancellationToken ct = default)
        => await conn.QuerySingleAsync<BeatmapsetCounts>(new CommandDefinition(
            $"""
             SELECT
                 (SELECT count(*) FROM beatmapsets s JOIN users u ON u.id = s.owner_id WHERE {favourite_where})::int AS Favourite,
                 (SELECT count(*) FROM beatmapsets s WHERE {ownedWhere("ranked")})::int AS Ranked,
                 (SELECT count(*) FROM beatmapsets s WHERE {ownedWhere("pending")})::int AS Pending,
                 (SELECT count(*) FROM beatmapsets s WHERE {ownedWhere("unranked")})::int AS Graveyard
             """,
            new { id = userId }, cancellationToken: ct));

    public sealed record BeatmapsetCounts(int Favourite, int Ranked, int Pending, int Graveyard);
}
