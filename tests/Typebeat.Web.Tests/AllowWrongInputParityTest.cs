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
/// <para>The claim being pinned, on both sides: a typed-through wrong char consumes its cell and is
/// judged <c>JudgementType.WrongChar</c>, which <c>DrawableTypeBeatHitObject.toHitResult</c> maps to
/// <c>HitResult.Miss</c>. So it is worth EXACTLY one miss on that cell, no more (a fix or an erase
/// cannot buy a second result, because the cell drawable applies one result ever) and no less (it
/// is not the bare combo break a REJECTED key leaves). Space stays strict in both models on both
/// sides, and the mash-fail streak stays on the rejection path only.</para>
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

            // ...and the mash-fail streak did NOT move. That guard only ever accrued on the
            // rejection path, so it is now Gatekeeper-only, which the browser can never select.
            Assert.That(probe.GetProperty("consecutiveWrongKeys").GetInt32(), Is.Zero);
            Assert.That(root.GetProperty("midCellWrongThenFixed").GetProperty("failed").GetBoolean(), Is.False);
        });
    }

    /// <summary>
    /// The headline equivalence, and the reason no golden combo table has to be re-derived by hand
    /// for the new branch: a wrong char typed into the last cell costs the submitted score exactly
    /// what never typing that cell at all costs, because both are one Miss in the same place in the
    /// judgement stream. Only the mistype count, which is priced by pp and by nothing else, tells
    /// the two apart.
    ///
    /// <para>This test states what the DESKTOP does, which is the whole job of this file, and NOT
    /// that a typo ought to be worth a miss. Backlog 109 asks precisely that question ("a miss is a
    /// character the player never typed, a typo is a typo"), so if the desktop ever stops mapping
    /// <c>WrongChar</c> to <c>HitResult.Miss</c>, this assertion is expected to move with it rather
    /// than to hold it back.</para>
    /// </summary>
    [Test]
    public void ATypedThroughWrongCharCostsExactlyWhatAMissedCellCosts()
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
    /// The guard that keeps a cell to ONE result however the play reaches it
    /// (<c>DrawableTypeBeatCharObject.ApplyEngineResult</c>: <c>if (Judged) return;</c>). Two ways
    /// to break it, and both are worse than a wrong number: a second result pushes the judged count
    /// past <c>maximum_statistics</c>, which fails <c>StatisticsValid</c> and stores the play
    /// UNRANKED. Backspacing is not an exotic path any more, it is what the default model expects
    /// the player to do about a typo.
    /// </summary>
    [Test]
    public void FixingOrErasingAWrongCharCannotBuyASecondResult()
    {
        var root = Harness();

        Assert.Multiple(() =>
        {
            foreach (string name in new[] { "lastCellTypedWrong", "lastCellWrongThenErased", "midCellWrongThenFixed" })
            {
                var stats = Dict(root.GetProperty(name), "statistics");

                Assert.That(stats.GetValueOrDefault("great") + stats.GetValueOrDefault("miss"), Is.EqualTo(15),
                    $"{name}: the 15 cells must account for exactly 15 judgements");
                Assert.That(stats.GetValueOrDefault("miss"), Is.EqualTo(1), name);
                Assert.That(Recompute(root.GetProperty(name)).StatisticsValid, Is.True, name);
            }

            // Erasing without fixing lands the cell back in Missed, which is what the player sees,
            // yet the submitted account is byte-identical to leaving the wrong char sitting there.
            var erased = root.GetProperty("lastCellWrongThenErased");
            var wrong = root.GetProperty("lastCellTypedWrong");
            Assert.That(erased.GetProperty("totalScore").GetInt64(), Is.EqualTo(wrong.GetProperty("totalScore").GetInt64()));
            Assert.That(Dict(erased, "statistics"), Is.EquivalentTo(Dict(wrong, "statistics")));
        });
    }

    /// <summary>
    /// Backspacing and retyping recovers the CELL (completion counts it, the player sees it fixed)
    /// but not the submitted judgement, and the two combo accounts part company over it: the engine's
    /// own live combo, which the HUD counts up, takes the retype, while the score processor's does
    /// not, because no result reached it. That asymmetry is the desktop client's, not an artefact of
    /// the mirror, and it is stated here so nobody "fixes" one side of it.
    /// </summary>
    [Test]
    public void AFixedTypoRecoversTheCellButNotTheJudgement()
    {
        var root = Harness();
        var fixedRun = root.GetProperty("midCellWrongThenFixed");

        Assert.Multiple(() =>
        {
            Assert.That(fixedRun.GetProperty("cellStates").GetString(), Does.Not.Contain("wrong"),
                "every cell ends up correct on screen");
            Assert.That(fixedRun.GetProperty("completion").GetDouble(), Is.EqualTo(14.0 / 15.0).Within(1e-12),
                "completion still reads the judgement, not the final cell state");

            Assert.That(fixedRun.GetProperty("engineMaxCombo").GetInt32(), Is.EqualTo(10), "the HUD combo takes the retype");
            Assert.That(fixedRun.GetProperty("maxCombo").GetInt32(), Is.EqualTo(9), "the SUBMITTED combo does not");
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
    /// <item><c>midCellWrongThenFixed</c>: greats at combo 1..5, the miss, then greats at 1..9 (the
    /// retype contributes nothing). Portion 300·(Σ(1..5)√i + Σ(1..9)√i) = 8306.499862,
    /// comboProgress 0.684182914, same accuracy, total = 673408.</item>
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
            ("midCellWrongThenFixed", 9, 673_408, 1),
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
