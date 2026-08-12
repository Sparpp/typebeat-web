namespace Typebeat.Web.Scoring;

/// <summary>
/// Server-side recompute + tamper-bounds for a submitted solo score.
///
/// The client computes score/accuracy/combo with the standardised <c>ScoreProcessor</c> maths.
/// typebeat's <c>TypeBeatScoreProcessor</c> subclass changes only the rank derivation and TWO
/// numeric weights, the uncorrected-typo tier's base score and the <c>great</c> tier's (see below);
/// everything else is the base implementation. What it actually submits over the wire is
/// <c>ScoreInfo.TotalScore</c> (the
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
///    Both dictionaries are transmitted, so accuracy is exact; we OVERRIDE the submitted value.
///  - <b>completion</b> and <b>rank</b>. Completion = typed cells (accuracy-affecting judgements
///    in <c>statistics</c> that <see cref="CountsAsTyped"/>) over TOTAL map cells
///    (accuracy-affecting counts in <c>maximum_statistics</c>). Rank is graded on completion, NOT
///    accuracy: typing every character RIGHT is an SS regardless of timing quality; what costs the
///    grade is a cell the play did not type right, i.e. one that scrolled past untyped (a miss) or
///    one left holding a wrong character (an uncorrected typo). Mirrors the client's
///    <c>TypeBeatScoreProcessor</c>; keep the cutoffs in the two files in sync.
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
/// <c>bonusPortion</c> are exact from the dictionaries, giving a provable ceiling. A submitted
/// total above the ceiling is impossible, so it is treated as out of bounds (caller unranks it).
///
/// Everything here is a pure function of the three transmitted quantities; no DB, no throwing on
/// hostile input (score-submit must never 500 for tamper-shaped data).
///
/// <para><b>THE FOUR QUALITY TIERS</b> (backlog 133). A correct keypress is graded on how many
/// CHARACTERS it was from the character the playhead was on, in four tiers, and they are the
/// identity on the osu results they are named for: <c>perfect</c> / <c>great</c> / <c>ok</c> /
/// <c>meh</c>, worth 300 / 200 / 100 / 50. Two things here follow from that and are load-bearing:</para>
/// <list type="bullet">
/// <item><c>maximum_statistics</c> is now one <b>perfect</b> per cell, not one great: the cell
/// judgement's MaxResult was raised from Great to Perfect to free the enum slot the fourth tier
/// needed. Every classifier below must therefore know <c>perfect</c>, or <c>accuracyMax</c> reads 0,
/// the denominator is not positive, and EVERY submitted play unranks.</item>
/// <item><c>great</c> is worth <b>200</b>, not 300. The tier was made by moving Great down rather
/// than Perfect up, because the base game already scores a Perfect at 300 (ScoreProcessor.cs:372,
/// "Perfect doesn't actually give more score / accuracy directly"). The per-cell MAXIMUM is
/// therefore still 300 and the accuracy denominator is exactly what it always was.</item>
/// <item>...but only for a score judged under that ladder. A score stored BEFORE it has a
/// <c>great</c> that WAS the top tier and was worth 300, and the two are told apart by the key its
/// own <c>maximum_statistics</c> uses, which moved in the same change (see
/// <see cref="JudgedBeforeTheFourthTier"/>). Every stored row therefore still recomputes to exactly
/// the numbers it was submitted with.</item>
/// </list>
///
/// <para><b>THE UNCORRECTED-TYPO CELL</b> (backlog 124 and 126). A cell the player typed WRONG and
/// never went back for arrives as the <c>good</c> key, which type!beat uses for nothing else. It is
/// a CELL STATE, not the typo count: since backlog 140 the number players are shown is
/// <c>combo_break</c> below, counting wrong keypresses, and this key is no longer surfaced anywhere
/// (every cell left holding a wrong character implied one of those keypresses, so the event count
/// covers it). Nothing here moved with that: the key still arrives, still weighs 50, still costs
/// completion and rank, and every stored row stays comparable. The client
/// picked it because a cell may only ever resolve as one of perfect/great/ok/meh/good/miss (osu
/// refuses any other result for a Perfect-max, Miss-min judgement) and the other five are the four
/// quality tiers plus the seal's miss; see the game's <c>TypeBeatResultMapping.UNFIXED_TYPO</c>.
/// Two consequences here, both deliberate:</para>
/// <list type="bullet">
/// <item>Its base score is <b>50</b>, not the base ruleset's 200. The client re-weights the tier
/// (<c>TypeBeatScoreProcessor.GetBaseScoreForResult</c>) so a typo costs the most accuracy a judged
/// cell can cost, i.e. exactly what it cost while it was stored as <c>meh</c>. This table has to
/// carry the same number or every recomputed accuracy would come out above what the client showed.
/// A type!beat map can never produce a genuine <c>good</c>, so nothing else is affected.</item>
/// <item>It is accuracy-affecting and a judgement, so it is in completion's DENOMINATOR, but it is
/// NOT typed (<see cref="CountsAsTyped"/>), so it is out of the numerator: an uncorrected typo costs
/// completion and rank exactly as a miss does. It is still not a MISS, which is what lets
/// <c>PerformancePoints</c> keep pricing it by the typo term rather than the cleanliness one.
/// Migration <c>008_completion_rank.sql</c> counts <c>good</c> as typed, which was right when it ran
/// (it is a one-off backfill of rows that all predate this key) and must not be edited.</item>
/// </list>
///
/// <para><b>TYPOS</b> (backlog 72, so named by 140). A wrong KEYPRESS arrives as the
/// <c>combo_break</c> key in <c>statistics</c>, one per press, and every classifier below already
/// answers false for it: it is not accuracy-affecting, not a hit, not combo-increasing, not a bonus,
/// and carries base score 0. That is load-bearing, not incidental. It keeps accuracy, completion and
/// rank byte-identical to what they were before the stat existed (so old and new scores stay
/// comparable), and it keeps the key out of the <c>accuracyJudged &gt; accuracyMax</c> invariant
/// below, which would otherwise UNRANK every play with a typo in it: typos have no counterpart in
/// <c>maximum_statistics</c> (that stays one perfect per cell), so counting them as judgements would
/// make any such play look like it contained more cells than the map has. Do not add
/// <c>combo_break</c> to any table here.</para>
///
/// <para>This is the ONE typo number the client, the browser and the site all show (backlog 140).
/// The key itself did not move and neither did anything below: 140 renamed a display, not a
/// statistic. Its one BEHAVIOURAL half is invisible here, because it is a rule about combo during
/// play rather than about the account: correcting the cell a wrong keypress spoiled resumes the
/// streak that keypress broke, so a fixed typo submits a higher <c>max_combo</c> than it used to.
/// <c>max_combo</c> is bounded here and not recomputed, and the bound (one per combo-increasing
/// judgement in <c>maximum_statistics</c>) is exactly what a fully restored run reaches, so a
/// restored score is in bounds by the same test as a clean one.</para>
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
    /// max_combo beyond the theoretical maximum, negative counts, or an empty maximum); such a
    /// score must never be ranked.
    ///
    /// <see cref="Accuracy"/> is the WHOLE-MAP accuracy (denominator over
    /// <c>maximum_statistics</c>): the client's final accuracy for a completed play.
    /// <see cref="JudgedAccuracy"/> uses the judged-only denominator (Σ maxBase over
    /// <c>statistics</c>), which is the client's RUNNING accuracy (ScoreProcessor.cs:261,393);
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
        bool preFourthTier = JudgedBeforeTheFourthTier(maximumStatistics);

        long numerator = 0;         // currentBaseScore (ScoreProcessor.cs:266)
        long judgedDenominator = 0; // currentMaximumBaseScore at end of play (ScoreProcessor.cs:261), judged cells only
        long denominator = 0;       // whole-map maximum base score (ScoreProcessor.cs:450)
        int accuracyJudged = 0;
        int accuracyMax = 0;
        int typedCells = 0; // accuracy-affecting HITS, the completion numerator
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
                numerator += (long)BaseScore(key, preFourthTier) * count;
                judgedDenominator += (long)MaxBaseScore(key) * count;
                accuracyJudged += count;

                if (CountsAsTyped(key))
                    typedCells += count;
            }

            if (IsBonus(key))
                bonusPortion += (long)BaseScore(key, preFourthTier) * count;
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
                denominator += (long)BaseScore(key, preFourthTier) * count;
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
        // JUDGED accuracy: the value the client actually baked into its total (running accuracy
        // at end of play; equals whole-map accuracy for completed plays). judgedAccuracy >=
        // accuracy always (same numerator, smaller denominator), so this ceiling is valid for
        // passed AND failed plays; using the whole-map value here falsely flagged honest fails.
        double raw = combo_portion_max * judgedAccuracy * 1.0
                     + accuracy_portion_max * Math.Pow(judgedAccuracy, accuracy_exponent) * accuracyProgress
                     + bonusPortion;

        // Degenerate maximums (no accuracy-affecting cells at all) describe no playable map;
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
    // ScoreProcessor.GetBaseScoreForResult (ScoreProcessor.cs:346-381) as TypeBeatScoreProcessor
    // overrides it. For a typing map only perfect/great/ok/meh/good/miss occur, but the full table
    // keeps the contract faithful to the base ruleset. Unknown keys fall through to base 0 / no
    // classification, so they can never inflate a score.

    /// <summary>
    /// What the client's <c>TypeBeatScoreProcessor</c> weighted a <c>great</c> at: 200 since backlog
    /// 133 made it the SECOND of four quality tiers, 300 before that, when it was the top one.
    /// </summary>
    private const int great_base_score = 200;

    private const int pre_fourth_tier_great_base_score = 300;

    /// <summary>
    /// Whether this submission was judged BEFORE backlog 133's fourth quality tier, read off its own
    /// <c>maximum_statistics</c>. That dictionary is one MaxResult per cell, and the MaxResult moved
    /// from Great to Perfect in the same change that re-weighted <c>great</c> from 300 to 200, so
    /// its key IS the era stamp: nothing else about a score says which ladder judged it, and every
    /// row stored before 133 carries <c>great</c> there while every row stored after carries
    /// <c>perfect</c>.
    ///
    /// <para>Without this a pre-133 SS would recompute at 2/3 of the accuracy it was submitted with
    /// (its top-tier cells priced as second-tier ones against an unmoved per-cell maximum), and its
    /// stored total would fall outside the ceiling its own statistics justify, so every stored row
    /// this contract is ever re-run over would read as tampered. It is not a tamper hole either: a
    /// client choosing the old keys gains nothing it could not gain by simply claiming perfects,
    /// since <c>statistics</c> is self-reported and this function only ever bounds a submission by
    /// its OWN dictionary.</para>
    /// </summary>
    private static bool JudgedBeforeTheFourthTier(IReadOnlyDictionary<string, int> maximumStatistics)
        => !maximumStatistics.ContainsKey("perfect") && maximumStatistics.ContainsKey("great");

    private static int BaseScore(string key, bool preFourthTier) => key switch
    {
        "perfect" => 300,
        // NOT the base ruleset's 300 for a post-133 score: `great` is the SECOND quality tier there
        // and the client re-weights it (TypeBeatScoreProcessor.GREAT_BASE_SCORE) so the four tiers
        // step 300 / 200 / 100 / 50. The tier was made by moving Great DOWN rather than Perfect up
        // precisely so the per-cell maximum, and therefore the accuracy denominator, did not move.
        "great" => preFourthTier ? pre_fourth_tier_great_base_score : great_base_score,
        // NOT the base ruleset's 200: in type!beat `good` is the uncorrected-typo tier and the
        // client re-weights it to the meh value (see the class docs). Keep the two in step.
        "good" => 50,
        "ok" => 100,
        "meh" => 50,
        "slider_tail_hit" => 150,
        "large_tick_hit" => 30,
        "small_tick_hit" => 10,
        "large_bonus" => 50,
        "small_bonus" => 10,
        _ => 0,
    };

    // Base score of the judgement's MaxResult for a judged cell of this key: what the client's
    // running currentMaximumBaseScore accrues per judgement (ScoreProcessor.cs:261). The
    // great-family results (perfect/great/good/ok/meh/miss) all belong to PERFECT-max judgements
    // (every type!beat judgement is Perfect-max since backlog 133); tick/tail families max at their
    // own hit result. The number is still 300, because the base game gives a Perfect the same base
    // score as a Great.
    private static int MaxBaseScore(string key) => key switch
    {
        "great" or "perfect" or "good" or "ok" or "meh" or "miss" => 300,
        "slider_tail_hit" => 150,
        "large_tick_hit" or "large_tick_miss" => 30,
        "small_tick_hit" or "small_tick_miss" => 10,
        _ => 0,
    };

    // HitResult.AffectsAccuracy (HitResult.cs:208-223): scorable and non-bonus, excluding
    // combo_break / legacy_combo_increase. Misses count (base 0); they are accuracy-affecting.
    private static bool AffectsAccuracy(string key) => key switch
    {
        "great" or "perfect" or "good" or "ok" or "meh" or "miss"
            or "small_tick_hit" or "small_tick_miss"
            or "large_tick_hit" or "large_tick_miss"
            or "slider_tail_hit" => true,
        _ => false,
    };

    // HitResult.IsHit (HitResult.cs:308-…): a successful judgement. Faithful to the base ruleset,
    // which is why `good` is in it; the completion numerator uses CountsAsTyped instead.
    private static bool IsHit(string key) => key switch
    {
        "great" or "perfect" or "good" or "ok" or "meh"
            or "small_tick_hit" or "large_tick_hit" or "slider_tail_hit" => true,
        _ => false,
    };

    /// <summary>
    /// Whether a judged cell counts as TYPED, i.e. belongs in completion's numerator. Every hit does
    /// EXCEPT the uncorrected-typo key: the player put a character in that cell and it was the wrong
    /// one, so the cell is no more typed than one the line ran out of time on and it costs
    /// completion, and therefore rank, exactly as a miss does (backlog 126).
    ///
    /// <para>Mirrors the client's <c>TypeBeatScoreProcessor.CountsAsTyped</c> and the browser
    /// engine's <c>typebeat-core.js</c>. Split out from <see cref="IsHit"/> rather than folded into
    /// it so that table stays a faithful copy of <c>HitResult.IsHit</c>: this is the one type!beat
    /// rule that departs from the base ruleset's reading of the key.</para>
    /// </summary>
    private static bool CountsAsTyped(string key) => IsHit(key) && key != unfixed_typo_key;

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

    /// <summary>
    /// The statistics key an UNCORRECTED TYPO is stored under (the client's
    /// <c>TypeBeatResultMapping.UNFIXED_TYPO</c>, i.e. <c>HitResult.Good</c>). See the class docs
    /// for why that member and not another.
    /// </summary>
    internal const string unfixed_typo_key = "good";
}
