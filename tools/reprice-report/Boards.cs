using Typebeat.Web.Scoring;

namespace Typebeat.Tools.RepriceReport;

/// <summary>Two plays whose order swapped, and what differed between them when it did.</summary>
internal sealed record OrderChange(
    string Where,
    long BeatmapId,
    long ScoreAbove,
    long ScoreBelow,
    int NotesAbove,
    int NotesBelow,
    long CellsAbove,
    long CellsBelow)
{
    /// <summary>
    /// Whether LENGTH is what separated these two. 152 allows an order change only here: the deleted
    /// factor is a function of the play's note count and the star bonus a function of the map's cell
    /// count, so two plays that agree on both are multiplied by exactly the same number and cannot
    /// legitimately change places.
    /// </summary>
    public bool LengthWasTheDifferentiator => NotesAbove != NotesBelow || CellsAbove != CellsBelow;
}

/// <summary>One user's move in the global ranking.</summary>
internal sealed record RankMove(long UserId, int RankBefore, int RankAfter, double TotalBefore, double TotalAfter)
{
    public int Movement => RankBefore - RankAfter;
}

internal sealed class BoardFindings
{
    /// <summary>True when the run held only a slice of the catalogue, in which case nothing here ran.</summary>
    public bool Filtered { get; init; }

    public int Boards { get; set; }
    public int BoardsReordered { get; set; }
    public int BoardsTopChanged { get; set; }
    public int RowsLeavingBoards { get; set; }
    public int RowsJoiningBoards { get; set; }
    public int RepresentativeChanges { get; set; }

    /// <summary>Every per-map order change, whichever side of the predicate it falls.</summary>
    public List<OrderChange> BoardOrderChanges { get; } = new();

    /// <summary>Every order change on the global top-plays board.</summary>
    public List<OrderChange> GlobalOrderChanges { get; } = new();

    public int RankedUsers { get; set; }
    public List<RankMove> RankMoves { get; } = new();

    public IEnumerable<OrderChange> Violations
        => BoardOrderChanges.Concat(GlobalOrderChanges).Where(c => !c.LengthWasTheDifferentiator);

    /// <summary>
    /// ORDER CHANGES ONLY WHERE LENGTH WAS THE DIFFERENTIATOR. Order changes themselves are the
    /// FEATURE (152: "length stops buying pp it no longer earns"), so they are counted and listed,
    /// never failed; what fails is a pair that swapped without length between them.
    /// </summary>
    public bool Passed => !Violations.Any();
}

/// <summary>
/// What the reprice does to the thing the decision is about: who is above whom.
///
/// <para>Modelled on the same three surfaces the site actually serves (<see cref="PpRanking"/>): a
/// per-map board of each player's BEST-pp play, the global top-plays board over the same folded
/// rows, and the per-user weighted total that the global ranking sorts. Eligibility comes from the
/// database through <see cref="PpRanking.EligiblePlaysSql"/>'s own definition, minus its price
/// predicate, which is applied here so it can be applied twice, once per side.</para>
///
/// <para>Rows the reprice did not touch stay on BOTH sides at their stored values. Leaving them out
/// would invent place changes, exactly as score-recalc's leaderboard section notes.</para>
/// </summary>
internal static class BoardAnalysis
{
    /// <param name="rows">Every score row the run loaded, repriced or not.</param>
    /// <param name="boardEligible">
    /// Score ids that are on a ranked board and belong to a listed account, i.e.
    /// <see cref="PpRanking.EligiblePlaysSql"/> WITHOUT its <c>pp &gt; 0</c> clause. The price
    /// predicate is applied per side here, so a row that stops earning pp can leave the board.
    /// </param>
    public static BoardFindings Analyse(IReadOnlyList<PpRow> rows, IReadOnlySet<long> boardEligible, bool filtered)
    {
        var findings = new BoardFindings { Filtered = filtered };

        if (filtered)
            return findings;

        var eligible = rows.Where(r => boardEligible.Contains(r.Stored.ScoreId)).ToList();

        findings.RowsLeavingBoards = eligible.Count(r => r.Stored.Pp > 0 && After(r) <= 0);
        findings.RowsJoiningBoards = eligible.Count(r => r.Stored.Pp <= 0 && After(r) > 0);

        foreach (var board in eligible.GroupBy(r => r.Stored.BeatmapId))
        {
            var before = Fold(board, Before);
            var after = Fold(board, After);

            if (before.Count == 0 && after.Count == 0)
                continue;

            findings.Boards++;
            findings.RepresentativeChanges += CountRepresentativeChanges(board);

            var common = Common(before, after);

            if (!common.Before.SequenceEqual(common.After))
                findings.BoardsReordered++;

            if (Top(before) != Top(after))
                findings.BoardsTopChanged++;

            Compare($"beatmap {board.Key}", board.Key, common.Before, common.After, board.ToDictionary(r => r.Stored.ScoreId), findings.BoardOrderChanges);
        }

        // The global top-plays board and the per-user total both run over the same fold: each
        // player's best-pp play per SONG (PpRanking.BestPerSetSql, backlog 162). Computed once, per
        // side. The per-BEATMAP boards above are a different thing and stay keyed on the beatmap:
        // a map's own leaderboard is still a map's own leaderboard.
        var bestBefore = FoldPerUserPerSet(eligible, Before);
        var bestAfter = FoldPerUserPerSet(eligible, After);

        var byId = eligible.ToDictionary(r => r.Stored.ScoreId);

        var globalBefore = bestBefore.OrderByDescending(Before).ThenBy(r => r.Stored.ScoreId).Select(r => r.Stored.ScoreId).ToList();
        var globalAfter = bestAfter.OrderByDescending(After).ThenBy(r => r.Stored.ScoreId).Select(r => r.Stored.ScoreId).ToList();

        var globalCommon = Common(globalBefore, globalAfter);
        Compare("global top plays", 0, globalCommon.Before, globalCommon.After, byId, findings.GlobalOrderChanges);

        BuildRankMoves(bestBefore, bestAfter, findings);

        return findings;
    }

    private static double Before(PpRow row) => row.Stored.Pp;

    /// <summary>
    /// The price the row would read at after the reprice. A row whose map could not be re-rated keeps
    /// its stored price on BOTH sides: it stays on the board either way, and dropping it would invent
    /// a place change out of a fetch failure.
    /// </summary>
    private static double After(PpRow row) => row.NewPp ?? row.Stored.Pp;

    /// <summary>One row per user (their best-pp play, ties to the earlier submission), then ordered.</summary>
    private static List<long> Fold(IEnumerable<PpRow> board, Func<PpRow, double> price)
        => board.Where(r => price(r) > 0)
                .GroupBy(r => r.Stored.UserId)
                .Select(u => u.OrderByDescending(price).ThenBy(r => r.Stored.ScoreId).First())
                .OrderByDescending(price)
                .ThenBy(r => r.Stored.ScoreId)
                .Select(r => r.Stored.ScoreId)
                .ToList();

    /// <summary>
    /// One row per (user, SET): the unit pp is earned in. The site folds twice, per map and then per
    /// set, because only the first stage can use <c>ix_scores_pp</c>; in memory the single grouping
    /// gives the identical rows, since both stages break ties the same way.
    /// </summary>
    private static List<PpRow> FoldPerUserPerSet(IEnumerable<PpRow> rows, Func<PpRow, double> price)
        => rows.Where(r => price(r) > 0)
               .GroupBy(r => (r.Stored.UserId, r.Map.Stored.SetId))
               .Select(g => g.OrderByDescending(price).ThenBy(r => r.Stored.ScoreId).First())
               .ToList();

    private static long Top(List<long> order) => order.Count > 0 ? order[0] : 0;

    private static int CountRepresentativeChanges(IEnumerable<PpRow> board)
        => board.GroupBy(r => r.Stored.UserId)
                .Count(u =>
                {
                    long? before = u.Where(r => Before(r) > 0).OrderByDescending(Before).ThenBy(r => r.Stored.ScoreId).FirstOrDefault()?.Stored.ScoreId;
                    long? after = u.Where(r => After(r) > 0).OrderByDescending(After).ThenBy(r => r.Stored.ScoreId).FirstOrDefault()?.Stored.ScoreId;

                    return before != after;
                });

    /// <summary>
    /// The two orders restricted to the rows present in BOTH, so a row that left the board is counted
    /// as a departure rather than reported as everything below it having moved.
    /// </summary>
    private static (List<long> Before, List<long> After) Common(List<long> before, List<long> after)
    {
        var shared = new HashSet<long>(before);
        shared.IntersectWith(after);

        return (before.Where(shared.Contains).ToList(), after.Where(shared.Contains).ToList());
    }

    /// <summary>
    /// Every pair whose order swapped. O(n^2) over one board's rows, which is what it takes to name
    /// the PAIR: a report that said "this board reordered" without saying which two plays traded
    /// places could not answer whether length was what separated them, which is the whole predicate.
    /// </summary>
    private static void Compare(
        string where,
        long beatmapId,
        List<long> before,
        List<long> after,
        IReadOnlyDictionary<long, PpRow> byId,
        List<OrderChange> into)
    {
        // The common case, by far, is a board that held its order, and the pair scan below is
        // quadratic. Checking the two sequences first makes that case free and keeps the scan for
        // the boards that actually moved.
        if (before.SequenceEqual(after))
            return;

        var position = new Dictionary<long, int>(after.Count);

        for (int i = 0; i < after.Count; i++)
            position[after[i]] = i;

        for (int i = 0; i < before.Count; i++)
        {
            for (int j = i + 1; j < before.Count; j++)
            {
                long above = before[i], below = before[j];

                if (position[above] <= position[below])
                    continue;

                var a = byId[above];
                var b = byId[below];

                into.Add(new OrderChange(where, beatmapId, above, below, a.Notes.Notes, b.Notes.Notes, a.Cells, b.Cells));
            }
        }
    }

    /// <summary>
    /// The per-user weighted total and the ranking it sorts, mirroring <see cref="PpRanking"/>'s
    /// <c>PerUserTotalSql</c> in memory: the i-th best deduped play contributes
    /// <c>pp * DECAY^i</c>, and the constant is read from <see cref="PerformancePoints.DECAY"/>
    /// rather than restated, so a retune moves this report with it.
    /// </summary>
    private static void BuildRankMoves(IReadOnlyList<PpRow> bestBefore, IReadOnlyList<PpRow> bestAfter, BoardFindings findings)
    {
        var before = Totals(bestBefore, Before);
        var after = Totals(bestAfter, After);

        var rankBefore = Rank(before);
        var rankAfter = Rank(after);

        findings.RankedUsers = rankBefore.Count;

        foreach (var (userId, rank) in rankBefore)
        {
            if (!rankAfter.TryGetValue(userId, out int now) || now == rank)
                continue;

            findings.RankMoves.Add(new RankMove(userId, rank, now, before[userId], after[userId]));
        }

        findings.RankMoves.Sort((x, y) => Math.Abs(y.Movement).CompareTo(Math.Abs(x.Movement)));
    }

    private static Dictionary<long, double> Totals(IReadOnlyList<PpRow> best, Func<PpRow, double> price)
    {
        var totals = new Dictionary<long, double>();

        foreach (var user in best.GroupBy(r => r.Stored.UserId))
        {
            double total = 0;
            int i = 0;

            foreach (var play in user.OrderByDescending(price).ThenBy(r => r.Stored.ScoreId))
                total += price(play) * Math.Pow(PerformancePoints.DECAY, i++);

            totals[user.Key] = total;
        }

        return totals;
    }

    /// <summary>Dense rank over the totals, biggest first, ties sharing a rank as the site's does.</summary>
    private static Dictionary<long, int> Rank(Dictionary<long, double> totals)
    {
        var ranks = new Dictionary<long, int>(totals.Count);
        int rank = 0;
        double? previous = null;

        foreach (var (userId, total) in totals.OrderByDescending(kvp => kvp.Value).ThenBy(kvp => kvp.Key))
        {
            if (previous is not double p || Math.Abs(p - total) > 1e-12)
                rank++;

            ranks[userId] = rank;
            previous = total;
        }

        return ranks;
    }
}
