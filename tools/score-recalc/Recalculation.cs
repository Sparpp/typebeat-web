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
    /// So false keeps the row on <see cref="Recalculation"/>'s stored-era default, where the two
    /// rules agree for as long as the run has no typo in it. Turning this into a bidirectional test
    /// would re-derive every clean modern row on a ladder it was never played on.</para>
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
/// Which sweep is being run. The two are NOT a threshold apart: they judge under different rules,
/// value total_score differently, and check themselves with different predicates. Keeping them as
/// one mode with a looser gate is exactly the failure backlog 142 warns about.
/// </summary>
public enum RecalcMode
{
    /// <summary>
    /// The verification sweep (backlog 114). Re-derives under the rules the row was PRICED under
    /// (all FOUR era axes at the era that judged the row: the typo rule PER ROW, see
    /// <see cref="Recalculation.StoredEraTypoRuleFor"/>, plus <see cref="ComboRestoreRule.Never"/>,
    /// <see cref="SpaceTimingRule.Timed"/> and
    /// <see cref="RateWindowRule.Unscaled"/>) and refuses any row it cannot reproduce exactly, then
    /// reports what today's TYPO rule alone would make of it, holding every other axis still. Its
    /// output answers "does the harness understand this row", which is the question a supersede sweep
    /// cannot ask of itself.
    /// </summary>
    Reproduce,

    /// <summary>
    /// The superseding sweep (backlog 136 and 142). Re-judges the run under ALL of today's rules
    /// (<see cref="TypoRule.Deferred"/>, <see cref="ComboRestoreRule.OnFix"/>,
    /// <see cref="SpaceTimingRule.Untimed"/> and <see cref="RateWindowRule.ScaledByRate"/>) and
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
    /// rate windows), so a row from before the backlog 133 arc reproduces again, and backlog 155
    /// stopped it re-grading a row judged since backlog 126 on the retired typo rule; what is left
    /// here is a row from the 133-to-147 window, whose ladder no longer exists in any form, or a
    /// genuine disagreement worth looking at.</para>
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
    double AppliedMultiplier = 1)
{
    public bool Recalculated => Skip == SkipReason.None;

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
/// are four such axes, all of them set on that pass to the era that judged the row: the typo rule
/// (backlog 109), combo restore (140), the untimed spacebar (148) and the rate-scaled windows (150).
/// Three of them are the same for every row in the table and are constants below. The TYPO axis is
/// not: it moved while the table was already filling, so the table holds rows from both sides of it
/// and the pass reads each row's era off the row itself (<see cref="StoredEraTypoRuleFor"/>). The one
/// era that is NOT expressible is the backlog 133-to-147 window, whose four-tier character-distance
/// ladder backlog 147 deleted: those rows cannot be reproduced by any setting of any switch, because
/// the code that judged them no longer exists.</para>
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
    /// <para>UNLIKE THE OTHER THREE AXES THIS IS NOT TRUE OF EVERY ROW IN THE TABLE, which is the
    /// whole of backlog 155. The other three rules moved after the rows were already stored, so
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
    /// The typo rule to re-derive THIS row under when reproducing it: the one that actually judged
    /// it, as far as the row itself can prove.
    ///
    /// <para>The proof is <see cref="StoredScore.ProvablyJudgedUnderTheDeferredTypoRule"/> and it runs
    /// in ONE DIRECTION. A row holding an uncorrected typo can only have come from a client running
    /// <see cref="live_typo_rule"/>, so that row is pinned to it. A row without one is not evidence of
    /// <see cref="stored_era_typo_rule"/>, it is evidence of nothing, so it keeps the default: for a
    /// run with no typo in it the two rules are the same judgement anyway, and the rows where they are
    /// not (a typo corrected before backlog 126 gave the cell its own key) are a residue to measure
    /// rather than to guess at.</para>
    /// </summary>
    public static TypoRule StoredEraTypoRuleFor(StoredScore stored)
        => stored.ProvablyJudgedUnderTheDeferredTypoRule ? live_typo_rule : stored_era_typo_rule;

    /// <summary>
    /// The combo-restore era EVERY stored row was PLAYED in (backlog 140): no score in the database
    /// was played under a rule that gives combo back for a corrected typo.
    ///
    /// <para>Both arms of a <see cref="RecalcMode.Reproduce"/> sweep pin this, including the one
    /// labelled "the rule the client uses now", which varies the TYPO rule alone. That sweep exists
    /// to move one axis and prove it; letting a second one move underneath it would make every
    /// number it reports impossible to attribute. It is also what makes the old-rule arm a proof
    /// rather than an approximation, in both modes.</para>
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
    /// played under.</para>
    /// </summary>
    private const ComboRestoreRule live_combo_rule = ComboRestoreRule.OnFix;

    /// <summary>
    /// The space-timing era EVERY stored row was PLAYED in (backlog 148, backlog 151): no score in
    /// the database was played by a client that took the spacebar out of the timing challenge.
    ///
    /// <para>This is the WIDEST of the four era axes, because every map has spaces. Judged on the
    /// live rule a stored row's spaces all come back as top-tier hits that never break combo, so
    /// <c>statistics</c> and <c>max_combo</c> both move and the reproduce pass fails on rows that are
    /// not corrupt at all. Before this constant existed the pass could not reproduce ANY row in the
    /// table, and the failure presented as "these replays are corrupt" when in fact the rules had
    /// moved underneath them.</para>
    /// </summary>
    private const SpaceTimingRule stored_era_space_rule = SpaceTimingRule.Timed;

    /// <summary>
    /// The space-timing rule live play uses, and therefore the one a
    /// <see cref="RecalcMode.Supersede"/> sweep re-judges under.
    /// </summary>
    private const SpaceTimingRule live_space_rule = SpaceTimingRule.Untimed;

    /// <summary>
    /// The rate-window era EVERY stored row was PLAYED in (backlog 150, backlog 151): the rate mods
    /// have been ranked for the whole life of the score table, and until this release none of them
    /// scaled the judgement windows, so a stored DT / NC / HT row was graded on windows fixed in
    /// BEATMAP milliseconds.
    ///
    /// <para>Set unconditionally rather than only for rows that carry a rate mod: the arm it gates is
    /// a loop over the run's rate mods, which is empty for everything else, so the axis is inert for
    /// a row it does not apply to (pinned by the game's <c>JudgementEraTest</c>). Deciding per row
    /// would mean parsing the mods twice and getting the same answer.</para>
    /// </summary>
    private const RateWindowRule stored_era_rate_rule = RateWindowRule.Unscaled;

    /// <summary>
    /// The rate-window rule live play uses, and therefore the one a
    /// <see cref="RecalcMode.Supersede"/> sweep re-judges under.
    /// </summary>
    private const RateWindowRule live_rate_rule = RateWindowRule.ScaledByRate;

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
        //    All FOUR era axes are set to the era that judged the row here, not just the typo rule:
        //    the typo rule (109), combo restore (140), the spacebar (148) and the rate windows (150).
        //    Every one of them is a rule that moved after rows were already in the table, and leaving
        //    any of them on the live arm would re-grade the run on a ladder it was never played on
        //    and report the difference as a corrupt row.
        //
        //    Three of the four are a constant, because they moved after the last row that could care
        //    was stored. The TYPO axis is read off the row (backlog 155): it moved while the table
        //    was filling, so a row holding an uncorrected typo is reproduced under the rule that left
        //    it standing, and everything else keeps the older default.
        var oldRule = TypeBeatReplayScorer.Score(
            playable,
            mods,
            score.Replay,
            StoredEraTypoRuleFor(stored),
            stored_era_combo_rule,
            stored_era_space_rule,
            stored_era_rate_rule);
        var oldStatistics = WireCounts.From(oldRule.Statistics);

        string mismatch = ReproductionMismatch(stored, oldRule, oldStatistics, preMistypeEra);
        bool reproduced = mismatch.Length == 0;

        if (mode == RecalcMode.Reproduce && !reproduced)
        {
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
        //    Reproduce varies the TYPO rule alone and holds the other three axes at the stored era,
        //    so every number it reports is attributable to that one axis. For a row already judged
        //    under today's typo rule this arm is the same judgement as the one above, and the row
        //    correctly reports as unmoved: there is no rule change left for it to be repriced by.
        //
        //    Supersede applies ALL of today's rules, judgement AND combo restore AND the spacebar
        //    AND the rate windows, together (backlog 136, decided 2026-08-13; backlog 151 adds the
        //    last two). Not because moving four axes is nicer to audit, it is worse, but because
        //    every halfway combination is one no client has ever run: a score judged with today's
        //    tiers and yesterday's combo rule could not be reproduced by replaying it anywhere,
        //    which is the property the whole tool is built on.
        var newRule = TypeBeatReplayScorer.Score(
            playable,
            mods,
            score.Replay,
            live_typo_rule,
            mode == RecalcMode.Supersede ? live_combo_rule : stored_era_combo_rule,
            mode == RecalcMode.Supersede ? live_space_rule : stored_era_space_rule,
            mode == RecalcMode.Supersede ? live_rate_rule : stored_era_rate_rule);

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
            appliedMultiplier);
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
