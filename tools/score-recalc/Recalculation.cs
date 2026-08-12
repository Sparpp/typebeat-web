using System.Runtime.Serialization;
using Newtonsoft.Json;
using Typebeat.Web;
using Typebeat.Web.Scoring;
using PerformancePoints = Typebeat.Web.Scoring.PerformancePoints;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat.Scoring;
using typebeat.Game.Scoring;

namespace Typebeat.Tools.ScoreRecalc;

/// <summary>
/// One stored score, as far as recalculation is concerned. Everything here is read, nothing is
/// derived: the whole point is to have the values the row currently holds to compare against.
/// </summary>
public sealed record StoredScore(
    long ScoreId,
    long BeatmapId,
    long SetId,
    long TotalScore,
    double Accuracy,
    double Completion,
    int MaxCombo,
    string Rank,
    bool Passed,
    bool Ranked,
    bool HasReplay,
    string StatisticsJson,
    string MaximumStatisticsJson,
    string ModsJson,
    double Pp,
    bool PpKnown,
    double BaseStars,
    double? SrDt,
    double? SrHt);

/// <summary>Why a score could not be recalculated. Every one of these is reported, never hidden.</summary>
public enum SkipReason
{
    None,

    /// <summary>The row has no stored replay, so there is nothing to re-derive from.</summary>
    NoReplay,

    /// <summary>The stored bytes did not decode as a type!beat replay.</summary>
    UndecodableReplay,

    /// <summary>The .osu the run was judged against is not in any package the tool could fetch.</summary>
    BeatmapUnavailable,

    /// <summary>
    /// The .osr holds no typing frames at all, so the run it describes was never recorded. Scores
    /// from before replay recording worked carry one of these: the file exists (and the row reads
    /// as having a replay) but there is nothing in it to re-derive from.
    /// </summary>
    EmptyReplay,

    /// <summary>
    /// A FAILED run. Its replay ends where the run ended, and backlog 109 moved when a typo costs
    /// health, so where the same run would have ended under the new rule is not derivable from a
    /// recording of the old one. Left exactly as stored.
    /// </summary>
    FailedRun,

    /// <summary>
    /// The old-rule re-derivation did not reproduce the stored statistics, so the harness and this
    /// row disagree about the run. Nothing is written for it.
    /// </summary>
    NotReproducible,
}

/// <summary>The full before/after for one score.</summary>
public sealed record RecalcResult(
    StoredScore Stored,
    SkipReason Skip,
    string? Detail,
    IReadOnlyDictionary<string, int>? OldRuleStatistics,
    int OldRuleMaxCombo,
    long OldRuleTotalScore,
    double? OldRulePp,
    double StoredScoreMultiplier,
    IReadOnlyDictionary<string, int>? NewStatistics,
    IReadOnlyDictionary<string, int>? NewMaximumStatistics,
    int NewMaxCombo,
    long NewTotalScore,
    double NewAccuracy,
    double NewCompletion,
    string? NewRank,
    bool NewStatisticsValid,
    bool NewTotalWithinBounds,
    bool NewRanked,
    double? NewPp,
    bool PpSettled)
{
    public bool Recalculated => Skip == SkipReason.None;

    public bool Moves =>
        Recalculated
        && (!SameCounts(Stored.StatisticsJson, NewStatistics)
            || Stored.MaxCombo != NewMaxCombo
            || Stored.TotalScore != NewTotalScore
            || Stored.Rank != NewRank
            || Math.Abs(Stored.Accuracy - NewAccuracy) > 1e-9
            || Math.Abs(Stored.Completion - NewCompletion) > 1e-9
            || Stored.Ranked != NewRanked
            || (Stored.PpKnown && NewPp is double pp && Math.Abs(Stored.Pp - pp) > 1e-9));

    /// <summary>
    /// The pp swing this RULE CHANGE is responsible for: the same formula run over the old-rule
    /// statistics and the new ones. Distinct from <c>stored.Pp -> NewPp</c>, which also carries any
    /// drift the stored value already had (a pp version bump the backfill has not reached, a star
    /// rating that moved). Null when the play could not be priced at all.
    /// </summary>
    public double? PpDeltaFromTheRuleChange => OldRulePp is double before && NewPp is double after ? after - before : null;

    private static bool SameCounts(string storedJson, IReadOnlyDictionary<string, int>? fresh)
    {
        var stored = WireCounts.Parse(storedJson);

        if (fresh == null)
            return false;

        foreach (var (key, count) in stored)
        {
            if (count != 0 && fresh.GetValueOrDefault(key) != count)
                return false;
        }

        foreach (var (key, count) in fresh)
        {
            if (count != 0 && stored.GetValueOrDefault(key) != count)
                return false;
        }

        return true;
    }
}

/// <summary>
/// The recalculation itself: replay the run twice, prove the old numbers, then value the new ones
/// with the server's own contract.
///
/// <para>Nothing here reimplements a judgement rule or a scoring formula. The statistics come from
/// <see cref="TypeBeatReplayScorer"/> (the game's engine driven into the game's score processor),
/// and everything derived from them comes from <see cref="ScoringContract"/> and
/// <see cref="PerformancePoints"/>, i.e. from exactly the code that priced the row in the first
/// place.</para>
/// </summary>
public static class Recalculation
{
    /// <summary>
    /// The <c>statistics</c> key wrong keypresses are counted under (backlog 72). A row that
    /// predates the stat has NO key at all, which is not the same as a zero: see
    /// <c>TypeBeatScoreProcessor.MistypesOf</c>, whose whole point is that an absent count must
    /// read as "unknown" rather than as a flawless run.
    /// </summary>
    private const string mistype_key = "combo_break";

    /// <summary>
    /// The combo-restore era EVERY stored row belongs to (backlog 140). Correcting a typo now
    /// resumes the streak its wrong keypress broke, but no score in the database was played that
    /// way: re-deriving one under <see cref="ComboRestoreRule.OnFix"/> would hand it combo its
    /// fingers never earned, and price every cell after the fix at a streak it never held.
    ///
    /// <para>So BOTH re-derivations below pin it to <see cref="ComboRestoreRule.Never"/>, including
    /// the one labelled "the rule the client uses now", which varies the TYPO rule alone. This
    /// sweep exists to move one axis and prove it; letting a second one move underneath it would
    /// make every number it reports impossible to attribute. If a sweep is ever wanted FOR backlog
    /// 140, it is a different sweep, with its own reproduction proof, and it starts here.</para>
    /// </summary>
    private const ComboRestoreRule combo_restore_rule = ComboRestoreRule.Never;

    /// <param name="backfillMistypes">
    /// Whether to introduce a mistype count into rows that predate the stat. Off by default, and
    /// deliberately: those rows were played by a client that never counted wrong keypresses, so
    /// writing one is BACKFILLING BACKLOG 72's stat, a different change with its own pp
    /// consequences (PerformancePoints prices mistypes), and folding it into this sweep would make
    /// the result impossible to audit. The reproduction check ignores the key for those rows either
    /// way, since its absence is an era marker, not a disagreement.
    /// </param>
    public static RecalcResult Run(StoredScore stored, ReplayArchive.DecodedReplay? decoded, bool backfillMistypes = false)
    {
        if (!stored.HasReplay)
            return Skipped(stored, SkipReason.NoReplay, null);

        if (decoded is null)
            return Skipped(stored, SkipReason.UndecodableReplay, null);

        if (decoded.Score is not Score score || decoded.Playable is not IBeatmap playable)
            return Skipped(stored, SkipReason.BeatmapUnavailable, decoded.MissingBeatmapHash);

        if (!stored.Passed)
            return Skipped(stored, SkipReason.FailedRun, null);

        if (score.Replay.Frames.Count == 0)
            return Skipped(stored, SkipReason.EmptyReplay, null);

        var mods = score.ScoreInfo.Mods;

        // A row from before the mistype stat existed. Its absence is an era marker, so it is not a
        // reproduction failure, and (unless asked) not something this sweep introduces either.
        bool preMistypeEra = !WireCounts.Parse(stored.StatisticsJson).ContainsKey(mistype_key);

        // 1. The proof. Re-derive under the rule the row was PRICED under and require the stored
        //    statistics back, exactly. A harness that cannot reproduce the old numbers has no
        //    business writing new ones.
        var oldRule = TypeBeatReplayScorer.Score(playable, mods, score.Replay, TypoRule.ImmediateMiss, combo_restore_rule);
        var oldStatistics = WireCounts.From(oldRule.Statistics);

        string mismatch = ReproductionMismatch(stored, oldRule, oldStatistics, preMistypeEra);

        if (mismatch.Length > 0)
        {
            return Skipped(stored, SkipReason.NotReproducible, mismatch) with
            {
                OldRuleStatistics = oldStatistics,
                OldRuleMaxCombo = oldRule.MaxCombo,
                OldRuleTotalScore = oldRule.TotalScore,
            };
        }

        // The multiplier THIS ROW was priced with, recovered rather than recomputed. The mod
        // multipliers have been retuned since some of these plays (Flashlight went 1.2x -> 1.05x,
        // the rate curve moved), and applying today's would move total_score by up to 2x for a
        // reason that has nothing to do with the typo rule. The base (pre-multiplier) score is what
        // backlog 109 actually moves, so the row's own multiplier is carried across it.
        double storedMultiplier = oldRule.TotalScoreWithoutMods > 0
            ? (double)stored.TotalScore / oldRule.TotalScoreWithoutMods
            : 1;

        // 2. The same run under the TYPO rule the client uses now. The COMBO-RESTORE rule stays at
        //    Never on both sides of the comparison (see combo_restore_rule): this sweep moves one
        //    axis at a time, and that one is not it.
        var newRule = TypeBeatReplayScorer.Score(playable, mods, score.Replay, TypoRule.Deferred, combo_restore_rule);
        var statistics = WireCounts.From(newRule.Statistics);
        var maximumStatistics = WireCounts.From(newRule.MaximumStatistics);

        if (preMistypeEra && !backfillMistypes)
            statistics.Remove(mistype_key);

        // 3. Everything the server derives from the statistics, through the server's own contract,
        //    following ScoreEndpoints.SubmitScore step for step.
        var recomputed = ScoringContract.Recompute(statistics, maximumStatistics, newRule.MaxCombo);

        double modMultiplier = ModMultiplier.MaxForStack(ScoreMods.Parse(stored.ModsJson).Select(m => ((string?)m.Acronym, m.Rate)));
        long modCeiling = ModMultiplier.TotalScoreCeiling(newRule.TotalScoreWithoutMods, modMultiplier);

        bool withinBounds = ScoringContract.TotalScoreWithinBounds(newRule.TotalScoreWithoutMods, recomputed)
                            && newRule.TotalScore >= 0
                            && newRule.TotalScore <= modCeiling;

        bool fullyJudged = recomputed.AccuracyProgress >= 1;
        double accuracy = fullyJudged ? recomputed.Accuracy : recomputed.JudgedAccuracy;
        // Base score times the multiplier this row was actually priced with (see above), which is
        // exactly newRule.TotalScore whenever the multiplier has not been retuned since.
        long multiplied = (long)Math.Round(newRule.TotalScoreWithoutMods * storedMultiplier, MidpointRounding.AwayFromZero);
        long totalScore = withinBounds ? multiplied : recomputed.TotalScoreCeiling;
        int maxCombo = Math.Clamp(newRule.MaxCombo, 0, recomputed.TheoreticalMaxCombo);
        string rank = recomputed.Rank; // passed runs only reach here; a fail keeps its stored F.

        // A row can only LOSE its ranked flag here. The other gates (play time, build, set status at
        // submission) are not re-derivable from a replay, so a row that was unranked for one of them
        // must not be handed its rank back by this tool; a row whose statistics stop being valid,
        // or whose total stops being justifiable, must lose it.
        bool ranked = stored.Ranked && recomputed.StatisticsValid && withinBounds && fullyJudged;

        // What the OLD statistics are worth under today's formula: the baseline the rule change is
        // measured against, independent of any drift the stored pp already carried.
        var oldRecomputed = ScoringContract.Recompute(oldStatistics, WireCounts.From(oldRule.MaximumStatistics), oldRule.MaxCombo);
        double oldAccuracy = oldRecomputed.AccuracyProgress >= 1 ? oldRecomputed.Accuracy : oldRecomputed.JudgedAccuracy;

        var (oldPp, _) = PerformancePoints.ForScore(
            stored.Ranked,
            ScoreMods.Parse(stored.ModsJson),
            PerformancePoints.CountNotes(preMistypeEra && !backfillMistypes ? WithoutMistypes(oldStatistics) : oldStatistics),
            oldAccuracy,
            oldRule.MaxCombo,
            stored.BaseStars,
            stored.SrDt,
            stored.SrHt);

        var (pp, ppSettled) = PerformancePoints.ForScore(
            ranked,
            ScoreMods.Parse(stored.ModsJson),
            PerformancePoints.CountNotes(statistics),
            accuracy,
            maxCombo,
            stored.BaseStars,
            stored.SrDt,
            stored.SrHt);

        return new RecalcResult(
            stored,
            SkipReason.None,
            null,
            oldStatistics,
            oldRule.MaxCombo,
            oldRule.TotalScore,
            oldPp,
            storedMultiplier,
            statistics,
            maximumStatistics,
            maxCombo,
            totalScore,
            accuracy,
            recomputed.Completion,
            rank,
            recomputed.StatisticsValid,
            withinBounds,
            ranked,
            pp,
            ppSettled);
    }

    /// <summary>
    /// Empty when the old-rule re-derivation matched the stored row; otherwise a description of
    /// every field that did not.
    ///
    /// <para>The comparison is against <c>statistics</c> and <c>max_combo</c>, the two quantities
    /// the server stores verbatim from the client. <c>total_score</c> is deliberately NOT required
    /// to match: the server clamps it (<c>ScoreEndpoints</c> stores the ceiling when a submission
    /// is out of bounds) and the mod multiplier is applied client-side, so a mismatch there is not
    /// evidence about the judgement. It is reported instead.</para>
    /// </summary>
    private static string ReproductionMismatch(StoredScore stored, TypeBeatReplayAccount oldRule, IReadOnlyDictionary<string, int> oldStatistics, bool preMistypeEra)
    {
        var problems = new List<string>();

        if (oldRule.UnconsumedFrames > 0)
            problems.Add($"{oldRule.UnconsumedFrames} replay frame(s) the engine never consumed");

        var storedStatistics = WireCounts.Parse(stored.StatisticsJson);

        foreach (string key in storedStatistics.Keys.Union(oldStatistics.Keys).OrderBy(k => k, StringComparer.Ordinal))
        {
            // A pre-backlog-72 row carries no mistype key, and the harness (which is today's code)
            // always counts them. That is an era difference, not a judgement difference: every
            // other count still has to match exactly.
            if (preMistypeEra && key == mistype_key)
                continue;

            int was = storedStatistics.GetValueOrDefault(key);
            int now = oldStatistics.GetValueOrDefault(key);

            if (was != now)
                problems.Add($"{key} {was} -> {now}");
        }

        if (stored.MaxCombo != oldRule.MaxCombo)
            problems.Add($"max_combo {stored.MaxCombo} -> {oldRule.MaxCombo}");

        return string.Join(", ", problems);
    }

    private static Dictionary<string, int> WithoutMistypes(IReadOnlyDictionary<string, int> counts)
    {
        var copy = new Dictionary<string, int>(counts);
        copy.Remove(mistype_key);
        return copy;
    }

    private static RecalcResult Skipped(StoredScore stored, SkipReason reason, string? detail) =>
        new(stored, reason, detail, null, 0, 0, null, 1, null, null, 0, 0, 0, 0, null, false, false, stored.Ranked, null, false);
}

/// <summary>
/// The bridge between the client's <see cref="HitResult"/> enum and the snake_case keys the
/// <c>statistics</c> jsonb and <see cref="ScoringContract"/> speak.
///
/// <para>The names are read off the enum's own <see cref="EnumMemberAttribute"/>, which is what the
/// client's Newtonsoft <c>StringEnumConverter</c> serializes with, so this cannot drift from the
/// wire even if a result is renamed or added.</para>
/// </summary>
public static class WireCounts
{
    private static readonly Dictionary<HitResult, string> names = BuildNames();

    private static Dictionary<HitResult, string> BuildNames()
    {
        var map = new Dictionary<HitResult, string>();

        foreach (var value in Enum.GetValues<HitResult>())
        {
            var member = typeof(HitResult).GetField(value.ToString());
            var attribute = member?.GetCustomAttributes(typeof(EnumMemberAttribute), false).OfType<EnumMemberAttribute>().FirstOrDefault();

            map[value] = attribute?.Value ?? value.ToString().ToLowerInvariant();
        }

        return map;
    }

    public static string Key(HitResult result) => names[result];

    /// <summary>Zero counts are dropped, exactly as the client drops them before submitting.</summary>
    public static Dictionary<string, int> From(IReadOnlyDictionary<HitResult, int> counts)
    {
        var wire = new Dictionary<string, int>();

        foreach (var (result, count) in counts)
        {
            if (count != 0)
                wire[Key(result)] = count;
        }

        return wire;
    }

    public static Dictionary<string, int> Parse(string? json)
        => JsonConvert.DeserializeObject<Dictionary<string, int>>(json ?? "{}") ?? new Dictionary<string, int>();

    public static string Serialize(IReadOnlyDictionary<string, int> counts)
        => JsonConvert.SerializeObject(counts);
}
