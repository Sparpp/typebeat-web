using Typebeat.Web.Data;
using Typebeat.Web.Storage;

namespace Typebeat.Web.Scoring;

/// <summary>
/// Re-ranks a score that was honest in every way and was stored unranked ONLY because its set was
/// still pending when it was submitted (backlog 270). Runs at every boot, immediately before
/// <see cref="Packages.PpBackfill"/>.
///
/// <para>
/// THE BUG. <c>scores.ranked</c> is decided once, at submission
/// (<c>ScoreEndpoints</c>: <c>ranked = setRanked &amp;&amp; passed &amp;&amp; ...</c>), and is
/// never revisited. Ranking a set later
/// (<c>Pages/Beatmapsets/Set.cshtml.cs</c>'s <c>transitionAsync</c>) updates
/// <c>beatmapsets.status</c> and nothing else, so a play made while its set was pending is settled
/// at pp 0 for ever, on a set that has priced every play made since. Five stored rows were sitting
/// in exactly that state when this was found, two of them worth about 315 pp each. A VERSION bump
/// does not fix them: <see cref="Packages.PpBackfill"/> only prices <c>ranked</c> rows, so the
/// backfill would restamp all five at the current version and leave every one of them at 0.
/// </para>
///
/// <para>
/// IT IS A STANDING SWEEP, NOT A ONE-SHOT MIGRATION, and that is the whole design. The condition
/// it tests ("this row is unranked, its set is ranked NOW, and every submit-time check still
/// holds") is intrinsically re-checkable and idempotent: a row it re-ranks stops being a candidate
/// on the next pass, and a row it declines is reconsidered honestly whenever the world changes.
/// So a set ranked next year heals at the next boot with no further code, no migration key and
/// nothing to remember, which is the property a one-shot pass would not have had. See
/// <see cref="GateRefund.Plan.Standing"/> for what that flag changes in the shared machinery.
/// </para>
///
/// <para>
/// THE RANK BUTTON RUNS IT TOO, SCOPED (backlog 352). Waiting for the next boot left a freshly
/// ranked set's pending-era plays off its board for days. So <c>Set.cshtml.cs</c>'s rank
/// transition calls <see cref="RunForSetAsync"/> straight after a successful pending to ranked
/// flip, then prices what it carried through <see cref="Packages.PpBackfill"/> narrowed to the
/// same set. That is the SAME plan through the same runner, not a second implementation: the set
/// id only narrows the candidate query. The boot sweep stays exactly as it was, as the safety net
/// for a set ranked by hand in SQL, a button run that failed (it is best-effort there), and any
/// future path that moves <c>beatmapsets.status</c>.
/// </para>
///
/// <para>
/// THE VERSION RULE (backlog 352), on both paths: a play is carried only when the version it was
/// played on has the current version's gameplay, i.e. its token's hash is the current checksum or
/// the two versions share a <see cref="Packages.GameplayFingerprint"/>. A play on an earlier upload
/// with different timing or a different recording stays unranked. See
/// <see cref="PlayedVersionRule"/> for how the earlier version is recovered and what it declines.
/// </para>
///
/// <para>
/// PRECISION OVER RECALL, exactly as the gate refunds have it (see <see cref="GateRefund"/>): a
/// row is re-ranked only when EVERY other submit-time condition can be re-derived from stored data
/// and holds. It passed and was fully judged, its statistics are valid, its stored total is inside
/// the ceiling those statistics justify, its build is not blocked, its play time clears the gate,
/// and it carries no always-unranked mod. That last one is why Now Is Gold +PT stays unranked and
/// must: Puppeteer is a clock-slaving follower, so its plays are unranked at every configuration
/// whatever their set's status says (<see cref="UnrankedMods"/>).
/// </para>
///
/// <para>
/// WHAT IT CANNOT DISTINGUISH, and accepts. A row unranked for a reason that leaves no trace in
/// the columns it can read is indistinguishable from a pending-set victim: there is no per-score
/// reason column and no <c>ranked_at</c>. The re-derivation above is what stands in, and it is the
/// same exposure <see cref="SkipGateRefund"/> already documents and accepts.
/// </para>
///
/// <para>
/// AGGREGATES: nothing to do, for the reason <see cref="GateRefund"/> gives in full. Accrual into
/// <c>user_stats</c> never depended on <c>ranked</c>, so these rows already contributed their
/// share, and every scoring surface is a live query over <c>scores.ranked</c>. What DOES need
/// doing is the pp stamp, and the shared runner does it: a re-ranked row is written back at
/// <c>pp_version = 0</c> so <see cref="Packages.PpBackfill"/>, which runs immediately after this,
/// prices it on the same boot.
/// </para>
/// </summary>
public static class SetRankRefund
{
    /// <summary>
    /// The <c>score_refunds</c> key. It is an AUDIT key here rather than a guard: a standing pass
    /// reconsiders every unranked row at every boot, so the row records that this sweep re-ranked
    /// this score and never decides whether it may (see <see cref="GateRefund.Plan.Standing"/>).
    /// </summary>
    public const string MIGRATION_KEY = "set_rank_refund";

    private static readonly GateRefund.Plan plan = new(
        MIGRATION_KEY,
        // Nothing to narrow: the shared candidate query already asks for an unranked, passed row
        // with an anchored elapsed time on a set that is ranked NOW, which is the whole population.
        CandidateFilterSql: "true",
        // No historical bound is tested (this is not a gate correction), so both read the CURRENT
        // one. The audit row therefore records the bound the play actually had to clear, twice.
        OldRequiredSeconds: row => PlayTimeGate.RequiredSeconds(row.DrainLengthS, row.SkippableS, row.Rate),
        NewRequiredSeconds: row => PlayTimeGate.RequiredSeconds(row.DrainLengthS, row.SkippableS, row.Rate),
        AppliesTo: _ => true,
        Summary: "the set was pending at submit time and is ranked now",
        Standing: true,
        VersionRule: true);

    /// <summary>The boot sweep: every ranked set.</summary>
    public static Task<int> RunAsync(Db db, IFileStore fileStore, ILogger logger, CancellationToken ct = default)
        => GateRefund.RunAsync(db, logger, plan, ct, setId: null, fileStore);

    /// <summary>
    /// The identical pass over ONE set, for the rank button: same plan, same checks, same audit
    /// row, only the candidate query narrowed to <paramref name="setId"/>.
    /// </summary>
    public static Task<int> RunForSetAsync(Db db, IFileStore fileStore, ILogger logger, long setId, CancellationToken ct = default)
        => GateRefund.RunAsync(db, logger, plan, ct, setId, fileStore);

    /// <summary>
    /// Whether this row is an honest play on a now-ranked set. Public so a test can assert the
    /// decision directly, row by row, exactly as the two gate refunds expose theirs.
    /// </summary>
    public static bool QualifiesForRefund(GateRefund.CandidateRow row) => GateRefund.Qualifies(plan, row);
}
