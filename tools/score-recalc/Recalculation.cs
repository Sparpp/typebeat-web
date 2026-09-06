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
    double? SrHt,
    // The Literate-converted map's three ratings (029_literate_stars.sql). Null for an offline
    // score, whose only rating comes from a local .osu parse, which prices a Literate play at
    // nothing rather than wrongly.
    double? SrLiterate,
    double? SrLiterateDt,
    double? SrLiterateHt,
    // beatmaps.checksum_md5 as it stands NOW, which is what tells a beatmap the tool merely failed
    // to download from one the row can no longer be re-derived against at all: if the replay names
    // a hash and the row's map today hashes to something else, the .osu the run was judged on is
    // not served any more (the set was re-uploaded) and no amount of retrying will fetch it. Empty
    // when unknown (offline runs, which have no beatmap row).
    string CurrentChecksumMd5 = "",
    // Who owns the row, so the report can model a leaderboard (best score per user).
    long UserId = 0)
{
    /// <summary>
    /// Whether this row was judged in the BACKLOG 133-TO-147 WINDOW: the day production graded on a
    /// four-tier character-distance ladder, which backlog 147 deleted. Read off the row's own
    /// <c>maximum_statistics</c> by <see cref="ScoringContract.JudgedUnderTheFourthTier"/>, the same
    /// predicate the server prices such a row with, so the tool cannot end up disagreeing with the
    /// contract about which era a row belongs to.
    ///
    /// <para>It is the one population a sweep can never CHECK. Reproducing a row means re-deriving it
    /// under the rules that judged it, and no setting of any era switch brings back code that is
    /// gone, so these rows are unreproducible by construction rather than by disagreement. That is
    /// why they are counted and named separately from the rows that fail to reproduce for a reason
    /// nobody has explained yet: superseding both is right, but only one of them is a surprise.</para>
    ///
    /// <para>Deliberately NOT a <c>SkipReason</c> and NOT an <see cref="UnreplayableCase"/>. Every arm
    /// of those means "nothing can be derived from this row", and one of these rows derives perfectly
    /// well: it has a replay, a map and a run, and only the CHECK is unavailable. Folding it in would
    /// let one <c>--unreplayable</c> answer decide two different questions.</para>
    ///
    /// <para>Serialized into <c>--out</c> alongside the rest of the row, so an operator can pick the
    /// population out of the JSON without knowing which keys stamp which era.</para>
    /// </summary>
    public bool JudgedOnTheDeletedLadder
        => ScoringContract.JudgedUnderTheFourthTier(WireCounts.Parse(MaximumStatisticsJson));

    /// <summary>
    /// Whether this row PROVES it was judged under <see cref="TypoRule.Deferred"/>, i.e. by a client
    /// from backlog 126 onwards. Read off the row's own <c>statistics</c> by
    /// <see cref="ScoringContract.CarriesAnUncorrectedTypo"/>: an uncorrected typo takes a key of its
    /// own (<c>good</c>, the game's <c>TypeBeatResultMapping.UNFIXED_TYPO</c>), and only the deferred
    /// rule can leave a cell holding a wrong character, because the other rule spends that cell's one
    /// result on a Miss the instant the wrong key lands.
    ///
    /// <para>THIS IS ONE-DIRECTIONAL, AND MUST STAY THAT WAY. True is a PROOF and pins the row's typo
    /// axis to the deferred rule. False is an ABSENCE OF EVIDENCE, not evidence of the old rule: a
    /// run with no uncorrected typo left standing has no such key to carry, whichever rule judged it.
    /// Turning this into a bidirectional test would re-derive every clean modern row on a ladder it
    /// was never played on.</para>
    ///
    /// <para>WHAT THE TWO ANSWERS MEAN TO THE PASS, AND WHY BACKLOG 158 DID NOT REVERSE 155. True
    /// still PINS, and pinning is still the fast path: <see cref="Recalculation.EraSearchFor"/> hands
    /// such a row only candidates on the deferred arm, so the axis is never searched for it and the
    /// proof is never second-guessed by a reconstruction. False no longer resolves silently to the
    /// older rule, which is the one thing that changed. It used to, and the cost was a population
    /// nothing could explain: a typo that was CORRECTED leaves no key either, so a modern row whose
    /// player fixed their mistakes was re-derived with that cell as a MISS it does not carry. So an
    /// absence now sends the typo axis into <see cref="Recalculation.EraSearch"/> alongside the axes
    /// that never had a key at all, and the row is pinned to the arm that REPRODUCES it. The
    /// older rule is still what is TRIED FIRST for such a row, so nothing that reproduced before
    /// changes cost or meaning.</para>
    ///
    /// <para>Deliberately NOT a score id or a date. The population happens to start at one id in
    /// production because it started at one deploy, but the id is an observation about that deploy
    /// and the key is a fact about the row, and only one of the two survives a backfill, an import or
    /// a re-numbering.</para>
    ///
    /// <para>Serialized into <c>--out</c> alongside the rest of the row, like
    /// <see cref="JudgedOnTheDeletedLadder"/>, so an operator can pick the population out of the JSON
    /// without knowing which key stamps which era.</para>
    /// </summary>
    public bool ProvablyJudgedUnderTheDeferredTypoRule
        => ScoringContract.CarriesAnUncorrectedTypo(WireCounts.Parse(StatisticsJson));
}

/// <summary>
/// One point in the space the reproduce pass SEARCHES: which spacebar rule (backlog 148), which
/// rate-window rule (backlog 150), which combo-restore rule (backlog 140), which typo rule
/// (backlog 109), what an off-time press cost (backlog 199), what a CORRECTED typo's cell was
/// worth (backlog 210) and what an UNCORRECTED one was worth (backlog 213) graded a row. All seven
/// travel
/// together because a row that cannot PROVE which arm judged it has to be told, and the only honest
/// way to decide what to tell it is to re-derive it under each and keep what comes back.
///
/// <para>THE TYPO AXIS IS NOT LIKE THE OTHER FOUR, AND BACKLOG 155 IS NOT REVERSED BY ITS BEING
/// HERE. The other four leave no trace of themselves in a row at all. The typo rule leaves one in
/// exactly one case: a typo LEFT STANDING takes <c>good</c>, a key only the deferred rule can
/// produce, so <see cref="StoredScore.ProvablyJudgedUnderTheDeferredTypoRule"/> PROVES that row's
/// typo era outright. That proof still wins and is still the fast path:
/// <see cref="Recalculation.EraSearchFor"/> pins such a row to <see cref="TypoRule.Deferred"/> and
/// never offers it a candidate on the other arm, so the axis is not searched for it at all. What
/// backlog 158 changed is only the case 155 documented as "no proof either way", the ABSENCE of the
/// key, which used to resolve silently to the older rule and now resolves by reconstruction like the
/// keyless axes. That is a composition of 155's decision, not a contradiction of it.</para>
///
/// <para>WHY THE ABSENCE NEEDED IT (backlog 158). A typo that was CORRECTED leaves no key, because
/// <c>good</c> only ever marks a cell left holding a wrong character. So a modern row whose player
/// fixed their mistakes proved nothing, kept the older rule, and had that cell re-derived as a MISS
/// it does not carry. Production ended on exactly two such rows (5410 and 5414), identified by a
/// check named before the run that produced it: <c>miss</c> moving by +2 with <c>max_combo</c> moving
/// by -2 in lockstep, which is the seam between 155 and 157 (combo restore only ever moves max_combo
/// when a typo was corrected, and a corrected typo is precisely the case that leaves no key).</para>
///
/// <para>THE SEPTUPLE IS A SEARCH SPACE, NOT A SETTING, and it is searched as a
/// 2 x 2 x 2 x 2 x 2 x 2 x 2
/// rather than as one switch even though real clients only ever shipped seven of the hundred and
/// twenty-eight
/// corners. They are seven independent facts about how a press was graded (the game's own
/// <c>RateWindowRule</c> doc says as much), they did not all move at the same time (the typo rule
/// moved at backlog 109, combo restore at 140, the spacebar and the rate windows together at the
/// 2026-08-13 release, the off-time rule at backlog 199, the correction cap at backlog 210, the
/// uncorrected typo's worth at backlog 213), and the
/// next change to any one of them has no reason to move the others.</para>
///
/// <para>THE OFF-TIME AXIS IS THE WIDEST OF THE SEVEN, alongside the spacebar. It reaches every row
/// that ever fumbled a beat, which is nearly all of them: under
/// <see cref="OffTimeRule.BreaksCombo"/>, the rule every stored row was played under, a right
/// character struck outside the outermost Meh window zeroed the run and spent its cell on a Miss,
/// and under the live rule it is a Meh that extends the run. So the two arms disagree on
/// <c>statistics</c>, on <c>max_combo</c>, on accuracy, on completion and therefore on rank and
/// pp.</para>
///
/// <para>THE WORTH AXIS IS THE ONLY ONE THE REPRODUCTION GATE CANNOT SEE (backlog 213). Under
/// <see cref="UnfixedTypoWorthRule.MehCredit"/>, the rule every row stored before it was played
/// under, a cell left holding a wrong character was re-weighted to Meh's 50 of 300; under the live
/// <see cref="UnfixedTypoWorthRule.Nothing"/> it is worth a miss's 0. That moves ACCURACY and
/// <c>total_score</c> and nothing else: <c>statistics</c> is byte-identical under both arms, because
/// the fold is a consumer-side reclassification and the seal's key never moved, and so are
/// <c>max_combo</c>, completion and rank. The reproduction gate compares <c>statistics</c> and
/// <c>max_combo</c> alone (see <see cref="Recalculation.EraSearchFor"/> and the note on
/// <c>ReproductionMismatch</c>), so no reconstruction can PROVE this axis the way one proves the
/// other six: it resolves by the search ORDER, which puts the stored arm first.</para>
///
/// <para>That is sound for the population the axis exists for, which is every row in the table at
/// the moment backlog 213 shipped, and it is what makes the old-rule arm recover a row's stored
/// total (and therefore its <c>storedMultiplier</c>) instead of reading a fold the row predates. It
/// will need a discriminator the day post-213 rows carrying <c>good</c> are common enough to matter:
/// a row's stored <c>accuracy</c> tells the two arms apart exactly, and is the obvious candidate,
/// but widening the gate to compare a DERIVED quantity is a decision of its own and is deliberately
/// not taken here.</para>
///
/// <para>THE CREDIT AXIS IS WIDE BUT SHALLOW (backlog 210). It reaches every row that ever fixed a
/// typo, which is a large population, but it moves less per row than any of the other five that
/// the gate can see: under
/// <see cref="CorrectionCreditRule.Full"/>, the rule every row stored before it was played under, a
/// corrected cell was graded on the retype's own timing alone, and under the live
/// <see cref="CorrectionCreditRule.Capped"/> it resolves at min(that tier, Ok). So the two arms
/// disagree on the TIER COUNTS in <c>statistics</c>, and therefore on accuracy and total_score, and
/// on nothing else: <c>max_combo</c>, the miss count, the mistype count, completion and rank come
/// back identical under both, because a capped cell is still a hit that extends the run and still
/// counts as typed (pinned by the game's <c>CorrectionCreditTest</c>). It is still a searched axis
/// and not a constant, because <c>statistics</c> is one of the two quantities the reproduction gate
/// compares.</para>
/// </summary>
public readonly record struct SearchedEra(SpaceTimingRule Space, RateWindowRule Rate, ComboRestoreRule Combo, TypoRule Typo, OffTimeRule OffTime, CorrectionCreditRule Credit, UnfixedTypoWorthRule Worth)
{
    /// <summary>The spelling the report prints, matching the rules' own type names.</summary>
    public override string ToString()
        => $"SpaceTimingRule.{Space} + RateWindowRule.{Rate} + ComboRestoreRule.{Combo} + TypoRule.{Typo} + OffTimeRule.{OffTime} + CorrectionCreditRule.{Credit} + UnfixedTypoWorthRule.{Worth}";
}

/// <summary>
/// Which sweep is being run. The two are NOT a threshold apart: they judge under different rules,
/// value total_score differently, and check themselves with different predicates. Keeping them as
/// one mode with a looser gate is exactly the failure backlog 142 warns about.
/// </summary>
public enum RecalcMode
{
    /// <summary>
    /// The verification sweep (backlog 114). Re-derives under the rules the row was PRICED under
    /// (all SEVEN era axes at the era that judged the row: the spacebar, the rate windows, combo
    /// restore, the off-time rule and the correction cap PER ROW by reconstruction, and the typo rule
    /// from the row's own
    /// keys where they PROVE one and by reconstruction where they prove nothing, see
    /// <see cref="Recalculation.EraSearchFor"/>)
    /// and refuses any row it cannot reproduce exactly, then reports what today's TYPO rule alone would
    /// make of it, holding every other axis still. Its output answers "does the harness understand
    /// this row", which is the question a supersede sweep cannot ask of itself.
    /// </summary>
    Reproduce,

    /// <summary>
    /// The superseding sweep (backlog 136 and 142). Re-judges the run under ALL of today's rules
    /// (<see cref="TypoRule.Deferred"/>, <see cref="ComboRestoreRule.OnFix"/>,
    /// <see cref="SpaceTimingRule.Untimed"/>, <see cref="RateWindowRule.ScaledByRate"/>,
    /// <see cref="OffTimeRule.MehHit"/>, <see cref="CorrectionCreditRule.Capped"/> and
    /// <see cref="UnfixedTypoWorthRule.Nothing"/>) and
    /// REPLACES the stored numbers with the result, because the user's decision is that a stored
    /// score must describe a game that is actually playable today.
    ///
    /// <para>Reproduction cannot be the gate here. For most rows that is a statement about the
    /// SWEEP, not about the row: a supersede run deliberately re-judges on rules the row was not
    /// played under, so its own numbers are expected to move. For one population it is structural:
    /// a row stored in the backlog 133-to-147 window was graded on a four-tier CHARACTER-DISTANCE
    /// ladder that backlog 147 deleted outright, so no era switch can bring it back and it provably
    /// will not reproduce however the axes are set. What replaces the gate is not a looser version
    /// of it but a different predicate, <see cref="Recalculation.StructuralMismatch"/>: the judgement
    /// of the run is allowed to move, the RUN is not. Same map, same cell count, same number of cells
    /// judged, every frame consumed. A row that fails THAT is still refused and still written nothing
    /// for, which is the corruption the original gate existed to catch.</para>
    /// </summary>
    Supersede,
}

/// <summary>Why a score could not be recalculated. Every one of these is reported, never hidden.</summary>
public enum SkipReason
{
    None,

    /// <summary>The row has no stored replay, so there is nothing to re-derive from.</summary>
    NoReplay,

    /// <summary>The stored bytes did not decode as a type!beat replay.</summary>
    UndecodableReplay,

    /// <summary>
    /// The .osu the run was judged against is not in any package the tool could fetch, but the
    /// row's beatmap still hashes to it. So the map has NOT changed and this is a fetch failure:
    /// a cold cache, an unreachable site, a package route that 404s. It is fixable by re-running
    /// somewhere the package is reachable, which is why a supersede APPLY refuses to proceed while
    /// any of these are outstanding: leaving them behind produces a half-superseded leaderboard
    /// that looks complete.
    /// </summary>
    BeatmapUnavailable,

    /// <summary>
    /// The row's beatmap exists and is fetchable, but it no longer hashes to what the replay names,
    /// i.e. the set was re-uploaded after the play. The exact .osu the run was judged against is
    /// gone, and judging the run against the map's CURRENT cells would score a different map, so
    /// the row is left alone. Unlike <see cref="BeatmapUnavailable"/> this is a fact about the data
    /// and retrying cannot change it.
    /// </summary>
    BeatmapReuploaded,

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
    ///
    /// <para><see cref="RecalcMode.Reproduce"/> only. In a supersede sweep this is EXPECTED and is
    /// reported as a diagnostic instead of refusing, which is precisely why the two modes are
    /// separate commands: were it one gate with a threshold, the threshold would have to be loose
    /// enough to pass a sweep that reproduces very little, and would then pass genuine corruption
    /// too.</para>
    ///
    /// <para>In a REPRODUCE sweep this reason should now be rare and interesting rather than
    /// universal. Backlog 151 gave the harness the two missing era switches (the spacebar and the
    /// rate windows), so a row from before the backlog 133 arc reproduces again; backlog 155 stopped
    /// it re-grading a row judged since backlog 126 on the retired typo rule; and backlog 156 stopped
    /// it re-grading a row played since the 2026-08-13 release on the pre-release windows, by proving
    /// each such row's window era by reconstruction. What is left here is a row from the 133-to-147
    /// window, whose ladder no longer exists in any form, or a genuine disagreement worth looking at,
    /// and a row reaching this reason now means NO combination of the expressible eras re-derived it.
    /// Backlog 157 folded combo restore into that search as well, which retired the last axis the
    /// pass held at one value for the whole table, and backlog 158 folded in the typo rule for rows
    /// whose keys prove nothing about it, which retired the last axis that resolved by assumption.</para>
    /// </summary>
    NotReproducible,

    /// <summary>
    /// <see cref="RecalcMode.Supersede"/>'s refusal, and the reason superseding still has teeth. The
    /// re-judged account does not describe the same RUN over the same MAP as the stored row: a
    /// different cell count, a different number of cells judged, or replay frames the engine never
    /// consumed. The judgement of a run is allowed to move under a supersede sweep. What the run
    /// WAS is not, and a row that disagrees about that would have its new numbers guessed rather
    /// than derived. Nothing is written for it.
    /// </summary>
    NotTheSameRun,
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
    bool PpSettled,
    // Which sweep produced this, since the two value total_score and check themselves differently
    // and a report of one must never be read as a report of the other.
    RecalcMode Mode = RecalcMode.Reproduce,
    // Whether the OLD-rule re-derivation matched the stored row exactly. A gate in Reproduce mode
    // and a pure diagnostic in Supersede mode, where a false here is the expected reading for every
    // row judged before backlog 133.
    bool Reproduced = false,
    // Why it did not, when it did not. Populated in both modes; only acted on in Reproduce.
    string? ReproductionDetail = null,
    // The mod score multiplier baked into NewTotalScore. In Reproduce mode this is the row's own,
    // recovered rather than reapplied; in Supersede mode it is today's, because a superseded score
    // has to be one today's client could produce.
    double AppliedMultiplier = 1,
    // The spacebar, rate-window, combo-restore and typo era the old-rule arm reproduced this row
    // under (backlog 156, joined by combo restore in 157 and by the typo rule in 158). NULL means no
    // era did, which is either a row the sweep never re-derived at all (no replay, no beatmap, a
    // failed run) or a row that reproduced under NONE of the combinations. Both of those are absences
    // and neither is an era, which is why this is nullable rather than defaulted: a default here would
    // put an era on a row nothing proved one for, and that is the exact thing backlog 156 refuses to do.
    SearchedEra? ReproducedUnderEra = null)
{
    public bool Recalculated => Skip == SkipReason.None;

    /// <summary>
    /// Whether this row's era had to be PROVED BY RECONSTRUCTION (backlog 156, backlog 157, backlog
    /// 158), i.e. the row did not come back under the era its own evidence starts it at and the pass
    /// re-derived it under the remaining combinations until one reproduced it exactly.
    ///
    /// <para>Derived rather than stored, because it is the same fact: the starting point is tried
    /// first and the search only ever runs when it fails, so an era other than that one can only have
    /// come from the search. Keeping it as a second flag would let the two disagree.</para>
    ///
    /// <para>THE STARTING POINT IS PER ROW, NOT <see cref="Recalculation.DefaultEra"/>, which matters
    /// since backlog 158 put the typo axis in the search. A row whose <c>good</c> key PROVES the
    /// deferred rule starts on that arm (see <see cref="Recalculation.DefaultEraFor"/>) and never has
    /// the axis searched, so comparing against the table-wide default would report every one of those
    /// rows as reconstructed when nothing was searched for them at all.</para>
    /// </summary>
    public bool EraProvedByReconstruction
        => ReproducedUnderEra is SearchedEra era && era != Recalculation.DefaultEraFor(Stored);

    /// <summary>
    /// The row could not be re-derived and the operator therefore has to say what should happen to
    /// it. <see cref="SkipReason.NotReproducible"/> and <see cref="SkipReason.NotTheSameRun"/> are
    /// deliberately NOT in here: those are refusals, not absences, and the answer to them is to
    /// investigate, never to rewrite the row.
    /// </summary>
    public bool Unreplayable => Skip is SkipReason.NoReplay
                                     or SkipReason.UndecodableReplay
                                     or SkipReason.EmptyReplay
                                     or SkipReason.BeatmapUnavailable
                                     or SkipReason.BeatmapReuploaded
                                     or SkipReason.FailedRun;

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
    ///
    /// <para>The attribution only means anything when the old-rule arm REPRODUCED, so it is the
    /// figure a reproduce sweep reports and a supersede sweep does not: there, the old-rule
    /// statistics are today's code's reading of a retired ladder, not the row's own numbers, and
    /// the honest before/after is <c>stored.Pp -> NewPp</c>.</para>
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
/// The recalculation itself: replay the run twice, once under the rules it was priced under and
/// once under the rules the mode is asking about, then value the result with the server's own
/// contract.
///
/// <para>Nothing here reimplements a judgement rule or a scoring formula. The statistics come from
/// <see cref="TypeBeatReplayScorer"/> (the game's engine driven into the game's score processor),
/// and everything derived from them comes from <see cref="ScoringContract"/> and
/// <see cref="PerformancePoints"/>, i.e. from exactly the code that priced the row in the first
/// place.</para>
///
/// <para>The FIRST replay is the difference between the two modes. In
/// <see cref="RecalcMode.Reproduce"/> it is a proof and a mismatch refuses the row; in
/// <see cref="RecalcMode.Supersede"/> it is a diagnostic, a mismatch is the expected reading, and
/// <see cref="StructuralMismatch"/> refuses instead. That is the inversion backlog 142 called for,
/// kept as two predicates rather than one with a threshold: a threshold loose enough to pass a
/// sweep where little reproduces would pass genuine corruption too.</para>
///
/// <para>What the first replay can reproduce is a question about ERAS, and every rule that has moved
/// since a row was stored has to be expressible or the pass reports rule drift as corruption. There
/// are seven such axes, all of them set on that pass to the era that judged the row: the typo rule
/// (backlog 109), combo restore (140), the untimed spacebar (148), the rate-scaled windows (150),
/// what an off-time press costs (199), what a CORRECTED typo's cell is worth (210) and what an
/// UNCORRECTED one is worth (213).
/// HOW THAT ERA IS DECIDED DIFFERS BY AXIS, in two ways, and the difference is the whole of what
/// makes the pass a proof rather than a guess:</para>
///
/// <list type="bullet">
/// <item>The TYPO rule is READ OFF THE ROW WHERE THE ROW CAN PROVE IT (<see cref="StoredEraTypoRuleFor"/>,
/// backlog 155). It moved while the table was already filling, so the table holds rows from both sides
/// of it, and a typo LEFT STANDING takes a key of its own that only the deferred rule can produce. That
/// key is a proof, it pins the row, and <see cref="EraSearchFor"/> then withholds every candidate on the
/// other arm, so a proved row never has this axis searched.</item>
/// <item>The SPACEBAR, the RATE WINDOWS, COMBO RESTORE, THE OFF-TIME RULE, and the TYPO RULE WHERE THE
/// ROW PROVES NOTHING, are PROVED BY RECONSTRUCTION (<see cref="EraSearch"/>, backlog 156 for the first
/// two, 157 for the third, 158 for the typo case and 199 for the off-time rule). None of the four
/// keyless ones ever leaves a key; the typo rule leaves none either
/// when the typo was CORRECTED, since the key marks only a cell left holding a wrong character. So
/// there is nothing to read: the pass re-derives a row that does not come back under
/// <see cref="DefaultEraFor"/> under each remaining combination and pins it to the one that reproduces
/// it exactly.</item>
/// </list>
///
/// <para>NO AXIS IS A CONSTANT ANY MORE, which is what backlog 157 finished, and no axis silently
/// falls back on a default either, which is what backlog 158 finished. Combo restore was the last axis
/// held at a single value for the whole table; the typo rule was the last whose UNPROVED rows resolved
/// to one arm by assumption. Production disproved both premises the same way: rows exist whose
/// re-derivation under the FULL live rule set matches stored in every field, and which the reproduce
/// pass still could not express.</para>
///
/// <para>The one era that is NOT expressible is the backlog 133-to-147 window, whose four-tier
/// character-distance ladder backlog 147 deleted: those rows cannot be reproduced by any setting of
/// any switch, because the code that judged them no longer exists.</para>
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
    /// The typo era a stored row is judged in when the row carries no proof of the other one (backlog
    /// 109): a wrong character spent its cell's one result on a Miss the instant it landed.
    ///
    /// <para>UNLIKE THE OTHER AXES THIS IS NOT TRUE OF EVERY ROW IN THE TABLE, which is the
    /// whole of backlog 155. The other rules moved after the rows were already stored, so
    /// "the stored era" is a constant for them. This one moved WHILE the table was filling: the
    /// deferred rule has been the only rule live play uses since backlog 109, and since backlog 126
    /// the cell it leaves standing has a key of its own, so the table holds rows from both sides of
    /// it. Applying this constant to all of them re-graded every recent row on a rule it was never
    /// played under and reported the difference as a corrupt row: each uncorrected typo came back a
    /// miss, so the re-derived misses came out at exactly stored miss + stored good.</para>
    ///
    /// <para>It stays the DEFAULT rather than becoming the exception because the evidence only points
    /// one way (see <see cref="StoredEraTypoRuleFor"/>), and because a row with no typo in it is
    /// judged identically by both rules, so the default costs nothing on the rows it cannot prove
    /// anything about.</para>
    ///
    /// <para>SINCE BACKLOG 158 IT IS ONLY A STARTING POINT for a row that cannot prove its typo era,
    /// exactly as the other axes' constants are. The rows it cannot prove anything about are not
    /// all rows with no typo in them: a typo that was CORRECTED leaves no key either, and those two
    /// rules do NOT agree about such a cell (the older one already spent it on a Miss). A row this
    /// default does not reproduce therefore goes to <see cref="EraSearch"/> on this axis too. A row
    /// whose key PROVES the other arm never sees this value at all.</para>
    /// </summary>
    private const TypoRule stored_era_typo_rule = TypoRule.ImmediateMiss;

    /// <summary>
    /// The typo rule live play uses (backlog 109, and the only rule any client has offered since),
    /// and therefore the one a <see cref="RecalcMode.Supersede"/> sweep re-judges under, the one a
    /// <see cref="RecalcMode.Reproduce"/> sweep reports the effect of, AND the one a row judged since
    /// backlog 126 has to be REPRODUCED under, because it is the rule that judged it.
    /// </summary>
    private const TypoRule live_typo_rule = TypoRule.Deferred;

    /// <summary>
    /// The typo rule to TRY FIRST when reproducing this row: the one that actually judged it, as far
    /// as the row itself can prove.
    ///
    /// <para>The proof is <see cref="StoredScore.ProvablyJudgedUnderTheDeferredTypoRule"/> and it runs
    /// in ONE DIRECTION. A row holding an uncorrected typo can only have come from a client running
    /// <see cref="live_typo_rule"/>, so that row is PINNED to it and <see cref="EraSearchFor"/> offers
    /// it nothing else. A row without one is not evidence of <see cref="stored_era_typo_rule"/>, it is
    /// evidence of nothing, so it STARTS there.</para>
    ///
    /// <para>THE WORD "STARTS" IS THE WHOLE OF BACKLOG 158. This used to be the last word for an
    /// unprovable row, on the reading that for a run with no typo in it the two rules are the same
    /// judgement anyway. That is true of a run with no typo and false of a run whose typo was
    /// CORRECTED: the key marks only a cell left holding a wrong character, so a corrected typo is
    /// unprovable AND graded differently by the two rules (the older one already spent the cell on a
    /// Miss). Those rows were the last thing production could not explain. An unprovable row now
    /// starts here and, if it does not come back, has the axis searched by
    /// <see cref="EraSearchFor"/>.</para>
    /// </summary>
    public static TypoRule StoredEraTypoRuleFor(StoredScore stored)
        => stored.ProvablyJudgedUnderTheDeferredTypoRule ? live_typo_rule : stored_era_typo_rule;

    /// <summary>
    /// The combo-restore era the reproduce pass TRIES FIRST (backlog 140): the break a wrong keypress
    /// took was permanent, and correcting the cell started a fresh run from zero.
    ///
    /// <para>IT WAS AN ASSERTION ABOUT EVERY ROW UNTIL BACKLOG 157, on the premise that backlog 140
    /// shipped after the last row that could care was stored. Production disproved it: scores 5410 and
    /// 5414 come back from the supersede pass with <c>Moves = false</c>, i.e. re-derived under the FULL
    /// live rule set they match what is stored in every field, so the harness understands those runs
    /// completely and only the reproduce pass could not say which era judged them. Pinned here for
    /// every row, this axis is one of the reasons it could not.</para>
    ///
    /// <para>WHAT THAT LOOKED LIKE IS WORTH KNOWING, because it is a trap. Such a row prints the
    /// DEFAULT arm's mismatch (see <see cref="EraSearch"/>), which carries a window-width signature
    /// whatever the row's real disagreement is, so the detail line sends a reader looking for a fifth
    /// window rule that does not exist. Every arm with the right windows and this axis at the wrong
    /// arm re-derives the tiers EXACTLY and leaves <c>max_combo</c> short, which qualifies as nothing:
    /// reproducing is <c>statistics</c> and <c>max_combo</c> together.</para>
    ///
    /// <para>So it is now a starting point, exactly like the spacebar and the rate windows. A row this
    /// default does not reproduce goes to <see cref="EraSearch"/>, and the majority of the table, which
    /// does come back under it, costs and means precisely what it did before.</para>
    /// </summary>
    private const ComboRestoreRule stored_era_combo_rule = ComboRestoreRule.Never;

    /// <summary>
    /// The combo-restore rule live play uses, and therefore the one a
    /// <see cref="RecalcMode.Supersede"/> sweep re-judges under (backlog 136, decided 2026-08-13).
    ///
    /// <para>This is the axis backlog 140 said would never move retroactively, and the user has
    /// since decided it must, for one reason: the alternative combination, today's judgement tiers
    /// with the old combo rule, is one no version of the client has ever run, so a score judged that
    /// way could not be reproduced by replaying it anywhere. The cost is deliberate and is the thing
    /// the report has to make visible before anyone applies it: every stored score containing a
    /// FIXED typo gains max_combo, total_score and pp, and a fixed typo ends up scoring identically
    /// to a clean play.</para>
    ///
    /// <para>Kept as a separate constant from <see cref="stored_era_combo_rule"/> rather than
    /// flipping that one, because both sweeps must stay expressible: reproduction is still how the
    /// tool verifies it understands a row, and it can only do that under the rules the row was
    /// played under. Since backlog 157 it is also one of the two arms <see cref="EraSearch"/> tries,
    /// because a row played since backlog 140 was judged under it.</para>
    /// </summary>
    private const ComboRestoreRule live_combo_rule = ComboRestoreRule.OnFix;

    /// <summary>
    /// The space-timing era the reproduce pass TRIES FIRST (backlog 148, backlog 151): the spacebar
    /// was inside the timing challenge, which is how every row stored before the 2026-08-13 release
    /// was graded.
    ///
    /// <para>This is the WIDEST of the four era axes, because every map has spaces. Judged on the
    /// wrong rule a row's spaces all move tier and stop or start breaking combo, so
    /// <c>statistics</c> and <c>max_combo</c> both move and the reproduce pass fails on rows that are
    /// not corrupt at all. Before this constant existed the pass could not reproduce ANY row in the
    /// table, and the failure presented as "these replays are corrupt" when in fact the rules had
    /// moved underneath them.</para>
    ///
    /// <para>IT IS NO LONGER TRUE OF EVERY ROW, which is the whole of backlog 156, and is why this is
    /// a starting point rather than an assertion. Backlog 148 shipped while the table was filling, so
    /// a client updated since then submits rows graded the other way, and the population GROWS with
    /// every play. A row this default does not reproduce goes to <see cref="EraSearch"/>.</para>
    /// </summary>
    private const SpaceTimingRule stored_era_space_rule = SpaceTimingRule.Timed;

    /// <summary>
    /// The space-timing rule live play uses, and therefore the one a
    /// <see cref="RecalcMode.Supersede"/> sweep re-judges under, and the one a row played since the
    /// release has to be REPRODUCED under, because it is the rule that judged it.
    /// </summary>
    private const SpaceTimingRule live_space_rule = SpaceTimingRule.Untimed;

    /// <summary>
    /// The rate-window era the reproduce pass TRIES FIRST (backlog 150, backlog 151): the rate mods
    /// have been ranked for the whole life of the score table, and until the 2026-08-13 release none
    /// of them scaled the judgement windows, so a DT / NC / HT row stored before it was graded on
    /// windows fixed in BEATMAP milliseconds.
    ///
    /// <para>Set unconditionally rather than only for rows that carry a rate mod: the arm it gates is
    /// a loop over the run's rate mods, which is empty for everything else, so the axis is inert for
    /// a row it does not apply to (pinned by the game's <c>JudgementEraTest</c>). Deciding per row
    /// would mean parsing the mods twice and getting the same answer. That inertness is also why the
    /// search below can leave this axis ambiguous on a row with no rate mod without it mattering.</para>
    ///
    /// <para>Like the spacebar, a STARTING POINT rather than an assertion since backlog 156: a rate
    /// row played since the release was graded on windows scaled by its clock rate.</para>
    /// </summary>
    private const RateWindowRule stored_era_rate_rule = RateWindowRule.Unscaled;

    /// <summary>
    /// The rate-window rule live play uses, and therefore the one a
    /// <see cref="RecalcMode.Supersede"/> sweep re-judges under, and the one a rate row played since
    /// the release has to be REPRODUCED under.
    /// </summary>
    private const RateWindowRule live_rate_rule = RateWindowRule.ScaledByRate;

    /// <summary>
    /// The off-time era the reproduce pass TRIES FIRST (backlog 199): a right character struck
    /// outside the outermost Meh window was a BREAK. The engine zeroed the run, discarded any
    /// outstanding restorable claim, and the cell took a Miss that carried the break into osu's
    /// combo, counted against the miss statistic and cost completion and rank.
    ///
    /// <para>True of every row in the table at the moment backlog 199 shipped, and (like the
    /// spacebar before it) it stops being true of every row the day a client carrying the new rule
    /// submits one, which is why it is a STARTING POINT rather than an assertion. A row this default
    /// does not reproduce goes to <see cref="EraSearch"/> on this axis exactly as it does on the
    /// other four.</para>
    ///
    /// <para>AS WIDE AS THE SPACEBAR AND WIDER THAN THE REST. It reaches every row that ever fumbled
    /// a beat, so judged on the wrong arm a row's mistimed presses all change result (Miss against
    /// Meh) and stop or start breaking the run: <c>statistics</c> and <c>max_combo</c> both move,
    /// which are exactly the two quantities the reproduction gate compares.</para>
    /// </summary>
    private const OffTimeRule stored_era_off_time_rule = OffTimeRule.BreaksCombo;

    /// <summary>
    /// The off-time rule live play uses (backlog 199), and therefore the one a
    /// <see cref="RecalcMode.Supersede"/> sweep re-judges under, and the one a row played since it
    /// shipped has to be REPRODUCED under, because it is the rule that judged it: the press is an
    /// accepted character worth zero points that extends the run, and its cell resolves as a Meh, so
    /// accuracy is the whole of what it costs.
    /// </summary>
    private const OffTimeRule live_off_time_rule = OffTimeRule.MehHit;

    /// <summary>
    /// The correction-credit era the reproduce pass TRIES FIRST (backlog 210): a cell that held a
    /// wrong character before it was ever judged was graded on the RETYPE's own timing and nothing
    /// else, so a fix struck inside the Great window was worth a full 300 and the typo cost the play
    /// no accuracy at all.
    ///
    /// <para>True of every row in the table at the moment backlog 210 shipped, and (like the
    /// spacebar and the off-time rule before it) it stops being true of every row the day a client
    /// carrying the cap submits one, which is why it is a STARTING POINT rather than an assertion. A
    /// row this default does not reproduce goes to <see cref="EraSearch"/> on this axis exactly as it
    /// does on the other six.</para>
    ///
    /// <para>THE NARROWEST OF THE SIX THE GATE CAN SEE IN WHAT IT MOVES, though not in what it
    /// reaches. It touches
    /// only rows that corrected a typo, and for those it moves the TIER COUNTS alone: a corrected
    /// cell's <c>great</c> becomes an <c>ok</c>. <c>max_combo</c> is identical under both arms, so
    /// the reproduction gate catches this axis on <c>statistics</c> only, which is exactly the seam
    /// that made a corrected typo invisible before backlog 158 (the corrected cell leaves no key of
    /// its own).</para>
    /// </summary>
    private const CorrectionCreditRule stored_era_credit_rule = CorrectionCreditRule.Full;

    /// <summary>
    /// The correction-credit rule live play uses (backlog 210), and therefore the one a
    /// <see cref="RecalcMode.Supersede"/> sweep re-judges under, and the one a row played since it
    /// shipped has to be REPRODUCED under: a corrected cell resolves at min(the retype's own tier,
    /// Ok), so perfect play strictly beats corrected play per cell.
    /// </summary>
    private const CorrectionCreditRule live_credit_rule = CorrectionCreditRule.Capped;

    /// <summary>
    /// The worth era the reproduce pass TRIES FIRST (backlog 213): a cell left holding a wrong
    /// character was re-weighted to a Meh's 50 of 300, the cheapest a JUDGED cell could be, rather
    /// than to a miss's 0.
    ///
    /// <para>True of every row in the table at the moment backlog 213 shipped, and it stops being
    /// true of every row the day a client carrying the fold submits one, exactly as the spacebar,
    /// the off-time rule and the correction cap did before it.</para>
    ///
    /// <para>UNLIKE THE OTHER SIX, THE SEARCH CANNOT PROVE THIS ONE. The reproduction gate compares
    /// <c>statistics</c> and <c>max_combo</c>, and this axis moves neither (see the note on
    /// <see cref="SearchedEra"/>): the fold is a consumer-side reclassification and the seal's key
    /// never moved, so both arms re-derive byte-identical counts and the axis resolves purely by
    /// this list's ORDER. That makes it a genuine STARTING POINT and not merely a preference: it is
    /// where a row stays unless a later candidate reproduces it on some OTHER axis, and the row's
    /// worth arm then rides along with whichever candidate that was.</para>
    ///
    /// <para>What the arm actually decides is the old-rule arm's <c>total_score</c>, and through it
    /// the multiplier this row was priced with. Getting it wrong on a pre-213 row would recover a
    /// "multiplier" that is really the fold wearing a multiplier's clothes, which is the same
    /// mistake <see cref="RecalcMode.Supersede"/> refuses to make with the ladder change.</para>
    /// </summary>
    private const UnfixedTypoWorthRule stored_era_worth_rule = UnfixedTypoWorthRule.MehCredit;

    /// <summary>
    /// The worth rule live play uses (backlog 213), and therefore the one a
    /// <see cref="RecalcMode.Supersede"/> sweep re-judges under, and the one a row played since it
    /// shipped has to be REPRODUCED under: an uncorrected typo is worth a miss's 0 of its cell's
    /// 300, because the player did not put that character in that cell.
    ///
    /// <para>It is also, unconditionally, what the SERVER's <c>ScoringContract</c> prices with: that
    /// table has no era arm, so every accuracy, rank and ceiling this tool derives from a re-judged
    /// account is a live-worth number whichever arm produced the account.</para>
    /// </summary>
    private const UnfixedTypoWorthRule live_worth_rule = UnfixedTypoWorthRule.Nothing;

    /// <summary>
    /// The era the reproduce pass tries first for a row that proves nothing, and the only one it tries
    /// for a row that comes back under it: the oldest of them all, with the spacebar inside the timing
    /// challenge, the windows unscaled by the rate, no combo given back for a corrected typo, a
    /// wrong character spending its cell on a Miss the instant it lands, a mistimed one doing the
    /// same and a corrected one graded on its retype's timing alone. Every row stored before
    /// backlog 109 is in this era, and it remains the overwhelming majority of the table, so the search
    /// below costs the table nothing.
    ///
    /// <para>NOT the starting point for EVERY row, since backlog 158 put the typo axis in the search:
    /// a row whose <c>good</c> key proves the deferred rule starts on that arm instead. Use
    /// <see cref="DefaultEraFor"/> whenever the question is "what did THIS row start at".</para>
    /// </summary>
    public static readonly SearchedEra DefaultEra = new(stored_era_space_rule, stored_era_rate_rule, stored_era_combo_rule, stored_era_typo_rule, stored_era_off_time_rule, stored_era_credit_rule, stored_era_worth_rule);

    /// <summary>
    /// The era THIS row is tried first under, which is <see cref="DefaultEra"/> with the typo axis set
    /// to whatever the row can prove about it (<see cref="StoredEraTypoRuleFor"/>). Always the first
    /// element of <see cref="EraSearchFor"/>, which is asserted in the test suite rather than left as a
    /// coincidence of two lists agreeing.
    ///
    /// <para>It exists because backlog 158 made the starting point a per-row question. Before it the
    /// typo axis was applied outside the search entirely, so "the era tried first" was one value for
    /// the whole table and <see cref="DefaultEra"/> was it. Now a row whose key PROVES the deferred
    /// rule starts on the deferred arm, and calling that a reconstruction would be wrong twice over:
    /// nothing was searched for it, and the thing that decided it was a proof, not a re-derivation.</para>
    /// </summary>
    public static SearchedEra DefaultEraFor(StoredScore stored)
        => DefaultEra with { Typo = StoredEraTypoRuleFor(stored) };

    /// <summary>
    /// The four-axis order backlog 158 left behind, every entry on the OLDER off-time arm and the
    /// OLDER credit arm: the
    /// sixteen combinations of the spacebar, the rate windows, combo restore and the typo rule, with
    /// the four a real client shipped ahead of the twelve corners no build ever offered.
    ///
    /// <para>Kept as its own list, and kept in its own order, because <see cref="EraSearch"/> is
    /// exactly this list with the off-time and credit axes expanded over it (see the note there).
    /// Fold the expansions into the literals and those two axes would be indistinguishable from the
    /// other four in the source, which is the one thing that must stay visible: they are the axes
    /// whose arms are interleaved rather than listed.</para>
    /// </summary>
    private static readonly IReadOnlyList<SearchedEra> four_axis_order = new[]
    {
        // The four eras a real client shipped on these axes. Ordered so that the deferred-arm entries
        // alone read as backlog 157's list did: everything old, then the live windows and combo rule,
        // then the middle era.
        DefaultEra,
        new SearchedEra(stored_era_space_rule, stored_era_rate_rule, stored_era_combo_rule, live_typo_rule, stored_era_off_time_rule, stored_era_credit_rule, stored_era_worth_rule),
        new SearchedEra(live_space_rule, live_rate_rule, live_combo_rule, live_typo_rule, stored_era_off_time_rule, stored_era_credit_rule, stored_era_worth_rule),
        new SearchedEra(stored_era_space_rule, stored_era_rate_rule, live_combo_rule, live_typo_rule, stored_era_off_time_rule, stored_era_credit_rule, stored_era_worth_rule),

        // The twelve corners no build ever offered, which are searched anyway: each axis is an
        // independent fact about how a press was graded, and the next change to one of them has no
        // reason to move the others. The typo rule is in here for the same reason the other three are,
        // and only ever reaches a row whose keys prove nothing about it (see EraSearchFor).
        new SearchedEra(live_space_rule, live_rate_rule, live_combo_rule, stored_era_typo_rule, stored_era_off_time_rule, stored_era_credit_rule, stored_era_worth_rule),
        new SearchedEra(stored_era_space_rule, stored_era_rate_rule, live_combo_rule, stored_era_typo_rule, stored_era_off_time_rule, stored_era_credit_rule, stored_era_worth_rule),
        new SearchedEra(live_space_rule, live_rate_rule, stored_era_combo_rule, stored_era_typo_rule, stored_era_off_time_rule, stored_era_credit_rule, stored_era_worth_rule),
        new SearchedEra(live_space_rule, live_rate_rule, stored_era_combo_rule, live_typo_rule, stored_era_off_time_rule, stored_era_credit_rule, stored_era_worth_rule),
        new SearchedEra(live_space_rule, stored_era_rate_rule, stored_era_combo_rule, stored_era_typo_rule, stored_era_off_time_rule, stored_era_credit_rule, stored_era_worth_rule),
        new SearchedEra(live_space_rule, stored_era_rate_rule, stored_era_combo_rule, live_typo_rule, stored_era_off_time_rule, stored_era_credit_rule, stored_era_worth_rule),
        new SearchedEra(live_space_rule, stored_era_rate_rule, live_combo_rule, stored_era_typo_rule, stored_era_off_time_rule, stored_era_credit_rule, stored_era_worth_rule),
        new SearchedEra(live_space_rule, stored_era_rate_rule, live_combo_rule, live_typo_rule, stored_era_off_time_rule, stored_era_credit_rule, stored_era_worth_rule),
        new SearchedEra(stored_era_space_rule, live_rate_rule, stored_era_combo_rule, stored_era_typo_rule, stored_era_off_time_rule, stored_era_credit_rule, stored_era_worth_rule),
        new SearchedEra(stored_era_space_rule, live_rate_rule, stored_era_combo_rule, live_typo_rule, stored_era_off_time_rule, stored_era_credit_rule, stored_era_worth_rule),
        new SearchedEra(stored_era_space_rule, live_rate_rule, live_combo_rule, stored_era_typo_rule, stored_era_off_time_rule, stored_era_credit_rule, stored_era_worth_rule),
        new SearchedEra(stored_era_space_rule, live_rate_rule, live_combo_rule, live_typo_rule, stored_era_off_time_rule, stored_era_credit_rule, stored_era_worth_rule),
    };

    /// <summary>
    /// The eras a row is re-derived under, IN ORDER, until one reproduces it exactly. This is backlog
    /// 156's answer to the era axes that have no key to read, extended by backlog 157 to the third of
    /// them and by backlog 199 to the fifth: PROVE the era by reconstruction instead of inferring
    /// it.
    ///
    /// <para>WHY NOT A TIMESTAMP, which is the obvious alternative and the wrong one. The release
    /// instant is known exactly (the web push IS the deploy), but the SERVER deploying is not the
    /// CLIENT updating: the game ships by packing Windows locally and dispatching CI, and players
    /// update whenever they update, so an old build goes on submitting old-era statistics for hours
    /// or days afterwards. A timestamp boundary would misclassify exactly those rows, and silently,
    /// since nothing in the output would show which side of the boundary a row fell. Reconstruction
    /// needs no release time, no schema change and no trust in a clock.</para>
    ///
    /// <para>WHY THE ORDER, and why the ambiguity it resolves is harmless. Reproducing means
    /// re-deriving <c>statistics</c> and <c>max_combo</c> EXACTLY, which are the two quantities the
    /// server stores verbatim from the client and everything else is derived from, so two
    /// combinations that both reproduce a row have both re-derived it to the same numbers: nothing
    /// downstream can tell which one was picked. Ambiguity is not rare either, it is the normal case,
    /// and adding the combo axis made it commoner still: the rate axis is inert on a row with no rate
    /// mod, the space axis is inert on a map with no spaces, and the combo axis is inert on a run with
    /// no typo corrected in it, which is most runs, and the off-time axis on a run that never struck
    /// a character off the ladder. The order therefore exists to make the choice
    /// DETERMINISTIC and the report's label stable, not to make it correct.</para>
    ///
    /// <para>WHAT THE ORDER PREFERS is the eras a client has ACTUALLY RUN, ahead of the corners no
    /// build ever offered. There are five of those, and they are a timeline rather than a pair,
    /// because the axes did not all move at once: everything old (before backlog 109), the deferred
    /// typo rule on its own (109 to 140), the middle era where the typo rule and combo restore had
    /// shipped and the windows had not (140 to the 2026-08-13 release), the windows and combo
    /// restore live with an off-time press still breaking (the release to backlog 199), and
    /// everything live (since 199). <see cref="four_axis_order"/> holds that preference on the four
    /// older axes, listing everything old, then 109-to-140, then the live windows, then the middle
    /// era, and the mixtures follow it there, keeping backlog 156's window-pair order, then backlog
    /// 157's older-combo-arm-first rule, then the older typo arm before the newer one within
    /// each.</para>
    ///
    /// <para>THE OFF-TIME AXIS IS EXPANDED IN PLACE (backlog 199) rather than written out as
    /// thirty-two literals: each of those sixteen combinations is emitted twice, older arm first.
    /// That is not a shortcut. It is what keeps the filtering property below true, since a
    /// four-axis combination's two off-time arms sit together wherever that combination sits, and
    /// it puts the era that SUCCEEDS a shipped one directly after it, so the two all-live entries
    /// (before and since 199) are neighbours. What it costs is that a corner can precede a shipped
    /// era belonging to a LATER four-axis combination, e.g. the pre-109 rules with today's off-time
    /// rule, a pairing no build ever shipped, sits second. That reaches nothing: a row reproducing
    /// under both that corner and a shipped era later in the list is a row indifferent to every axis
    /// they disagree on, so the two have re-derived it to the same numbers and only the label
    /// differs.</para>
    ///
    /// <para>THE CREDIT AXIS IS EXPANDED THE SAME WAY (backlog 210), over the off-time expansion
    /// rather than under it, which doubles the list again to sixty-four. Same shape, same reason,
    /// and the two properties it has to keep both survive: each of the thirty-two entries is still
    /// immediately followed by its own credit twin, so filtering on any other axis still reads the
    /// remaining ones in their own order, and the two all-live entries (before and since 210) are
    /// again neighbours at the end of the first four-axis group's block. The timeline the shipped
    /// eras form now has six points rather than five, the sixth being backlog 210.</para>
    ///
    /// <para>THE WORTH AXIS IS EXPANDED THE SAME WAY AGAIN (backlog 213), over the credit expansion,
    /// which doubles the list to a hundred and twenty-eight. Same shape, same reason, and both
    /// properties survive once more: each of the sixty-four entries is immediately followed by its
    /// own worth twin, and the two all-live entries (before and since 213) are neighbours. The
    /// timeline the shipped eras form has seven points, the seventh being backlog 213.</para>
    ///
    /// <para>IT IS THE ONE AXIS THE SEARCH CANNOT DECIDE, and expanding it is therefore cheap in
    /// meaning as well as in time: the two arms re-derive byte-identical <c>statistics</c> and
    /// <c>max_combo</c> (the fold is a consumer-side reclassification, so nothing about the account
    /// this gate reads moves with it), so whichever candidate reproduces a row, its worth twin
    /// reproduces it too and the FIRST of the pair always wins. The stored arm is first, which is
    /// what the axis exists to get right. See the note on <see cref="SearchedEra"/> for what would
    /// have to change to make it decidable.</para>
    ///
    /// <para>THE ORDER IS BUILT SO THAT FILTERING IT DOES NOT DISTURB IT, which is what
    /// <see cref="EraSearchFor"/> does to pin a proved row. Read only the deferred-arm entries and you
    /// get backlog 157's eight combinations, each expanded over the off-time axis, in backlog 157's
    /// order; read only the older-arm entries and you get the same sixteen in the same order. So
    /// neither population's label can move because the other population's arms were interleaved
    /// between them.</para>
    ///
    /// <para>THE DEFAULT STAYS FIRST, which is what keeps the cost on the residual: a row that comes
    /// back under it is never re-derived a second time, so the majority of the table pays exactly what
    /// it paid before this search existed and its result means exactly what it meant before. "The
    /// default" is per row on the typo axis (see <see cref="DefaultEraFor"/>), so this holds for a
    /// proved row too.</para>
    ///
    /// <para>WHAT IT DOES NOT DO: assign an era to a row no combination reproduces. That row keeps the
    /// default arm's mismatch and reports as unexplained exactly as it does today. A search that
    /// always finds an answer would turn the reproduce pass from a proof into a shrug.</para>
    /// </summary>
    public static readonly IReadOnlyList<SearchedEra> EraSearch = four_axis_order
        .SelectMany(era => new[] { era, era with { OffTime = live_off_time_rule } })
        .SelectMany(era => new[] { era, era with { Credit = live_credit_rule } })
        .SelectMany(era => new[] { era, era with { Worth = live_worth_rule } })
        .ToArray();

    /// <summary>
    /// The eras THIS row is re-derived under, in order, which is <see cref="EraSearch"/> with the typo
    /// axis PINNED away when the row can prove it (backlog 158, composing backlog 155).
    ///
    /// <para>THE PROOF WINS AND IS NOT RE-LITIGATED. A row carrying a <c>good</c> key was judged by a
    /// client running <see cref="live_typo_rule"/>, because no other rule can leave a cell holding a
    /// wrong character, so that row is handed only the sixteen candidates on that arm. The other
    /// sixteen
    /// are not tried, not preferred over and not compared against: an axis a row can be ASKED about is
    /// not an axis to search, and searching it would let a coincidence outvote a proof.</para>
    ///
    /// <para>THE ABSENCE OF THE KEY IS WHAT GOT SEARCHED, and it is the only thing backlog 158 changed.
    /// Backlog 155 documented that absence as "no proof either way", never as evidence of the older
    /// rule, but the pass then resolved it to the older rule anyway because it had nothing else to do
    /// with it. The rows where that was wrong are exactly the rows whose typo was CORRECTED: the key
    /// marks only a cell left standing, so a corrected typo proves nothing AND is graded differently by
    /// the two rules. Such a row now gets all thirty-two candidates, still starting at
    /// <see cref="DefaultEra"/>, and is pinned to the arm that reproduces it.</para>
    ///
    /// <para>AMBIGUITY ON THIS AXIS IS THE NORMAL CASE AND IS HARMLESS, more so than on any of the
    /// other four. A run with no typo in it at all is judged identically by both rules, so it
    /// reproduces under both arms of every combination that reproduces it under either, which doubles
    /// the number of matching corners for most of the table. That costs nothing for the same reason
    /// backlog 156 recorded: reproducing means re-deriving <c>statistics</c> and <c>max_combo</c>
    /// EXACTLY, so every arm that reproduces a row has produced the same account of it and nothing
    /// downstream can tell which was picked. The order is there to make the label deterministic, not to
    /// make it correct.</para>
    /// </summary>
    public static IReadOnlyList<SearchedEra> EraSearchFor(StoredScore stored)
        => stored.ProvablyJudgedUnderTheDeferredTypoRule ? deferred_typo_search : EraSearch;

    /// <summary>
    /// <see cref="EraSearch"/> restricted to the arm a <c>good</c> key proves, i.e. the candidate list
    /// for a row backlog 155 pins. Precomputed rather than filtered per row so the pin costs a field
    /// read rather than an allocation on every score in the table.
    /// </summary>
    private static readonly IReadOnlyList<SearchedEra> deferred_typo_search =
        EraSearch.Where(era => era.Typo == live_typo_rule).ToArray();

    /// <param name="backfillMistypes">
    /// Whether to introduce a mistype count into rows that predate the stat. Off by default, and
    /// deliberately: those rows were played by a client that never counted wrong keypresses, so
    /// writing one is BACKFILLING BACKLOG 72's stat, a different change with its own pp
    /// consequences (PerformancePoints prices mistypes), and folding it into this sweep would make
    /// the result impossible to audit. The reproduction check ignores the key for those rows either
    /// way, since its absence is an era marker, not a disagreement.
    /// </param>
    /// <param name="mode">
    /// Which sweep this is. <see cref="RecalcMode.Reproduce"/> is the default and the safe one:
    /// it refuses anything it cannot reproduce. <see cref="RecalcMode.Supersede"/> re-judges under
    /// today's rules and REPLACES the stored numbers, so it is never reached without a caller
    /// naming it, and the CLI makes naming it a separate command with its own confirmation.
    /// </param>
    public static RecalcResult Run(StoredScore stored, ReplayArchive.DecodedReplay? decoded, bool backfillMistypes = false, RecalcMode mode = RecalcMode.Reproduce)
    {
        if (!stored.HasReplay)
            return Skipped(stored, SkipReason.NoReplay, null, mode);

        if (decoded is null)
            return Skipped(stored, SkipReason.UndecodableReplay, null, mode);

        if (decoded.Score is not Score score || decoded.Playable is not IBeatmap playable)
            return Skipped(stored, MissingBeatmapReason(stored, decoded.MissingBeatmapHash), decoded.MissingBeatmapHash, mode);

        if (!stored.Passed)
            return Skipped(stored, SkipReason.FailedRun, null, mode);

        if (score.Replay.Frames.Count == 0)
            return Skipped(stored, SkipReason.EmptyReplay, null, mode);

        var mods = score.ScoreInfo.Mods;

        // A row from before the mistype stat existed. Its absence is an era marker, so it is not a
        // reproduction failure, and (unless asked) not something this sweep introduces either.
        bool preMistypeEra = !WireCounts.Parse(stored.StatisticsJson).ContainsKey(mistype_key);

        // 1. Re-derive under the rules the row was PRICED under and ask for the stored statistics
        //    back, exactly. In Reproduce mode that is a PROOF and a failure refuses the row: a
        //    harness that cannot reproduce the old numbers has no business writing new ones. In
        //    Supersede mode it is a DIAGNOSTIC and a failure is the expected reading for anything
        //    judged in the backlog 133-to-147 window, because today's code no longer owns the ladder
        //    that graded it. Same computation, opposite meaning, which is why the two are separate
        //    commands.
        //
        //    All SEVEN era axes are set to the era that judged the row here, not just the typo rule:
        //    the typo rule (109), combo restore (140), the spacebar (148), the rate windows (150),
        //    what an off-time press cost (199) and what a CORRECTED typo's cell was worth (210).
        //    Every one of them is a rule that moved after rows were already in the table, and leaving
        //    any of them on the live arm would re-grade the run on a ladder it was never played on
        //    and report the difference as a corrupt row.
        //
        //    NONE of the seven is a constant any more, and none of them falls back on one either. The
        //    TYPO axis is READ OFF THE ROW where the row can prove it (backlog 155), since a typo LEFT
        //    STANDING takes a key only the deferred rule can produce, and such a row is PINNED: the
        //    candidate list it gets from EraSearchFor holds only that arm, so the axis is never
        //    searched for it. Everything else is PROVED BY RECONSTRUCTION: the spacebar and the rate
        //    windows (backlog 156), combo restore (157), the typo rule for a row whose keys prove
        //    nothing about it (158, the case of a typo that was CORRECTED, which leaves no key and is
        //    graded differently by the two rules), the off-time rule (199, which leaves no key
        //    either: an off-time press and a press just inside the Meh window resolve as the same
        //    `meh`), and the correction cap (210, which leaves no key either: a capped cell resolves
        //    as an ordinary `ok`). The row is re-derived under DefaultEraFor(stored)
        //    first, and only if that does not come back is it re-derived under the remaining
        //    combinations until one reproduces it exactly.
        //
        //    THAT IS A COMPOSITION OF BACKLOG 155, NOT A REVERSAL OF IT. 155 documented the absence of
        //    the key as "no proof either way" and never as evidence of the older rule; what it lacked
        //    was anything to do with an absence except default it. Searching it is that something.
        //
        //    The search runs on the residual alone, which is the point of trying the default first:
        //    a row from before backlog 109 (the overwhelming majority of the table) costs exactly what
        //    it cost before, one re-derivation, and its result means exactly what it meant before. A
        //    row no combination reproduces keeps THIS arm's mismatch and reports as unexplained, which
        //    is what stops the search from being a way of always finding an answer.
        //
        //    THE EASY AND HARD ROCK WINDOW SCALES ARE STILL NOT AN ERA AXIS HERE, even though the HR
        //    scale itself did get retuned: backlog 264 dropped the live halving, so a NEW Hard Rock
        //    play now judges at 1.0x windows. That change needed no switch in this tool because it
        //    does not live here. It lives on the replay's own CONFIG frame (bit 13,
        //    flag_unhalved_hard_rock_windows): a stored row already carries which ladder it was
        //    played under, so TypeBeatReplayScorer.createEngine reads the scale off the row instead
        //    of off a table this search would have to maintain. Easy's half of the argument is
        //    unchanged: that mod has only ever had ONE behaviour, so a row carrying it was
        //    necessarily played with that scaling and applying it unconditionally reproduces the row.
        //    Such a row fails the default on the SPACE axis like any other, and the search fixes it
        //    there. This would need revisiting only if Easy's window scale were ever retuned, or if
        //    Hard Rock's stopped being recoverable from the replay's own CONFIG frame.
        TypeBeatReplayAccount ScoreUnder(SearchedEra candidate) => TypeBeatReplayScorer.Score(
            playable,
            mods,
            score.Replay,
            candidate.Typo,
            candidate.Combo,
            candidate.Space,
            candidate.Rate,
            offTimeRule: candidate.OffTime,
            creditRule: candidate.Credit,
            worthRule: candidate.Worth);

        // The era this row STARTS at, which is the table-wide default on the four keyless axes and
        // whatever the row's own keys prove on the typo axis. Always the first candidate the row is
        // offered, so a row that comes back here is never re-derived a second time.
        var startingPoint = DefaultEraFor(stored);

        var era = startingPoint;
        var oldRule = ScoreUnder(era);
        var oldStatistics = WireCounts.From(oldRule.Statistics);

        string mismatch = ReproductionMismatch(stored, oldRule, oldStatistics, preMistypeEra);
        bool reproduced = mismatch.Length == 0;
        SearchedEra? provedEra = reproduced ? era : null;

        if (!reproduced)
        {
            foreach (var candidate in EraSearchFor(stored))
            {
                if (candidate == startingPoint)
                    continue;

                var attempt = ScoreUnder(candidate);
                var attemptStatistics = WireCounts.From(attempt.Statistics);

                if (ReproductionMismatch(stored, attempt, attemptStatistics, preMistypeEra).Length > 0)
                    continue;

                era = candidate;
                oldRule = attempt;
                oldStatistics = attemptStatistics;
                provedEra = candidate;
                reproduced = true;
                break;
            }
        }

        if (mode == RecalcMode.Reproduce && !reproduced)
        {
            // The mismatch reported is the STARTING POINT arm's, not the last combination the search
            // tried. A reader looking at an unexplained row wants the disagreement against the era the
            // row is most likely to be from, and a list of sixteen near misses would bury it. It is
            // worth knowing what that costs the reader, since backlogs 157 and 158 were two whole
            // items' worth of it: the printed signature is that one arm's, so a row whose real era is
            // several axes away shows its disagreement on every one of them at once, and chasing that
            // signature on its own leads somewhere there is nothing to find. Both items' residuals
            // printed a window-width shape while their real fault was on the combo and typo axes.
            return Skipped(stored, SkipReason.NotReproducible, mismatch, mode) with
            {
                OldRuleStatistics = oldStatistics,
                OldRuleMaxCombo = oldRule.MaxCombo,
                OldRuleTotalScore = oldRule.TotalScore,
                ReproductionDetail = mismatch,
            };
        }

        // The multiplier THIS ROW was priced with, recovered rather than recomputed. The mod
        // multipliers have been retuned since some of these plays (Flashlight went 1.2x -> 1.05x,
        // the rate curve moved), and applying today's would move total_score by up to 2x for a
        // reason that has nothing to do with the typo rule. The base (pre-multiplier) score is what
        // backlog 109 actually moves, so the row's own multiplier is carried across it.
        //
        // Recoverable ONLY when the old rule reproduced, which is why Supersede does not use it: the
        // ratio is stored_total / re-derived_base, and once the re-derived base is a different
        // ladder's number the ratio is not a multiplier at all, it is the ladder change wearing a
        // multiplier's clothes. Reported for both modes; applied only by Reproduce.
        double storedMultiplier = oldRule.TotalScoreWithoutMods > 0
            ? (double)stored.TotalScore / oldRule.TotalScoreWithoutMods
            : 1;

        // 2. The same run under the rules this mode is asking about.
        //
        //    Reproduce varies the TYPO rule alone and holds the other six axes at the stored era,
        //    so every number it reports is attributable to that one axis. For a row already judged
        //    under today's typo rule this arm is the same judgement as the one above, and the row
        //    correctly reports as unmoved: there is no rule change left for it to be repriced by.
        //
        //    "The stored era" for those six means the era the arm above ESTABLISHED for this row, on
        //    ALL SIX AXES, not the default (backlog 156, backlog 157 for combo restore, backlog 199
        //    for the off-time rule, backlog 210 for the correction cap, backlog 213 for what an
        //    UNCORRECTED typo's cell was worth). Holding
        //    any of them at the default for a row proved to be from a later era would vary more than
        //    one axis while claiming to vary one, and would report a row played since that era's
        //    release as moving backwards onto a ladder it was never on. The off-time and credit axes
        //    are why a pre-199 or pre-210 row is REPRICED by a supersede sweep and not by a reproduce
        //    one: the reproduce arm holds each where the row was judged, exactly as it holds the
        //    spacebar.
        //
        //    THE WORTH AXIS IS HELD THE SAME WAY AND IS THE ONE PLACE THE ARM DIVERGES FROM THE
        //    SERVER'S OWN READING. `ScoringContract` has no era arm for the fold (backlog 213 chose
        //    to re-weight the key unconditionally, since nothing recomputes a settled row's
        //    accuracy), so a pre-213 row's re-judged account is priced HERE under the 50 it was
        //    played with and read BELOW under the 0 the contract carries. That is the honest pair:
        //    total_score is what the row's own era made of the run, and accuracy, rank and the
        //    ceiling are what today's server makes of it. The two do not have to agree, and where
        //    the difference bites (a total the fold's tighter ceiling no longer covers) the row is
        //    reported out of bounds rather than silently re-priced, which is the same refusal
        //    every other out-of-bounds row gets.
        //
        //    THE TYPO AXIS IS THE ONE THIS ARM DOES NOT TAKE FROM `era`, and that is not an oversight
        //    (backlog 158 put the axis in the search and left this line alone). This arm is the LIVE
        //    typo rule by definition: it is the axis a reproduce sweep exists to vary. `era.Typo` is
        //    what the row was judged under, which the arm above already used. Where the two agree, on
        //    a row proved or reconstructed onto the deferred arm, the row correctly reports as unmoved.
        //
        //    Supersede applies ALL of today's rules, judgement AND combo restore AND the spacebar
        //    AND the rate windows AND the off-time rule AND the correction cap AND the uncorrected
        //    typo's worth, together (backlog
        //    136, decided 2026-08-13; backlog 151 adds the window pair, backlog 199 the fifth,
        //    backlog 210 the sixth, backlog 213 the seventh). Not because moving seven axes
        //    at once is nicer to audit, it is worse, but because
        //    every halfway combination is one no client has ever run: a score judged with today's
        //    tiers and yesterday's combo rule could not be reproduced by replaying it anywhere,
        //    which is the property the whole tool is built on.
        var newRule = TypeBeatReplayScorer.Score(
            playable,
            mods,
            score.Replay,
            live_typo_rule,
            mode == RecalcMode.Supersede ? live_combo_rule : era.Combo,
            mode == RecalcMode.Supersede ? live_space_rule : era.Space,
            mode == RecalcMode.Supersede ? live_rate_rule : era.Rate,
            offTimeRule: mode == RecalcMode.Supersede ? live_off_time_rule : era.OffTime,
            creditRule: mode == RecalcMode.Supersede ? live_credit_rule : era.Credit,
            worthRule: mode == RecalcMode.Supersede ? live_worth_rule : era.Worth);

        var statistics = WireCounts.From(newRule.Statistics);
        var maximumStatistics = WireCounts.From(newRule.MaximumStatistics);

        if (preMistypeEra && !backfillMistypes)
            statistics.Remove(mistype_key);

        // 2b. Supersede's own refusal, replacing the reproduction gate rather than relaxing it. The
        //     JUDGEMENT of the run is expected to move here; the RUN is not. If the map has a
        //     different number of cells than the row was judged over, or a different number of them
        //     were judged, or the engine went inert for part of the recording, then the tool and the
        //     row are not talking about the same play and any number written would be a guess.
        if (mode == RecalcMode.Supersede)
        {
            string structural = StructuralMismatch(stored, newRule, statistics, maximumStatistics);

            if (structural.Length > 0)
            {
                return Skipped(stored, SkipReason.NotTheSameRun, structural, mode) with
                {
                    OldRuleStatistics = oldStatistics,
                    OldRuleMaxCombo = oldRule.MaxCombo,
                    OldRuleTotalScore = oldRule.TotalScore,
                    Reproduced = reproduced,
                    ReproductionDetail = reproduced ? null : mismatch,
                    ReproducedUnderEra = provedEra,
                };
            }
        }

        // 3. Everything the server derives from the statistics, through the server's own contract,
        //    following ScoreEndpoints.SubmitScore step for step.
        var recomputed = ScoringContract.Recompute(statistics, maximumStatistics, newRule.MaxCombo);

        // THE CEILING IS PRICED FROM THE ROW'S STORED MODS, and it bounds a total the re-derivation
        // computed from the REPLAY's mod list, so the two sources have to be kept in mind together
        // (backlog 136's failure mode was a ceiling that did not cover the total it was bounding, and
        // it nearly emptied the boards).
        //
        // They come from one submission (ScoreEndpoints stores the submitted mods verbatim, and the
        // .osr's score-info blob carries the same list), so they agree except in one case: a mod the
        // RULESET no longer has survives in the jsonb but cannot be instantiated when the .osr is
        // decoded, so it prices the ceiling and does not price the total. That direction is safe only
        // while every deleted mod prices at or above 1.0. Rhythmic, the one mod deleted so far
        // (backlog 147), is 1.10, so an RH row gets a ceiling 10% LOOSER than the total it bounds.
        // Deleting a mod worth LESS than 1.0 (Easy and No Fail are 0.5) would invert that and clamp
        // every honest row carrying it, so do not delete one without pricing this line first.
        //
        // An acronym the table does not know at all falls to UNKNOWN_MOD_MULTIPLIER = 2.0, which is
        // an UPPER bound, so an unrecognised mod also errs loose rather than tight.
        double modMultiplier = ModMultiplier.MaxForStack(ScoreMods.Parse(stored.ModsJson).Select(m => ((string?)m.Acronym, m.Rate)));
        long modCeiling = ModMultiplier.TotalScoreCeiling(newRule.TotalScoreWithoutMods, modMultiplier);

        bool withinBounds = ScoringContract.TotalScoreWithinBounds(newRule.TotalScoreWithoutMods, recomputed)
                            && newRule.TotalScore >= 0
                            && newRule.TotalScore <= modCeiling;

        bool fullyJudged = recomputed.AccuracyProgress >= 1;
        double accuracy = fullyJudged ? recomputed.Accuracy : recomputed.JudgedAccuracy;

        // Reproduce: base score times the multiplier this row was actually priced with (see above),
        // which is exactly newRule.TotalScore whenever the multiplier has not been retuned since.
        //
        // Supersede: newRule.TotalScore as the score processor produced it, i.e. TODAY's multiplier.
        // A superseded row has to be a score today's client could put on the wire, and carrying a
        // retired 1.2x Flashlight across a re-judgement would produce one it could not. It also
        // cannot use the recovered ratio: that ratio is only a multiplier when the two totals came
        // off the same ladder, which under a supersede sweep is exactly what is not true.
        long multiplied = mode == RecalcMode.Supersede
            ? newRule.TotalScore
            : (long)Math.Round(newRule.TotalScoreWithoutMods * storedMultiplier, MidpointRounding.AwayFromZero);

        long totalScore = withinBounds ? multiplied : recomputed.TotalScoreCeiling;

        double appliedMultiplier = mode == RecalcMode.Supersede
            ? (newRule.TotalScoreWithoutMods > 0 ? (double)newRule.TotalScore / newRule.TotalScoreWithoutMods : 1)
            : storedMultiplier;

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
            stored.SrHt,
            new PerformancePoints.LiterateStars(stored.SrLiterate, stored.SrLiterateDt, stored.SrLiterateHt));

        var (pp, ppSettled) = PerformancePoints.ForScore(
            ranked,
            ScoreMods.Parse(stored.ModsJson),
            PerformancePoints.CountNotes(statistics),
            accuracy,
            maxCombo,
            stored.BaseStars,
            stored.SrDt,
            stored.SrHt,
            new PerformancePoints.LiterateStars(stored.SrLiterate, stored.SrLiterateDt, stored.SrLiterateHt));

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
            ppSettled,
            mode,
            reproduced,
            reproduced ? null : mismatch,
            appliedMultiplier,
            provedEra);
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

    /// <summary>
    /// Empty when the re-judged account describes the same RUN over the same MAP as the stored row;
    /// otherwise a description of how they differ. This is <see cref="RecalcMode.Supersede"/>'s
    /// gate, and it is not <see cref="ReproductionMismatch"/> with the tolerances turned up: it
    /// compares different quantities, chosen because they are the ones a re-judgement CANNOT move.
    ///
    /// <list type="bullet">
    /// <item>Every frame consumed. A recording the engine went inert for means the harness and the
    /// run disagree about the map, whatever the numbers say.</item>
    /// <item>The same number of cells in <c>maximum_statistics</c>. That dictionary is one MaxResult
    /// per cell; backlog 133 moved its KEY (<c>great</c> to <c>perfect</c>), which is exactly why
    /// counting rather than comparing is what survives, but it cannot move the COUNT. A different
    /// count means a different map.</item>
    /// <item>The same number of accuracy-affecting judgements in <c>statistics</c>. Re-judging moves
    /// cells between tiers and between miss/typo/hit, and all of those are accuracy-affecting, so
    /// the total is invariant under every rule change this sweep applies. A different total means a
    /// different run, or a run the tool fed a different cell list.</item>
    /// </list>
    ///
    /// <para>Counting is done by <see cref="ScoringContract.CountAccuracyAffecting"/>, the server's
    /// own classifier, so this cannot start disagreeing with the code that ranks the row.</para>
    /// </summary>
    private static string StructuralMismatch(
        StoredScore stored,
        TypeBeatReplayAccount account,
        IReadOnlyDictionary<string, int> statistics,
        IReadOnlyDictionary<string, int> maximumStatistics)
    {
        var problems = new List<string>();

        if (account.UnconsumedFrames > 0)
            problems.Add($"{account.UnconsumedFrames} replay frame(s) the engine never consumed");

        int storedCells = ScoringContract.CountAccuracyAffecting(WireCounts.Parse(stored.MaximumStatisticsJson));
        int freshCells = ScoringContract.CountAccuracyAffecting(maximumStatistics);

        if (storedCells != freshCells)
            problems.Add($"the map has {freshCells} cell(s), the row was judged over {storedCells}");

        int storedJudged = ScoringContract.CountAccuracyAffecting(WireCounts.Parse(stored.StatisticsJson));
        int freshJudged = ScoringContract.CountAccuracyAffecting(statistics);

        if (storedJudged != freshJudged)
            problems.Add($"the row judged {storedJudged} cell(s), the replay judges {freshJudged}");

        return string.Join(", ", problems);
    }

    /// <summary>
    /// Which flavour of "the beatmap did not resolve" this is, which decides whether re-running the
    /// tool could ever fix it. If the row's map still hashes to what the replay names, the package
    /// simply was not fetched (cold cache, unreachable site) and the sweep is INCOMPLETE rather than
    /// blocked. If it hashes to something else, the set was re-uploaded and the exact .osu the run
    /// was judged on is gone for good.
    /// </summary>
    private static SkipReason MissingBeatmapReason(StoredScore stored, string? wantedHash)
        => !string.IsNullOrEmpty(stored.CurrentChecksumMd5)
           && !string.IsNullOrEmpty(wantedHash)
           && !string.Equals(stored.CurrentChecksumMd5, wantedHash, StringComparison.OrdinalIgnoreCase)
            ? SkipReason.BeatmapReuploaded
            : SkipReason.BeatmapUnavailable;

    private static Dictionary<string, int> WithoutMistypes(IReadOnlyDictionary<string, int> counts)
    {
        var copy = new Dictionary<string, int>(counts);
        copy.Remove(mistype_key);
        return copy;
    }

    private static RecalcResult Skipped(StoredScore stored, SkipReason reason, string? detail, RecalcMode mode) =>
        new(stored, reason, detail, null, 0, 0, null, 1, null, null, 0, 0, 0, 0, null, false, false, stored.Ranked, null, false, mode);
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
