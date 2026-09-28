using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.Judgements;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.Objects;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.Scoring;
using typebeat.Game.Scoring;

namespace Typebeat.WireCompat;

/// <summary>
/// The desktop's LIVE play, headless, with HEALTH in it (backlog 306): a <see cref="TypingEngine"/>
/// with a real <see cref="TypeBeatScoreProcessor"/> AND a real <see cref="TypeBeatHealthProcessor"/>
/// behind it, wired at the seams a Player and <c>TypeBeatPlayfield</c> wire them.
///
/// <para><see cref="TypeBeatReplayScorer"/> is the headless assembly the other parity tests use, and
/// it re-derives no health on purpose (a stored row's fail is not the recalculation's business), so
/// it cannot say when a run dies or what a failed run submits. This is the same assembly with the
/// other processor added:</para>
///
/// <list type="bullet">
/// <item>Every cell's ONE osu result (a cell takes only its first, the drawable's
/// <c>if (Judged) return;</c>) is applied to the health processor and then the score processor, in
/// the Player's own order (<c>DrawableRuleset.NewResult</c>), and each seal ends with the LINE
/// object's IgnoreHit, which health runs its fail test on like any other result.</item>
/// <item>The six result-less health seams come from <see cref="TypeBeatHealthFeed.Attach"/>, the
/// very call the live playfield makes, so this is not a copy of the playfield's wiring but the
/// wiring itself.</item>
/// <item>The score seams (the hand-mirrored breaks, the restores, the combo-neutral ledger) are
/// <see cref="TypeBeatReplayScorer"/>'s under the live rules, and
/// <see cref="HealthLiveParityTest"/> holds a surviving run's account against that scorer to prove
/// it.</item>
/// </list>
///
/// <para>A FAILED run concludes as <c>Player.PerformFail</c> schedules it: the step that failed
/// runs to its end and the account is then populated through <see cref="ScoreProcessor.FailScore"/>.
/// The caller stops feeding at that step. "Runs to its end" does NOT mean the rest of a failing
/// seal's results reach the score: health is applied first, stamps each later result
/// <see cref="JudgementResult.FailedAtJudgement"/>, and <see cref="ScoreProcessor"/> drops a result
/// so stamped, which is why the order below is the Player's and not arbitrary.</para>
/// </summary>
internal sealed class LiveHealthArm : IDisposable
{
    public readonly TypingEngine Engine;
    public readonly TypeBeatScoreProcessor Score;
    public readonly TypeBeatHealthProcessor Health = new TypeBeatHealthProcessor();

    private readonly TypeBeatRuleset ruleset = new TypeBeatRuleset();
    private readonly Dictionary<int, (Slot line, SortedDictionary<int, Slot> cells)> lines = new Dictionary<int, (Slot, SortedDictionary<int, Slot>)>();
    private readonly IDisposable feed;

    /// <param name="playable">The playable beatmap (nested per-cell objects built), whose line
    /// objects' <c>LineIndex</c> matches the engine's line order.</param>
    /// <param name="engine">A started engine under the live rules, over the same lines.</param>
    /// <param name="noFail">Refuse every fail (<see cref="HealthProcessor.Failed"/> answering false),
    /// which is how osu's No Fail runs: the bar stays live and keeps moving. The fuzz sweep's health
    /// arm plays that way so its runs finish in full against a scorer with no health at all.</param>
    public LiveHealthArm(IBeatmap playable, TypingEngine engine, bool noFail = false)
    {
        Engine = engine;
        Score = new TypeBeatScoreProcessor(ruleset);
        Score.Mods.Value = Array.Empty<Mod>();
        Score.ApplyBeatmap(playable);

        foreach (var lineObject in playable.HitObjects.OfType<TypeBeatHitObject>())
        {
            var cells = new SortedDictionary<int, Slot>();

            foreach (var nested in lineObject.NestedHitObjects.OfType<TypeBeatCharObject>())
                cells[nested.CellIndex] = new Slot(nested);

            lines[lineObject.LineIndex] = (new Slot(lineObject), cells);
        }

        if (noFail)
        {
            Health.Failed += () =>
            {
                FailRequests++;
                FailRequested?.Invoke();
                return false;
            };
        }

        engine.CharJudged += onCharJudged;
        engine.LineSealed += onLineSealed;
        engine.Mistyped += onMistyped;
        engine.ComboRestored += onComboRestored;
        engine.WordAbandoned += onWordAbandoned;
        engine.AbandonSealed += onAbandonSealed;

        // Attached after the score seams, as the playfield attaches it.
        feed = TypeBeatHealthFeed.Attach(engine, () => Health);
    }

    /// <summary>How many times the health processor asked to fail and was refused (No Fail only).</summary>
    public int FailRequests { get; private set; }

    /// <summary>Raised on every refused fail request (No Fail only), so a driver can date the first.</summary>
    public event Action? FailRequested;

    private void apply(Slot slot, HitResult result)
    {
        if (!slot.Take(result))
            return;

        Health.ApplyResult(slot.Result);
        Score.ApplyResult(slot.Result);
    }

    private void onCharJudged(CharJudgement judgement)
    {
        if (TypeBeatResultMapping.CellResult(judgement.Type, TypoRule.Deferred) is HitResult result
            && lines.TryGetValue(judgement.LineIndex, out var line)
            && line.cells.TryGetValue(judgement.CellIndex, out var cell))
            apply(cell, result);

        if (Engine.FletcherEnabled && judgement.ComboAfter == 0)
            Score.Combo.Value = 0;
    }

    private void onLineSealed(LineSealResult sealResult)
    {
        if (Engine.BackDatedSealBreak && sealResult.ComboBroken)
            Score.Combo.Value = Math.Min(Score.Combo.Value, sealResult.SurvivingCombo);

        if (!lines.TryGetValue(sealResult.LineIndex, out var line))
            return;

        foreach ((int cellIndex, var cell) in line.cells)
        {
            if (cell.Judged)
                continue;

            var result = TypeBeatResultMapping.UnresolvedCellResult(Engine.CellLeftWrong(sealResult.LineIndex, cellIndex), TypoRule.Deferred);

            if (result == TypeBeatResultMapping.UNFIXED_TYPO || (Engine.BackDatedSealBreak && result == TypeBeatResultMapping.SEAL_MISS))
                Score.MarkComboNeutral(sealResult.LineIndex, cellIndex);

            apply(cell, result);
        }

        apply(line.line, TypeBeatResultMapping.LINE_RESULT);
    }

    private void onMistyped()
    {
        Score.Combo.Value = 0;
        Score.RecordMistype();
    }

    private void onComboRestored(int streak) => Score.RestoreCombo(streak);

    private void onWordAbandoned(AbandonedCells abandoned) => Score.Combo.Value = 0;

    private void onAbandonSealed(AbandonedCells abandoned)
    {
        foreach (int cellIndex in abandoned.CellIndices)
            Score.MarkComboNeutral(abandoned.LineIndex, cellIndex);
    }

    /// <summary>
    /// The account the Player would submit now: <see cref="ScoreProcessor.FailScore"/> for a failed
    /// run (rank F, passed false), <see cref="ScoreProcessor.PopulateScore"/> otherwise.
    /// </summary>
    public ScoreInfo Conclude()
    {
        var info = new ScoreInfo { Ruleset = ruleset.RulesetInfo, Passed = true };

        if (Health.HasFailed)
            Score.FailScore(info);
        else
            Score.PopulateScore(info);

        info.Statistics = info.Statistics.Where(kvp => kvp.Value != 0).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        info.MaximumStatistics = info.MaximumStatistics.Where(kvp => kvp.Value != 0).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

        return info;
    }

    public void Dispose()
    {
        Engine.CharJudged -= onCharJudged;
        Engine.LineSealed -= onLineSealed;
        Engine.Mistyped -= onMistyped;
        Engine.ComboRestored -= onComboRestored;
        Engine.WordAbandoned -= onWordAbandoned;
        Engine.AbandonSealed -= onAbandonSealed;
        feed.Dispose();
    }

    /// <summary>One judged object, the drawable's single <see cref="JudgementResult"/>.</summary>
    private sealed class Slot(HitObject hitObject)
    {
        public readonly JudgementResult Result = new JudgementResult(hitObject, hitObject.Judgement);

        public bool Judged { get; private set; }

        public bool Take(HitResult type)
        {
            if (Judged)
                return false;

            Judged = true;
            Result.Type = type;
            return true;
        }
    }
}
