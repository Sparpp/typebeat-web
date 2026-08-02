using System.Globalization;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Typebeat.Web.Data;
using Typebeat.Web.Scoring;
using Typebeat.Web.Social;

namespace Typebeat.Web.Pages.Users;

/// <summary>
/// User profile (/users/{id} and /users/{name}): cover band (preset gradient keyed by user id),
/// avatar, joined/last-seen, the stats card (global rank by user_stats.total_score, totals grid,
/// grade counts), and stacked sections: Pinned / Best scores / Recent scores / Most played /
/// Play history / Maps / Favourites (card partial reuse). A name URL canonical-redirects to the id
/// URL; an all-digit path segment always reads as an id (so a digits-only USERNAME is only
/// reachable by id).
///
/// Pinned scores (task 63) are this page's own feature end to end: the section query below, and
/// the Pin/Unpin POST handlers the owner-only control on each score row submits to. All of the
/// rules (yours, ranked, at most <see cref="ScorePins.MaxPins"/>) are enforced in
/// <see cref="ScorePins"/>, server-side; the control's visibility is only cosmetic.
///
/// Grade counts follow osu semantics: each map contributes only the user's BEST ranked+passed
/// score (the same per-map-best fold the leaderboards use), not every play, so the row reads
/// as "maps you've SS'd", matching how the game presents grades on results/leaderboards.
/// Accuracy is the plain average over those same per-map bests (the game aggregates nothing
/// today, UserWire serves zeroed statistics, so this page defines the semantics).
/// </summary>
public sealed class ProfileModel(Db db) : TypebeatPageModel
{
    private const int score_section_size = 20;
    private const int most_played_size = 10;
    private const int card_section_size = 12;

    /// <summary>Distinct neon-karaoke cover-band presets (profile-cover--N in site.css).</summary>
    public const int CoverPresetCount = 4;

    public UserHeader ProfileUser { get; private set; } = null!;

    /// <summary>Global rank by cumulative ranked score (<see cref="GlobalRanking"/>); null → unranked.</summary>
    public long? GlobalRank { get; private set; }

    /// <summary>Sum of best score per ranked map; the metric global rank is drawn from.</summary>
    public long RankedScore { get; private set; }

    public long TotalScore { get; private set; }
    public int PlayCount { get; private set; }
    public long PlayTimeS { get; private set; }

    /// <summary>Distinct ranked beatmapsets the user has submitted any score on.</summary>
    public int MapsPlayed { get; private set; }

    /// <summary>Total ranked beatmapsets in the (publicly visible) pool.</summary>
    public int RankedMapPool { get; private set; }

    /// <summary>Average accuracy across per-map best scores; null when there are none.</summary>
    public double? Accuracy { get; private set; }

    public GradeCounts Grades { get; private set; } = new(0, 0, 0, 0, 0, 0);

    /// <summary>
    /// Scores this user pinned, newest pin first, shown above every other section. Empty for a
    /// user with no pins (the view then renders no section at all), and empty for everyone while
    /// the pinned scores are all currently invisible (see the section query).
    /// </summary>
    public IReadOnlyList<ScoreRowModel> PinnedScores { get; private set; } = [];

    /// <summary>Set from the ?pin= redirect after a refused pin (PRG); shown above the section.</summary>
    public string? PinNotice { get; private set; }

    public IReadOnlyList<ScoreRowModel> BestScores { get; private set; } = [];
    public IReadOnlyList<ScoreRowModel> RecentScores { get; private set; } = [];
    public IReadOnlyList<MostPlayedRow> MostPlayed { get; private set; } = [];

    /// <summary>
    /// Plays per month for the Play History chart (<see cref="PlayHistory"/>), or null when the
    /// user has no recorded month at all, which hides the whole section. Null rather than an empty
    /// model so the view has one thing to test.
    /// </summary>
    public BarChartModel? PlayHistoryChart { get; private set; }

    public IReadOnlyList<BeatmapsetCardModel> Maps { get; private set; } = [];
    public bool HasMoreMaps { get; private set; }

    public IReadOnlyList<BeatmapsetCardModel> Favourites { get; private set; } = [];
    public bool HasMoreFavourites { get; private set; }

    public bool IsOwnProfile => CurrentUser?.Id == ProfileUser.Id;

    /// <summary>
    /// Follower/following counts (header links) plus whether the viewer already follows this user
    /// and/or watches them as a mapper (which way the Follow button and the bell are flipped).
    /// </summary>
    public ProfileFollowState FollowState { get; private set; } = ProfileFollowState.Empty;

    public async Task<IActionResult> OnGetAsync(string idOrName, string? pin = null)
    {
        // Refused pins come back here by redirect (PRG), same shape as /settings?saved=.
        PinNotice = pin switch
        {
            "limit" => $"You can pin up to {ScorePins.MaxPins} scores. Unpin one to make room.",
            "unranked" => "That score is not ranked, so it cannot be pinned.",
            _ => null,
        };

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

        // ---- header: follow / watch state ----

        FollowState = await Follows.ProfileStateAsync(conn, id, viewerId, HttpContext.RequestAborted);

        // ---- stats card ----

        var ranking = await GlobalRanking.ForUserAsync(conn, id, HttpContext.RequestAborted);
        GlobalRank = ranking.GlobalRank;
        RankedScore = ranking.RankedScore;

        // Value tuple defaults to all-zero when the user_stats row doesn't exist yet.
        (TotalScore, PlayCount, PlayTimeS) = await conn.QuerySingleOrDefaultAsync<(long, int, long)>(
            "SELECT total_score, play_count, play_time_s FROM user_stats WHERE user_id = @id",
            new { id });

        // Maps played out of the ranked pool: distinct ranked sets with any score by this user,
        // over the count of publicly-visible ranked sets (restricted mappers' sets are delisted).
        (MapsPlayed, RankedMapPool) = await conn.QuerySingleAsync<(int, int)>(
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

        // Pinned: the user's own curation, newest pin first, capped by the pin cap itself.
        // Gated by exactly the filters the other score sections use (ranked scores on browsable
        // sets), so a score that is deleted, unranked by an admin, or on a set that gets hidden
        // simply stops appearing here, like it stops appearing under Best/Recent. Its pin row
        // survives (and still counts against the cap) unless the SCORE row goes, which cascades.
        PinnedScores = (await conn.QueryAsync<ScoreRowModel>(
            $"""
             {score_row_select}
             FROM (
                 SELECT sc.id, sc.beatmap_id, sc.rank, sc.completion, sc.accuracy, sc.total_score, sc.ended_at,
                        sc.mods::text AS mods, sc.replay_key IS NOT NULL AS has_replay,
                        p.pinned_at
                 FROM score_pins p
                 JOIN scores sc ON sc.id = p.score_id
                 WHERE p.user_id = @id AND sc.ranked
             ) best
             JOIN beatmaps b ON b.id = best.beatmap_id
             JOIN beatmapsets s ON s.id = b.set_id
             WHERE s.status IN ('pending', 'unranked', 'ranked')
             ORDER BY best.pinned_at DESC, best.id DESC
             LIMIT {ScorePins.MaxPins}
             """,
            new { id })).ToList();

        BestScores = (await conn.QueryAsync<ScoreRowModel>(
            $"""
             {score_row_select}
             FROM (
                 SELECT DISTINCT ON (sc.beatmap_id)
                        sc.id, sc.beatmap_id, sc.rank, sc.completion, sc.accuracy, sc.total_score, sc.ended_at,
                        sc.mods::text AS mods, sc.replay_key IS NOT NULL AS has_replay
                 FROM scores sc
                 WHERE sc.user_id = @id AND sc.ranked AND sc.passed
                 ORDER BY sc.beatmap_id, sc.total_score DESC, sc.id ASC
             ) best
             JOIN beatmaps b ON b.id = best.beatmap_id
             JOIN beatmapsets s ON s.id = b.set_id
             WHERE s.status IN ('pending', 'unranked', 'ranked')
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
                        sc.mods::text AS mods, sc.replay_key IS NOT NULL AS has_replay
                 FROM scores sc
                 WHERE sc.user_id = @id AND sc.ranked
             ) best
             JOIN beatmaps b ON b.id = best.beatmap_id
             JOIN beatmapsets s ON s.id = b.set_id
             WHERE s.status IN ('pending', 'unranked', 'ranked')
             ORDER BY best.ended_at DESC, best.id DESC
             LIMIT {score_section_size}
             """,
            new { id })).ToList();

        // Pin controls, on your own profile only: every score row this page renders is yours and
        // ranked (all three queries filter sc.ranked), so each one is pinnable, and the control
        // reads "unpin" for the ones already pinned.
        if (viewerId != 0 && viewerId == id)
        {
            var pinnedIds = await ScorePins.PinnedScoreIdsAsync(conn, id);

            foreach (var row in PinnedScores.Concat(BestScores).Concat(RecentScores))
            {
                row.ShowPinControl = true;
                row.IsPinned = pinnedIds.Contains(row.ScoreId);
            }
        }

        MostPlayed = (await conn.QueryAsync<MostPlayedRow>(
            $"""
             SELECT s.id     AS SetId,
                    s.title  AS Title,
                    s.artist AS Artist,
                    s.title_unicode  AS TitleUnicode,
                    s.artist_unicode AS ArtistUnicode,
                    s.explicit       AS Explicit,
                    CASE WHEN s.cover_key IS NOT NULL THEN '/' || s.cover_key || '/list.jpg' END AS CoverUrl,
                    count(*) AS Plays
             FROM scores sc
             JOIN beatmaps b ON b.id = sc.beatmap_id
             JOIN beatmapsets s ON s.id = b.set_id
             WHERE sc.user_id = @id AND s.status IN ('pending', 'unranked', 'ranked')
             GROUP BY sc.beatmap_id, s.id, s.title, s.artist, s.title_unicode, s.artist_unicode, s.explicit, s.cover_key
             ORDER BY count(*) DESC, s.id ASC
             LIMIT {most_played_size}
             """,
            new { id })).ToList();

        // Play history: the monthly rollup, gap-filled into a continuous axis (024_play_history.sql).
        // A user with nothing recorded gets no chart at all, and the view renders no section.
        var playMonths = await PlayHistory.ForUserAsync(conn, id, ct: HttpContext.RequestAborted);
        PlayHistoryChart = playMonths.Count > 0 ? PlayHistory.Chart(playMonths) : null;

        // ---- card sections ----

        // Owned maps: everyone sees published sets; the owner also sees their hidden and
        // removed sets (the card's status pill explains itself).
        bool ownProfile = viewerId == id;

        var maps = (await conn.QueryAsync<BeatmapsetCardModel>(
            $"""
             {BeatmapsetCardSql.Select}
             WHERE s.owner_id = @id AND (s.status IN ('pending', 'unranked', 'ranked') OR @ownProfile)
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
             WHERE s.status IN ('pending', 'unranked', 'ranked') AND (NOT u.restricted OR s.owner_id = @viewerId)
             ORDER BY fav.created_at DESC, s.id DESC
             LIMIT {card_section_size + 1}
             """,
            new { id, viewerId })).ToList();

        HasMoreFavourites = favourites.Count > card_section_size;
        if (HasMoreFavourites)
            favourites.RemoveAt(card_section_size);
        Favourites = favourites;

        ViewData["Title"] = ProfileUser.Username;
        ViewData["MetaDescription"] = $"{ProfileUser.Username}'s type!beat profile: scores, maps and favourites.";

        return Page();
    }

    /// <summary>
    /// Pin one of your own scores (the control on each score row). The {idOrName} in the URL is
    /// whichever profile the form was rendered on and is deliberately IGNORED: the pin belongs to
    /// the session user, so the answer is always their own profile. Anonymous → /login, like the
    /// favourite toggle. Refusals come back as a ?pin= notice, except "not yours", which is a 404
    /// because only a forged POST can produce it.
    /// </summary>
    public async Task<IActionResult> OnPostPinAsync(long scoreId)
    {
        if (CurrentUser is null)
            return Redirect("/login");

        await using var conn = await db.OpenAsync(HttpContext.RequestAborted);

        var result = await ScorePins.PinAsync(conn, CurrentUser.Id, scoreId, HttpContext.RequestAborted);

        return result switch
        {
            PinResult.NotYours => NotFound(),
            PinResult.NotRanked => Redirect($"/users/{CurrentUser.Id}?pin=unranked#pinned"),
            PinResult.LimitReached => Redirect($"/users/{CurrentUser.Id}?pin=limit#pinned"),
            _ => Redirect($"/users/{CurrentUser.Id}#pinned"),
        };
    }

    /// <summary>Unpin one of your own scores; a no-op if it was not pinned.</summary>
    public async Task<IActionResult> OnPostUnpinAsync(long scoreId)
    {
        if (CurrentUser is null)
            return Redirect("/login");

        await using var conn = await db.OpenAsync(HttpContext.RequestAborted);
        await ScorePins.UnpinAsync(conn, CurrentUser.Id, scoreId);

        return Redirect($"/users/{CurrentUser.Id}#pinned");
    }

    // ---- follow / watch toggles ----

    /// <summary>Follow button: toggles the 'user' edge from viewer to this profile.</summary>
    public Task<IActionResult> OnPostFollowAsync(string idOrName, string? returnUrl)
        => toggleFollowAsync(idOrName, Follows.UserKind, returnUrl);

    /// <summary>
    /// Bell: toggles the 'mapper' edge, which puts this user's future uploads in the viewer's
    /// /watching feed. Deliberately allowed on any profile, including one with no maps yet
    /// (023_follows.sql explains why), so nothing here checks for beatmapsets.
    /// </summary>
    public Task<IActionResult> OnPostWatchAsync(string idOrName, string? returnUrl)
        => toggleFollowAsync(idOrName, Follows.MapperKind, returnUrl);

    /// <summary>
    /// Shared body of the two toggles. Mirrors the favourite button on the set page: signed-out
    /// posts bounce to /login, a fetch()-driven submit gets JSON back so the header updates in
    /// place, and a plain form submit redirects for the no-JS path.
    ///
    /// The route parameter is the page's <c>{idOrName}</c>, but only the numeric id form is
    /// accepted here: the button is always rendered with the resolved id, so a name in this slot
    /// is not a real user action, and quietly resolving it would give the write path a second
    /// identity lookup to keep in sync with the GET's.
    /// </summary>
    private async Task<IActionResult> toggleFollowAsync(string idOrName, string kind, string? returnUrl)
    {
        if (CurrentUser is null)
            return Redirect("/login");

        if (!long.TryParse(idOrName, NumberStyles.None, CultureInfo.InvariantCulture, out long targetId))
            return NotFound();

        // Self-follow is rejected before the write (the table's CHECK would otherwise turn a
        // hand-crafted post into a 500). Nothing renders these buttons on your own profile.
        if (targetId == CurrentUser.Id)
            return BadRequest();

        await using var conn = await db.OpenAsync(HttpContext.RequestAborted);

        // Restricted users are delisted site-wide, and their profile 404s, so they cannot be
        // followed either.
        bool followable = await conn.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM users WHERE id = @targetId AND NOT restricted)",
            new { targetId });

        if (!followable)
            return NotFound();

        bool on = await Follows.ToggleAsync(conn, CurrentUser.Id, targetId, kind, HttpContext.RequestAborted);

        if (string.Equals(Request.Headers["X-Requested-With"], "fetch", StringComparison.Ordinal))
        {
            var state = await Follows.ProfileStateAsync(conn, targetId, CurrentUser.Id, HttpContext.RequestAborted);
            return new JsonResult(new { on, kind, followers = state.Followers, following = state.Following });
        }

        return Redirect(Url.IsLocalUrl(returnUrl) ? returnUrl : $"/users/{targetId}");
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

    public sealed record MostPlayedRow(long SetId, string Title, string Artist, string? TitleUnicode, string? ArtistUnicode, bool Explicit, string? CoverUrl, long Plays)
    {
        /// <summary>Title, or its original non-romanized text when the viewer prefers that.</summary>
        public string DisplayTitle(bool preferOriginal) => MetadataDisplay.Pick(Title, TitleUnicode, preferOriginal);

        /// <summary>Artist, or its original non-romanized text when the viewer prefers that.</summary>
        public string DisplayArtist(bool preferOriginal) => MetadataDisplay.Pick(Artist, ArtistUnicode, preferOriginal);
    }
}
