using Dapper;
using Npgsql;
using Typebeat.Web.Scoring;

namespace Typebeat.Web.Pages.Beatmapsets;

/// <summary>
/// The set page's SQL (<see cref="SetModel"/>): its reads and the plain writes behind the favourite,
/// report and description handlers. The comments, the favourite toggle and the reviewer levers keep
/// their own owners (<see cref="Social.BeatmapsetComments"/>, <see cref="Social.Favourites"/>,
/// <see cref="Social.BeatmapsetReview"/>).
/// </summary>
public static class SetQueries
{
    /// <summary>The set's header and stats-box row, with whether <paramref name="viewerId"/> favourited it; null when there is no such set.</summary>
    public static Task<SetModel.SetDetails?> DetailsAsync(NpgsqlConnection conn, long id, long viewerId)
        => conn.QuerySingleOrDefaultAsync<SetModel.SetDetails>(
            """
            SELECT s.id               AS Id,
                   s.title            AS Title,
                   s.artist           AS Artist,
                   s.title_unicode    AS TitleUnicode,
                   s.artist_unicode   AS ArtistUnicode,
                   s.source           AS Source,
                   s.tags             AS Tags,
                   s.description      AS Description,
                   s.status           AS Status,
                   s.explicit         AS Explicit,
                   u.username::text   AS Creator,
                   u.restricted       AS OwnerRestricted,
                   s.owner_id         AS OwnerId,
                   s.cover_key        AS CoverKey,
                   CASE WHEN s.preview_key IS NOT NULL THEN '/' || s.preview_key END AS PreviewUrl,
                   s.play_count       AS PlayCount,
                   s.favourite_count  AS FavouriteCount,
                   s.download_count   AS DownloadCount,
                   -- No s.bpm: the stats box's BPM row became the peak-WPM row (028_wpm_curve.sql),
                   -- and nothing else on this page ever read it. The column is still on the set and
                   -- still drives the bpm: search operator; it is only unread HERE.
                   s.submitted_at     AS SubmittedAt,
                   s.updated_at       AS UpdatedAt,
                   EXISTS (SELECT 1 FROM favourites f WHERE f.set_id = s.id AND f.user_id = @viewerId) AS IsFavourited,
                   -- Pre-M3 sets (backfilled by migration 004) have live diffs but no uploaded
                   -- package: the Download button must not render a dead /download link for them.
                   EXISTS (SELECT 1 FROM set_versions v WHERE v.set_id = s.id AND v.package_key IS NOT NULL) AS HasPackage,
                   -- Appended on purpose: Dapper binds a positional record's constructor parameters
                   -- to the reader's columns BY POSITION, so a new column has to be appended at the
                   -- same index in both this SELECT and SetDetails.
                   s.language         AS Language,
                   -- Backlog 418: a live .osu difficulty, the card's HasPlayableDiff test (see
                   -- SetDetails.CanWebplay).
                   EXISTS (SELECT 1 FROM beatmaps b2 WHERE b2.set_id = s.id AND b2.filename LIKE '%.osu') AS HasPlayableDiff
            FROM beatmapsets s
            JOIN users u ON u.id = s.owner_id
            WHERE s.id = @id
            """,
            new { id, viewerId });

    /// <summary>Every live difficulty in the set, hardest first.</summary>
    public static async Task<List<SetModel.DiffStats>> DiffsAsync(NpgsqlConnection conn, long id)
        => (await conn.QueryAsync<SetModel.DiffStats>(
            """
            SELECT b.id                    AS Id,
                   b.version_name          AS Name,
                   b.total_length_s        AS TotalLengthS,
                   b.word_count            AS WordCount,
                   b.char_count            AS CharCount,
                   b.wpm::double precision AS Wpm,
                   b.difficulty_rating     AS Stars,
                   b.lyrics                AS Lyrics,
                   -- 028_wpm_curve.sql. NULL on any row the pace backfill has not reached, and on
                   -- any map too short to measure; DiffStats.HasCurve folds the two together.
                   b.peak_wpm              AS PeakWpm,
                   b.peak_cpm              AS PeakCpm,
                   -- 033_target_wpm.sql; NULL only until the v18 sweep fills it, since a per-line
                   -- figure needs no rolling window the way the two above do.
                   b.target_wpm            AS TargetWpm,
                   b.wpm_curve             AS WpmCurve,
                   -- 037_lyric_font.sql. NULL means "no font chosen", which hides the row; the
                   -- name is informational (only the desktop client renders the font).
                   b.lyric_font            AS LyricFont,
                   -- 039_lyrics_original.sql: the same lyrics in their original script, line for
                   -- line, or '' when the difficulty carries none (backlog 332).
                   b.lyrics_original       AS LyricsOriginal
            FROM beatmaps b
            WHERE b.set_id = @id AND b.filename IS NOT NULL
            ORDER BY b.difficulty_rating DESC, b.id ASC
            """,
            new { id })).ToList();

    /// <summary>
    /// Per-difficulty leaderboard: best passed score per user on <paramref name="beatmapId"/>, with
    /// the same eligibility and ordering the game-facing endpoint in ScoreEndpoints reads, both built
    /// from <see cref="BeatmapLeaderboard"/> so they cannot drift. <paramref name="wantRanked"/>
    /// picks the ranked board or the Unranked one.
    /// </summary>
    public static async Task<List<SetModel.ScoreRow>> BoardAsync(NpgsqlConnection conn, long beatmapId, bool wantRanked)
        => (await conn.QueryAsync<SetModel.ScoreRow>(
            $"""
            SELECT best.id           AS ScoreId,
                   best.user_id      AS UserId,
                   best.username     AS Username,
                   best.avatar_key   AS AvatarKey,
                   best.total_score  AS TotalScore,
                   best.accuracy     AS Accuracy,
                   best.completion   AS Completion,
                   best.max_combo    AS MaxCombo,
                   best.rank         AS Rank,
                   best.ended_at     AS EndedAt,
                   best.mods         AS ModsJson,
                   best.statistics   AS StatisticsJson,
                   best.has_replay   AS HasReplay,
                   best.country_code AS CountryCode
            FROM (
                SELECT DISTINCT ON (sc.user_id)
                       sc.id, sc.user_id, sc.total_score, sc.accuracy, sc.completion, sc.max_combo, sc.rank,
                       sc.ended_at, sc.mods::text AS mods, sc.statistics::text AS statistics,
                       sc.replay_key IS NOT NULL AS has_replay,
                       u.username::text AS username, u.avatar_key, u.country_code::text AS country_code
                FROM scores sc
                JOIN users u ON u.id = sc.user_id
                WHERE sc.beatmap_id = @beatmapId AND {BeatmapLeaderboard.OnBoard("sc")}
                ORDER BY sc.user_id, {BeatmapLeaderboard.Order("sc")}
            ) best
            ORDER BY {BeatmapLeaderboard.Order("best")}
            LIMIT 50
            """,
            new { beatmapId, wantRanked })).ToList();

    /// <summary>The set's status, or null when there is no such set.</summary>
    public static Task<string?> StatusAsync(NpgsqlConnection conn, long id)
        => conn.ExecuteScalarAsync<string?>(
            "SELECT status FROM beatmapsets WHERE id = @id", new { id });

    public static Task<int> FavouriteCountAsync(NpgsqlConnection conn, long id)
        => conn.ExecuteScalarAsync<int>(
            "SELECT favourite_count FROM beatmapsets WHERE id = @id", new { id });

    /// <summary>Whether the set exists and is not removed, the only sets a report may name.</summary>
    public static Task<bool> ReportableAsync(NpgsqlConnection conn, long id)
        => conn.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM beatmapsets WHERE id = @id AND status <> 'removed')", new { id });

    /// <summary>reporter_id is nullable by design (001_init.sql): anonymous reports are accepted.</summary>
    public static Task InsertReportAsync(NpgsqlConnection conn, long id, long? reporterId, string reason)
        => conn.ExecuteAsync(
            "INSERT INTO reports (set_id, reporter_id, reason) VALUES (@id, @reporterId, @reason)",
            new { id, reporterId, reason });

    /// <summary>
    /// Saves the set's plain-text description. Permission IS the WHERE clause (owner or map
    /// reviewer): an unpermitted or nonexistent target matches no row, and the caller answers
    /// NotFound. Returns the rows changed.
    /// </summary>
    public static Task<int> UpdateDescriptionAsync(NpgsqlConnection conn, long id, string description, long uid, bool canReview)
        => conn.ExecuteAsync(
            """
            UPDATE beatmapsets
            SET description = @description
            WHERE id = @id AND (owner_id = @uid OR @canReview)
            """,
            new { id, description, uid, canReview });
}
