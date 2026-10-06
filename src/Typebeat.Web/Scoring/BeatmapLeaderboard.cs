namespace Typebeat.Web.Scoring;

/// <summary>
/// The one definition of a BEATMAP LEADERBOARD: which plays are on a map's board, which single play
/// represents a player there, and in what order the board reads. Every surface that shows, counts or
/// positions a board row builds its SQL out of the fragments here (the game client's board in
/// <c>Endpoints/ScoreEndpoints</c>, the set page's board in <c>Pages/Beatmapsets/Set</c>, and the
/// profile's first places in <c>Pages/Users/Profile</c>), so no two of them can drift apart over a
/// tie-break or a visibility rule.
///
/// <para>The rules, all of them:</para>
/// <list type="bullet">
/// <item>A play is ON a board when it PASSED and its stored <c>ranked</c> flag matches the board:
/// every map serves a ranked board and an unranked one (the latter is where non-default rates and
/// unranked mods land), and a row never crosses between them (<see cref="OnBoard"/>).</item>
/// <item>WHICH board a map serves is decided by its set's CURRENT status, re-read per request, never
/// trusted from the stored flags: 'ranked' and 'loved' sets serve the ranked board, 'pending' and
/// 'unranked' sets serve the unranked one, and anything else (hidden, removed) serves no board at
/// all. That switch lives at each call site because it also decides what the page renders instead.
/// A loved set's board is ranked but earns no pp: its plays store ranked with pp 0, and the game's
/// board sends no pp for any of its rows. First places stay confined to 'ranked' sets
/// (<see cref="FirstPlacesOfUserSql"/>), as do the pp and ranked-score rankings.</item>
/// <item>A player is represented by their BEST play on the board, one row each
/// (<c>DISTINCT ON (user_id)</c> in <see cref="Order"/>).</item>
/// <item>The board sorts by total score descending, and a tie is broken by the EARLIER submission
/// (lower score id): there is therefore exactly one rank-1 row per map (<see cref="Order"/>,
/// <see cref="Outranks"/>).</item>
/// </list>
///
/// <para>
/// NOT a rule, deliberately recorded because it surprises people: a board does NOT delist restricted
/// or deleted accounts, though the global rankings (<see cref="GlobalRanking"/>,
/// <see cref="PpRanking"/>) do. A restricted player keeps whatever board positions they held. First
/// places match that, because they are defined as "the board's rank-1 row is yours" and nothing
/// else. If boards should start delisting them, the fix is one predicate HERE plus the join it
/// needs, and every board (game client, set page, first places) moves together.
/// </para>
///
/// <para>
/// The fragments take the table alias to qualify, because their call sites already use different
/// ones (<c>s</c>, <c>sc</c>, <c>best</c>). They interpolate only C# literals supplied by this
/// codebase, never request data.
/// </para>
/// </summary>
public static class BeatmapLeaderboard
{
    /// <summary>
    /// A play is on a board: it passed, and its ranked flag matches the board being read.
    /// <paramref name="board"/> defaults to the <c>@wantRanked</c> parameter the two per-map board
    /// queries bind; pass <c>"true"</c> for "the ranked board" outright.
    /// </summary>
    public static string OnBoard(string score, string board = "@wantRanked")
        => $"{score}.passed AND {score}.ranked = {board}";

    /// <summary>
    /// The board's ordering: highest total score first, the EARLIER submission (lower id) winning a
    /// tie. Used both to fold a player's plays down to their best (as the tail of a
    /// <c>DISTINCT ON (user_id)</c> ordering) and to sort the folded rows into the board.
    /// </summary>
    public static string Order(string score)
        => $"{score}.total_score DESC, {score}.id ASC";

    /// <summary>
    /// The comparison form of <see cref="Order"/>: true when the row aliased
    /// <paramref name="score"/> sits ABOVE the score bound to the <c>@totalScore</c> /
    /// <c>@scoreId</c> parameters. Counting the rows that satisfy it is what makes a 1-based board
    /// position.
    /// </summary>
    public static string Outranks(string score)
        => $"({score}.total_score > @totalScore OR ({score}.total_score = @totalScore AND {score}.id < @scoreId))";

    /// <summary>
    /// FIRST PLACES: every ranked map whose leaderboard's rank-1 row belongs to <c>@id</c>, as that
    /// row. Embed as a subquery (aliased <c>best</c> for the profile's score-row SELECT), and join
    /// <c>beatmaps</c> / <c>beatmapsets</c> on <c>beatmap_id</c> for the display columns.
    ///
    /// <para>
    /// Columns: <c>id</c>, <c>beatmap_id</c>, <c>user_id</c>, <c>rank</c>, <c>completion</c>,
    /// <c>accuracy</c>, <c>total_score</c>, <c>max_combo</c>, <c>passed</c>, <c>ranked</c>,
    /// <c>pp</c>, <c>ended_at</c>, <c>mods</c> (text), <c>statistics</c> (text),
    /// <c>maximum_statistics</c> (text), <c>has_replay</c>. Callers project the subset they render;
    /// the list is wide enough to serve the game client's score rows as well as the website's,
    /// which is the point of there being one definition rather than two.
    /// </para>
    ///
    /// <para>
    /// It folds straight to the board's TOP row per map rather than folding per user first: the
    /// board's rank-1 row is the maximum of <see cref="Order"/> over the per-user bests, and a
    /// player's best is itself the maximum of that same ordering over their own plays, so the
    /// two-step fold and this one-step <c>DISTINCT ON (beatmap_id)</c> select the same row. Taking
    /// the shortcut keeps the tie-break identical by construction (it is literally
    /// <see cref="Order"/>) instead of by inspection.
    /// </para>
    ///
    /// <para>
    /// COMPUTED ON READ, no table and no migration: the candidate maps are pruned to the ones the
    /// user has a board-eligible play on (they cannot hold #1 anywhere else), and each map's top row
    /// is an index-order read of <c>ix_scores_leaderboard (beatmap_id, ranked, total_score DESC)</c>,
    /// which 001_init.sql already created for the boards themselves.
    /// </para>
    /// </summary>
    public static readonly string FirstPlacesOfUserSql =
        $"""
         SELECT board.id, board.beatmap_id, board.user_id, board.rank, board.completion, board.accuracy,
                board.total_score, board.max_combo, board.passed, board.ranked, board.pp,
                board.ended_at, board.mods, board.statistics, board.maximum_statistics, board.has_replay
         FROM (
             SELECT DISTINCT ON (sc.beatmap_id)
                    sc.id, sc.beatmap_id, sc.user_id, sc.rank, sc.completion, sc.accuracy,
                    sc.total_score, sc.max_combo, sc.passed, sc.ranked, sc.pp,
                    sc.ended_at, sc.mods::text AS mods,
                    sc.statistics::text AS statistics, sc.maximum_statistics::text AS maximum_statistics,
                    sc.replay_key IS NOT NULL AS has_replay
             FROM scores sc
             JOIN beatmaps bm ON bm.id = sc.beatmap_id
             JOIN beatmapsets bset ON bset.id = bm.set_id
             WHERE {OnBoard("sc", "true")}
               AND bset.status = 'ranked'
               AND sc.beatmap_id IN (
                   SELECT mine.beatmap_id FROM scores mine
                   WHERE mine.user_id = @id AND {OnBoard("mine", "true")}
               )
             ORDER BY sc.beatmap_id, {Order("sc")}
         ) board
         WHERE board.user_id = @id
         """;
}
