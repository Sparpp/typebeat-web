namespace Typebeat.Web.Scoring;

/// <summary>
/// Server-side recompute + tamper-bounds for a submitted solo score.
///
/// The client computes score/accuracy/combo with the standardised <c>ScoreProcessor</c> maths.
/// typebeat's <c>TypeBeatScoreProcessor</c> subclass changes only the rank derivation and ONE
/// numeric weight, the uncorrected-typo tier's base score (see below); everything else is the base
/// implementation. What it actually submits over the wire is <c>ScoreInfo.TotalScore</c> (the
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
/// <para><b>THREE QUALITY TIERS, AND THE FOUR-TIER WINDOW THAT SHIPPED</b> (backlog 133, 134 and
/// 147). A correct keypress is graded on how many MILLISECONDS it was from its character's target
/// time, in three tiers, and they are the identity on the osu results they are named for:
/// <c>great</c> / <c>ok</c> / <c>meh</c>, worth 300 / 100 / 50, with <c>maximum_statistics</c> one
/// <b>great</b> per cell. That is what the game has judged in for almost all of its life.</para>
///
/// <para>Between backlog 133 and backlog 147 it judged on CHARACTER DISTANCE in FOUR tiers instead:
/// <c>perfect</c> / <c>great</c> / <c>ok</c> / <c>meh</c> worth 300 / 200 / 100 / 50, with
/// <c>maximum_statistics</c> one <b>perfect</b> per cell (the cell judgement's MaxResult was raised
/// from Great to Perfect to free the enum slot the fourth tier needed). THAT LADDER SHIPPED, so rows
/// judged under it exist and this contract is re-run over stored rows, which makes two things here
/// load-bearing:</para>
/// <list type="bullet">
/// <item>Every classifier below must still know <c>perfect</c>. For a four-tier row it is the
/// per-cell maximum, so a classifier that does not answer for it reads <c>accuracyMax</c> 0, gets a
/// non-positive denominator, and UNRANKS the row.</item>
/// <item><c>great</c> is worth 300 for a three-tier row and <b>200</b> for a four-tier one, because
/// that tier was made by moving Great down rather than Perfect up (the base game already scores a
/// Perfect at 300, ScoreProcessor.cs:372, "Perfect doesn't actually give more score / accuracy
/// directly"). The two eras are told apart by the key the row's own <c>maximum_statistics</c> uses,
/// which moved in the same change and moved back with it (see
/// <see cref="JudgedUnderTheFourthTier"/>). The per-cell MAXIMUM is 300 either way, so the accuracy
/// denominator never moved at all, and every stored row recomputes to exactly the numbers it was
/// submitted with.</item>
/// </list>
///
/// <para><b>THE UNCORRECTED-TYPO CELL</b> (backlog 124 and 126). A cell the player typed WRONG and
/// never went back for arrives as the <c>good</c> key, which type!beat uses for nothing else. It is
/// a CELL STATE, not the typo count: since backlog 140 the number players are shown is
/// <c>combo_break</c> below, counting wrong keypresses, and this key is no longer surfaced anywhere
/// (every cell left holding a wrong character implied one of those keypresses, so the event count
/// covers it). Nothing here moved with that: the key still arrives, still carries a weight, still
/// costs completion and rank, and every stored row stays comparable. The client
/// picked it because a cell may only ever resolve as one of great/ok/meh/good/miss (osu refuses
/// any other result for a Great-max, Miss-min judgement) and the other four are the three quality
/// tiers plus the seal's miss; see the game's <c>TypeBeatResultMapping.UNFIXED_TYPO</c>. Two
/// consequences here, both deliberate:</para>
/// <list type="bullet">
/// <item>Its base score is <b>0</b> since backlog 213, and was <b>50</b> from backlog 124 until
/// then; it has never been the base ruleset's 200. The client re-weights the tier
/// (<c>TypeBeatScoreProcessor.GetBaseScoreForResult</c>) and this table has to carry the same
/// number or every recomputed accuracy would come out above what the client showed. 124 put it at
/// the most accuracy a JUDGED cell could cost, on the reading that a cell the player finished
/// wrongly is not a cell the line ran out of time on; 213 takes it to a miss's 0, because the
/// player did not put that character in that cell either way. The cell's MAXIMUM stays a
/// <c>great</c> (<see cref="MaxBaseScore"/>), so the denominator does not move and the re-weight
/// is paid in full. A type!beat map can never produce a genuine <c>good</c>, so nothing else is
/// affected.</item>
/// <item>It is accuracy-affecting and a judgement, so it is in completion's DENOMINATOR, but it is
/// NOT typed (<see cref="CountsAsTyped"/>), so it is out of the numerator: an uncorrected typo costs
/// completion and rank exactly as a miss does, and has since backlog 126. That half is UNTOUCHED by
/// backlog 213, which found it already done: the fold moved accuracy and pp onto the reading
/// completion had used all along. <c>PerformancePoints</c> now prices the cell by the cleanliness
/// term rather than the typo one (<c>misses = miss + good</c>), and takes the keypress that
/// produced it back out of the typo count so one flub is priced once.
/// Migration <c>008_completion_rank.sql</c> counts <c>good</c> as typed, which was right when it ran
/// (it is a one-off backfill of rows that all predate this key) and must not be edited.</item>
/// </list>
///
/// <para><b>THE FOLD IS ON THE READING, NOT ON THE WIRE</b> (backlog 213). The key still arrives,
/// still means "a cell left holding a wrong character", and is still stored verbatim, so old rows
/// and new ones stay comparable and the typo-versus-timeout distinction survives in the data. What
/// changed is every CONSUMER: this table's weight, <c>PerformancePoints.CountNotes</c>, the game's
/// display seam and the site's MISS column, which now shows <c>miss + good</c> so the shown columns
/// sum to the cell count again.</para>
///
/// <para><b>TYPOS</b> (backlog 72, so named by 140). A wrong KEYPRESS arrives as the
/// <c>combo_break</c> key in <c>statistics</c>, one per press, and every classifier below already
/// answers false for it: it is not accuracy-affecting, not a hit, not combo-increasing, not a bonus,
/// and carries base score 0. That is load-bearing, not incidental. It keeps accuracy, completion and
/// rank byte-identical to what they were before the stat existed (so old and new scores stay
/// comparable), and it keeps the key out of the <c>accuracyJudged &gt; accuracyMax</c> invariant
/// below, which would otherwise UNRANK every play with a typo in it: typos have no counterpart in
/// <c>maximum_statistics</c> (that stays one per cell), so counting them as judgements would
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

    // Accuracy → rank cutoffs, mirroring TypeBeatScoreProcessor.ACCURACY_CUTOFF_* (and its
    // S_MISS_LIMIT): grades are awarded on timing accuracy with a missed-cell condition on SS and S.
    private const double accuracy_cutoff_x = 0.98;
    private const double accuracy_cutoff_s = 0.92;
    private const double accuracy_cutoff_a = 0.85;
    private const double accuracy_cutoff_b = 0.75;
    private const double accuracy_cutoff_c = 0.60;
    private const double s_miss_limit = 0.03;

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
        bool fourthTier = JudgedUnderTheFourthTier(maximumStatistics);

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
                numerator += (long)BaseScore(key, fourthTier) * count;
                judgedDenominator += (long)MaxBaseScore(key) * count;
                accuracyJudged += count;

                if (CountsAsTyped(key))
                    typedCells += count;
            }

            if (IsBonus(key))
                bonusPortion += (long)BaseScore(key, fourthTier) * count;
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
                denominator += (long)BaseScore(key, fourthTier) * count;
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

        return new Recomputed(accuracy, judgedAccuracy, completion, ceiling, RankFromStatistics(accuracy, statistics), valid, theoreticalMaxCombo, accuracyProgress);
    }

    /// <summary>
    /// Whether a submitted total score is inside the provable ceiling (and non-negative). A legit
    /// full-combo play hits the ceiling exactly (identical formula with comboProgress = 1); anything
    /// above it is unachievable.
    /// </summary>
    public static bool TotalScoreWithinBounds(long submittedTotalScore, in Recomputed recomputed)
        => submittedTotalScore >= 0 && submittedTotalScore <= recomputed.TotalScoreCeiling;

    /// <summary>
    /// How many ACCURACY-AFFECTING judgements a counts dictionary describes, i.e. how many cells it
    /// accounts for. Over <c>statistics</c> that is the cells the run judged; over
    /// <c>maximum_statistics</c> it is the cells the map has.
    ///
    /// <para>Exposed for <c>tools/score-recalc</c>'s supersede sweep, which re-judges a stored row
    /// under rules that deliberately MOVE every number on it and therefore cannot check itself by
    /// comparing them. What it can still check is that the replay describes the same run over the
    /// same map, and this count is what that reduces to: the tier keys move (a pre-133 row's cells
    /// were graded on a different ladder, and its maximum uses <c>great</c> where today's uses
    /// <c>perfect</c>), but how many cells there are, and how many of them the run judged, cannot.
    /// It is a wrapper over <see cref="AffectsAccuracy"/> rather than a second copy of that table,
    /// because a tool holding its own copy would be free to drift from the one the server ranks
    /// with.</para>
    /// </summary>
    public static int CountAccuracyAffecting(IReadOnlyDictionary<string, int>? counts)
    {
        if (counts is null)
            return 0;

        int total = 0;

        foreach (var (key, count) in counts)
        {
            if (count > 0 && AffectsAccuracy(key))
                total += count;
        }

        return total;
    }

    /// <summary>
    /// Accuracy + missed fraction → rank string, mirroring TypeBeatScoreProcessor.RankFromAccuracy.
    /// Grades are awarded on timing accuracy, with an extra missed-cell condition on SS (no cell
    /// missed at all) and S (under S_MISS_LIMIT of the map's cells missed). There are no mods, so
    /// the silver ranks (SH/XH) never apply. The strings match the client's <c>ScoreRank</c> enum
    /// names, which its <c>StringEnumConverter</c> parses on the wire.
    /// </summary>
    public static string RankFromAccuracy(double accuracy, double missedFraction)
    {
        accuracy = double.IsFinite(accuracy) ? Math.Clamp(accuracy, 0, 1) : 0;
        missedFraction = double.IsFinite(missedFraction) ? Math.Clamp(missedFraction, 0, 1) : 1;

        if (accuracy >= accuracy_cutoff_x && missedFraction == 0) return "X";
        if (accuracy >= accuracy_cutoff_s && missedFraction < s_miss_limit) return "S";
        if (accuracy >= accuracy_cutoff_a) return "A";
        if (accuracy >= accuracy_cutoff_b) return "B";
        if (accuracy >= accuracy_cutoff_c) return "C";
        return "D";
    }

    /// <summary>
    /// Rank from the transmitted dictionaries, mirroring TypeBeatScoreProcessor.RankFromStatistics:
    /// the missed fraction is the misses over judged cells, from the same <see cref="PerformancePoints.CountNotes"/>
    /// the client uses (an uncorrected typo counts as a missed cell there, exactly as it does for pp).
    /// </summary>
    public static string RankFromStatistics(double accuracy, IReadOnlyDictionary<string, int>? statistics)
    {
        var counts = PerformancePoints.CountNotes(statistics);
        double missedFraction = counts.Notes > 0 ? (double)counts.Misses / counts.Notes : 0;
        return RankFromAccuracy(accuracy, missedFraction);
    }

    // ---- HitResult numeric weights + classification ----
    // Keys are the EnumMember snake_case values (HitResult.cs). Values mirror
    // ScoreProcessor.GetBaseScoreForResult (ScoreProcessor.cs:346-381) as TypeBeatScoreProcessor
    // overrides it. For a typing map only great/ok/meh/good/miss occur, plus perfect on a row stored
    // while backlog 133's fourth tier was live, but the full table keeps the contract faithful to
    // the base ruleset. Unknown keys fall through to base 0 / no classification, so they can never
    // inflate a score.

    /// <summary>
    /// What the client's <c>TypeBeatScoreProcessor</c> weights a <c>great</c> at: 300, the top of a
    /// three-tier ladder, which is what it has been for all but one day of this game's life.
    /// </summary>
    private const int great_base_score = 300;

    /// <summary>
    /// What a <c>great</c> was worth to a client running backlog 133's four-tier ladder, where it
    /// was the SECOND tier of 300 / 200 / 100 / 50 (<c>TypeBeatScoreProcessor.GREAT_BASE_SCORE</c>,
    /// deleted with the ladder by backlog 147).
    /// </summary>
    private const int fourth_tier_great_base_score = 200;

    /// <summary>
    /// Whether this submission was judged UNDER backlog 133's fourth quality tier, read off its own
    /// <c>maximum_statistics</c>. That dictionary is one MaxResult per cell, and the MaxResult moved
    /// from Great to Perfect in the same change that re-weighted <c>great</c> from 300 to 200, so
    /// its key IS the era stamp: nothing else about a score says which ladder judged it, and only a
    /// row stored while that ladder was live carries <c>perfect</c> there.
    ///
    /// <para>THIS FUNCTION IS THE INVERSION OF THE ONE BACKLOG 134 ADDED, AND MUST NOT BECOME A
    /// DELETION. It was written when the four-tier ladder was the live rule and the three-tier rows
    /// were the legacy ones; backlog 147 reverted the judgement, so the four-tier rows are the
    /// legacy ones now. What did not change is that both kinds exist in the database: 133 and 134
    /// SHIPPED (game <c>66d8ae6</c>), so production judged on the character ladder for a day and
    /// the rows it stored are still there. Delete this and every one of those rows recomputes with
    /// its second-tier cells priced at 300 against an unmoved per-cell maximum, i.e. above the
    /// accuracy it was submitted with, and its stored total then falls outside the ceiling its own
    /// statistics justify, which <c>GateRefund.Qualifies</c> (the one path that re-runs this
    /// contract over rows the DB already holds) reads as TAMPERED.</para>
    ///
    /// <para>It is not a tamper hole in either direction: <c>statistics</c> is self-reported, so a
    /// client picking whichever key set it likes gains nothing it could not gain by simply claiming
    /// more top-tier cells, and this function only ever bounds a submission by its OWN dictionary.
    /// A dictionary with neither key, or with both, falls to the CURRENT rules, which is the right
    /// default for anything malformed.</para>
    ///
    /// <para>PUBLIC for <c>tools/score-recalc</c>, which needs the same answer for a different
    /// question (backlog 151). Pricing a row is what this predicate does here; there it names the one
    /// population a supersede sweep can never CHECK itself against, because reproducing a row means
    /// re-deriving it on the ladder that judged it and backlog 147 deleted that ladder. The tool
    /// calls this rather than testing the two keys itself, so the rows it calls unreproducible are by
    /// construction the same rows the server prices as fourth-tier: a private copy in the tool would
    /// be free to drift from the contract that ranks them.</para>
    /// </summary>
    public static bool JudgedUnderTheFourthTier(IReadOnlyDictionary<string, int> maximumStatistics)
        => maximumStatistics.ContainsKey("perfect") && !maximumStatistics.ContainsKey("great");

    /// <summary>
    /// Whether this submission holds an UNCORRECTED TYPO, read off its own <c>statistics</c>: the
    /// <see cref="unfixed_typo_key"/> key, which type!beat uses for nothing else (the client's
    /// <c>TypeBeatResultMapping.UNFIXED_TYPO</c>, backlog 126).
    ///
    /// <para>PUBLIC for <c>tools/score-recalc</c>, which asks it as an ERA question, and the answer
    /// is ONE-DIRECTIONAL. Only a client running the deferred typo rule can leave a cell holding a
    /// wrong character, so a row carrying this key PROVES it was judged under that rule. A row
    /// WITHOUT it proves nothing at all: a run that corrected every typo, or made none, has no such
    /// cell to key whichever rule judged it. Reading a false here as "judged under the old rule" is
    /// reading evidence of absence into an absence of evidence, and would re-derive a modern clean
    /// row on a retired ladder. The tool therefore uses a true here to PIN a row to the deferred
    /// rule, and treats false as "no proof either way".</para>
    ///
    /// <para>A key present with a count of ZERO is not proof either, which is why this counts rather
    /// than tests for the key: a zero says the run finished with no wrong character left standing,
    /// which both rules can produce, so it carries exactly as much era evidence as no key at all.</para>
    ///
    /// <para>Asked through the contract rather than by testing the literal string, so the tool and
    /// the pricing tables here (<c>BaseScore</c>, <c>CountsAsTyped</c>) can never end up naming two
    /// different keys.</para>
    /// </summary>
    public static bool CarriesAnUncorrectedTypo(IReadOnlyDictionary<string, int> statistics)
        => statistics.GetValueOrDefault(unfixed_typo_key) > 0;

    private static int BaseScore(string key, bool fourthTier) => key switch
    {
        // Only a four-tier row can carry one. It is the top tier there, and the base game scores a
        // Perfect at 300 anyway, so the per-cell maximum is 300 under either ladder.
        "perfect" => 300,
        // 300 as the top of today's three tiers; 200 for a row judged under backlog 133's four,
        // where the fourth tier was made by moving Great DOWN rather than Perfect up, precisely so
        // the per-cell maximum, and therefore the accuracy denominator, did not move.
        "great" => fourthTier ? fourth_tier_great_base_score : great_base_score,
        // NOT the base ruleset's 200, and NOT the 50 backlog 124 set: in type!beat `good` is the
        // uncorrected-typo tier, and since backlog 213 it is worth what a miss is worth, because
        // the player did not put that character in that cell. The client re-weights the tier the
        // same way (TypeBeatScoreProcessor.GetBaseScoreForResult under the live
        // UnfixedTypoWorthRule.Nothing) and so does typebeat-core.js; keep the three in step.
        //
        // UNCONDITIONAL, where the client's re-weight is era-gated. This contract prices LIVE
        // submissions, and every live client is on the new weight; a stored row's accuracy is not
        // recomputed by anything that reaches this table (it stands as submitted), so there is no
        // population here for an era arm to serve. The one path that re-runs this contract over
        // stored rows is GateRefund.Qualifies, which uses the ceiling as a tamper bound: a
        // pre-213 row carrying `good` therefore gets a slightly TIGHTER bound than the one it was
        // submitted against, so such a row can fail a refund it would once have passed. That is a
        // conservative direction (it withholds a refund, it cannot unrank anything), and the
        // alternative, era-gating this table off `maximum_statistics` the way the fourth tier is,
        // has nothing to key on: the fold moved no key at all.
        "good" => 0,
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
    // great-family results (great/perfect/good/ok/meh/miss) all belong to Great-max judgements
    // (every type!beat judgement is Great-max, and was Perfect-max for the one day backlog 133's
    // fourth tier was live); tick/tail families max at their own hit result. The number is 300
    // either way, because the base game gives a Perfect the same base score as a Great.
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
