namespace Typebeat.Tools.ScoreRecalc;

/// <summary>
/// A row the sweep could not re-derive, grouped by WHY. Backlog 136 asks for an explicit decision
/// on these rather than a silent default, and a single "no usable replay" bucket cannot carry one:
/// the six causes have different fixes, different likelihoods of being the tool's fault rather than
/// the data's, and different right answers.
/// </summary>
public enum UnreplayableCase
{
    /// <summary>
    /// <c>replay_key IS NULL</c>: the row was submitted without a replay, or predates replay upload.
    /// Nothing to re-derive from, ever. RECOMMENDED POLICY: keep. The row is honest, it is simply
    /// unverifiable, and <c>ScoringContract.JudgedUnderTheFourthTier</c> still reads it correctly
    /// (backlog 142 keeps that discriminator alive for exactly these rows). Unranking it would take
    /// a real placement away from a real player for an infrastructure reason.
    /// </summary>
    NoReplay,

    /// <summary>
    /// Bytes are stored but do not decode as a type!beat replay. RECOMMENDED POLICY: keep, and go
    /// look. This one is not a fact about the play, it is evidence of a storage or encoding bug, and
    /// the answer to a storage bug is not to rewrite the scores it touched. Every id is listed
    /// individually in the report for that reason.
    /// </summary>
    Unreadable,

    /// <summary>
    /// The replay decodes but holds no typing frames, so the run it claims to describe was never
    /// recorded (scores from before replay recording worked land here). RECOMMENDED POLICY: keep,
    /// same reasoning as <see cref="NoReplay"/>, which this is in every respect except that the file
    /// exists.
    /// </summary>
    EmptyReplay,

    /// <summary>
    /// The .osu the run names could not be fetched, though the row's beatmap still hashes to it.
    /// A FETCH FAILURE, not a fact about the data. RECOMMENDED POLICY: none, fix it and re-run.
    /// <c>supersede-apply</c> refuses to proceed while any of these are outstanding, because
    /// applying around them leaves a leaderboard where some rows are superseded and some are not,
    /// for a reason no one can see afterwards.
    /// </summary>
    BeatmapMissing,

    /// <summary>
    /// The row's beatmap is present but no longer hashes to what the replay names: the set was
    /// re-uploaded after the play. The exact .osu the run was judged on is gone and re-running
    /// cannot bring it back. RECOMMENDED POLICY: keep. Judging the run against the map's current
    /// cells would score a different map, which is worse than leaving a stale number alone.
    /// </summary>
    BeatmapChanged,

    /// <summary>
    /// <c>passed = false</c>. A failed run's replay ends where its health ran out, and both backlog
    /// 109 and 133 moved when a cell costs health, so where the same run would have ended under
    /// today's rules is not derivable from a recording of yesterday's. RECOMMENDED POLICY: keep.
    /// A failed run is not competing for a leaderboard place, so leaving it is nearly free.
    /// </summary>
    FailedRun,
}

/// <summary>What a supersede sweep does with a row it could not re-derive.</summary>
public enum UnreplayablePolicy
{
    /// <summary>Leave the row exactly as stored. It keeps its pre-133 numbers and its board place.</summary>
    Keep,

    /// <summary>
    /// Clear <c>scores.ranked</c> (and therefore its pp) so the row leaves the leaderboard. The only
    /// way to end up with a board that is entirely one era, at the cost of deleting real placements
    /// for rows whose only fault is that nobody can check them.
    /// </summary>
    Unrank,
}

/// <summary>
/// Exactly which rows a run would write, and why, computed before anything is written and printed
/// before anything is written. It is the object an <c>apply</c> executes and the object a report
/// describes, so the report cannot describe a different sweep from the one that runs.
/// </summary>
public sealed class WritePlan
{
    public required RecalcMode Mode { get; init; }

    /// <summary>Re-derived rows whose values actually moved. Fully rewritten.</summary>
    public required IReadOnlyList<RecalcResult> Rejudged { get; init; }

    /// <summary>Rows the policy unranks. Only <c>ranked</c> and <c>pp</c> are touched.</summary>
    public required IReadOnlyList<RecalcResult> Unranked { get; init; }

    /// <summary>Every unreplayable row, grouped by cause, whatever the policy does with it.</summary>
    public required IReadOnlyDictionary<UnreplayableCase, IReadOnlyList<RecalcResult>> Unreplayable { get; init; }

    /// <summary>The policy chosen for each cause, for the causes a policy was given for.</summary>
    public required IReadOnlyDictionary<UnreplayableCase, UnreplayablePolicy> Policy { get; init; }

    /// <summary>
    /// Causes this run actually hit that the caller has not decided about. Non-empty is a warning in
    /// a report and a hard refusal in <c>supersede-apply</c>: the point is that no row is disposed of
    /// by a default nobody chose, while an operator is still never made to answer for a case their
    /// data does not contain.
    /// </summary>
    public required IReadOnlyList<UnreplayableCase> Undecided { get; init; }

    /// <summary>Rows refused by the gate. Never written, and in supersede mode they block an apply.</summary>
    public required IReadOnlyList<RecalcResult> Refused { get; init; }

    /// <summary>
    /// Rows judged on a ladder this code no longer has (<see cref="StoredScore.JudgedOnTheDeletedLadder"/>),
    /// whatever else the sweep does with them, and the count <c>--expect-unreproducible</c> names.
    ///
    /// <para>SINCE THE WINDOW RETUNE THAT IS EVERY ROW, and the list is kept rather than collapsed to
    /// a count for two reasons: the property it selects on may narrow again if a future ladder change
    /// ever carries an era bit, and the guard reads a LIST so the operator's number is checked
    /// against the rows the apply would actually touch. It used to be the backlog 133-to-147 window
    /// alone; that sub-population is still named separately in the report, because its mismatch has a
    /// different shape (see <see cref="StoredScore.JudgedOnTheFourTierCharacterLadder"/>).</para>
    ///
    /// <para>Every row the run considered is counted, not just the ones it re-derived, because the
    /// membership is a FACT ABOUT THE DATA rather than about how the sweep went: a row was played on
    /// the ladder it was played on whether or not its replay decoded, and no client can produce a new
    /// one for a retired ladder, so the same selection gives the same count on the report and on the
    /// apply. It is computed here rather than in the report so the number an operator reads and the
    /// number the guard checks cannot be two different definitions.</para>
    /// </summary>
    public required IReadOnlyList<RecalcResult> DeletedLadderWindow { get; init; }

    /// <summary>
    /// Rows whose own <c>statistics</c> PROVE they were judged under today's typo rule
    /// (<see cref="StoredScore.ProvablyJudgedUnderTheDeferredTypoRule"/>), i.e. the rows the
    /// reproduce pass re-derives under <c>TypoRule.Deferred</c> instead of the older default.
    ///
    /// <para>Named and counted for the same reason the window above is: it is a population, not a
    /// percentage. Before backlog 155 every one of these was re-derived on the retired rule, came
    /// back with its uncorrected typos turned into misses, and was reported as a row nobody could
    /// explain. An operator reading the report has to be able to see how many rows the pass is
    /// pinning that way, in either sweep, rather than infer it from a reproduction rate that got
    /// better.</para>
    ///
    /// <para>Every row the run considered is counted, not only the ones it re-derived, exactly as for
    /// the window: carrying the key is a fact about the row, which holds whether or not its replay
    /// decoded.</para>
    /// </summary>
    public required IReadOnlyList<RecalcResult> PinnedToTheDeferredTypoRule { get; init; }

    /// <summary>
    /// Rows whose SPACEBAR, RATE-WINDOW, COMBO-RESTORE and TYPO era the pass had to prove by
    /// reconstruction (<see cref="RecalcResult.EraProvedByReconstruction"/>, backlog 156, 157 and 158):
    /// they did not come back under <see cref="Recalculation.DefaultEraFor"/>, so each remaining
    /// combination of those axes was re-derived and the row pinned to the one that reproduced it
    /// exactly.
    ///
    /// <para>A row PINNED BY ITS OWN KEY is not in here, even though its typo era is also not the
    /// table-wide default: <see cref="PinnedToTheDeferredTypoRule"/> counts those, nothing was searched
    /// for them, and the two populations answer different questions (what a row could PROVE, against
    /// what had to be RECONSTRUCTED for it). A row can be in both, when its key proved the typo axis
    /// and its windows still had to be reconstructed.</para>
    ///
    /// <para>Named for the same reason as the two populations above, and with more urgency: this one
    /// GROWS WITH EVERY PLAY. Every row submitted since the 2026-08-13 release is in it, so a reader
    /// watching it stay flat run over run is watching the tool stop understanding new rows.</para>
    ///
    /// <para>UNLIKE the two above it is counted over the rows the sweep actually RE-DERIVED, and that
    /// asymmetry is deliberate rather than an oversight. Carrying a <c>perfect</c> key or a
    /// <c>good</c> key is a fact about the row, readable whether or not its replay decoded. Which era
    /// judged a row on these three axes is not written down anywhere: it is established BY the
    /// re-derivation, so a row with no usable replay has no era here, and giving it one would be the
    /// assignment without evidence that backlog 156 exists to refuse.</para>
    /// </summary>
    public required IReadOnlyList<RecalcResult> PinnedByEraSearch { get; init; }

    /// <summary>
    /// Whether the run selected a SUBSET of the scores (<c>--score</c> or <c>--limit</c>). The
    /// report's leaderboard section has to know: over a slice of a board it cannot tell a real place
    /// change from a row it simply did not load, so it declines to guess.
    /// </summary>
    public required bool Filtered { get; init; }

    public int RowsWritten => Rejudged.Count + Unranked.Count;

    public static WritePlan Build(IReadOnlyList<RecalcResult> results, RecalcMode mode, IReadOnlyDictionary<UnreplayableCase, UnreplayablePolicy> policy, bool filtered)
    {
        var unreplayable = new Dictionary<UnreplayableCase, IReadOnlyList<RecalcResult>>();

        foreach (var group in results.Where(r => r.Unreplayable).GroupBy(r => CaseOf(r.Skip)))
            unreplayable[group.Key] = group.ToList();

        // The policy applies to the SUPERSEDE sweep only. A reproduce run leaves every row it could
        // not re-derive exactly as it found it, which is what it has always done, and superseding
        // must never be the easier of the two to trigger.
        var unranked = mode == RecalcMode.Supersede
            ? unreplayable.Where(kvp => policy.GetValueOrDefault(kvp.Key) == UnreplayablePolicy.Unrank)
                          .SelectMany(kvp => kvp.Value)
                          .Where(r => r.Stored.Ranked)
                          .OrderBy(r => r.Stored.ScoreId)
                          .ToList()
            : new List<RecalcResult>();

        var undecided = mode == RecalcMode.Supersede
            ? unreplayable.Keys.Where(c => !policy.ContainsKey(c)).OrderBy(c => c.ToString(), StringComparer.Ordinal).ToList()
            : new List<UnreplayableCase>();

        return new WritePlan
        {
            Mode = mode,
            Rejudged = results.Where(r => r.Moves).OrderBy(r => r.Stored.ScoreId).ToList(),
            Unranked = unranked,
            Unreplayable = unreplayable,
            Policy = policy,
            Undecided = undecided,
            Refused = results.Where(r => r.Skip is SkipReason.NotReproducible or SkipReason.NotTheSameRun).OrderBy(r => r.Stored.ScoreId).ToList(),
            DeletedLadderWindow = results.Where(r => r.Stored.JudgedOnTheDeletedLadder).OrderBy(r => r.Stored.ScoreId).ToList(),
            PinnedToTheDeferredTypoRule = results.Where(r => r.Stored.ProvablyJudgedUnderTheDeferredTypoRule).OrderBy(r => r.Stored.ScoreId).ToList(),
            PinnedByEraSearch = results.Where(r => r.EraProvedByReconstruction).OrderBy(r => r.Stored.ScoreId).ToList(),
            Filtered = filtered,
        };
    }

    public static UnreplayableCase CaseOf(SkipReason reason) => reason switch
    {
        SkipReason.NoReplay => UnreplayableCase.NoReplay,
        SkipReason.UndecodableReplay => UnreplayableCase.Unreadable,
        SkipReason.EmptyReplay => UnreplayableCase.EmptyReplay,
        SkipReason.BeatmapUnavailable => UnreplayableCase.BeatmapMissing,
        SkipReason.BeatmapReuploaded => UnreplayableCase.BeatmapChanged,
        SkipReason.FailedRun => UnreplayableCase.FailedRun,
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "not an unreplayable row"),
    };

    /// <summary>The spelling used on the command line, which is the one the report prints too.</summary>
    public static string Name(UnreplayableCase value) => value switch
    {
        UnreplayableCase.NoReplay => "no-replay",
        UnreplayableCase.Unreadable => "unreadable",
        UnreplayableCase.EmptyReplay => "empty-replay",
        UnreplayableCase.BeatmapMissing => "beatmap-missing",
        UnreplayableCase.BeatmapChanged => "beatmap-changed",
        UnreplayableCase.FailedRun => "failed-run",
        _ => value.ToString(),
    };

    public static bool TryParseCase(string name, out UnreplayableCase value)
    {
        foreach (var candidate in Enum.GetValues<UnreplayableCase>())
        {
            if (string.Equals(Name(candidate), name, StringComparison.OrdinalIgnoreCase))
            {
                value = candidate;
                return true;
            }
        }

        value = default;
        return false;
    }

    public static bool TryParsePolicy(string name, out UnreplayablePolicy value)
    {
        switch (name.ToLowerInvariant())
        {
            case "keep": value = UnreplayablePolicy.Keep; return true;
            case "unrank": value = UnreplayablePolicy.Unrank; return true;
            default: value = default; return false;
        }
    }
}
