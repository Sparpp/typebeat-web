using Dapper;
using Npgsql;
using Typebeat.Web.Scoring;

namespace Typebeat.Web.Pages.Users;

/// <summary>
/// The profile page's SQL (<see cref="ProfileModel"/>), one method per statement. Which rows each
/// score section holds is <see cref="ProfileScores"/>' and <see cref="BeatmapLeaderboard"/>'s, shared
/// with the game client's profile overlay; this class only projects them for the page. The
/// section sizes stay the page's and arrive as <c>limit</c>.
/// </summary>
public static class ProfileQueries
{
    /// <summary>The score row every score section reads back off its <c>best</c> subquery (<see cref="ScoreRowModel"/>, positional).</summary>
    private const string score_row_select =
        """
        SELECT best.id         AS ScoreId,
               best.has_replay AS HasReplay,
               s.id            AS SetId,
               s.title         AS Title,
               s.artist        AS Artist,
               s.title_unicode  AS TitleUnicode,
               s.artist_unicode AS ArtistUnicode,
               s.explicit       AS Explicit,
               b.version_name   AS Version,
               CASE WHEN s.cover_key IS NOT NULL THEN '/' || s.cover_key || '/list.jpg' END AS CoverUrl,
               best.rank       AS Rank,
               best.completion AS Completion,
               best.accuracy   AS Accuracy,
               best.total_score AS TotalScore,
               best.ended_at   AS Date,
               best.mods       AS ModsJson
        """;

    // What each section's row-set fragment (Scoring/ProfileScores) has to project for the
    // SELECT above to read it back off the `best` subquery. pp rides along for every section
    // even though only Best orders by it: one column list keeps the fragments interchangeable
    // here, and the cost is one double per row already being fetched.
    private const string section_columns =
        """
        sc.id, sc.beatmap_id, sc.rank, sc.completion, sc.accuracy, sc.total_score, sc.pp,
        sc.ended_at, sc.mods::text AS mods, sc.replay_key IS NOT NULL AS has_replay
        """;

    /// <summary>The unrestricted user with this name (case-insensitive: the parameter is cast to citext, since a bare text param makes Postgres pick text = text), or null.</summary>
    public static Task<long?> IdByNameAsync(NpgsqlConnection conn, string name)
        => conn.ExecuteScalarAsync<long?>(
            "SELECT id FROM users WHERE username = @name::citext AND NOT restricted",
            new { name });

    public static Task<ProfileModel.UserHeader?> HeaderAsync(NpgsqlConnection conn, long id)
        => conn.QuerySingleOrDefaultAsync<ProfileModel.UserHeader>(
            """
            SELECT u.id          AS Id,
                   u.username::text AS Username,
                   u.avatar_key  AS AvatarKey,
                   u.cover_key   AS CoverKey,
                   u.description AS Description,
                   u.created_at  AS CreatedAt,
                   u.last_visit  AS LastVisit,
                   u.restricted  AS Restricted,
                   u.country_code::text AS CountryCode
            FROM users u
            WHERE u.id = @id
            """,
            new { id });

    /// <summary>The stored section order (026_profile_order.sql), null for the default.</summary>
    public static Task<string[]?> SectionOrderAsync(NpgsqlConnection conn, long id)
        => conn.ExecuteScalarAsync<string[]?>(
            "SELECT profile_order FROM users WHERE id = @id", new { id });

    /// <summary>Total score, play count and play time; all zero when the user_stats row does not exist yet.</summary>
    public static Task<(long, int, long)> StatsAsync(NpgsqlConnection conn, long id)
        => conn.QuerySingleOrDefaultAsync<(long, int, long)>(
            "SELECT total_score, play_count, play_time_s FROM user_stats WHERE user_id = @id",
            new { id });

    /// <summary>
    /// Maps played out of the ranked pool: distinct ranked sets with any score by this user, over the
    /// count of publicly-visible ranked sets (restricted mappers' sets are delisted).
    /// </summary>
    public static Task<(int, int)> MapsPlayedAsync(NpgsqlConnection conn, long id)
        => conn.QuerySingleAsync<(int, int)>(
            """
            SELECT
                (SELECT count(DISTINCT b.set_id)
                 FROM scores sc
                 JOIN beatmaps b    ON b.id = sc.beatmap_id
                 JOIN beatmapsets s ON s.id = b.set_id
                 JOIN users ow      ON ow.id = s.owner_id
                 WHERE sc.user_id = @id AND s.status = 'ranked' AND NOT ow.restricted) AS played,
                (SELECT count(*)
                 FROM beatmapsets s
                 JOIN users ow ON ow.id = s.owner_id
                 WHERE s.status = 'ranked' AND NOT ow.restricted) AS pool
            """,
            new { id });

    /// <summary>Per-grade counts and accuracy sums over the user's per-map bests (the leaderboard fold, any board status).</summary>
    public static async Task<List<(string Rank, long Count, double AccuracySum)>> GradeRowsAsync(NpgsqlConnection conn, long id)
        => (await conn.QueryAsync<(string Rank, long Count, double AccuracySum)>(
            $"""
            SELECT best.rank AS Rank, count(*) AS Count, sum(best.accuracy) AS AccuracySum
            FROM (
                SELECT DISTINCT ON (sc.beatmap_id) sc.rank, sc.accuracy
                FROM scores sc
                WHERE sc.user_id = @id AND {BeatmapLeaderboard.OnBoard("sc", "true")}
                ORDER BY sc.beatmap_id, {BeatmapLeaderboard.Order("sc")}
            ) best
            GROUP BY best.rank
            """,
            new { id })).ToList();

    public static async Task<List<ScoreRowModel>> PinnedAsync(NpgsqlConnection conn, long id)
        => (await conn.QueryAsync<ScoreRowModel>(
            $"""
             {score_row_select}
             FROM ({ProfileScores.PinnedOfUser(section_columns)}) best
             JOIN beatmaps b ON b.id = best.beatmap_id
             JOIN beatmapsets s ON s.id = b.set_id
             WHERE {ProfileScores.OnVisibleSet("s")}
             ORDER BY {ProfileScores.PinnedOrder}
             LIMIT {ScorePins.MaxPins}
             """,
            new { id })).ToList();

    public static async Task<List<ScoreRowModel>> BestAsync(NpgsqlConnection conn, long id, int limit)
        => (await conn.QueryAsync<ScoreRowModel>(
            $"""
             {score_row_select}
             FROM ({ProfileScores.BestOfUser(section_columns)}) best
             JOIN beatmaps b ON b.id = best.beatmap_id
             JOIN beatmapsets s ON s.id = b.set_id
             WHERE {ProfileScores.OnVisibleSet("s")}
             ORDER BY {ProfileScores.BestOrder}
             LIMIT {limit}
             """,
            new { id })).ToList();

    /// <summary>scores.pp for the given score ids.</summary>
    public static async Task<Dictionary<long, double>> PpByScoreIdAsync(NpgsqlConnection conn, long[] bestScoreIds)
        => (await conn.QueryAsync<(long ScoreId, double Pp)>(
            "SELECT id AS ScoreId, pp AS Pp FROM scores WHERE id = ANY(@bestScoreIds)",
            new { bestScoreIds })).ToDictionary(r => r.ScoreId, r => r.Pp);

    public static Task<int> FirstPlaceCountAsync(NpgsqlConnection conn, long id)
        => conn.ExecuteScalarAsync<int>(
            $"SELECT count(*) FROM ({BeatmapLeaderboard.FirstPlacesOfUserSql}) fp",
            new { id });

    public static async Task<List<ScoreRowModel>> FirstPlacesAsync(NpgsqlConnection conn, long id, int limit)
        => (await conn.QueryAsync<ScoreRowModel>(
            $"""
             {score_row_select}
             FROM ({BeatmapLeaderboard.FirstPlacesOfUserSql}) best
             JOIN beatmaps b ON b.id = best.beatmap_id
             JOIN beatmapsets s ON s.id = b.set_id
             ORDER BY {ProfileScores.FirstPlacesOrder}
             LIMIT {limit}
             """,
            new { id })).ToList();

    public static async Task<List<ScoreRowModel>> RecentAsync(NpgsqlConnection conn, long id, int limit)
        => (await conn.QueryAsync<ScoreRowModel>(
            $"""
             {score_row_select}
             FROM ({ProfileScores.RecentOfUser(section_columns)}) best
             JOIN beatmaps b ON b.id = best.beatmap_id
             JOIN beatmapsets s ON s.id = b.set_id
             WHERE {ProfileScores.OnVisibleSet("s")}
             ORDER BY {ProfileScores.RecentOrder}
             LIMIT {limit}
             """,
            new { id })).ToList();

    /// <summary>The user's most-watched ranked scores (025_replay_views.sql), most views first; the partial index (user_id, replay_views DESC, id) answers it without a join.</summary>
    public static async Task<List<(long ScoreId, int Views)>> MostViewedRankingAsync(NpgsqlConnection conn, long id, int limit)
        => (await conn.QueryAsync<(long ScoreId, int Views)>(
            $"""
             SELECT id AS ScoreId, replay_views AS Views
             FROM scores
             WHERE user_id = @id AND replay_views > 0 AND ranked
             ORDER BY replay_views DESC, id ASC
             LIMIT {limit}
             """,
            new { id })).ToList();

    /// <summary>The given scores as score rows, on browsable sets only, in no particular order.</summary>
    public static Task<IEnumerable<ScoreRowModel>> ScoresByIdAsync(NpgsqlConnection conn, long[] ids)
        => conn.QueryAsync<ScoreRowModel>(
            $"""
             {score_row_select}
             FROM (
                 SELECT sc.id, sc.beatmap_id, sc.rank, sc.completion, sc.accuracy, sc.total_score, sc.ended_at,
                        sc.mods::text AS mods, sc.replay_key IS NOT NULL AS has_replay
                 FROM scores sc
                 WHERE sc.id = ANY(@ids)
             ) best
             JOIN beatmaps b ON b.id = best.beatmap_id
             JOIN beatmapsets s ON s.id = b.set_id
             WHERE s.status IN ('pending', 'unranked', 'ranked', 'loved')
             """,
            new { ids });

    public static async Task<List<ProfileModel.MostPlayedRow>> MostPlayedAsync(NpgsqlConnection conn, long id, int limit)
        => (await conn.QueryAsync<ProfileModel.MostPlayedRow>(
            $"""
             SELECT s.id     AS SetId,
                    s.title  AS Title,
                    s.artist AS Artist,
                    s.title_unicode  AS TitleUnicode,
                    s.artist_unicode AS ArtistUnicode,
                    s.explicit       AS Explicit,
                    CASE WHEN s.cover_key IS NOT NULL THEN '/' || s.cover_key || '/list.jpg' END AS CoverUrl,
                    mp.plays AS Plays
             FROM ({ProfileScores.MostPlayedOfUserSql}) mp
             JOIN beatmaps b ON b.id = mp.beatmap_id
             JOIN beatmapsets s ON s.id = b.set_id
             ORDER BY {ProfileScores.MostPlayedOrder}
             LIMIT {limit}
             """,
            new { id })).ToList();

    /// <summary>The user's own maps as cards; <paramref name="ownProfile"/> adds their hidden and removed sets (never an upload placeholder).</summary>
    public static async Task<List<BeatmapsetCardModel>> MapsAsync(NpgsqlConnection conn, long id, bool ownProfile, long viewerId, int limit)
        => (await conn.QueryAsync<BeatmapsetCardModel>(
            $"""
             {BeatmapsetCardSql.Select}
             WHERE s.owner_id = @id AND (s.status IN ('pending', 'unranked', 'ranked', 'loved') OR @ownProfile)
               AND (s.status <> 'hidden' OR EXISTS (SELECT 1 FROM beatmaps lb WHERE lb.set_id = s.id AND lb.filename IS NOT NULL))
             ORDER BY s.submitted_at DESC, s.id DESC
             LIMIT {limit}
             """,
            new { id, ownProfile, viewerId })).ToList();

    public static async Task<List<BeatmapsetCardModel>> FavouritesAsync(NpgsqlConnection conn, long id, long viewerId, int limit)
        => (await conn.QueryAsync<BeatmapsetCardModel>(
            $"""
             {BeatmapsetCardSql.Select}
             JOIN favourites fav ON fav.set_id = s.id AND fav.user_id = @id
             WHERE s.status IN ('pending', 'unranked', 'ranked', 'loved') AND (NOT u.restricted OR s.owner_id = @viewerId)
             ORDER BY fav.created_at DESC, s.id DESC
             LIMIT {limit}
             """,
            new { id, viewerId })).ToList();

    /// <summary>Ranked plays and published sets, the counts the sitemap's profile query reads (backlog 418).</summary>
    public static Task<(long, long)> IndexableCountsAsync(NpgsqlConnection conn, long id)
        => conn.QuerySingleAsync<(long, long)>(
            """
            SELECT (SELECT count(*) FROM scores WHERE user_id = @id AND ranked),
                   (SELECT count(*) FROM beatmapsets WHERE owner_id = @id AND status IN ('pending', 'unranked', 'ranked', 'loved'))
            """,
            new { id });

    /// <summary>
    /// Two statements rather than one with a nullable array parameter: Dapper hands an array
    /// straight to Npgsql as a text[] (that is why ANY(@ids) works above), but a NULL one
    /// arrives as an untyped DBNull, and "back to the default" is worth spelling out in SQL
    /// instead of relying on Postgres inferring the column's type for it.
    /// </summary>
    public static async Task SetProfileOrderAsync(NpgsqlConnection conn, long id, string[]? stored)
    {
        if (stored is null)
            await conn.ExecuteAsync("UPDATE users SET profile_order = NULL WHERE id = @id", new { id });
        else
            await conn.ExecuteAsync("UPDATE users SET profile_order = @stored WHERE id = @id", new { stored, id });
    }

    /// <summary>Restricted users are delisted site-wide, and their profile 404s, so they cannot be followed either.</summary>
    public static Task<bool> FollowableAsync(NpgsqlConnection conn, long targetId)
        => conn.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM users WHERE id = @targetId AND NOT restricted)",
            new { targetId });
}
