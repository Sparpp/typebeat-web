using Dapper;
using Newtonsoft.Json.Linq;
using Typebeat.Web.Data;
using Typebeat.Web.Scoring;

namespace Typebeat.Web.Pages.Rankings;

/// <summary>
/// Global leaderboard, three boards behind one page.
///
/// <list type="bullet">
/// <item><b>performance</b> (the default, <c>?board=performance</c>): total pp. The MAIN global
/// ranking. Each player's best-pp play per ranked SONG (the set, not the single difficulty),
/// sorted, summed with a decay (<see cref="PpRanking"/>, docs/pp.md). It rewards clearing the
/// hardest maps with the fewest misses, not volume.</item>
/// <item><b>score</b> (<c>?board=score</c>): the original cumulative-score board, unchanged. Each
/// player's best score per ranked map, added up (<see cref="GlobalRanking"/>). It is now explicitly
/// the score-farming board rather than the global ranking.</item>
/// <item><b>plays</b> (<c>?board=plays</c>): top pp plays of all time. DIFFERENT IN KIND from the
/// other two: its rows are INDIVIDUAL SCORES ordered by pp, not per-user aggregates, so it answers
/// "what are the biggest plays ever set" rather than "who has the most pp".</item>
/// </list>
///
/// All three share the same eligibility rules, because each metric is defined by one SQL constant:
/// pending/hidden/removed sets contribute nothing, unranked and failed scores never count, and
/// restricted or deleted accounts are delisted like everywhere else. The plays board reuses
/// <see cref="PpRanking.BestPerSetSql"/> outright, which is literally the row set the performance
/// board's total is summed over, so a play can never appear on one and be invisible to the other.
/// </summary>
public sealed class IndexModel(Db db) : TypebeatPageModel
{
    public const string PerformanceBoard = "performance";
    public const string ScoreBoard = "score";
    public const string PlaysBoard = "plays";

    public sealed record PerformanceRow(
        long UserId,
        string Username,
        string? AvatarKey,
        string CountryCode,
        double TotalPp,
        long PpPlayCount,
        long CumulativeScore);

    public sealed record ScoreRow(
        long UserId,
        string Username,
        string? AvatarKey,
        string CountryCode,
        long CumulativeScore,
        long TotalCumulativeScore,
        long RankedScoreCount);

    /// <summary>
    /// One row of the top-plays board: a single score, with the player and the map it was set on.
    /// The judgement counts come out of the statistics jsonb the same way the set page's leaderboard
    /// row reads them (wire keys great/ok/meh/miss, plus combo_break for mistypes), because the two
    /// are the same kind of row and must read the same.
    /// </summary>
    public sealed record PlayRow(
        long ScoreId,
        double Pp,
        long UserId,
        string Username,
        long SetId,
        string Title,
        string Artist,
        string? TitleUnicode,
        string? ArtistUnicode,
        bool Explicit,
        string? Version,
        double BaseStars,
        double? StarsDoubleTime,
        double? StarsHalfTime,
        double? StarsLiterate,
        double? StarsLiterateDoubleTime,
        double? StarsLiterateHalfTime,
        string Rank,
        double Completion,
        double Accuracy,
        int MaxCombo,
        DateTime EndedAt,
        string ModsJson,
        string StatisticsJson,
        bool HasReplay)
    {
        /// <summary>Title, or its original non-romanized text when the viewer prefers that.</summary>
        public string DisplayTitle(bool preferOriginal) => MetadataDisplay.Pick(Title, TitleUnicode, preferOriginal);

        /// <summary>Artist, or its original non-romanized text when the viewer prefers that.</summary>
        public string DisplayArtist(bool preferOriginal) => MetadataDisplay.Pick(Artist, ArtistUnicode, preferOriginal);

        public string GradeLabel => GradeDisplay.Label(Rank);
        public string GradeClass => GradeDisplay.CssClass(Rank);

        /// <summary>
        /// The star rating THIS PLAY'S pp WAS PRICED FROM: the map's rating recomputed at the play's
        /// clock rate for Double Time / Half Time, the base rating otherwise. Resolved through
        /// <see cref="PerformancePoints.StarsFor"/>, the same call
        /// <see cref="PerformancePoints.ForScore"/> makes, rather than by reading
        /// <c>difficulty_rating</c> directly: docs/pp.md prices rate EXCLUSIVELY through the
        /// recomputed rating and never as a flat multiplier, so on a DT row the base rating is not
        /// the number the pp came from, and showing it would misexplain the board's dominant term.
        ///
        /// <para>
        /// FROM, not "equal to", on a Half Time row: since backlog 90 an HT play is priced from
        /// <c>sr_ht</c> and then multiplied by <see cref="PerformancePoints.HalfTimeMultiplier"/>,
        /// so this rating is the play's difficulty but no longer its whole price. The column stays
        /// the rating rather than becoming some penalty-adjusted number, because a star rating is
        /// what a player reads it as; the header tooltip carries the caveat.
        /// </para>
        ///
        /// <para>
        /// Never null in practice. <see cref="PerformancePoints.ForScore"/> prices nothing at all,
        /// and its callers store 0, whenever <see cref="PerformancePoints.StarsFor"/> yields no
        /// rating (a custom rate, a multi-rate stack, or a map missing a rating the play needs:
        /// <c>sr_dt</c> for a DT play, BOTH <c>sr_ht</c> and <c>sr_dt</c> for an HT one, and the
        /// matching <c>sr_literate*</c> for anything carrying Literate), and the board only
        /// carries plays with <c>pp &gt; 0</c>, so every row here priced from one of the six
        /// stored ratings and the sr columns only ever go from null to filled. It stays nullable
        /// anyway, and renders as an empty cell, because the honest answer to "which rating is this"
        /// is nothing rather than a number that did not price the play.
        /// </para>
        ///
        /// <para>
        /// Read from the map's CURRENT ratings, so a re-ingest that moves a map's stars before
        /// <see cref="Packages.PpBackfill"/> re-prices its scores shows the new rating beside an
        /// older pp. That staleness window is a property of every stored pp, not of this column,
        /// and the current rating is the more useful of the two to show.
        /// </para>
        /// </summary>
        public double? EffectiveStars
            => PerformancePoints.StarsFor(Mods, BaseStars, StarsDoubleTime, StarsHalfTime,
                new PerformancePoints.LiterateStars(StarsLiterate, StarsLiterateDoubleTime, StarsLiterateHalfTime)).Stars;

        private JObject? statistics;
        private JObject Statistics => statistics ??= JObject.Parse(string.IsNullOrEmpty(StatisticsJson) ? "{}" : StatisticsJson);

        /// <summary>
        /// Characters the play did not type right, or null for a play with none: cells the song
        /// scrolled past untyped PLUS cells left holding a wrong character (the <c>good</c> key),
        /// folded together by <see cref="JudgementDisplay.MissColumn"/> since backlog 213, exactly
        /// as the set page's leaderboard folds them. Nullable so that
        /// it renders BLANK at zero the way <see cref="Typos"/> beside it always has (backlog 140),
        /// rather than a "0" next to an empty typo cell.
        /// </summary>
        public int? Miss => JudgementDisplay.MissColumn(Statistics);

        /// <summary>
        /// TYPOS: wrong KEYPRESSES, on the wire under the <c>combo_break</c> key (docs/pp.md's
        /// 2026-08-03 amendment, renamed to the player's vocabulary by backlog 140), or null when
        /// this play does not CARRY the stat: plays that predate it have no key at all, and absence
        /// is not zero. The column only appears once some row on the board carries one, exactly like
        /// the set page's leaderboard, so an old play never renders a fabricated clean run.
        ///
        /// <para>The site's ONE typo number since backlog 140. The seal-state count that used to sit
        /// beside it (cells left holding a wrong character, the <c>good</c> key) took no column of
        /// its own from then until backlog 213, which put it in <see cref="Miss"/>: every such cell
        /// implied a wrong keypress, so this event count covered the EVENT, but the character the
        /// player never typed right appeared nowhere at all.</para>
        ///
        /// <para>Beside Miss, and not folded into it: this counts wrong KEYPRESSES, the corrected
        /// ones included, where Miss counts CELLS the play did not get right.</para>
        /// </summary>
        public int? Typos => Statistics.Value<int?>("combo_break");

        /// <summary>Mod badges from the mods jsonb (see <see cref="ScoreMods.Parse"/>).</summary>
        public IReadOnlyList<ScoreMod> Mods => mods ??= ScoreMods.Parse(ModsJson);

        private IReadOnlyList<ScoreMod>? mods;
    }

    /// <summary>Which board is showing: <see cref="PerformanceBoard"/>, <see cref="ScoreBoard"/>
    /// or <see cref="PlaysBoard"/>.</summary>
    public string Board { get; private set; } = PerformanceBoard;

    public IReadOnlyList<PerformanceRow> PerformanceRows { get; private set; } = [];
    public IReadOnlyList<ScoreRow> ScoreRows { get; private set; } = [];
    public IReadOnlyList<PlayRow> PlayRows { get; private set; } = [];

    /// <summary>Whether the showing board has any rows at all (drives the empty state).</summary>
    public bool IsEmpty => Board switch
    {
        ScoreBoard => ScoreRows.Count == 0,
        PlaysBoard => PlayRows.Count == 0,
        _ => PerformanceRows.Count == 0,
    };

    private const int page_size = 50;

    public async Task OnGetAsync(string? board)
    {
        // Anything unrecognised falls back to the main board rather than 404ing: /rankings is a
        // linked-to, shareable URL and a stale query string must still render something.
        Board = board switch
        {
            ScoreBoard => ScoreBoard,
            PlaysBoard => PlaysBoard,
            _ => PerformanceBoard,
        };

        await using var conn = await db.OpenAsync(HttpContext.RequestAborted);

        if (Board == PerformanceBoard)
        {
            // Cumulative score rides along as a secondary column so the two boards can be compared
            // at a glance; it is a LEFT JOIN because a pp-earning player always has a cumulative
            // total, but the reverse framing keeps the query honest if that ever stops holding.
            PerformanceRows = (await conn.QueryAsync<PerformanceRow>(
                    $"""
                     SELECT u.id AS UserId,
                            u.username AS Username,
                            u.avatar_key AS AvatarKey,
                            u.country_code AS CountryCode,
                            p.total_pp AS TotalPp,
                            p.pp_play_count AS PpPlayCount,
                            COALESCE(t.ranked_score, 0) AS CumulativeScore
                     FROM ({PpRanking.PerUserTotalSql}) p
                     JOIN users u ON u.id = p.user_id
                     LEFT JOIN ({GlobalRanking.PerUserCumulativeSql}) t ON t.user_id = p.user_id
                     ORDER BY p.total_pp DESC, u.id ASC
                     LIMIT @limit
                     """,
                    new { limit = page_size }))
                .ToList();

            return;
        }

        if (Board == PlaysBoard)
        {
            // The biggest plays ever set, not the biggest players. NO per-user dedup: a player who
            // owns several of the hardest songs holds several rows, which is the point of a
            // "top plays" board (the Performance tab is already the one-row-per-player view).
            //
            // There IS a per-(user, SONG) fold, and it is not a hand-rolled one: PpRanking.
            // BestPerSetSql is the exact row set the pp total is summed over, so this board shows
            // the plays that actually count, and twenty near-identical retries of one map by one
            // player (or one clear each of a song's Easy, Normal and Insane) collapse to the single
            // best of them instead of filling the page.
            //
            // The LIMIT lands BEFORE the display joins: the inner select reads only four of the
            // five columns the pp fragments carry (id / user_id / beatmap_id / pp; set_id has done
            // its job in the fold), and just the 50 survivors are hydrated by primary key. The
            // per-map stage underneath the set
            // fold is served by ix_scores_pp (020_performance_points.sql); this board's global pp
            // ordering is NOT, since that index leads with user_id, so the folded set is sorted. It
            // is a sort over the eligible plays, the same set the Performance board already folds
            // on every render.
            PlayRows = (await conn.QueryAsync<PlayRow>(
                    $"""
                     SELECT top.id AS ScoreId,
                            top.pp AS Pp,
                            u.id AS UserId,
                            u.username AS Username,
                            bs.id AS SetId,
                            bs.title AS Title,
                            bs.artist AS Artist,
                            bs.title_unicode AS TitleUnicode,
                            bs.artist_unicode AS ArtistUnicode,
                            bs.explicit AS Explicit,
                            b.version_name AS Version,
                            b.difficulty_rating AS BaseStars,
                            b.sr_dt AS StarsDoubleTime,
                            b.sr_ht AS StarsHalfTime,
                            b.sr_literate AS StarsLiterate,
                            b.sr_literate_dt AS StarsLiterateDoubleTime,
                            b.sr_literate_ht AS StarsLiterateHalfTime,
                            sc.rank AS Rank,
                            sc.completion AS Completion,
                            sc.accuracy AS Accuracy,
                            sc.max_combo AS MaxCombo,
                            sc.ended_at AS EndedAt,
                            sc.mods::text AS ModsJson,
                            sc.statistics::text AS StatisticsJson,
                            sc.replay_key IS NOT NULL AS HasReplay
                     FROM (
                         SELECT best.id, best.user_id, best.beatmap_id, best.pp
                         FROM ({PpRanking.BestPerSetSql}) best
                         ORDER BY {PpRanking.TopPlaysOrder("best")}
                         LIMIT @limit
                     ) top
                     JOIN scores sc ON sc.id = top.id
                     JOIN users u ON u.id = top.user_id
                     JOIN beatmaps b ON b.id = top.beatmap_id
                     JOIN beatmapsets bs ON bs.id = b.set_id
                     ORDER BY {PpRanking.TopPlaysOrder("top")}
                     """,
                    new { limit = page_size }))
                .ToList();

            return;
        }

        // Same cumulative-ranked-score metric as the profile stats card and the client user
        // endpoint (GlobalRanking); one definition so every score-ranking surface agrees.
        ScoreRows = (await conn.QueryAsync<ScoreRow>(
                $"""
                 SELECT u.id AS UserId,
                        u.username AS Username,
                        u.avatar_key AS AvatarKey,
                        u.country_code AS CountryCode,
                        t.ranked_score AS CumulativeScore,
                        COALESCE(us.total_score, 0) AS TotalCumulativeScore,
                        t.ranked_map_count AS RankedScoreCount
                 FROM ({GlobalRanking.PerUserCumulativeSql}) t
                 JOIN users u ON u.id = t.user_id
                 LEFT JOIN user_stats us ON us.user_id = u.id
                 ORDER BY t.ranked_score DESC, u.id ASC
                 LIMIT @limit
                 """,
                new { limit = page_size }))
            .ToList();
    }
}
