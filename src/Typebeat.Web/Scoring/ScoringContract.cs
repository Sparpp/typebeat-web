namespace Typebeat.Web.Scoring;

/// <summary>
/// Server-side recompute + tamper-bounds for a submitted solo score.
///
/// The client computes score/accuracy/combo with the standardised <c>ScoreProcessor</c> maths
/// (typebeat's <c>TypeBeatScoreProcessor</c> subclass overrides ONLY the rank derivation, nothing
/// numeric). What it actually submits over the wire is <c>ScoreInfo.TotalScore</c> (the
/// processor's <c>TotalScore</c> value) plus the per-result <c>statistics</c> /
/// <c>maximum_statistics</c> dictionaries and <c>max_combo</c>.
///
/// What we can reproduce EXACTLY server-side:
///  - <b>accuracy</b>. The client accuracy at end of play is
///    <c>currentBaseScore / currentMaximumBaseScore</c>
///    (ScoreProcessor.cs:393) where each term is the numeric base value of a hit result
///    (ScoreProcessor.GetBaseScoreForResult, ScoreProcessor.cs:346-381) summed over the
///    accuracy-affecting judgements (HitResult.AffectsAccuracy, HitResult.cs:208-223).
///    numerator = Σ base(result)·count over <c>statistics</c>;
///    denominator = Σ base(maxResult)·count over <c>maximum_statistics</c>.
///    Both dictionaries are transmitted, so accuracy is exact — we OVERRIDE the submitted value.
///  - <b>completion</b> and <b>rank</b>. Completion = typed cells (accuracy-affecting hits in
///    <c>statistics</c>) over TOTAL map cells (accuracy-affecting counts in
///    <c>maximum_statistics</c>). Rank is graded on completion, NOT accuracy — typing every
///    character is an SS regardless of timing quality; only cells that scrolled past untyped
///    (misses) cost the grade. Mirrors the client's <c>TypeBeatScoreProcessor</c> — keep the
///    cutoffs in the two files in sync.
///  - <b>theoretical max combo</b>. Every combo-increasing judgement in <c>maximum_statistics</c>
///    (HitResult.IncreasesCombo = AffectsCombo &amp;&amp; IsHit, HitResult.cs:171-203). For a typing
///    map that is the note count. Submitted <c>max_combo</c> may not exceed it.
///
/// What we CANNOT reproduce exactly: the total score. ScoreProcessor.ComputeTotalScore
/// (ScoreProcessor.cs:427-432) is
///   500000·acc·comboProgress + 500000·acc^5·accuracyProgress + bonusPortion
/// and <c>comboProgress = currentComboPortion / maximumComboPortion</c> depends on the full combo
/// HISTORY (each combo-scoring judgement contributes base·combo^0.5, ScoreProcessor.cs:344), which
/// is NOT transmitted. So instead of recomputing the total we bound it: <c>comboProgress ≤ 1</c>,
/// the no-mod score multiplier is 1 (M1 forces <c>mods = []</c>), <c>accuracyProgress</c> and
/// <c>bonusPortion</c> are exact from the dictionaries — giving a provable ceiling. A submitted
/// total above the ceiling is impossible, so it is treated as out of bounds (caller unranks it).
///
/// Everything here is a pure function of the three transmitted quantities — no DB, no throwing on
/// hostile input (score-submit must never 500 for tamper-shaped data).
/// </summary>
public static class ScoringContract
{
    // ScoreProcessor.ComputeTotalScore split (ScoreProcessor.cs:429-431).
    private const double combo_portion_max = 500_000;
    private const double accuracy_portion_max = 500_000;
    private const double accuracy_exponent = 5; // Math.Pow(Accuracy.Value, 5), ScoreProcessor.cs:430

    // Completion → rank cutoffs (TypeBeatScoreProcessor.COMPLETION_CUTOFF_*; same band shape the
    // base game used for accuracy, but graded on the fraction of cells typed).
    private const double cutoff_x = 1;
    private const double cutoff_s = 0.95;
    private const double cutoff_a = 0.9;
    private const double cutoff_b = 0.8;
    private const double cutoff_c = 0.7;

    private static readonly IReadOnlyDictionary<string, int> empty = new Dictionary<string, int>();

    /// <summary>
    /// The outcome of a recompute. <see cref="Accuracy"/> and <see cref="Rank"/> are authoritative
    /// (they replace whatever the client submitted). <see cref="TotalScoreCeiling"/> is the maximum
    /// total score the transmitted statistics could possibly justify; check the submitted total
    /// against it with <see cref="TotalScoreWithinBounds"/>. <see cref="StatisticsValid"/> is false
    /// when a hard invariant is broken (accuracy would exceed 1, more judgements than the map has,
    /// max_combo beyond the theoretical maximum, negative counts, or an empty maximum) — such a
    /// score must never be ranked.
    ///
    /// <see cref="Accuracy"/> is the WHOLE-MAP accuracy (denominator over
    /// <c>maximum_statistics</c>) — the client's final accuracy for a completed play.
    /// <see cref="JudgedAccuracy"/> uses the judged-only denominator (Σ maxBase over
    /// <c>statistics</c>), which is the client's RUNNING accuracy (ScoreProcessor.cs:261,393) —
    /// what a FAILED play's submitted total was computed from. For a completed play the two are
    /// equal (every cell judged); they diverge only on fails, where the whole-map value would
    /// falsely flag honest submissions as out of bounds.
    ///
    /// <see cref="Completion"/> is whole-map: typed cells over the map's total cell count, so a
    /// failed run reads as "typed 43% of the map". <see cref="Rank"/> is graded on it.
    /// </summary>
    public readonly record struct Recomputed(
        double Accuracy,
        double JudgedAccuracy,
        double Completion,
        long TotalScoreCeiling,
        string Rank,
        bool StatisticsValid,
        int TheoreticalMaxCombo,
        double AccuracyProgress);

    /// <summary>
    /// Recomputes accuracy/rank and derives the total-score ceiling from the transmitted
    /// dictionaries. Never throws: unknown result keys contribute nothing, and degenerate input
    /// (null/empty maximum, negative counts) yields <c>StatisticsValid == false</c> rather than an
    /// exception.
    /// </summary>
    public static Recomputed Recompute(
        IReadOnlyDictionary<string, int>? statistics,
        IReadOnlyDictionary<string, int>? maximumStatistics,
        int maxCombo)
    {
        statistics ??= empty;
        maximumStatistics ??= empty;

        bool valid = true;

        long numerator = 0;         // currentBaseScore (ScoreProcessor.cs:266)
        long judgedDenominator = 0; // currentMaximumBaseScore at end of play (ScoreProcessor.cs:261) — judged cells only
        long denominator = 0;       // whole-map maximum base score (ScoreProcessor.cs:450)
        int accuracyJudged = 0;
        int accuracyMax = 0;
        int typedCells = 0; // accuracy-affecting HITS — the completion numerator
        long bonusPortion = 0; // Σ base(result) over bonus hits (GetBonusScoreChange, ScoreProcessor.cs:338)

        foreach (var (key, count) in statistics)
        {
            if (count < 0)
            {
                valid = false;
                continue;
            }

            if (AffectsAccuracy(key))
            {
                numerator += (long)BaseScore(key) * count;
                judgedDenominator += (long)MaxBaseScore(key) * count;
                accuracyJudged += count;

                if (IsHit(key))
                    typedCells += count;
            }

            if (IsBonus(key))
                bonusPortion += (long)BaseScore(key) * count;
        }

        int theoreticalMaxCombo = 0;

        foreach (var (key, count) in maximumStatistics)
        {
            if (count < 0)
            {
                valid = false;
                continue;
            }

            if (AffectsAccuracy(key))
            {
                denominator += (long)BaseScore(key) * count;
                accuracyMax += count;
            }

            if (IncreasesCombo(key))
                theoreticalMaxCombo += count;
        }

        // Hard invariants. Any breach means the score cannot be trusted for ranking.
        if (denominator <= 0)
            valid = false;
        if (numerator > denominator) // accuracy would exceed 1 → statistics not ⊆ maximum_statistics
            valid = false;
        if (accuracyJudged > accuracyMax) // more accuracy-affecting judgements than the map contains
            valid = false;
        if (maxCombo < 0 || maxCombo > theoreticalMaxCombo)
            valid = false;

        double rawAccuracy = denominator > 0 ? (double)numerator / denominator : 0;
        double accuracy = Math.Clamp(rawAccuracy, 0, 1);
        double judgedAccuracy = judgedDenominator > 0 ? Math.Clamp((double)numerator / judgedDenominator, 0, 1) : 0;
        double accuracyProgress = accuracyMax > 0
            ? (double)Math.Min(accuracyJudged, accuracyMax) / accuracyMax
            : 0;

        // Whole-map completion: cells typed over the map's total cell count. Clamped for the same
        // reason accuracy is (statistics ⊄ maximum_statistics is tamper-shaped, not a 500).
        double completion = accuracyMax > 0
            ? Math.Clamp((double)typedCells / accuracyMax, 0, 1)
            : 0;

        // Provable ceiling: comboProgress at its maximum (1), no-mod multiplier 1. Uses the
        // JUDGED accuracy — the value the client actually baked into its total (running accuracy
        // at end of play; equals whole-map accuracy for completed plays). judgedAccuracy >=
        // accuracy always (same numerator, smaller denominator), so this ceiling is valid for
        // passed AND failed plays; using the whole-map value here falsely flagged honest fails.
        double raw = combo_portion_max * judgedAccuracy * 1.0
                     + accuracy_portion_max * Math.Pow(judgedAccuracy, accuracy_exponent) * accuracyProgress
                     + bonusPortion;

        // Degenerate maximums (no accuracy-affecting cells at all) describe no playable map —
        // nothing can justify any score, so the ceiling is 0 rather than whatever the judged
        // numerator alone would suggest. (StatisticsValid is already false in this case.)
        long ceiling = denominator <= 0 ? 0 : (long)Math.Round(raw, MidpointRounding.ToEven);

        return new Recomputed(accuracy, judgedAccuracy, completion, ceiling, RankFromCompletion(completion), valid, theoreticalMaxCombo, accuracyProgress);
    }

    /// <summary>
    /// Whether a submitted total score is inside the provable ceiling (and non-negative). A legit
    /// full-combo play hits the ceiling exactly (identical formula with comboProgress = 1); anything
    /// above it is unachievable.
    /// </summary>
    public static bool TotalScoreWithinBounds(long submittedTotalScore, in Recomputed recomputed)
        => submittedTotalScore >= 0 && submittedTotalScore <= recomputed.TotalScoreCeiling;

    /// <summary>
    /// Completion (0..1) → rank string, mirroring TypeBeatScoreProcessor.RankFromCompletion.
    /// Typing every cell is an X (SS) regardless of timing; only missed cells cost the grade.
    /// There are no mods, so the silver ranks (SH/XH) never apply. The strings match the client's
    /// <c>ScoreRank</c> enum names, which its <c>StringEnumConverter</c> parses on the wire.
    /// </summary>
    public static string RankFromCompletion(double completion)
    {
        if (completion >= cutoff_x) return "X";
        if (completion >= cutoff_s) return "S";
        if (completion >= cutoff_a) return "A";
        if (completion >= cutoff_b) return "B";
        if (completion >= cutoff_c) return "C";
        return "D";
    }

    // ---- HitResult numeric weights + classification ----
    // Keys are the EnumMember snake_case values (HitResult.cs). Values mirror
    // ScoreProcessor.GetBaseScoreForResult (ScoreProcessor.cs:346-381). For a typing map only
    // great/ok/meh/miss occur, but the full table keeps the contract faithful to the base ruleset.
    // Unknown keys fall through to base 0 / no classification, so they can never inflate a score.

    private static int BaseScore(string key) => key switch
    {
        "great" or "perfect" => 300,
        "good" => 200,
        "ok" => 100,
        "meh" => 50,
        "slider_tail_hit" => 150,
        "large_tick_hit" => 30,
        "small_tick_hit" => 10,
        "large_bonus" => 50,
        "small_bonus" => 10,
        _ => 0,
    };

    // Base score of the judgement's MaxResult for a judged cell of this key — what the client's
    // running currentMaximumBaseScore accrues per judgement (ScoreProcessor.cs:261). The
    // great-family results (great/perfect/good/ok/meh/miss) all belong to Great-max judgements
    // (every type!beat judgement is Great-max); tick/tail families max at their own hit result.
    private static int MaxBaseScore(string key) => key switch
    {
        "great" or "perfect" or "good" or "ok" or "meh" or "miss" => 300,
        "slider_tail_hit" => 150,
        "large_tick_hit" or "large_tick_miss" => 30,
        "small_tick_hit" or "small_tick_miss" => 10,
        _ => 0,
    };

    // HitResult.AffectsAccuracy (HitResult.cs:208-223): scorable and non-bonus, excluding
    // combo_break / legacy_combo_increase. Misses count (base 0) — they are accuracy-affecting.
    private static bool AffectsAccuracy(string key) => key switch
    {
        "great" or "perfect" or "good" or "ok" or "meh" or "miss"
            or "small_tick_hit" or "small_tick_miss"
            or "large_tick_hit" or "large_tick_miss"
            or "slider_tail_hit" => true,
        _ => false,
    };

    // HitResult.IsHit (HitResult.cs:308-…): a successful judgement. Among the accuracy-affecting
    // keys this is everything except the miss family — the completion numerator ("cells typed").
    private static bool IsHit(string key) => key switch
    {
        "great" or "perfect" or "good" or "ok" or "meh"
            or "small_tick_hit" or "large_tick_hit" or "slider_tail_hit" => true,
        _ => false,
    };

    // HitResult.IncreasesCombo = AffectsCombo && IsHit (HitResult.cs:171-203). Misses and
    // combo_break affect combo but are not hits, so they do not increase it.
    private static bool IncreasesCombo(string key) => key switch
    {
        "great" or "perfect" or "good" or "ok" or "meh"
            or "large_tick_hit" or "slider_tail_hit" => true,
        _ => false,
    };

    // HitResult.IsBonus (HitResult.cs:267-278).
    private static bool IsBonus(string key) => key is "small_bonus" or "large_bonus";
}
