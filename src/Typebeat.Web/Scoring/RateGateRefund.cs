using Typebeat.Web.Data;

namespace Typebeat.Web.Scoring;

/// <summary>
/// The refund half of 017_rate_gate_refund.sql: restores the <c>ranked</c> flag on scores the
/// rate-blind play-time gate unranked purely because the player used an UP-rate mod. Runs at
/// startup, immediately after <see cref="SkipGateRefund"/>, and is a no-op from then on.
///
/// <para>
/// THE BUG. The gate compared real elapsed seconds against a bound in MAP time. A play at 1.5x
/// covers the map in <c>drain / 1.5</c> real seconds, which is 0.667 x drain and therefore below the
/// 0.9 x drain the gate demanded, so a default Double Time play could not clear it at all: every
/// rate above roughly 1.111x was unranked outright, however honest. The corrected bound divides by
/// the rate (see <see cref="PlayTimeGate"/>). Down-rates were never caught by this, they take longer
/// than 1.0x and the un-divided bound only ever under-demanded of them, which is why this pass
/// refuses to consider them at all.
/// </para>
///
/// <para>
/// All the machinery is <see cref="GateRefund"/>'s, shared with 016 (candidate query, the
/// re-derivation of every other submit-time condition, the guard row, the audit trail, and the
/// finding that aggregates need nothing: accrual never depended on the gate). What is specific to
/// this pass:
/// </para>
///
/// <list type="bullet">
/// <item>A candidate must carry a rate mod at a rate ABOVE 1.00 in its stored stack. A stack with no
/// rate mod, or a down-rate, or a tamper-shaped stack holding both (read at its slowest member, see
/// <see cref="RateMods.EffectiveRate"/>), was never this bug's victim and is left alone.</item>
/// <item>The map's skip allowance is NOT required to be known here. A DT play on a gap-free map is
/// exactly the target case; the allowance simply contributes 0 to the corrected bound.</item>
/// </list>
///
/// <para>
/// WHICH HISTORICAL BOUND "THE GATE FIRED" IS TESTED AGAINST. Two bounds have existed:
/// <c>0.9 x drain</c> (before task 47) and <c>0.9 x (drain - skippable)</c> (after it). Task 47 and
/// this correction deploy together, so no stored row has ever been judged by the second one: every
/// existing score met <c>0.9 x drain</c>, and that is what this pass tests fired, which is also the
/// strictest of the two. Were the two ever split across deploys, a row whose elapsed landed between
/// the bounds could be read as a gate victim when something else unranked it; that exposure is
/// exactly the one 016 already accepts (its own runner would have re-ranked the same row first), so
/// this adds none of its own. Everything else the submit path decided is re-derived and must hold,
/// so a row unranked for any other reason cannot be moved by either pass.
/// </para>
///
/// <para>
/// ORDER WITH 016. Both run at every boot, 016 first, each under its own <c>score_refunds</c> key.
/// A row 016 re-ranks stops being a candidate here (the candidate query takes unranked rows only),
/// so the two can never both pay for the same score, and a row 016 declined is reconsidered here on
/// its own merits: a skipping DT player needs BOTH corrections and gets ranked by this one.
/// </para>
/// </summary>
public static class RateGateRefund
{
    /// <summary>The score_refunds key; each refund gets its own and its own guard.</summary>
    public const string MIGRATION_KEY = "017_rate_gate_refund";

    private static readonly GateRefund.Plan plan = new(
        MIGRATION_KEY,
        // Cheap SQL narrowing only; whether the stack really holds an up-rate is decided in
        // AppliesTo, by the same reader that prices the mods.
        CandidateFilterSql: "s.mods <> '[]'::jsonb",
        // The bound every stored row was actually judged by (see the class remarks).
        OldRequiredSeconds: row => PlayTimeGate.RequiredSeconds(row.DrainLengthS, 0),
        // The full corrected bound: skips come off the map time, the rate converts it to real time.
        NewRequiredSeconds: row => PlayTimeGate.RequiredSeconds(row.DrainLengthS, row.SkippableS, row.Rate),
        AppliesTo: row => row.Rate > 1.0,
        Summary: "elapsed cleared 0.9 x (drain - skippable) / rate");

    public static Task<int> RunAsync(Db db, ILogger logger, CancellationToken ct = default)
        => GateRefund.RunAsync(db, logger, plan, ct);

    /// <summary>
    /// Whether this row was unranked by the rate-blind gate ALONE and clears the rate-corrected
    /// bound. Public so the migration test can assert the decision directly, row by row.
    /// </summary>
    public static bool QualifiesForRefund(GateRefund.CandidateRow row) => GateRefund.Qualifies(plan, row);
}
