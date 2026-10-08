using Dapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using Typebeat.Web.Caching;
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
///
/// <para>
/// PAGED BY OFFSET (<c>?page=N</c>, 1-based), deliberately unlike the set listing's keyset
/// paging: rankings are small (hundreds of rows), the order is a computed rank rather than a
/// stored column, and numbered random access is the point of the page. Each board's page count
/// comes from a <c>count(*)</c> over THE SAME shared fragment its rows are read from
/// (<see cref="PerformanceCountSql"/> and friends), so the pager and the rows cannot disagree
/// about the population. An out-of-range or unparseable <c>?page</c> clamps into range rather
/// than 404ing, for the same reason an unknown <c>?board</c> falls back to the main board: the
/// URL is linked-to and shareable, and a stale query string must still render something.
/// </para>
///
/// <para>
/// SEARCHABLE BY PLAYER NAME (<c>?q=</c>, backlog 407): a case-insensitive substring match on the
/// username, with LIKE's wildcards taken literally. The filter runs AFTER the board is ranked, so a
/// matched row keeps its TRUE global rank (<c>BoardRank</c>, a <c>ROW_NUMBER()</c> over the whole
/// board's own order) rather than being renumbered from 1, and the pager counts the searched
/// population through the same fragment with the same predicate.
/// </para>
///
/// <para>Output-cached for anonymous visitors for 60 s, varying by board, page and q (backlogs 366
/// and 407).</para>
/// </summary>
[OutputCache(PolicyName = CachePolicies.Rankings)]
public sealed class IndexModel(Db db) : TypebeatPageModel
{
    public const string PerformanceBoard = "performance";
    public const string ScoreBoard = "score";
    public const string PlaysBoard = "plays";

    /// <summary>
    /// Rows per page, on every board. Settable ONLY so the page tests can force the shapes a
    /// shared database cannot be steered into at 50 rows a page (a one-page board, a deep
    /// ellipsis window) without seeding thousands of users; production never writes it.
    /// </summary>
    public static int PageSize { get; set; } = 50;

    /// <summary>
    /// Each board's population count, one <c>count(*)</c> over EXACTLY the fragment its rows are
    /// read from, so the pager's last page and the rows on it cannot drift apart: a user or play
    /// the fragment delists (restricted, deleted, unranked, failed, non-ranked set) is missing
    /// from both or neither.
    /// </summary>
    public static readonly string PerformanceCountSql = $"SELECT count(*) FROM ({PpRanking.PerUserTotalSql}) totals";

    /// <inheritdoc cref="PerformanceCountSql"/>
    public static readonly string ScoreCountSql = $"SELECT count(*) FROM ({GlobalRanking.PerUserCumulativeSql}) totals";

    /// <inheritdoc cref="PerformanceCountSql"/>
    public static readonly string PlaysCountSql = $"SELECT count(*) FROM ({PpRanking.BestPerSetSql}) plays";

    /// <summary>
    /// The longest search the page honours, in characters. Comfortably past the 15-character
    /// username cap (<c>AccountValidation</c>), so it never cuts a real name short; anything longer
    /// is clamped rather than rejected.
    /// </summary>
    public const int MaxQueryLength = 32;

    /// <summary>The username predicate every searched query shares, against <c>@pattern</c>
    /// (built by <see cref="LikePattern"/>, whose backslash escapes are what ESCAPE names).</summary>
    private static string usernameMatches(string users) => $@"{users}.username ILIKE @pattern ESCAPE '\'";

    /// <summary>
    /// <see cref="PerformanceCountSql"/> narrowed to the players whose name matches the search:
    /// the same fragment, so a searched pager and its rows still cannot disagree.
    /// </summary>
    public static readonly string PerformanceSearchCountSql =
        $"SELECT count(*) FROM ({PpRanking.PerUserTotalSql}) totals JOIN users u ON u.id = totals.user_id WHERE {usernameMatches("u")}";

    /// <inheritdoc cref="PerformanceSearchCountSql"/>
    public static readonly string ScoreSearchCountSql =
        $"SELECT count(*) FROM ({GlobalRanking.PerUserCumulativeSql}) totals JOIN users u ON u.id = totals.user_id WHERE {usernameMatches("u")}";

    /// <summary><see cref="PlaysCountSql"/> narrowed to the plays whose PLAYER's name matches the search.</summary>
    public static readonly string PlaysSearchCountSql =
        $"SELECT count(*) FROM ({PpRanking.BestPerSetSql}) plays JOIN users u ON u.id = plays.user_id WHERE {usernameMatches("u")}";

    /// <summary>
    /// The search as the page uses it: trimmed, clamped to <see cref="MaxQueryLength"/>, and null
    /// when nothing is left, so <c>?q=</c> and <c>?q=%20</c> are the unsearched board.
    /// </summary>
    public static string? NormaliseQuery(string? q)
    {
        string trimmed = (q ?? string.Empty).Trim();

        if (trimmed.Length > MaxQueryLength)
            trimmed = trimmed[..MaxQueryLength].TrimEnd();

        return trimmed.Length == 0 ? null : trimmed;
    }

    /// <summary>
    /// The ILIKE pattern for a substring search: the query wrapped in <c>%</c>, with the three
    /// characters LIKE treats specially (backslash, <c>%</c>, <c>_</c>) backslash-escaped, so a
    /// player searching "50%" or "a_b" matches exactly those characters and nothing broader.
    /// </summary>
    public static string LikePattern(string query)
        => "%" + query.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_") + "%";

    public sealed record PerformanceRow(
        long UserId,
        string Username,
        string? AvatarKey,
        string CountryCode,
        double TotalPp,
        long PpPlayCount,
        long CumulativeScore,
        long BoardRank);

    public sealed record ScoreRow(
        long UserId,
        string Username,
        string? AvatarKey,
        string CountryCode,
        long CumulativeScore,
        long TotalCumulativeScore,
        long RankedScoreCount,
        long BoardRank);

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
        bool HasReplay,
        string? Ratings,
        string CountryCode,
        long BoardRank)
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
        /// FROM, and since backlog 265 also EQUAL TO, on a Half Time row: from backlog 90 to
        /// backlog 265 an HT play was priced from <c>sr_ht</c> and then multiplied by a mirror
        /// penalty on top, so the rating was its difficulty but not its whole price. That
        /// multiplier is gone, and every rate is now priced by the rating in this column and
        /// nothing else.
        /// </para>
        ///
        /// <para>
        /// Never null in practice. <see cref="PerformancePoints.ForScore"/> prices nothing at all,
        /// and its callers store 0, whenever <see cref="PerformancePoints.StarsFor"/> yields no
        /// rating (a custom rate, a multi-rate stack, or a map whose RATING MATRIX does not carry
        /// the cell the play needs: its judgement arm, its stream and its rate,
        /// 034_ratings_matrix.sql), and the board only carries plays with <c>pp &gt; 0</c>, so every
        /// row here priced from a stored cell and the matrix only ever goes from null to filled. It
        /// stays nullable anyway, and renders as an empty cell, because the honest answer to "which
        /// rating is this" is nothing rather than a number that did not price the play.
        /// </para>
        ///
        /// <para>
        /// THE SIX RATING COLUMNS ARE STILL SELECTED alongside the matrix, and are what the row's
        /// own star readouts print. They are the matrix's arm-none stars, so on a play in no
        /// judgement arm this column and the map's published rating are the same number; on an Easy
        /// or Hard Rock play they are deliberately not, which is the whole point of the arm being a
        /// rating input.
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
            => PerformancePoints.StarsFor(Mods, BeatmapRatings.Parse(Ratings)).Stars;

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

    /// <summary>The player-name search, already normalised (<see cref="NormaliseQuery"/>), or null
    /// for the whole board.</summary>
    public string? Query { get; private set; }

    /// <summary>Whether a search is narrowing the board.</summary>
    public bool IsSearch => Query is not null;

    /// <summary>Whether the showing board has any rows at all (drives the empty state, which reads
    /// "no player matches" instead when <see cref="IsSearch"/>).</summary>
    public bool IsEmpty => Board switch
    {
        ScoreBoard => ScoreRows.Count == 0,
        PlaysBoard => PlayRows.Count == 0,
        _ => PerformanceRows.Count == 0,
    };

    /// <summary>The showing page, 1-based, already clamped into <c>1..LastPage</c>.</summary>
    public int PageNumber { get; private set; } = 1;

    /// <summary>The showing board's last page: <c>ceil(population / PageSize)</c>, never below 1.</summary>
    public int LastPage { get; private set; } = 1;

    /// <summary>
    /// How many rows of the (possibly searched) population the page skips. NOT what the rank cells
    /// print: every row carries its own <c>BoardRank</c>, its position in the WHOLE board's total
    /// order, so ranks continue across pages and a searched row shows the rank it truly holds. On
    /// an unsearched board the two agree (row i of page p is rank <c>RankOffset + i + 1</c>).
    /// </summary>
    public int RankOffset => (PageNumber - 1) * PageSize;

    /// <summary>The pager renders only when there is somewhere else to go.</summary>
    public bool ShowPager => LastPage > 1;

    /// <summary>
    /// A page link that keeps the board selector and the search: the bare canonical URL for the
    /// main board's first page, and no redundant <c>page=1</c> anywhere, so a pager link to a
    /// board's first page is byte-identical to its tab link.
    /// </summary>
    public string PageUrl(int page) => RankingsUrl(Board, page, Query);

    /// <summary>A tab link: that board's first page, keeping the search so switching boards does
    /// not drop it.</summary>
    public string TabUrl(string board) => RankingsUrl(board, 1, Query);

    /// <summary>The showing board's first page with the search dropped (the no-match way out).</summary>
    public string ClearSearchUrl => RankingsUrl(Board, 1, null);

    /// <summary>
    /// Every rankings link, in one fixed parameter order (board, page, q), each omitted at its
    /// default: the main board, page 1, no search.
    /// </summary>
    public static string RankingsUrl(string board, int page, string? query)
    {
        var parts = new List<string>(3);

        if (board != PerformanceBoard)
            parts.Add($"board={board}");

        if (page > 1)
            parts.Add($"page={page}");

        if (query is not null)
            parts.Add($"q={Uri.EscapeDataString(query)}");

        return parts.Count == 0 ? "/rankings" : "/rankings?" + string.Join("&", parts);
    }

    /// <summary>
    /// The numbered links the pager shows: first, last and current plus-or-minus two, with a null
    /// wherever a run of pages is elided (rendered as an ellipsis). A gap of exactly one page is
    /// filled with the page itself rather than an ellipsis wider than the number it hides.
    /// </summary>
    public static IReadOnlyList<int?> PagerWindow(int current, int last)
    {
        var items = new List<int?>();
        int previous = 0;

        for (int page = 1; page <= last; page++)
        {
            if (page != 1 && page != last && Math.Abs(page - current) > 2)
                continue;

            if (previous != 0 && page - previous > 1)
                items.Add(page - previous == 2 ? previous + 1 : null);

            items.Add(page);
            previous = page;
        }

        return items;
    }

    // [FromQuery] is LOAD-BEARING on the page parameter: "page" is Razor Pages' reserved route
    // value (it holds the page path, "/Rankings/Index"), so an unattributed int? named page reads
    // the route value first, fails to parse it, and arrives null on every request.
    public async Task OnGetAsync(string? board, [FromQuery(Name = "page")] int? page, [FromQuery(Name = "q")] string? q)
    {
        Query = NormaliseQuery(q);

        // Anything unrecognised falls back to the main board rather than 404ing: /rankings is a
        // linked-to, shareable URL and a stale query string must still render something.
        Board = board switch
        {
            ScoreBoard => ScoreBoard,
            PlaysBoard => PlaysBoard,
            _ => PerformanceBoard,
        };

        await using var conn = await db.OpenAsync(HttpContext.RequestAborted);

        // Null when unsearched: the filter clauses below are only emitted alongside it, so an
        // unsearched query carries no predicate at all.
        string? pattern = Query is null ? null : LikePattern(Query);

        // The pager's population, counted over the very fragment the rows below are read from.
        // Counted FIRST because the page number clamps against it: ?page=0, ?page=garbage (which
        // fails binding and arrives null) and ?page=999999 all land on a real page, the same
        // never-404 stance the board fallback above takes.
        long total = await conn.ExecuteScalarAsync<long>((Board, pattern is null) switch
        {
            (ScoreBoard, true) => ScoreCountSql,
            (ScoreBoard, false) => ScoreSearchCountSql,
            (PlaysBoard, true) => PlaysCountSql,
            (PlaysBoard, false) => PlaysSearchCountSql,
            (_, true) => PerformanceCountSql,
            _ => PerformanceSearchCountSql,
        }, new { pattern });

        LastPage = (int)Math.Max(1, (total + PageSize - 1) / PageSize);
        PageNumber = Math.Clamp(page ?? 1, 1, LastPage);

        if (Board == PerformanceBoard)
        {
            // Cumulative score rides along as a secondary column so the two boards can be compared
            // at a glance; it is a LEFT JOIN because a pp-earning player always has a cumulative
            // total, but the reverse framing keeps the query honest if that ever stops holding.
            //
            // BoardRank is numbered over the WHOLE board inside the subquery, and the search
            // filters the numbered rows afterwards, so a matched player keeps the rank they hold.
            PerformanceRows = (await conn.QueryAsync<PerformanceRow>(
                    $"""
                     SELECT ranked.*
                     FROM (
                         SELECT u.id AS UserId,
                                u.username AS Username,
                                u.avatar_key AS AvatarKey,
                                u.country_code AS CountryCode,
                                p.total_pp AS TotalPp,
                                p.pp_play_count AS PpPlayCount,
                                COALESCE(t.ranked_score, 0) AS CumulativeScore,
                                ROW_NUMBER() OVER (ORDER BY p.total_pp DESC, u.id ASC) AS BoardRank
                         FROM ({PpRanking.PerUserTotalSql}) p
                         JOIN users u ON u.id = p.user_id
                         LEFT JOIN ({GlobalRanking.PerUserCumulativeSql}) t ON t.user_id = p.user_id
                     ) ranked
                     {(pattern is null ? "" : "WHERE " + usernameMatches("ranked"))}
                     ORDER BY ranked.BoardRank
                     LIMIT @limit OFFSET @offset
                     """,
                    new { limit = PageSize, offset = RankOffset, pattern }))
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
            // The rank is numbered over every eligible play and the search (by the PLAY's player)
            // filters the numbered rows, so a searched play keeps its true position on the board.
            //
            // The LIMIT and OFFSET land BEFORE the display joins: the inner select reads only four
            // of the five columns the pp fragments carry (id / user_id / beatmap_id / pp; set_id
            // has done its job in the fold), and just the page's survivors are hydrated by primary
            // key. The
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
                            sc.replay_key IS NOT NULL AS HasReplay,
                            b.ratings::text AS Ratings,
                            u.country_code::text AS CountryCode,
                            top.board_rank AS BoardRank
                     FROM (
                         SELECT ranked.id, ranked.user_id, ranked.beatmap_id, ranked.pp, ranked.board_rank
                         FROM (
                             SELECT best.id, best.user_id, best.beatmap_id, best.pp,
                                    ROW_NUMBER() OVER (ORDER BY {PpRanking.TopPlaysOrder("best")}) AS board_rank
                             FROM ({PpRanking.BestPerSetSql}) best
                         ) ranked
                         {(pattern is null ? "" : "JOIN users fu ON fu.id = ranked.user_id WHERE " + usernameMatches("fu"))}
                         ORDER BY ranked.board_rank
                         LIMIT @limit OFFSET @offset
                     ) top
                     JOIN scores sc ON sc.id = top.id
                     JOIN users u ON u.id = top.user_id
                     JOIN beatmaps b ON b.id = top.beatmap_id
                     JOIN beatmapsets bs ON bs.id = b.set_id
                     ORDER BY top.board_rank
                     """,
                    new { limit = PageSize, offset = RankOffset, pattern }))
                .ToList();

            return;
        }

        // Same cumulative-ranked-score metric as the profile stats card and the client user
        // endpoint (GlobalRanking); one definition so every score-ranking surface agrees.
        // Ranked over the whole board first and searched afterwards, as the performance board is.
        ScoreRows = (await conn.QueryAsync<ScoreRow>(
                $"""
                 SELECT ranked.*
                 FROM (
                     SELECT u.id AS UserId,
                            u.username AS Username,
                            u.avatar_key AS AvatarKey,
                            u.country_code AS CountryCode,
                            t.ranked_score AS CumulativeScore,
                            COALESCE(us.total_score, 0) AS TotalCumulativeScore,
                            t.ranked_map_count AS RankedScoreCount,
                            ROW_NUMBER() OVER (ORDER BY t.ranked_score DESC, u.id ASC) AS BoardRank
                     FROM ({GlobalRanking.PerUserCumulativeSql}) t
                     JOIN users u ON u.id = t.user_id
                     LEFT JOIN user_stats us ON us.user_id = u.id
                 ) ranked
                 {(pattern is null ? "" : "WHERE " + usernameMatches("ranked"))}
                 ORDER BY ranked.BoardRank
                 LIMIT @limit OFFSET @offset
                 """,
                new { limit = PageSize, offset = RankOffset, pattern }))
            .ToList();
    }
}
