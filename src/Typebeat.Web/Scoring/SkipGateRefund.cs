using Typebeat.Web.Data;

namespace Typebeat.Web.Scoring;

/// <summary>
/// The refund half of 016_refund_skip_gate.sql: restores the <c>ranked</c> flag on scores that the
/// pre-task-47 play-time gate unranked purely because the player used the game's instrumental-skip
/// button. Runs at startup after <c>PaceBackfill</c> has filled <c>beatmaps.skippable_s</c> from the
/// stored .osu blobs, and is a no-op from then on.
///
/// <para>
/// This is a <see cref="GateRefund.Plan"/> and nothing else; every mechanism it used to own (the
/// candidate query, the re-derivation of the submit path's other conditions, the guard row, the
/// audit trail, the aggregates reasoning) moved to <see cref="GateRefund"/> unchanged when task 54
/// added a second refund of the same shape. What is specific to this one:
/// </para>
///
/// <list type="bullet">
/// <item>Only maps with a KNOWN skip allowance are candidates (<c>skippable_s &gt; 0</c>). Nothing is
/// refunded on a map whose allowance is zero or unknown, which is exactly the
/// fail-toward-the-old-behaviour rule the gate itself follows.</item>
/// <item>The bound the row was submitted under is <c>0.9 x drain</c>, the pre-task-47 gate.</item>
/// <item>The corrected bound is <c>0.9 x (drain - skippable)</c>. It is evaluated at rate 1.0
/// deliberately: this pass reproduces the task-47 fix exactly as it was written, and a row that only
/// clears once its rate mod is taken into account belongs to <see cref="RateGateRefund"/>, which
/// reconsiders it under its own key.</item>
/// </list>
/// </summary>
public static class SkipGateRefund
{
    /// <summary>The score_refunds key; each refund gets its own and its own guard.</summary>
    public const string MIGRATION_KEY = "016_refund_skip_gate";

    private static readonly GateRefund.Plan plan = new(
        MIGRATION_KEY,
        CandidateFilterSql: "b.skippable_s > 0",
        OldRequiredSeconds: row => PlayTimeGate.RequiredSeconds(row.DrainLengthS, 0),
        NewRequiredSeconds: row => PlayTimeGate.RequiredSeconds(row.DrainLengthS, row.SkippableS),
        // A known skip allowance is the whole precondition, and the candidate query already applied
        // it; nothing about the mod stack matters to this correction.
        AppliesTo: _ => true,
        Summary: "elapsed cleared 0.9 x (drain - skippable)");

    public static Task<int> RunAsync(Db db, ILogger logger, CancellationToken ct = default)
        => GateRefund.RunAsync(db, logger, plan, ct);

    /// <summary>
    /// Whether this row was unranked by the pre-task-47 gate ALONE and clears the skip-adjusted
    /// bound. Public so the migration test can assert the decision directly, row by row.
    /// </summary>
    public static bool QualifiesForRefund(GateRefund.CandidateRow row) => GateRefund.Qualifies(plan, row);
}
