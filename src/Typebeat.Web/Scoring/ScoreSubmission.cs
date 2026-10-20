using System.Data.Common;
using Dapper;
using Newtonsoft.Json;

namespace Typebeat.Web.Scoring;

/// <summary>
/// The pieces of score submission the two routes share: the game's
/// <c>ScoreEndpoints.SubmitScore</c> and the browser's <c>PlayEndpoints.SubmitScoreAsync</c>.
///
/// <para>
/// THE PIPELINE STILL EXISTS TWICE. Each route keeps its own token lock, recompute,
/// gate, pp call, INSERT and stats upsert. What lives here is every piece that was a private copy
/// in both, or that an integrity check needs to be the same on both: the ranked verdict of a
/// submitted acronym, the claimed maximum held against the map, the <c>user_stats.hit_counts</c>
/// merge, and the caller's standing on the board. A check added to submission belongs here, so it cannot land on one route only.
/// </para>
///
/// <para>
/// The statistics allowlist itself is <see cref="ScoringContract.KnownKeysOnly"/>, next to the
/// classifiers it mirrors; both routes reach it through <see cref="KnownStatistics"/> before they
/// read either dictionary.
/// </para>
/// </summary>
public static class ScoreSubmission
{
    /// <summary>
    /// Whether one submitted acronym lets its play rank; the server-side mirror of the client's
    /// per-mod <c>Ranked</c>. The caller ANDs it across the stack (<see cref="StackRanked"/>).
    ///
    /// <list type="bullet">
    /// <item>A null or blank acronym is ranked: it names no mod, and the stored stack drops it.</item>
    /// <item>An acronym <see cref="ModMultiplier"/> does not price is UNRANKED. It used to
    /// rank at the 2.0x <see cref="ModMultiplier.UNKNOWN_MOD_MULTIPLIER"/>, which is above every
    /// honest stack, so one invented acronym on a perfect run boarded at 2,000,000.</item>
    /// <item>An always-unranked mod (<see cref="UnrankedMods"/>) is unranked.</item>
    /// </list>
    ///
    /// <para>
    /// The rate mods (DT/NC/HT/DC) are ranked at EVERY speed, not just their default: the client pays
    /// them on a continuous curve (<see cref="RateMultiplier"/>), so an odd rate is priced, not
    /// banned.
    /// </para>
    /// </summary>
    public static bool ModRanked(string? acronym)
    {
        if (string.IsNullOrWhiteSpace(acronym))
            return true;

        return ModMultiplier.IsKnown(acronym) && !UnrankedMods.IsAlwaysUnranked(acronym);
    }

    /// <summary>Whether every member of this stack lets its play rank (<see cref="ModRanked"/>).</summary>
    public static bool StackRanked(IEnumerable<string?> acronyms) => acronyms.All(ModRanked);

    /// <summary>
    /// The client's two dictionaries as both routes read, store and echo them: only
    /// <c>HitResult</c> names survive (<see cref="ScoringContract.KnownKeysOnly"/>), and a bonus count
    /// is held to the same key's count in the surviving maximum
    /// (<see cref="ScoringContract.BonusWithinMaximum"/>). A type!beat map has no bonus judgement, and
    /// the maximum is the client's own claim, so a bonus key in the maximum is dropped too: otherwise a
    /// client could lift both sides together. <c>DroppedKeys</c> is how many keys the two lost between
    /// them, for the caller's log line.
    /// </summary>
    public static (Dictionary<string, int> Statistics, Dictionary<string, int> MaximumStatistics, int DroppedKeys) KnownStatistics(
        IReadOnlyDictionary<string, int>? statistics, IReadOnlyDictionary<string, int>? maximumStatistics)
    {
        var maximum = ScoringContract.KnownKeysOnly(maximumStatistics)
                                     .Where(kv => !ScoringContract.IsBonus(kv.Key))
                                     .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        var known = ScoringContract.BonusWithinMaximum(ScoringContract.KnownKeysOnly(statistics), maximum);

        int dropped = (statistics?.Count ?? 0) - known.Count + (maximumStatistics?.Count ?? 0) - maximum.Count;

        return (known, maximum, dropped);
    }

    /// <summary>
    /// Sums the per-key hit counts of <paramref name="add"/> into the stored <c>hit_counts</c> JSON.
    /// Zero and negative (tamper) counts are ignored, and so is any key that is not a
    /// <c>HitResult</c> name (the callers have already dropped those; this keeps the merge
    /// safe on its own). The sum is taken in <c>long</c> and held at <see cref="int.MaxValue"/>, where
    /// it used to wrap negative, so the column stays readable as the <c>int</c> map it has always been.
    /// </summary>
    public static string MergeHitCounts(string existingJson, IReadOnlyDictionary<string, int> add)
    {
        var merged = JsonConvert.DeserializeObject<Dictionary<string, long>>(existingJson) ?? new Dictionary<string, long>();

        foreach (var (key, count) in add)
        {
            if (count <= 0 || !ScoringContract.IsHitResultKey(key))
                continue;

            merged[key] = Math.Min(merged.GetValueOrDefault(key) + count, int.MaxValue);
        }

        return JsonConvert.SerializeObject(merged);
    }

    /// <summary>
    /// The prefix of every build a BROWSER play is registered under: <c>web-player</c>, or
    /// <c>web-</c> plus the page's script revision (<c>PlayEndpoints</c>). A desktop build is 32 hex
    /// digits, so the prefix alone tells the two clients apart on a stored row.
    /// </summary>
    public const string BROWSER_BUILD_PREFIX = "web-";

    /// <summary>
    /// Whether a stored play came from the browser player, read off its build's
    /// <c>version_hash</c>. A browser play is stored unranked until the browser sends a keypress
    /// log the server can audit, so nothing may rank one later either
    /// (<see cref="GateRefund.Qualifies"/>).
    /// </summary>
    public static bool IsBrowserBuild(string? versionHash)
        => versionHash != null && versionHash.StartsWith(BROWSER_BUILD_PREFIX, StringComparison.Ordinal);

    /// <summary>
    /// The error both routes answer a refused <see cref="MaximumMatchesMap"/> with (422, as the other
    /// submit refusals). Both clients print a submit error's text to the player.
    /// </summary>
    public const string MAP_MISMATCH_REFUSAL = "maximum_statistics does not match the beatmap";

    /// <summary>
    /// Whether a submission's claimed maximum agrees with the map it was played on: its
    /// accuracy-affecting count (<see cref="ScoringContract.CountAccuracyAffecting"/>, one Great per
    /// judged cell on an honest client) must equal the cells the server counted for that map and
    /// stream (<see cref="MapJudgedCellsAsync"/>). The maximum used to be the client's word alone,
    /// so a run cut short and claimed as a ten-cell map recomputed as a complete X.
    ///
    /// <para>A null count (a row the backfill has not reached, or a token issued for another
    /// version of the map) is accepted unchecked, which is how every submission was treated before
    /// the count existed.</para>
    /// </summary>
    public static bool MaximumMatchesMap(IReadOnlyDictionary<string, int>? maximumStatistics, int? mapJudgedCells)
        => mapJudgedCells is not int cells || ScoringContract.CountAccuracyAffecting(maximumStatistics) == cells;

    /// <summary>
    /// The cells the map behind <paramref name="tokenId"/> judges on the default or the Literate
    /// stream (048_judged_cells.sql), or null when that is not known for the version played: the
    /// pair is not counted yet, was counted under another <see cref="Packages.Lyrics.JudgedCells.VERSION"/>,
    /// or the map was re-uploaded after the token was issued (the token's <c>beatmap_hash</c> is not
    /// the stored checksum any more, so the stored count describes a map this run never saw).
    /// </summary>
    /// <summary>
    /// The Polyglot twin of <see cref="MaximumMatchesMap"/> (PR 43): whether a Polyglot play's claimed
    /// maximum is one the map's Polyglot stream (<see cref="Packages.Lyrics.PolyglotSummary"/>) can
    /// produce. On a Latin or Cyrillic map every scoring object is one judgement, so the claim must be
    /// exactly <see cref="Packages.Lyrics.PolyglotSummary.Stream.Cells"/>. A JAPANESE character is
    /// judged once per romaji input and its maximum follows the spelling the player typed (the
    /// game's <c>TypeBeatScoreProcessor.adjustJapaneseMaximum</c>), so the claim must lie between
    /// one judgement per object and the longest accepted spelling
    /// (<see cref="Packages.Lyrics.PolyglotSummary.Stream.MaximumInputs"/>). A map whose Polyglot
    /// summary is not stored yet (a matrix the sweep has not rewritten) is accepted unchecked, as an
    /// unknown judged-cell count is.
    /// </summary>
    public static bool PolyglotMaximumMatchesMap(IReadOnlyDictionary<string, int>? maximumStatistics, Packages.Lyrics.PolyglotSummary? summary, bool literate)
    {
        if (summary is null)
            return true;

        var stream = summary.For(literate);
        int claimed = ScoringContract.CountAccuracyAffecting(maximumStatistics);

        return summary.Japanese
            ? claimed >= stream.Cells && claimed <= stream.MaximumInputs
            : claimed == stream.Cells;
    }

    public static async Task<int?> MapJudgedCellsAsync(DbConnection conn, DbTransaction? tx, long tokenId, bool literate)
        => await conn.ExecuteScalarAsync<int?>(
            """
            SELECT CASE WHEN @literate THEN b.judged_cells_literate ELSE b.judged_cells END
            FROM score_tokens t
            JOIN beatmaps b ON b.id = t.beatmap_id
            WHERE t.id = @tokenId
              AND b.judged_cells_version = @version
              AND lower(b.checksum_md5) = lower(t.beatmap_hash)
            """,
            new { tokenId, literate, version = Packages.Lyrics.JudgedCells.VERSION }, tx);

    /// <summary>
    /// The user's best row on one board of a map (the ranked board when <paramref name="wantRanked"/>,
    /// the unranked one otherwise) and its 1-based position among every player's best there, or
    /// null when they have no row on it. Built entirely from the <see cref="BeatmapLeaderboard"/>
    /// fragments, so eligibility, the per-player fold and the tie-break are the board's own rules
    /// rather than a copy of them.
    /// </summary>
    public static async Task<(long BestScoreId, int Position)?> UserStandingAsync(
        DbConnection conn, DbTransaction? tx, long beatmapId, long userId, bool wantRanked)
    {
        var best = await conn.QuerySingleOrDefaultAsync<BestScoreRow>(
            $"""
            SELECT s.id AS id, s.total_score AS totalScore
            FROM scores s
            WHERE s.beatmap_id = @beatmapId AND s.user_id = @userId AND {BeatmapLeaderboard.OnBoard("s")}
            ORDER BY {BeatmapLeaderboard.Order("s")}
            LIMIT 1
            """,
            new { beatmapId, userId, wantRanked }, tx);

        if (best is null)
            return null;

        int position = await conn.ExecuteScalarAsync<int>(
            $"""
            SELECT 1 + COUNT(*)
            FROM (
                SELECT DISTINCT ON (s.user_id) s.user_id, s.total_score, s.id
                FROM scores s
                WHERE s.beatmap_id = @beatmapId AND {BeatmapLeaderboard.OnBoard("s")}
                ORDER BY s.user_id, {BeatmapLeaderboard.Order("s")}
            ) b
            WHERE {BeatmapLeaderboard.Outranks("b")}
            """,
            new { beatmapId, totalScore = best.TotalScore, scoreId = best.Id, wantRanked }, tx);

        return (best.Id, position);
    }

    private sealed record BestScoreRow(long Id, long TotalScore);
}
