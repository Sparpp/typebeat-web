using System.Text.Json;
using Typebeat.Web.Scoring;

namespace Typebeat.Web.Tests;

/// <summary>
/// Fidelity guard for the DEFAULT wrong-key model across the browser/server seam (backlog 107).
///
/// <para>Typing a wrong character THROUGH, instead of having it rejected, used to be an opt-in
/// desktop setting the browser deliberately did not implement: <c>typebeat-core.js</c> was
/// strict-only, and that was defensible while every desktop player was strict too. It is now the
/// default on the desktop, with strict rejection surviving only as the Gatekeeper mod, and /play
/// scores land on the SAME leaderboards. Without the matching JS change every browser play would be
/// judged under a model no desktop player uses, so the boards would quietly mix two scoring systems.
/// This fixture is what says the browser now judges wrong keys the way the desktop does.</para>
///
/// <para>The claim being pinned, on both sides, and it MOVED in backlog 109: a typed-through wrong
/// char consumes its cell but resolves NOTHING. A miss is a character the line ran out of time on; a
/// typo is a typo, and the player can still backspace and get the cell right, so the cell's one osu
/// result is DEFERRED. Fix it and the retype is that result (a real Great, recovering completion and
/// rank); leave it and the seal misses it exactly like a cell nobody touched. Either way the cell is
/// worth exactly one result, because the cell drawable applies one result ever.</para>
///
/// <para>The keypress itself costs a mistype and a combo break in BOTH models, and in neither does
/// that break travel on a judgement result, so both sides mirror it by hand into the score processor
/// (<c>TypeBeatPlayfield.onMistyped</c>). That is what stops <c>max_combo</c> counting on through the
/// rest of the line after a break the engine has already taken. Space stays strict in both models on
/// both sides, and the mash-fail streak stays on the rejection path only.</para>
///
/// <para>As in <see cref="MistypeParityTest"/>, nothing is hardcoded twice: the Node harness runs
/// real plays through the SHIPPED JS and the dictionaries it emits are fed to the server's own
/// <see cref="ScoringContract"/>, which is the code that judges them in production.</para>
/// </summary>
public class AllowWrongInputParityTest
{
    private const string mistype_key = "combo_break";

    private static JsonElement Harness() => JsHarness.Run("CoreAllowWrongHarness.cjs");

    private static Dictionary<string, int> Dict(JsonElement run, string key)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var property in run.GetProperty(key).EnumerateObject())
            result[property.Name] = property.Value.GetInt32();

        return result;
    }

    private static ScoringContract.Recomputed Recompute(JsonElement run)
        => ScoringContract.Recompute(Dict(run, "statistics"), Dict(run, "maximumStatistics"), run.GetProperty("maxCombo").GetInt32());

    [Test]
    public void TheBrowserTypesWrongCharactersThroughByDefault()
    {
        var root = Harness();

        Assert.Multiple(() =>
        {
            // The flag itself, because "the browser is permanently non-Gatekeeper" is the whole
            // argument for defaulting it on: /play sends no mods payload, so nothing can clear it.
            Assert.That(root.GetProperty("clean").GetProperty("allowWrongInput").GetBoolean(), Is.True);

            var probe = root.GetProperty("midCellWrongThenFixed").GetProperty("probe");

            // The cell TOOK the char and the caret moved on: this is the behaviour change.
            Assert.That(probe.GetProperty("state").GetString(), Is.EqualTo("wrong"));
            Assert.That(probe.GetProperty("typedChar").GetString(), Is.EqualTo("z"));
            Assert.That(probe.GetProperty("caretIndex").GetInt32(), Is.EqualTo(6), "the wrong key consumed cell 5");
            Assert.That(probe.GetProperty("mistypes").GetInt32(), Is.EqualTo(1), "it is still a mistype");

            // ...and the backlog-109 pair, at the instant the wrong key lands: the cell has handed
            // the score processor NOTHING (five judgements, one per cell before it, and the cell
            // itself unjudged), while the submitted combo has ALREADY broken.
            Assert.That(probe.GetProperty("judged").GetBoolean(), Is.False, "a typo resolves no cell");
            Assert.That(probe.GetProperty("processorJudged").GetInt32(), Is.EqualTo(5));
            Assert.That(probe.GetProperty("processorCombo").GetInt32(), Is.Zero, "the break is mirrored by hand");

            // ...and the mash-fail streak did NOT move. That guard only ever accrued on the
            // rejection path, so it is now Gatekeeper-only, which the browser can never select.
            Assert.That(probe.GetProperty("consecutiveWrongKeys").GetInt32(), Is.Zero);
            Assert.That(root.GetProperty("midCellWrongThenFixed").GetProperty("failed").GetBoolean(), Is.False);
        });
    }

    /// <summary>
    /// An UNCORRECTED typo costs the submitted score exactly what never typing that cell at all
    /// costs, because both end as one Miss in the same place in the judgement stream. Only the
    /// mistype count, which is priced by pp and by nothing else, tells the two apart.
    ///
    /// <para>Backlog 109 kept this deliberately, having asked the question it used to be an artefact
    /// of ("a miss is a character the player never typed, a typo is a typo"). The alternative, never
    /// letting a typo be a miss, would drop the cell out of <c>notes</c> and quietly move accuracy,
    /// the combo ratio and the pp length term. So a typo you go back for costs a mistype and a
    /// combo, and one you leave costs a mistype and a cell. What changed is that the cell is now
    /// spent at the SEAL, on the play's evidence, rather than at the keypress on a guess: the
    /// <c>lastCell*</c> runs below put the typo on the final cell precisely so the two timings
    /// coincide and the equivalence is exact.</para>
    /// </summary>
    [Test]
    public void AnUncorrectedTypoCostsExactlyWhatAMissedCellCosts()
    {
        var root = Harness();

        var wrong = root.GetProperty("lastCellTypedWrong");
        var skipped = root.GetProperty("lastCellSkipped");

        var wrongStats = Dict(wrong, "statistics");
        var skippedStats = Dict(skipped, "statistics");

        Assert.Multiple(() =>
        {
            Assert.That(wrong.GetProperty("totalScore").GetInt64(), Is.EqualTo(skipped.GetProperty("totalScore").GetInt64()));
            Assert.That(wrong.GetProperty("maxCombo").GetInt32(), Is.EqualTo(skipped.GetProperty("maxCombo").GetInt32()));
            Assert.That(wrong.GetProperty("accuracy").GetDouble(), Is.EqualTo(skipped.GetProperty("accuracy").GetDouble()));
            Assert.That(wrong.GetProperty("completion").GetDouble(), Is.EqualTo(skipped.GetProperty("completion").GetDouble()));
            Assert.That(wrong.GetProperty("rank").GetString(), Is.EqualTo(skipped.GetProperty("rank").GetString()));

            // Same dictionary once the mistype key is set aside.
            Assert.That(wrongStats[mistype_key], Is.EqualTo(1));
            Assert.That(skippedStats, Does.Not.ContainKey(mistype_key));
            wrongStats.Remove(mistype_key);
            Assert.That(wrongStats, Is.EquivalentTo(skippedStats));

            // And it is strictly worse than the clean run, i.e. it really did cost something.
            Assert.That(wrong.GetProperty("totalScore").GetInt64(),
                Is.LessThan(root.GetProperty("clean").GetProperty("totalScore").GetInt64()));
        });
    }

    /// <summary>
    /// Every cell accounts for EXACTLY one result however the play reaches it, which is the guard in
    /// <c>DrawableTypeBeatCharObject.ApplyEngineResult</c> (<c>if (Judged) return;</c>) plus the fact
    /// that a typo now takes none. Getting this wrong is worse than a wrong number in either
    /// direction: a second result pushes the judged count past <c>maximum_statistics</c>, which fails
    /// <c>StatisticsValid</c> and stores the play UNRANKED, and a missing one leaves a cell nobody
    /// ever judged. Backspacing is not an exotic path, it is what the default model expects the
    /// player to do about a typo.
    /// </summary>
    [Test]
    public void EveryCellAccountsForExactlyOneResultHoweverTheTypoEnds()
    {
        var root = Harness();

        Assert.Multiple(() =>
        {
            foreach (string name in new[] { "lastCellTypedWrong", "lastCellWrongThenErased", "midCellTypedWrong", "midCellWrongThenFixed" })
            {
                var run = root.GetProperty(name);
                var stats = Dict(run, "statistics");

                Assert.That(stats.GetValueOrDefault("great") + stats.GetValueOrDefault("miss"), Is.EqualTo(15),
                    $"{name}: the 15 cells must account for exactly 15 judgements");
                Assert.That(Recompute(run).StatisticsValid, Is.True, name);
            }

            // Left alone, the cell is a miss...
            foreach (string name in new[] { "lastCellTypedWrong", "lastCellWrongThenErased", "midCellTypedWrong" })
                Assert.That(Dict(root.GetProperty(name), "statistics").GetValueOrDefault("miss"), Is.EqualTo(1), name);

            // ...and fixed, it is a Great instead. One result either way, never two and never none.
            Assert.That(Dict(root.GetProperty("midCellWrongThenFixed"), "statistics"), Does.Not.ContainKey("miss"));

            // Erasing without fixing lands the cell back in 'untyped', which is what the player sees,
            // and the seal then misses it, so the submitted account is byte-identical to leaving the
            // wrong char sitting there.
            var erased = root.GetProperty("lastCellWrongThenErased");
            var wrong = root.GetProperty("lastCellTypedWrong");
            Assert.That(erased.GetProperty("totalScore").GetInt64(), Is.EqualTo(wrong.GetProperty("totalScore").GetInt64()));
            Assert.That(Dict(erased, "statistics"), Is.EquivalentTo(Dict(wrong, "statistics")));
        });
    }

    /// <summary>
    /// The trap deferring the result opens, and the reason this is not a two-line change. osu's combo
    /// is maintained INCREMENTALLY off judgement results, so a wrong keypress that raises no result
    /// leaves the submitted <c>max_combo</c> nothing to break it, and it would count straight on
    /// through a break the engine has already taken. Both sides mirror the break by hand instead.
    ///
    /// <para><c>midCellTypedWrong</c> is where that is visible: the typo is on cell 5 of 15 and is
    /// never fixed, so the break must land on the keypress while the cell's miss lands at the seal,
    /// nine judgements later. Submitted <c>max_combo</c> is 9 (cells 6..14). Without the hand-written
    /// break it would be 15, and the browser would out-score the identical desktop play on the shared
    /// leaderboards. The engine's own live combo agrees at 9, which is the whole point: the two
    /// accounts are separate, and they have to reach the same number here.</para>
    /// </summary>
    [Test]
    public void TheSubmittedComboBreaksAtTheTypoNotAtTheSeal()
    {
        var root = Harness();
        var run = root.GetProperty("midCellTypedWrong");

        Assert.Multiple(() =>
        {
            Assert.That(run.GetProperty("maxCombo").GetInt32(), Is.EqualTo(9),
                "the submitted combo restarts at the typo, not at the seal that eventually misses it");
            Assert.That(run.GetProperty("engineMaxCombo").GetInt32(), Is.EqualTo(9), "and the HUD combo agrees");
            Assert.That(run.GetProperty("maxCombo").GetInt32(),
                Is.LessThan(root.GetProperty("clean").GetProperty("maxCombo").GetInt32()));

            // The whole submitted account is byte-identical to what the OLD model produced for this
            // play: nothing scores between the break and the miss, so moving the miss to the seal
            // moved no number. An uncorrected typo costs exactly what it always cost.
            Assert.That(run.GetProperty("totalScore").GetInt64(), Is.EqualTo(673_408));
            Assert.That(Dict(run, "statistics"),
                Is.EquivalentTo(new Dictionary<string, int> { ["great"] = 14, ["miss"] = 1, ["combo_break"] = 1 }));
        });
    }

    /// <summary>
    /// Backlog 122, the other half of the test above. The break belongs to the KEYPRESS, and it
    /// happens exactly once: the run the player builds through the rest of the line after an
    /// uncorrected typo survives the seal and carries into the next line.
    ///
    /// <para>Backlog 109 had made it happen twice. Deferring the cell's result forced the keypress
    /// break to be mirrored by hand, but the deferred result is still a Miss when nobody fixes the
    /// cell, and osu resets combo on every Miss, so the seal cut the run a second time, nine cells
    /// after the mistake. That is strictly harsher than the single pre-109 break, which is the
    /// opposite of what deferring the result was for, and it is why 17 of 149 stored scores lost
    /// total_score on recalculation.</para>
    ///
    /// <para>One line cannot show it: there the deferred miss lands after every other cell has been
    /// judged, so it can only cut the run short of the NEXT line, which is what the two-line runs
    /// exist for. Line 0's cells 6..14 rebuild a run of 9 and line 1 adds ten more, so the submitted
    /// max_combo reads 19 if the run survived the seal and 10 if it did not.</para>
    /// </summary>
    [Test]
    public void TheComboRunAfterAnUncorrectedTypoSurvivesTheSeal()
    {
        var root = Harness();
        var run = root.GetProperty("twoLineMidCellTypedWrong");

        Assert.Multiple(() =>
        {
            Assert.That(run.GetProperty("totalCells").GetInt32(), Is.EqualTo(25), "15 cells on line 0, 10 on line 1");

            Assert.That(run.GetProperty("maxCombo").GetInt32(), Is.EqualTo(19),
                "nine cells of line 0 after the typo, plus all ten of line 1: the seal does not cut the run");

            // It really did break, once, where the player made the mistake.
            Assert.That(root.GetProperty("twoLineClean").GetProperty("maxCombo").GetInt32(), Is.EqualTo(25));
            Assert.That(run.GetProperty("maxCombo").GetInt32(),
                Is.LessThan(root.GetProperty("twoLineClean").GetProperty("maxCombo").GetInt32()));

            // The HUD's own live combo is a SEPARATE account with its own rules, and it still
            // restarts at the seal (TypingEngine's seal loop breaks it for any missed cell, wrong
            // ones included since backlog 109). Only the number pinned above is submitted, and only
            // that one is what the shared leaderboards rank.
            Assert.That(run.GetProperty("engineMaxCombo").GetInt32(), Is.EqualTo(10));
        });
    }

    /// <summary>
    /// What backlog 122 must NOT move. The cell still MISSES, so every quantity derived from the
    /// result stream is exactly what an uncorrected typo always cost: one miss, one mistype, and
    /// completion and rank short by that one cell. Only the combo the later judgements are weighted
    /// by changed, which is why no pp constant moves with it.
    ///
    /// <para><see cref="TheSubmittedComboBreaksAtTheTypoNotAtTheSeal"/> is the same claim on the
    /// single-line run, where it is even stronger: that whole account, total score included, is
    /// byte-identical to what it was before.</para>
    /// </summary>
    [Test]
    public void TheUncorrectedTypoAccountIsUnchangedApartFromCombo()
    {
        var root = Harness();
        var run = root.GetProperty("twoLineMidCellTypedWrong");

        Assert.Multiple(() =>
        {
            // notes = great + ok + meh + miss, one per cell, with the mistype counted apart.
            Assert.That(Dict(run, "statistics"),
                Is.EquivalentTo(new Dictionary<string, int> { ["great"] = 24, ["miss"] = 1, ["combo_break"] = 1 }));

            Assert.That(run.GetProperty("accuracy").GetDouble(), Is.EqualTo(24.0 / 25.0).Within(1e-12));
            Assert.That(run.GetProperty("completion").GetDouble(), Is.EqualTo(24.0 / 25.0).Within(1e-12));
            Assert.That(run.GetProperty("rank").GetString(), Is.EqualTo("S"));

            // ...and the server agrees, recomputing the same play through its own contract.
            var recomputed = Recompute(run);
            Assert.That(recomputed.StatisticsValid, Is.True);
            Assert.That(recomputed.Rank, Is.EqualTo("S"));
        });
    }

    /// <summary>
    /// THE point of backlog 109: backspacing and retyping recovers the cell for real. It ends green
    /// on screen, it ends a Great in the statistics, and completion and the rank recover with it,
    /// because the typo never spent the cell's one result. Before, the fix went green while the
    /// statistics kept a miss for ever, so the play could see an A it had typed an X's worth of.
    ///
    /// <para>The two combo accounts also stop parting company over it: the HUD's live combo and the
    /// submitted one both read 10, where the submitted one used to lag at 9. What the fix does NOT
    /// buy back is the mistake itself, which is right: the mistype is still counted (and still priced
    /// by pp) and the combo it broke is still broken.</para>
    /// </summary>
    [Test]
    public void AFixedTypoRecoversTheCellTheJudgementAndTheRank()
    {
        var root = Harness();
        var fixedRun = root.GetProperty("midCellWrongThenFixed");
        var leftRun = root.GetProperty("midCellTypedWrong");

        Assert.Multiple(() =>
        {
            Assert.That(fixedRun.GetProperty("cellStates").GetString(), Does.Not.Contain("wrong"),
                "every cell ends up correct on screen");
            Assert.That(Dict(fixedRun, "statistics").GetValueOrDefault("great"), Is.EqualTo(15),
                "...and the judgement agrees with the screen now");

            Assert.That(fixedRun.GetProperty("completion").GetDouble(), Is.EqualTo(1).Within(1e-12));
            Assert.That(fixedRun.GetProperty("rank").GetString(), Is.EqualTo("X"));

            Assert.That(fixedRun.GetProperty("engineMaxCombo").GetInt32(), Is.EqualTo(10), "the HUD combo takes the retype");
            Assert.That(fixedRun.GetProperty("maxCombo").GetInt32(), Is.EqualTo(10), "and so does the SUBMITTED combo");

            // The identical play with the typo left alone does not recover: the recovery is earned by
            // going back for the cell, not handed out for having typed the rest of the line.
            Assert.That(leftRun.GetProperty("completion").GetDouble(), Is.EqualTo(14.0 / 15.0).Within(1e-12));
            Assert.That(leftRun.GetProperty("rank").GetString(), Is.EqualTo("A"));

            // What the fix does not buy back.
            Assert.That(Dict(fixedRun, "statistics")[mistype_key], Is.EqualTo(1));
            Assert.That(fixedRun.GetProperty("totalScore").GetInt64(),
                Is.LessThan(root.GetProperty("clean").GetProperty("totalScore").GetInt64()));
        });
    }

    /// <summary>
    /// The carve-out that did not move: SPACE is the word-advance key, not a glyph, so neither
    /// direction of it is ever typed through. Both halves still take the rejection path, in the
    /// default model, on both sides, and they are therefore the only wrong keypresses a browser
    /// player can make that still feed the 13-in-a-row mash guard.
    /// </summary>
    [Test]
    public void SpaceStaysStrictInBothDirections()
    {
        var root = Harness();

        Assert.Multiple(() =>
        {
            foreach (string name in new[] { "wrongKeyOnWordGap", "spaceKeyOnLetter" })
            {
                var probe = root.GetProperty(name).GetProperty("probe");

                Assert.That(probe.GetProperty("state").GetString(), Is.EqualTo("untyped"), $"{name}: nothing was written");
                Assert.That(probe.GetProperty("typedChar").ValueKind, Is.EqualTo(JsonValueKind.Null), name);
                Assert.That(probe.GetProperty("consecutiveWrongKeys").GetInt32(), Is.EqualTo(1),
                    $"{name}: the rejection path is the one that feeds the mash guard");
                Assert.That(probe.GetProperty("mistypes").GetInt32(), Is.EqualTo(1), name);

                // The cell was held, so it is still typed correctly afterwards: no miss anywhere.
                var stats = Dict(root.GetProperty(name), "statistics");
                Assert.That(stats.GetValueOrDefault("great"), Is.EqualTo(15), name);
                Assert.That(stats, Does.Not.ContainKey("miss"), name);
                Assert.That(stats[mistype_key], Is.EqualTo(1), name);
            }

            // Caret held means the SAME cell is still the target: cell 3 for the word gap, cell 0
            // for the letter.
            Assert.That(root.GetProperty("wrongKeyOnWordGap").GetProperty("probe").GetProperty("caretIndex").GetInt32(), Is.EqualTo(3));
            Assert.That(root.GetProperty("spaceKeyOnLetter").GetProperty("probe").GetProperty("caretIndex").GetInt32(), Is.EqualTo(0));
        });
    }

    /// <summary>
    /// The arithmetic, stated once as literals so a reviewer can check the mirror against the C#
    /// model rather than only against itself. Derived exactly as <see cref="MistypeParityTest"/>
    /// derives its table (<c>ScoreProcessor.ApplyResultInternal</c> / <c>updateScore</c>), with
    /// <c>maximumComboPortion = 300 * Σ(i=1..15)√i = 12140.758980</c> from the autoplay simulation:
    ///
    /// <list type="bullet">
    /// <item><c>clean</c>: combo 1..15, comboProgress 1, accuracy 1, so the full 1000000.</item>
    /// <item><c>lastCellTypedWrong</c> (and the skipped/erased runs it must equal): greats at combo
    /// 1..14 then a miss contributing 300·0^0.5 = 0. Portion 300·36.596213 = 10978.863976,
    /// comboProgress 0.904297993. Judged accuracy is 14·300/(15·300) = 0.933333, accuracyProgress
    /// 15/15 = 1, so total = round(500000·0.933333·0.904297993 + 500000·0.933333^5) = 776129.</item>
    /// <item><c>midCellTypedWrong</c>: greats at combo 1..5, the typo (a hand-written break, no
    /// result), greats at combo 1..9, and only THEN the seal's miss at combo 0, contributing 0.
    /// Portion 300·(Σ(1..5)√i + Σ(1..9)√i) = 8306.499862, comboProgress 0.684182914, same accuracy,
    /// total = 673408. Identical to what the old model produced for this play, because the miss
    /// moved to a place where it weighted nothing either way.</item>
    /// <item><c>midCellWrongThenFixed</c>: greats at combo 1..5, the typo, then greats at combo
    /// 1..10, the first of which IS the fixed cell (its own first and only result). Portion
    /// 300·(Σ(1..5)√i + Σ(1..10)√i) = 9255.183160, comboProgress 0.762323276, and now accuracy is a
    /// flat 1 because nothing missed, so total = round(500000·0.762323276 + 500000) = 881162.</item>
    /// <item><c>wrongKeyOnWordGap</c>: 15 greats, one rejected key breaking combo before cell 3, so
    /// combo runs 1..3 then 1..12. Portion 10018.580688, comboProgress 0.825202173, accuracy 1,
    /// total = round(500000·0.825202173 + 500000) = 912601.</item>
    /// <item><c>spaceKeyOnLetter</c>: the break lands on an already-zero combo, so the run is still
    /// 1..15 and the score is still exactly 1000000. A wrong keypress on the opening cell costs
    /// combo nothing; it still costs pp, through the mistype count.</item>
    /// </list>
    /// </summary>
    [Test]
    public void TheSubmittedTotalsMatchTheHandDerivedCsharpModel()
    {
        var root = Harness();

        (string Run, int MaxCombo, long TotalScore, int Mistypes)[] expected =
        [
            ("clean", 15, 1_000_000, 0),
            ("lastCellTypedWrong", 14, 776_129, 1),
            ("lastCellSkipped", 14, 776_129, 0),
            ("lastCellWrongThenErased", 14, 776_129, 1),
            ("midCellTypedWrong", 9, 673_408, 1),
            ("midCellWrongThenFixed", 10, 881_162, 1),
            ("wrongKeyOnWordGap", 12, 912_601, 1),
            ("spaceKeyOnLetter", 15, 1_000_000, 1),
        ];

        Assert.Multiple(() =>
        {
            foreach (var (name, maxCombo, totalScore, mistypes) in expected)
            {
                var run = root.GetProperty(name);

                Assert.That(run.GetProperty("maxCombo").GetInt32(), Is.EqualTo(maxCombo), $"{name} max_combo");
                Assert.That(run.GetProperty("totalScore").GetInt64(), Is.EqualTo(totalScore), $"{name} total_score");
                Assert.That(Dict(run, "statistics").GetValueOrDefault(mistype_key), Is.EqualTo(mistypes), $"{name} mistypes");

                // maximum_statistics stays one great per cell in every run: a wrong key must never
                // inflate the denominator of accuracy, completion or pp.
                Assert.That(Dict(run, "maximumStatistics"),
                    Is.EquivalentTo(new Dictionary<string, int> { ["great"] = 15 }), name);

                var recomputed = Recompute(run);
                Assert.That(recomputed.StatisticsValid, Is.True, name);
                Assert.That(ScoringContract.TotalScoreWithinBounds(run.GetProperty("totalScore").GetInt64(), recomputed), Is.True, name);
            }
        });
    }
}
