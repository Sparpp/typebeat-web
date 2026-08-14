using Dapper;
using Npgsql;

namespace Typebeat.Web.Scoring;

/// <summary>
/// The one definition of each of the profile's SCORE SECTIONS: which of a user's plays belong in
/// Pinned, Best, First places, Recent and Most played, and in what order each of those reads.
///
/// <para>
/// Both surfaces that render those sections build their SQL out of the fragments here: the website
/// profile page (<c>Pages/Users/Profile.cshtml.cs</c>) and the game client's profile overlay,
/// served by <c>Endpoints/ProfileScoreEndpoints</c>. That is the whole point of the file. A section
/// that means one thing in the browser and another in the client is not a cosmetic difference, it
/// is the same failure mode this codebase already avoided once by making
/// <see cref="BeatmapLeaderboard"/> the single board definition: a second hand-written copy of
/// "ranked and passed, best score first" drifts, and nobody notices until two screens disagree
/// about a player's top play.
/// </para>
///
/// <para>
/// SHAPE OF THE FRAGMENTS. Each section is a row-set builder taking the columns to project (always
/// qualified <c>sc.</c>, the alias of <c>scores</c> inside the subquery) plus the ORDER BY that
/// section reads in, over the alias <c>best</c> the callers give the subquery. The projection is
/// the caller's because the two callers genuinely need different columns: the page selects
/// display metadata for a Razor row, the endpoint selects the fields the client's
/// <c>SoloScoreInfo</c> binds. What must NOT differ, and therefore lives here, is which rows those
/// are and what order they come in.
/// </para>
///
/// <para>
/// Every fragment is completed by the caller with a join to <c>beatmaps</c>/<c>beatmapsets</c> and
/// the <see cref="OnVisibleSet"/> predicate. That join is not folded in here because the two
/// callers alias it differently and the endpoint needs the set's own columns anyway.
/// </para>
///
/// <para>
/// They interpolate only C# literals supplied by this codebase, never request data. The bound
/// parameter is always <c>@id</c>, the profile owner.
/// </para>
/// </summary>
public static class ProfileScores
{
    /// <summary>
    /// A set whose scores are allowed to show at all: published, in the sense the rest of the site
    /// uses. Hidden and removed sets must not leak their titles through somebody's score list, and
    /// 'pending' sets are browsable, so plays on them legitimately appear.
    /// </summary>
    public static string OnVisibleSet(string set)
        => $"{set}.status IN ('pending', 'unranked', 'ranked')";

    /// <summary>
    /// PINNED: the user's own curation (022_score_pins.sql), gated by exactly the filters the other
    /// sections use, so a score that is deleted or unranked by an admin stops appearing here just
    /// as it stops appearing under Best and Recent. The pin row survives and still counts against
    /// <see cref="ScorePins.MaxPins"/>; only the SCORE going takes the pin with it, by cascade.
    ///
    /// <para>Projects <paramref name="columns"/> plus <c>pinned_at</c>, which <see cref="PinnedOrder"/> reads.</para>
    /// </summary>
    public static string PinnedOfUser(string columns) =>
        $"""
         SELECT {columns}, p.pinned_at
         FROM score_pins p
         JOIN scores sc ON sc.id = p.score_id
         WHERE p.user_id = @id AND sc.ranked
         """;

    /// <summary>Newest pin first; the id tie-break makes it total.</summary>
    public const string PinnedOrder = "best.pinned_at DESC, best.id DESC";

    /// <summary>
    /// BEST: the pp board's view of this user (task 77), NOT the score board's. One row per SONG,
    /// the best-pp play anywhere in the set, which is the unit
    /// <see cref="PpRanking.BestPerSetSql"/> sums, so the section reads as the same thing /rankings
    /// totals. The fold is on the SET rather than the difficulty (backlog 162) for exactly that
    /// reason: a profile that listed a play the player's own total no longer counts would break the
    /// equivalence this comment exists to promise.
    ///
    /// <para>
    /// Eligibility is <see cref="BeatmapLeaderboard.OnBoard"/> on the ranked board, deliberately
    /// WIDER than <see cref="PpRanking.EligiblePlaysSql"/>: a play worth 0pp (custom rate, or not
    /// yet priced) is still your best play on that song and still belongs in the list, it just
    /// sorts to the tail. Restricting to <c>pp &gt; 0</c> here would hide plays the player can
    /// plainly see on the map's own leaderboard. The 162 change widens the UNIT, and must not
    /// narrow that population.
    /// </para>
    ///
    /// <para>
    /// The join to <c>beatmaps</c> is here only to reach <c>set_id</c>, and is aliased <c>bm</c>
    /// rather than <c>b</c> because every caller joins its own <c>beatmaps b</c> outside this
    /// subquery for display columns. The tie-break is unchanged (<c>pp DESC</c> then
    /// <see cref="BeatmapLeaderboard.Order"/>), so a song whose difficulties are all 0pp still
    /// resolves deterministically to its best-SCORING row rather than an arbitrary one.
    /// </para>
    ///
    /// <para>
    /// <paramref name="columns"/> must include <c>sc.pp</c>: <see cref="BestOrder"/> reads it, and
    /// ordering in SQL above the LIMIT is load bearing. "The top N by total score, re-sorted by pp"
    /// is a different SET from "the top N by pp", and a big-score 0pp play would displace a genuine
    /// top-N pp play out of the list entirely, which no amount of re-sorting afterwards undoes.
    /// </para>
    /// </summary>
    public static string BestOfUser(string columns) =>
        $"""
         SELECT DISTINCT ON (bm.set_id) {columns}
         FROM scores sc
         JOIN beatmaps bm ON bm.id = sc.beatmap_id
         WHERE sc.user_id = @id AND {BeatmapLeaderboard.OnBoard("sc", "true")}
         ORDER BY bm.set_id, sc.pp DESC, {BeatmapLeaderboard.Order("sc")}
         """;

    /// <summary>
    /// Biggest pp first, then the leaderboard's own order (total score, earlier submission), which
    /// is total because score ids are unique. pp ties are not an edge case: every custom-rate and
    /// not-yet-priced play sits at exactly 0, and that whole tail then keeps best-score-first order
    /// instead of being shuffled by submission time.
    /// </summary>
    public const string BestOrder = "best.pp DESC, best.total_score DESC, best.id ASC";

    /// <summary>
    /// RECENT: every ranked play, newest first, fails included (rank F renders like the game does).
    /// Still ranked-only, so a score an admin unranked never resurfaces here.
    ///
    /// <para>
    /// NOT time-windowed, deliberately, and this is where it departs from osu-web (whose
    /// <c>scores/recent</c> means "the last 24 hours" and is empty for almost everybody almost
    /// always). It is defined as the website's Recent section is defined, because those two are the
    /// same section on two screens; <see cref="CountsForUserAsync"/> counts exactly this row set,
    /// so the number beside the heading is the number of rows you can actually page to.
    /// </para>
    /// </summary>
    public static string RecentOfUser(string columns) =>
        $"""
         SELECT {columns}
         FROM scores sc
         WHERE sc.user_id = @id AND sc.ranked
         """;

    /// <summary>Newest first; the id tie-break makes it total when two plays share an instant.</summary>
    public const string RecentOrder = "best.ended_at DESC, best.id DESC";

    /// <summary>
    /// FIRST PLACES read their rows from <see cref="BeatmapLeaderboard.FirstPlacesOfUserSql"/> (the
    /// board owns what a #1 is); this is only the order the SECTION presents them in. Newest first,
    /// like Recent, rather than by score, so a fresh #1 is at the top where the player looks.
    /// </summary>
    public const string FirstPlacesOrder = "best.ended_at DESC, best.id DESC";

    /// <summary>
    /// MOST PLAYED: plays per BEATMAP. Every play counts, passed or failed, ranked or unranked,
    /// which is the same "what is a play" the stats card's play count and the play-history chart
    /// use (see <see cref="PlayHistory"/>); the only filter is set visibility.
    ///
    /// <para>
    /// Yields exactly two columns, <c>beatmap_id</c> and <c>plays</c>. Unlike the score sections
    /// this one takes no projection, because it is an AGGREGATE: any display column a caller added
    /// would have to be repeated in the GROUP BY, and a caller that got that wrong would silently
    /// split one map's plays across several rows. Embed it as a subquery aliased <c>mp</c> and join
    /// <c>beatmaps</c>/<c>beatmapsets</c> on <c>beatmap_id</c> for whatever the caller renders; the
    /// join is one-to-one, so it cannot disturb the counts.
    /// </para>
    /// </summary>
    public static readonly string MostPlayedOfUserSql =
        $"""
         SELECT sc.beatmap_id AS beatmap_id, count(*) AS plays
         FROM scores sc
         JOIN beatmaps b ON b.id = sc.beatmap_id
         JOIN beatmapsets s ON s.id = b.set_id
         WHERE sc.user_id = @id AND {OnVisibleSet("s")}
         GROUP BY sc.beatmap_id
         """;

    /// <summary>
    /// Most played first, over the subquery aliased <c>mp</c>. Tie-broken by beatmap id, which is
    /// TOTAL because the grouping key IS the beatmap; a set id would not be, since two difficulties
    /// of one set can tie on both.
    /// </summary>
    public const string MostPlayedOrder = "mp.plays DESC, mp.beatmap_id ASC";

    // ---- counters ----

    /// <summary>
    /// The five section sizes the client's profile overlay prints beside its subsection headings
    /// (<c>PaginatedProfileSubsection.GetCount</c> reads them straight off the user payload).
    ///
    /// <para>
    /// Each one counts EXACTLY the row set its section serves, built from the fragments above, for
    /// the reason the client comment records: a counter derived independently of the list it labels
    /// is a number that can disagree with the rows underneath it, and a confident wrong count is
    /// worse than none. One round trip, because the profile fetch already runs several.
    /// </para>
    /// </summary>
    /// <param name="userId">The profile owner. Bound as <c>@id</c>, which every fragment expects.</param>
    public static async Task<SectionCounts> CountsForUserAsync(
        NpgsqlConnection conn, long userId, CancellationToken ct = default)
        => await conn.QuerySingleAsync<SectionCounts>(new CommandDefinition(
            $"""
             SELECT
                 (SELECT count(*) FROM ({PinnedOfUser("sc.id, sc.beatmap_id")}) best
                  JOIN beatmaps b ON b.id = best.beatmap_id
                  JOIN beatmapsets s ON s.id = b.set_id
                  WHERE {OnVisibleSet("s")})::int AS Pinned,
                 (SELECT count(*) FROM ({BestOfUser("sc.id, sc.beatmap_id, sc.pp, sc.total_score")}) best
                  JOIN beatmaps b ON b.id = best.beatmap_id
                  JOIN beatmapsets s ON s.id = b.set_id
                  WHERE {OnVisibleSet("s")})::int AS Best,
                 (SELECT count(*) FROM ({BeatmapLeaderboard.FirstPlacesOfUserSql}) fp)::int AS FirstPlaces,
                 (SELECT count(*) FROM ({RecentOfUser("sc.id, sc.beatmap_id")}) best
                  JOIN beatmaps b ON b.id = best.beatmap_id
                  JOIN beatmapsets s ON s.id = b.set_id
                  WHERE {OnVisibleSet("s")})::int AS Recent,
                 (SELECT count(*) FROM ({MostPlayedOfUserSql}) mp)::int AS MostPlayed
             """,
            new { id = userId }, cancellationToken: ct));

    /// <summary>
    /// The five counts, in the order the client's sections read them. Ints because the client's
    /// <c>APIUser</c> counter fields are ints, and a profile cannot plausibly overflow one.
    /// </summary>
    public sealed record SectionCounts(int Pinned, int Best, int FirstPlaces, int Recent, int MostPlayed);
}
