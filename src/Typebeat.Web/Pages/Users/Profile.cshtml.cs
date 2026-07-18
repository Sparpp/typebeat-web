using System.Globalization;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Typebeat.Web.Data;

namespace Typebeat.Web.Pages.Users;

/// <summary>
/// User profile (/users/{id} and /users/{name}): cover band (preset gradient keyed by user id),
/// avatar, joined/last-seen, the stats card (global rank by user_stats.total_score, totals grid,
/// grade counts), and stacked sections — Best scores / Recent scores / Most played / Maps /
/// Favourites (card partial reuse). A name URL canonical-redirects to the id URL; an all-digit
/// path segment always reads as an id (so a digits-only USERNAME is only reachable by id).
///
/// Grade counts follow osu semantics: each map contributes only the user's BEST ranked+passed
/// score (the same per-map-best fold the leaderboards use), not every play — so the row reads
/// as "maps you've SS'd", matching how the game presents grades on results/leaderboards.
/// Accuracy is the plain average over those same per-map bests (the game aggregates nothing
/// today — UserWire serves zeroed statistics — so this page defines the semantics).
/// </summary>
public sealed class ProfileModel(Db db) : TypebeatPageModel
{
    private const int score_section_size = 20;
    private const int most_played_size = 10;
    private const int card_section_size = 12;

    /// <summary>Distinct neon-karaoke cover-band presets (profile-cover--N in site.css).</summary>
    public const int CoverPresetCount = 4;

    public UserHeader ProfileUser { get; private set; } = null!;

    /// <summary>Dense rank among user_stats by total_score; null → unranked (no positive score).</summary>
    public long? GlobalRank { get; private set; }

    public long TotalScore { get; private set; }
    public int PlayCount { get; private set; }
    public long PlayTimeS { get; private set; }

    /// <summary>Average accuracy across per-map best scores; null when there are none.</summary>
    public double? Accuracy { get; private set; }

    public GradeCounts Grades { get; private set; } = new(0, 0, 0, 0, 0, 0);

    public IReadOnlyList<ScoreRowModel> BestScores { get; private set; } = [];
    public IReadOnlyList<ScoreRowModel> RecentScores { get; private set; } = [];
    public IReadOnlyList<MostPlayedRow> MostPlayed { get; private set; } = [];

    public IReadOnlyList<BeatmapsetCardModel> Maps { get; private set; } = [];
    public bool HasMoreMaps { get; private set; }

    public IReadOnlyList<BeatmapsetCardModel> Favourites { get; private set; } = [];
    public bool HasMoreFavourites { get; private set; }

    public bool IsOwnProfile => CurrentUser?.Id == ProfileUser.Id;

    public async Task<IActionResult> OnGetAsync(string idOrName)
    {
        await using var conn = await db.OpenAsync(HttpContext.RequestAborted);

        if (!long.TryParse(idOrName, NumberStyles.None, CultureInfo.InvariantCulture, out long id))
        {
            // Name lookup, then canonical-redirect to the id URL. The parameter must be cast:
            // a bare text param makes Postgres pick text = text (case-SENSITIVE); citext = citext
            // gives the case-insensitive match usernames deserve.
            long? resolved = await conn.ExecuteScalarAsync<long?>(
                "SELECT id FROM users WHERE username = @name::citext AND NOT restricted",
                new { name = idOrName });

            return resolved is long found ? Redirect($"/users/{found}") : NotFound();
        }

        var user = await conn.QuerySingleOrDefaultAsync<UserHeader>(
            """
            SELECT u.id          AS Id,
                   u.username::text AS Username,
                   u.avatar_key  AS AvatarKey,
                   u.cover_key   AS CoverKey,
                   u.description AS Description,
                   u.created_at  AS CreatedAt,
                   u.last_visit  AS LastVisit,
                   u.restricted  AS Restricted
            FROM users u
            WHERE u.id = @id
            """,
            new { id });

        // Restricted profiles are hidden like their scores are (mirrors the auth layer, where
        // restricted users read as signed out).
        if (user is null || user.Restricted)
            return NotFound();

        ProfileUser = user;

        long viewerId = CurrentUser?.Id ?? 0;

        // ---- stats card ----

        GlobalRank = await conn.ExecuteScalarAsync<long?>(
            """
            SELECT rnk
            FROM (
                SELECT user_id, dense_rank() OVER (ORDER BY total_score DESC) AS rnk
                FROM user_stats
                WHERE total_score > 0
            ) ranked
            WHERE ranked.user_id = @id
            """,
            new { id });

        // Value tuple defaults to all-zero when the user_stats row doesn't exist yet.
        (TotalScore, PlayCount, PlayTimeS) = await conn.QuerySingleOrDefaultAsync<(long, int, long)>(
            "SELECT total_score, play_count, play_time_s FROM user_stats WHERE user_id = @id",
            new { id });

        // Grade counts + accuracy from the SAME per-map-best fold (see class doc).
        var gradeRows = (await conn.QueryAsync<(string Rank, long Count, double AccuracySum)>(
            """
            SELECT best.rank AS Rank, count(*) AS Count, sum(best.accuracy) AS AccuracySum
            FROM (
                SELECT DISTINCT ON (sc.beatmap_id) sc.rank, sc.accuracy
                FROM scores sc
                WHERE sc.user_id = @id AND sc.ranked AND sc.passed
                ORDER BY sc.beatmap_id, sc.total_score DESC, sc.id ASC
            ) best
            GROUP BY best.rank
            """,
            new { id })).ToList();

        int ss = 0, s = 0, a = 0, b = 0, c = 0, d = 0;
        long bestCount = 0;
        double accuracySum = 0;

        foreach (var row in gradeRows)
        {
            bestCount += row.Count;
            accuracySum += row.AccuracySum;

            int n = (int)row.Count;
            switch (row.Rank)
            {
                case "X" or "XH": ss += n; break;
                case "S" or "SH": s += n; break;
                case "A": a += n; break;
                case "B": b += n; break;
                case "C": c += n; break;
                case "D": d += n; break;
            }
        }

        Grades = new GradeCounts(ss, s, a, b, c, d);
        Accuracy = bestCount > 0 ? accuracySum / bestCount : null;

        // ---- score sections (published sets only: hidden/removed titles must not leak here;
        //      pending sets are browsable, so their plays legitimately show) ----

        const string score_row_select =
            """
            SELECT s.id            AS SetId,
                   s.title         AS Title,
                   s.artist        AS Artist,
                   CASE WHEN s.cover_key IS NOT NULL THEN '/' || s.cover_key || '/list.jpg' END AS CoverUrl,
                   best.rank       AS Rank,
                   best.completion AS Completion,
                   best.accuracy   AS Accuracy,
                   best.total_score AS TotalScore,
                   best.ended_at   AS Date,
                   best.mods       AS ModsJson
            """;

        BestScores = (await conn.QueryAsync<ScoreRowModel>(
            $"""
             {score_row_select}
             FROM (
                 SELECT DISTINCT ON (sc.beatmap_id)
                        sc.id, sc.beatmap_id, sc.rank, sc.completion, sc.accuracy, sc.total_score, sc.ended_at,
                        sc.mods::text AS mods
                 FROM scores sc
                 WHERE sc.user_id = @id AND sc.ranked AND sc.passed
                 ORDER BY sc.beatmap_id, sc.total_score DESC, sc.id ASC
             ) best
             JOIN beatmaps b ON b.id = best.beatmap_id
             JOIN beatmapsets s ON s.id = b.set_id
             WHERE s.status IN ('pending', 'ranked')
             ORDER BY best.total_score DESC, best.id ASC
             LIMIT {score_section_size}
             """,
            new { id })).ToList();

        // Recent plays include fails (rank F renders like the game), still ranked-only so
        // admin-unranked scores never resurface.
        RecentScores = (await conn.QueryAsync<ScoreRowModel>(
            $"""
             {score_row_select}
             FROM (
                 SELECT sc.id, sc.beatmap_id, sc.rank, sc.completion, sc.accuracy, sc.total_score, sc.ended_at,
                        sc.mods::text AS mods
                 FROM scores sc
                 WHERE sc.user_id = @id AND sc.ranked
             ) best
             JOIN beatmaps b ON b.id = best.beatmap_id
             JOIN beatmapsets s ON s.id = b.set_id
             WHERE s.status IN ('pending', 'ranked')
             ORDER BY best.ended_at DESC, best.id DESC
             LIMIT {score_section_size}
             """,
            new { id })).ToList();

        MostPlayed = (await conn.QueryAsync<MostPlayedRow>(
            $"""
             SELECT s.id     AS SetId,
                    s.title  AS Title,
                    s.artist AS Artist,
                    CASE WHEN s.cover_key IS NOT NULL THEN '/' || s.cover_key || '/list.jpg' END AS CoverUrl,
                    count(*) AS Plays
             FROM scores sc
             JOIN beatmaps b ON b.id = sc.beatmap_id
             JOIN beatmapsets s ON s.id = b.set_id
             WHERE sc.user_id = @id AND s.status IN ('pending', 'ranked')
             GROUP BY sc.beatmap_id, s.id, s.title, s.artist, s.cover_key
             ORDER BY count(*) DESC, s.id ASC
             LIMIT {most_played_size}
             """,
            new { id })).ToList();

        // ---- card sections ----

        // Owned maps: everyone sees published sets; the owner also sees their hidden and
        // removed sets (the card's status pill explains itself).
        bool ownProfile = viewerId == id;

        var maps = (await conn.QueryAsync<BeatmapsetCardModel>(
            $"""
             {BeatmapsetCardSql.Select}
             WHERE s.owner_id = @id AND (s.status IN ('pending', 'ranked') OR @ownProfile)
             ORDER BY s.submitted_at DESC, s.id DESC
             LIMIT {card_section_size + 1}
             """,
            new { id, ownProfile, viewerId })).ToList();

        HasMoreMaps = maps.Count > card_section_size;
        if (HasMoreMaps)
            maps.RemoveAt(card_section_size);
        Maps = maps;

        // Restricted mappers' sets are delisted site-wide; they drop out of favourite walls too
        // (their "mapped by" link would 404 for every viewer).
        var favourites = (await conn.QueryAsync<BeatmapsetCardModel>(
            $"""
             {BeatmapsetCardSql.Select}
             JOIN favourites fav ON fav.set_id = s.id AND fav.user_id = @id
             WHERE s.status IN ('pending', 'ranked') AND (NOT u.restricted OR s.owner_id = @viewerId)
             ORDER BY fav.created_at DESC, s.id DESC
             LIMIT {card_section_size + 1}
             """,
            new { id, viewerId })).ToList();

        HasMoreFavourites = favourites.Count > card_section_size;
        if (HasMoreFavourites)
            favourites.RemoveAt(card_section_size);
        Favourites = favourites;

        ViewData["Title"] = ProfileUser.Username;
        ViewData["MetaDescription"] = $"{ProfileUser.Username}'s type!beat profile — scores, maps and favourites.";

        return Page();
    }

    // ---- display helpers ----

    /// <summary>"today" / "yesterday" / "n days ago" style; null → "never" (fresh account).</summary>
    public static string LastSeenLabel(DateTime? lastVisit)
    {
        if (lastVisit is not DateTime seen)
            return "never";

        var elapsed = DateTime.UtcNow - seen;

        if (elapsed < TimeSpan.FromHours(24))
            return "today";
        if (elapsed < TimeSpan.FromHours(48))
            return "yesterday";
        if (elapsed < TimeSpan.FromDays(60))
            return $"{(int)elapsed.TotalDays} days ago";
        if (elapsed < TimeSpan.FromDays(365))
            return $"{(int)(elapsed.TotalDays / 30)} months ago";

        int years = (int)(elapsed.TotalDays / 365);
        return years == 1 ? "a year ago" : $"{years} years ago";
    }

    /// <summary>Humanized play time: "3d 4h" / "2h 15m" / "42m".</summary>
    public static string FormatPlayTime(long seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));

        if (t.TotalDays >= 1)
            return $"{(int)t.TotalDays}d {t.Hours}h";
        if (t.TotalHours >= 1)
            return $"{(int)t.TotalHours}h {t.Minutes}m";
        return $"{t.Minutes}m";
    }

    public sealed record UserHeader(
        long Id, string Username, string? AvatarKey, string? CoverKey, string Description,
        DateTime CreatedAt, DateTime? LastVisit, bool Restricted)
    {
        /// <summary>Uploaded avatar (settings page), or null → initial-letter fallback.</summary>
        public string? AvatarUrl => AvatarKey is null ? null : $"/{AvatarKey}";

        /// <summary>Uploaded banner (settings page), or null → the preset gradient cover band.</summary>
        public string? CoverUrl => CoverKey is null ? null : $"/{CoverKey}";
    }

    public sealed record GradeCounts(int Ss, int S, int A, int B, int C, int D);

    public sealed record MostPlayedRow(long SetId, string Title, string Artist, string? CoverUrl, long Plays);
}
