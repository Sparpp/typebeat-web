using System.Globalization;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using Typebeat.Web.Caching;
using Typebeat.Web.Data;
using Typebeat.Web.Scoring;
using Typebeat.Web.Social;

namespace Typebeat.Web.Pages.Users;

/// <summary>
/// User profile (/users/{id} and /users/{name}): cover band (preset gradient keyed by user id),
/// avatar, joined/last-seen, the stats card (BOTH global ranks, pp as the headline and cumulative
/// score as a labelled second, each beside the metric it is drawn from; totals grid;
/// grade counts), and stacked sections: Pinned / Best scores / First places / Recent scores /
/// Most played / Most viewed replays / Replay views / Play history / Maps / Favourites (card
/// partial reuse). A name URL canonical-redirects to the id URL; an all-digit path segment always
/// reads as an id (so a digits-only USERNAME is only reachable by id).
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
///
/// <para>The anonymous GET is output-cached for 30 s (backlog 366) and tagged user:{id} once the
/// id is resolved, so a score by this player evicts it at once; the follow, pin and reorder forms
/// render only for a signed-in viewer, whom the cache never serves.</para>
/// </summary>
[OutputCache(PolicyName = CachePolicies.Profile)]
public sealed class ProfileModel(Db db) : TypebeatPageModel
{
    private const int score_section_size = 20;
    private const int most_played_size = 10;
    private const int most_viewed_size = 10;
    private const int card_section_size = 12;

    /// <summary>Distinct neon-karaoke cover-band presets (profile-cover--N in site.css).</summary>
    public const int CoverPresetCount = 4;

    public UserHeader ProfileUser { get; private set; } = null!;

    /// <summary>
    /// Rank on the SCORE board, by cumulative ranked score (<see cref="GlobalRanking"/>); null →
    /// unranked. No longer the card's headline: since task 61 the site's main board is pp, so this
    /// is the second rank, and it is labelled as such (see <see cref="PpRank"/>).
    /// </summary>
    public long? GlobalRank { get; private set; }

    /// <summary>Sum of best score per ranked map; the metric global rank is drawn from.</summary>
    public long RankedScore { get; private set; }

    /// <summary>
    /// Rank on the PERFORMANCE board, by total pp (<see cref="PpRanking"/>), the card's headline
    /// rank because it is the site's main ranking (/rankings leads with it). null → unranked,
    /// exactly the shape <see cref="GlobalRank"/> uses, and rendered with the same "Unranked" word.
    /// </summary>
    public long? PpRank { get; private set; }

    /// <summary>
    /// Total pp (docs/pp.md), the metric <see cref="PpRank"/> is drawn from, shown beside it. 0 for
    /// a user with no pp-earning play, which is also the user who is unranked on that board.
    /// </summary>
    public double TotalPp { get; private set; }

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

    /// <summary>
    /// The user's best plays BY PP (task 77): their best-pp play on each map, pp descending, capped
    /// at <see cref="score_section_size"/>. The only section that fills
    /// <see cref="ScoreRowModel.Pp"/>, and the only one whose row order is that number. It is the
    /// profile's local view of the /rankings performance board, so the two agree on which play of
    /// yours is your best; the cumulative-score board's answer to that question is no longer what
    /// this section shows.
    /// </summary>
    public IReadOnlyList<ScoreRowModel> BestScores { get; private set; } = [];

    /// <summary>
    /// Scores where this user CURRENTLY holds #1 on a ranked map's leaderboard (see
    /// <see cref="BeatmapLeaderboard.FirstPlacesOfUserSql"/>), newest score first, capped at
    /// <see cref="score_section_size"/> rows; <see cref="FirstPlaceCount"/> is the uncapped total.
    /// </summary>
    public IReadOnlyList<ScoreRowModel> FirstPlaces { get; private set; } = [];

    /// <summary>
    /// How many first places the user holds in total (the stats card's number, and what the
    /// section's "showing N of M" note counts against). Recomputed on every view, so losing a #1
    /// to somebody else's better score drops it here the moment that score lands.
    /// </summary>
    public int FirstPlaceCount { get; private set; }

    public IReadOnlyList<ScoreRowModel> RecentScores { get; private set; } = [];
    public IReadOnlyList<MostPlayedRow> MostPlayed { get; private set; } = [];

    /// <summary>
    /// Times other players have watched this user's replays, all time (<see cref="ReplayViews"/>);
    /// the stats card's "Replays watched by others". Zero for everyone whose replays nobody has
    /// watched, and for everyone at all until views start landing after 025 deploys.
    /// </summary>
    public long ReplayViewCount { get; private set; }

    /// <summary>
    /// The user's most-watched scores, most views first, capped at <see cref="most_viewed_size"/>.
    /// Empty when nothing of theirs has been watched, which hides the whole section.
    /// </summary>
    public IReadOnlyList<ScoreRowModel> MostViewedReplays { get; private set; } = [];

    /// <summary>
    /// Views per month for the Replay views chart (<see cref="ReplayViews"/>), or null when no
    /// month has any, which hides that section. Null rather than an empty model, like
    /// <see cref="PlayHistoryChart"/>, so the view has one thing to test.
    /// </summary>
    public BarChartModel? ReplayViewsChart { get; private set; }

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
    /// The order the sections render in: THIS profile's owner's stored order (026_profile_order.sql)
    /// resolved against the known section set, or the default for the (vast majority of) users who
    /// have never reordered. It is the owner's layout for every visitor, not the viewer's: whose
    /// profile you are looking at decides, exactly like osu-web's profile_order.
    /// </summary>
    public IReadOnlyList<string> SectionOrder { get; private set; } = ProfileSections.Default;

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
                   u.restricted  AS Restricted,
                   u.country_code::text AS CountryCode
            FROM users u
            WHERE u.id = @id
            """,
            new { id });

        // Restricted profiles are hidden like their scores are (mirrors the auth layer, where
        // restricted users read as signed out).
        if (user is null || user.Restricted)
            return NotFound();

        ProfileUser = user;
        CachePolicies.AddTag(HttpContext, CacheTags.User(id));

        // The section order, read on its own rather than as a 9th column on the query above: that
        // one materializes the positional UserHeader record, where appending a column means
        // appending a constructor parameter (the Dapper landmine this file already lives with),
        // and the section order is not header data. One extra primary-key lookup on a page that
        // already runs a dozen aggregate queries.
        SectionOrder = ProfileSections.Resolve(await conn.ExecuteScalarAsync<string[]?>(
            "SELECT profile_order FROM users WHERE id = @id", new { id }));

        long viewerId = CurrentUser?.Id ?? 0;

        // ---- header: follow / watch state ----

        FollowState = await Follows.ProfileStateAsync(conn, id, viewerId, HttpContext.RequestAborted);

        // ---- stats card ----

        // Two ranks, one per board, each read from that board's single source of truth: no query
        // for either metric lives on this page (task 78). They are separate metrics on purpose, so
        // being #1 on one and unranked on the other is a real, renderable state.
        var ranking = await GlobalRanking.ForUserAsync(conn, id, HttpContext.RequestAborted);
        GlobalRank = ranking.GlobalRank;
        RankedScore = ranking.RankedScore;

        var performance = await PpRanking.ForUserAsync(conn, id, HttpContext.RequestAborted);
        PpRank = performance.GlobalRank;
        TotalPp = performance.TotalPp;

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

        // What each section's row-set fragment (Scoring/ProfileScores) has to project for the
        // SELECT above to read it back off the `best` subquery. pp rides along for every section
        // even though only Best orders by it: one column list keeps the fragments interchangeable
        // here, and the cost is one double per row already being fetched.
        const string section_columns =
            """
            sc.id, sc.beatmap_id, sc.rank, sc.completion, sc.accuracy, sc.total_score, sc.pp,
            sc.ended_at, sc.mods::text AS mods, sc.replay_key IS NOT NULL AS has_replay
            """;

        // Pinned: the user's own curation, newest pin first, capped by the pin cap itself.
        // WHICH rows those are is ProfileScores', not this page's: the game client's profile
        // overlay serves the same section from the same fragment, so the two cannot drift.
        PinnedScores = (await conn.QueryAsync<ScoreRowModel>(
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

        // Best scores: the pp board's view of this user, not the score board's (task 77). Both the
        // per-SONG fold and the section's order lead with pp, so the number each row headlines is
        // also the number that put it where it is, and the list reads as the same thing /rankings
        // sums (PpRanking: best-pp play per ranked set, pp descending). The grade counts and mean
        // accuracy above are a different fold, still per BEATMAP, because they are the score
        // board's view rather than the pp board's.
        //
        // ORDERED IN SQL, ABOVE THE LIMIT, deliberately. pp is fetched by a separate query below
        // (it cannot ride the shared score-row SELECT, see ScoreRowModel.Pp), and the obvious
        // shortcut, re-sorting the fetched rows in C#, is WRONG: "the top 20 by total score,
        // re-sorted by pp" is a different SET from "the top 20 by pp", and it is the second one
        // this section is supposed to show. A big-score 0pp play would displace a genuine top-20 pp
        // play out of the list entirely, and no amount of re-sorting afterwards brings it back. The
        // fix costs nothing: pp only has to be a column of the inner subquery to be ORDER BY-able
        // here, and the outer SELECT list (the positionally-mapped one) is untouched.
        //
        // TIE-BREAK: pp DESC, then the leaderboard's own order (total_score DESC, id ASC), which is
        // total because score ids are unique. pp ties are not an edge case here: every custom-rate
        // and not-yet-priced play sits at exactly 0, and that whole tail then keeps precisely the
        // order this section had before, best score first, instead of being shuffled by submission
        // time. Where pp differs, the order agrees with PpRanking's by construction.
        //
        // Both the fold and the order now live in ProfileScores, shared with the game client's
        // "Best performance" subsection, which is the same section on a different screen.
        BestScores = (await conn.QueryAsync<ScoreRowModel>(
            $"""
             {score_row_select}
             FROM ({ProfileScores.BestOfUser(section_columns)}) best
             JOIN beatmaps b ON b.id = best.beatmap_id
             JOIN beatmapsets s ON s.id = b.set_id
             WHERE {ProfileScores.OnVisibleSet("s")}
             ORDER BY {ProfileScores.BestOrder}
             LIMIT {score_section_size}
             """,
            new { id })).ToList();

        // pp per Best-scores row (scores.pp, docs/pp.md), opt-in only on this section: a second
        // small query keyed by score id, merged onto the already-hydrated rows in C#, the same
        // shape as the replay-views ranking below and for the identical reason (see the doc
        // comment on ScoreRowModel.Pp). It reads the column the query above already ORDERed by,
        // which is redundant only in the sense that a primary-key lookup of at most
        // score_section_size rows is: the alternative is a 17th column on the shared SELECT, which
        // breaks every other section that shares it. 0 is a real value (unpriced/custom-rate plays)
        // so it is set unconditionally for every row, never left null once a row is known to be here.
        if (BestScores.Count > 0)
        {
            long[] bestScoreIds = BestScores.Select(r => r.ScoreId).ToArray();

            var ppByScoreId = (await conn.QueryAsync<(long ScoreId, double Pp)>(
                "SELECT id AS ScoreId, pp AS Pp FROM scores WHERE id = ANY(@bestScoreIds)",
                new { bestScoreIds })).ToDictionary(r => r.ScoreId, r => r.Pp);

            foreach (var row in BestScores)
                row.Pp = ppByScoreId.GetValueOrDefault(row.ScoreId);
        }

        // First places: the maps whose leaderboard rank-1 row is this user's. Defined by the BOARD,
        // not by pp or by score size, and built from the same BeatmapLeaderboard fragments the game
        // client's board and the set page's board are built from, so "first place" here always
        // means "top of that board" (tie-break included). Computed on read: no table, no migration.
        //
        // Two reads of one definition: the total (the stats card, and the section's "of M" note)
        // and the capped page of rows. No set-status filter is needed on the outer query, first
        // places only exist on 'ranked' sets, which is a subset of what the other sections show.
        FirstPlaceCount = await conn.ExecuteScalarAsync<int>(
            $"SELECT count(*) FROM ({BeatmapLeaderboard.FirstPlacesOfUserSql}) fp",
            new { id });

        if (FirstPlaceCount > 0)
            FirstPlaces = (await conn.QueryAsync<ScoreRowModel>(
                $"""
                 {score_row_select}
                 FROM ({BeatmapLeaderboard.FirstPlacesOfUserSql}) best
                 JOIN beatmaps b ON b.id = best.beatmap_id
                 JOIN beatmapsets s ON s.id = b.set_id
                 ORDER BY {ProfileScores.FirstPlacesOrder}
                 LIMIT {score_section_size}
                 """,
                new { id })).ToList();

        // Recent plays include fails (rank F renders like the game), still ranked-only so
        // admin-unranked scores never resurface. Shared with the client's Historical section.
        RecentScores = (await conn.QueryAsync<ScoreRowModel>(
            $"""
             {score_row_select}
             FROM ({ProfileScores.RecentOfUser(section_columns)}) best
             JOIN beatmaps b ON b.id = best.beatmap_id
             JOIN beatmapsets s ON s.id = b.set_id
             WHERE {ProfileScores.OnVisibleSet("s")}
             ORDER BY {ProfileScores.RecentOrder}
             LIMIT {score_section_size}
             """,
            new { id })).ToList();

        // Most viewed replays (025_replay_views.sql): the same score rows, ordered by how often
        // OTHER players watched them. The total is read first and gates the query, because for
        // almost every user it is zero and there is then nothing to look for.
        //
        // Visibility matches the other score sections exactly (ranked scores on browsable sets), so
        // a score that gets unranked or whose set is hidden leaves this list like it leaves
        // Best/Recent. The stats-card total deliberately does NOT follow it out: those views
        // happened, and the number is "how often your replays were watched", not "how often the
        // scores currently listed below were watched".
        ReplayViewCount = await ReplayViews.TotalForUserAsync(conn, id, HttpContext.RequestAborted);

        if (ReplayViewCount > 0)
        {
            // Two steps, deliberately. The ranking is its own query over scores alone, which the
            // partial index (user_id, replay_views DESC, id) answers without touching a join; the
            // rows are then hydrated through the SHARED score-row select, unchanged. Selecting the
            // count as a 17th column instead is what one would try first and it does not work: the
            // score-row select is mapped onto a positional record, and Dapper demands a constructor
            // matching the WHOLE column list, so one extra column makes every row fail to
            // materialize. The count is attached in C# afterwards, like the pin flags.
            var ranked = (await conn.QueryAsync<(long ScoreId, int Views)>(
                $"""
                 SELECT id AS ScoreId, replay_views AS Views
                 FROM scores
                 WHERE user_id = @id AND replay_views > 0 AND ranked
                 ORDER BY replay_views DESC, id ASC
                 LIMIT {most_viewed_size}
                 """,
                new { id })).ToList();

            if (ranked.Count > 0)
            {
                long[] ids = ranked.Select(r => r.ScoreId).ToArray();

                var hydrated = (await conn.QueryAsync<ScoreRowModel>(
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
                     WHERE s.status IN ('pending', 'unranked', 'ranked')
                     """,
                    new { ids })).ToDictionary(r => r.ScoreId);

                // Ordered by the ranking query, not by the hydration query: a score whose set has
                // since been hidden simply drops out (the section then shows fewer rows than the
                // cap), exactly as it drops out of Best and Recent.
                var rows = new List<ScoreRowModel>(ranked.Count);

                foreach (var (scoreId, views) in ranked)
                {
                    if (!hydrated.TryGetValue(scoreId, out var row))
                        continue;

                    row.ReplayViews = views;
                    rows.Add(row);
                }

                MostViewedReplays = rows;
            }
        }

        // Pin controls, on your own profile only: every score row this page renders is yours and
        // ranked (all five section queries are ranked-only, first places doubly so), so each one is
        // pinnable, and the control reads "unpin" for the ones already pinned.
        if (viewerId != 0 && viewerId == id)
        {
            var pinnedIds = await ScorePins.PinnedScoreIdsAsync(conn, id);

            foreach (var row in PinnedScores.Concat(BestScores).Concat(FirstPlaces).Concat(RecentScores).Concat(MostViewedReplays))
            {
                row.ShowPinControl = true;
                row.IsPinned = pinnedIds.Contains(row.ScoreId);
            }
        }

        // Most played, per BEATMAP (a set with several difficulties contributes one row each), from
        // the same aggregate the client's Historical section pages over. The display columns come
        // from a join rather than from the GROUP BY, which is what lets one definition serve two
        // very different projections without either of them repeating the grouping key.
        MostPlayed = (await conn.QueryAsync<MostPlayedRow>(
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
             LIMIT {most_played_size}
             """,
            new { id })).ToList();

        // Play history: the monthly rollup, gap-filled into a continuous axis (024_play_history.sql).
        // A user with nothing recorded gets no chart at all, and the view renders no section.
        var playMonths = await PlayHistory.ForUserAsync(conn, id, ct: HttpContext.RequestAborted);
        PlayHistoryChart = playMonths.Count > 0 ? PlayHistory.Chart(playMonths) : null;

        // Replay views over time, the same treatment for the other rollup (025_replay_views.sql).
        // Skipped outright when the total is zero: no rows can exist, so the query would be a
        // guaranteed miss on the profile of every user who has never been watched.
        if (ReplayViewCount > 0)
        {
            var viewMonths = await ReplayViews.ForUserAsync(conn, id, ct: HttpContext.RequestAborted);
            ReplayViewsChart = viewMonths.Count > 0 ? ReplayViews.Chart(viewMonths) : null;
        }

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

    // ---- section order ----

    /// <summary>
    /// Stores the whole section order in one write (task 68). Like the pin handlers, the
    /// {idOrName} in the URL is IGNORED: the order belongs to the session user, so the only
    /// profile this can ever rewrite is their own, and a signed-in visitor POSTing at somebody
    /// else's profile URL just rearranges their own page. Anonymous goes to /login.
    ///
    /// <paramref name="order"/> is the full list, in the new order, as the client read it back out
    /// of the DOM (repeated <c>order</c> form fields). Validation is
    /// <see cref="ProfileSections.TrySanitize"/>'s: unknown ids, an empty list, or more entries
    /// than there are sections are REFUSED outright with 400, so garbage never reaches the column;
    /// an order equal to the default stores null, which is the same thing but survives a future
    /// section being added.
    /// </summary>
    public async Task<IActionResult> OnPostReorderAsync(string[]? order)
    {
        if (CurrentUser is null)
            return Redirect("/login");

        if (!ProfileSections.TrySanitize(order, out string[]? stored))
            return BadRequest();

        await using var conn = await db.OpenAsync(HttpContext.RequestAborted);

        // Two statements rather than one with a nullable array parameter: Dapper hands an array
        // straight to Npgsql as a text[] (that is why ANY(@ids) works above), but a NULL one
        // arrives as an untyped DBNull, and "back to the default" is worth spelling out in SQL
        // instead of relying on Postgres inferring the column's type for it.
        if (stored is null)
            await conn.ExecuteAsync("UPDATE users SET profile_order = NULL WHERE id = @id", new { id = CurrentUser.Id });
        else
            await conn.ExecuteAsync("UPDATE users SET profile_order = @stored WHERE id = @id", new { stored, id = CurrentUser.Id });

        // The fetch path only needs to know it landed; the page it is on already shows the new
        // order (the client moved the DOM before asking). The plain-POST path (a replayed request,
        // curl) gets the profile back, re-rendered from the stored order.
        if (string.Equals(Request.Headers["X-Requested-With"], "fetch", StringComparison.Ordinal))
            return new JsonResult(new { order = stored ?? ProfileSections.Default.ToArray() });

        return Redirect($"/users/{CurrentUser.Id}");
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
        DateTime CreatedAt, DateTime? LastVisit, bool Restricted, string CountryCode)
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
