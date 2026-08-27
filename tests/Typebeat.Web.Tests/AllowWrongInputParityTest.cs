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
/// <para>The claim being pinned, on both sides, and it moved in backlog 109 and again in 124: a
/// typed-through wrong char consumes its cell but resolves NOTHING at the keypress. A miss is a
/// character the player never finished; a typo is a character they finished wrongly, and they can
/// still backspace and get the cell right, so the cell's one osu result is DEFERRED. Fix it and the
/// retype is that result (a real Great). Leave it and the seal resolves it under a key of its OWN,
/// <c>good</c>, NOT a miss (backlog 124 and 126), so it costs accuracy and completion but never the
/// miss count. Erase it and leave the cell empty and it is a miss again, because then the character
/// really was never finished. Either way the cell is worth exactly one result, because the cell drawable
/// applies one result ever.</para>
///
/// <para>The keypress itself costs a mistype and a combo break in BOTH models, and in neither does
/// that break travel on a judgement result, so both sides mirror it by hand into the score processor
/// (<c>TypeBeatPlayfield.onMistyped</c>). That is what stops <c>max_combo</c> counting on through the
/// rest of the line after a break the engine has already taken. The space KEY stays strict in both
/// models on both sides, and the mash-fail streak stays on the rejection path only. WHICH CELLS the
/// type-through reaches moved once more in backlog 181: the lyric characters always, and the word
/// gap too, which is live on the desktop for every mod stack and therefore unconditional here (the
/// browser plays live and only live, so it cannot be on the far side of that era flag).</para>
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
    /// Backlog 124, stated as the comparison it reverses. An UNCORRECTED typo and a cell nobody
    /// typed used to be the same submitted account, distinguishable only by the mistype count.
    /// They are two different facts about a play, so they are now two different results: the typo
    /// is a <c>good</c>, the untyped cell a <c>miss</c>. Both runs put the spoiled cell LAST, so
    /// their results land in the same place in the judgement stream and the comparison is exact:
    /// everything except the tier is held still.
    ///
    /// <para>What the typo costs, since backlog 126: accuracy (the typo tier is weighted 50 against
    /// the cell's 300 maximum), the mistype, the combo break already taken at the keypress, AND
    /// completion and rank, exactly as the miss costs them. The two runs come apart only in the MISS
    /// COUNT, and therefore in pp and in total score.</para>
    /// </summary>
    [Test]
    public void AnUncorrectedTypoCostsCompletionLikeAMissButIsNotOne()
    {
        var root = Harness();

        var wrong = root.GetProperty("lastCellTypedWrong");
        var skipped = root.GetProperty("lastCellSkipped");

        Assert.Multiple(() =>
        {
            // The cell was finished but not TYPED, so it costs completion and rank just as the miss
            // below does, while the miss count stays clean.
            Assert.That(Dict(wrong, "statistics"),
                Is.EquivalentTo(new Dictionary<string, int> { ["great"] = 14, ["good"] = 1, ["combo_break"] = 1 }));
            Assert.That(wrong.GetProperty("completion").GetDouble(), Is.EqualTo(14.0 / 15.0).Within(1e-12));
            Assert.That(wrong.GetProperty("rank").GetString(), Is.EqualTo("A"));

            // The cell was NEVER finished: a miss, and it costs completion and rank as it always has.
            Assert.That(Dict(skipped, "statistics"),
                Is.EquivalentTo(new Dictionary<string, int> { ["great"] = 14, ["miss"] = 1 }));
            Assert.That(skipped.GetProperty("completion").GetDouble(), Is.EqualTo(14.0 / 15.0).Within(1e-12));
            Assert.That(skipped.GetProperty("rank").GetString(), Is.EqualTo("A"));

            // ...and the server agrees, recomputing the browser's dictionary through its own
            // contract: good is accuracy-affecting there but not TYPED, so the ranks match.
            Assert.That(Recompute(wrong).Rank, Is.EqualTo("A"));
            Assert.That(Recompute(wrong).Completion, Is.EqualTo(14.0 / 15.0).Within(1e-12));

            // What the typo pays in accuracy, at the re-weighted (Meh) rate, plus one mistype.
            Assert.That(wrong.GetProperty("accuracy").GetDouble(), Is.EqualTo((14 * 300 + 50) / 4500.0).Within(1e-12));
            Assert.That(skipped.GetProperty("accuracy").GetDouble(), Is.EqualTo(14.0 / 15.0).Within(1e-12));

            // Combo is the one number the two agree on, because the break was taken at the keypress
            // and the seal's hit is applied combo-neutral, so it cannot hand the cell back either.
            Assert.That(wrong.GetProperty("maxCombo").GetInt32(), Is.EqualTo(14));
            Assert.That(skipped.GetProperty("maxCombo").GetInt32(), Is.EqualTo(14));

            // A miss still hurts more than a typo, and a typo still hurts.
            Assert.That(wrong.GetProperty("totalScore").GetInt64(), Is.GreaterThan(skipped.GetProperty("totalScore").GetInt64()));
            Assert.That(wrong.GetProperty("totalScore").GetInt64(),
                Is.LessThan(root.GetProperty("clean").GetProperty("totalScore").GetInt64()));
        });
    }

    /// <summary>
    /// The distinction is drawn on the cell's STATE, not on its history, and this is the run that
    /// says so: a typo the player backspaced away and then left EMPTY is a character they did not
    /// finish, so it is a miss again, byte-identical to never having touched the cell. The mistype
    /// stays, because the wrong key really was pressed.
    /// </summary>
    [Test]
    public void ATypoErasedAndLeftEmptyIsAMissAgain()
    {
        var root = Harness();

        var erased = root.GetProperty("lastCellWrongThenErased");
        var skipped = root.GetProperty("lastCellSkipped");

        var erasedStats = Dict(erased, "statistics");

        Assert.Multiple(() =>
        {
            Assert.That(erasedStats[mistype_key], Is.EqualTo(1));
            erasedStats.Remove(mistype_key);
            Assert.That(erasedStats, Is.EquivalentTo(Dict(skipped, "statistics")));

            Assert.That(erased.GetProperty("totalScore").GetInt64(), Is.EqualTo(skipped.GetProperty("totalScore").GetInt64()));
            Assert.That(erased.GetProperty("completion").GetDouble(), Is.EqualTo(skipped.GetProperty("completion").GetDouble()));
            Assert.That(erased.GetProperty("rank").GetString(), Is.EqualTo("A"));

            // ...and it is strictly worse than leaving the wrong character sitting there, which is
            // the point: the wrong character at least finished the cell.
            Assert.That(erased.GetProperty("totalScore").GetInt64(),
                Is.LessThan(root.GetProperty("lastCellTypedWrong").GetProperty("totalScore").GetInt64()));
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

                // `ok` is in the sum since backlog 210: a CORRECTED cell is capped at Ok, so the
                // fixed run's fifteenth judgement arrives under that key rather than as a `great`.
                // It is still exactly one result for the cell, which is what this counts.
                Assert.That(stats.GetValueOrDefault("great") + stats.GetValueOrDefault("ok") + stats.GetValueOrDefault("good") + stats.GetValueOrDefault("miss"),
                    Is.EqualTo(15), $"{name}: the 15 cells must account for exactly 15 judgements");
                Assert.That(Recompute(run).StatisticsValid, Is.True, name);
            }

            // Left sitting wrong, the cell is a typo: judged, counted, and not a miss.
            foreach (string name in new[] { "lastCellTypedWrong", "midCellTypedWrong" })
            {
                var stats = Dict(root.GetProperty(name), "statistics");
                Assert.That(stats.GetValueOrDefault("good"), Is.EqualTo(1), name);
                Assert.That(stats, Does.Not.ContainKey("miss"), name);
                Assert.That(stats, Does.Not.ContainKey("meh"), name);
            }

            // Erased and left empty, it is a miss; fixed, it is an Ok (backlog 210 caps a corrected
            // cell there however well the retype was timed). One result every way, never two and
            // never none.
            Assert.That(Dict(root.GetProperty("lastCellWrongThenErased"), "statistics").GetValueOrDefault("miss"), Is.EqualTo(1));
            Assert.That(Dict(root.GetProperty("midCellWrongThenFixed"), "statistics"), Does.Not.ContainKey("miss"));
            Assert.That(Dict(root.GetProperty("midCellWrongThenFixed"), "statistics"), Does.Not.ContainKey("good"));
            Assert.That(Dict(root.GetProperty("midCellWrongThenFixed"), "statistics").GetValueOrDefault("ok"), Is.EqualTo(1),
                "the fixed cell is the capped one, and it is the only one");
        });
    }

    /// <summary>
    /// The trap deferring the result opens, and the reason this is not a two-line change. osu's combo
    /// is maintained INCREMENTALLY off judgement results, so a wrong keypress that raises no result
    /// leaves the submitted <c>max_combo</c> nothing to break it, and it would count straight on
    /// through a break the engine has already taken. Both sides mirror the break by hand instead.
    ///
    /// <para><c>midCellTypedWrong</c> is where that is visible: the typo is on cell 5 of 15 and is
    /// never fixed, so the break must land on the keypress while the cell's own result lands at the
    /// seal, nine judgements later. Submitted <c>max_combo</c> is 9 (cells 6..14). Without the
    /// hand-written break it would be 15, and the browser would out-score the identical desktop play
    /// on the shared leaderboards. Since backlog 124 the seal's result is a HIT, so the other
    /// direction matters too: applied normally it would extend the run to 10 and inflate
    /// <c>max_combo</c> by the very cell that broke it, which is what the combo-neutral mark stops.
    /// The engine's own live combo agrees at 9, which is the whole point: the two accounts are
    /// separate, and they have to reach the same number here.</para>
    /// </summary>
    [Test]
    public void TheSubmittedComboBreaksAtTheTypoNotAtTheSeal()
    {
        var root = Harness();
        var run = root.GetProperty("midCellTypedWrong");

        Assert.Multiple(() =>
        {
            Assert.That(run.GetProperty("maxCombo").GetInt32(), Is.EqualTo(9),
                "the submitted combo restarts at the typo, and the seal neither cuts nor extends it");
            Assert.That(run.GetProperty("engineMaxCombo").GetInt32(), Is.EqualTo(9), "and the HUD combo agrees");
            Assert.That(run.GetProperty("maxCombo").GetInt32(),
                Is.LessThan(root.GetProperty("clean").GetProperty("maxCombo").GetInt32()));

            Assert.That(run.GetProperty("totalScore").GetInt64(), Is.EqualTo(733_802));
            Assert.That(Dict(run, "statistics"),
                Is.EquivalentTo(new Dictionary<string, int> { ["great"] = 14, ["good"] = 1, ["combo_break"] = 1 }));
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

            // Backlog 123, closed by 124: the HUD's own live combo is a SEPARATE account, and it
            // used to restart at the seal, because backlog 109 had made TypingEngine's seal loop
            // count a wrong cell as missed. It no longer does, so the counter the player watches and
            // the number the leaderboards rank agree again, as they did pre-109.
            Assert.That(run.GetProperty("engineMaxCombo").GetInt32(), Is.EqualTo(19));
        });
    }

    /// <summary>
    /// The DENOMINATOR, which is the constraint backlog 124 had to work inside. Taking the cell out
    /// of the miss count must not take it out of the count altogether: it stays one judged note, so
    /// <c>notes</c> is one per cell and accuracy, the combo ratio and the pp length term keep
    /// measuring the map the player actually played. Had the cell simply stopped resolving, a line
    /// typed entirely as typos would judge nothing and read completion 1 over an empty denominator.
    ///
    /// <para>The server has to agree, because it recomputes every submitted play through its own
    /// <see cref="ScoringContract"/>: <c>good</c> is accuracy-affecting there too, and left out of
    /// the typed count there too, so the browser's rank and the recomputed rank are the same S.</para>
    /// </summary>
    [Test]
    public void TheUncorrectedTypoStaysInTheDenominator()
    {
        var root = Harness();
        var run = root.GetProperty("twoLineMidCellTypedWrong");

        Assert.Multiple(() =>
        {
            // notes = great + ok + meh + good + miss, one per cell, the mistype counted apart.
            Assert.That(Dict(run, "statistics"),
                Is.EquivalentTo(new Dictionary<string, int> { ["great"] = 24, ["good"] = 1, ["combo_break"] = 1 }));

            Assert.That(run.GetProperty("accuracy").GetDouble(), Is.EqualTo((24 * 300 + 50) / 7500.0).Within(1e-12));

            // 25 cells JUDGED and 24 of them TYPED, which is the denominator doing its job: 24/25 is
            // an S, not the "1 over an empty denominator" a non-resolving cell would have produced.
            Assert.That(run.GetProperty("completion").GetDouble(), Is.EqualTo(24 / 25.0).Within(1e-12));
            Assert.That(run.GetProperty("rank").GetString(), Is.EqualTo("S"));

            // ...and the server agrees, recomputing the same play through its own contract.
            var recomputed = Recompute(run);
            Assert.That(recomputed.StatisticsValid, Is.True);
            Assert.That(recomputed.Completion, Is.EqualTo(24 / 25.0).Within(1e-12));
            Assert.That(recomputed.Rank, Is.EqualTo("S"));
        });
    }

    /// <summary>
    /// THE point of backlog 109: backspacing and retyping recovers the cell for real. It ends green
    /// on screen, it ends a JUDGED cell in the statistics, and completion and the rank recover with
    /// it, because the typo never spent the cell's one result. Before, the fix went green while the
    /// statistics kept a miss for ever, so the play could see an A it had typed an X's worth of.
    ///
    /// <para>The two combo accounts also stop parting company over it: the HUD's live combo and the
    /// submitted one agree, where the submitted one used to lag by one. Since backlog 126 the fix
    /// buys back completion and rank, because an uncorrected typo is not a cell TYPED, and since
    /// backlog 140 it buys back the COMBO as well: correcting the cell resumes the streak the wrong
    /// key broke, so both accounts read the full 15.
    /// Fixing a typo is therefore worth score and not only accuracy, which is what makes going back
    /// for it the right play under a typo stat that counts keypresses.</para>
    ///
    /// <para>What the fix does NOT buy back, and this is where backlog 210 moved the line. It used to
    /// buy back the total score EXACTLY, so a fixed typo reached the clean run's numbers bit for bit
    /// and the detour was free. Now the corrected cell is capped at Ok (min(the retype's tier, Ok)),
    /// so the run keeps its streak, its completion and its X, and pays 200 of that cell's 300 in
    /// accuracy and total score. The wrong keypress is also still counted under <c>combo_break</c>
    /// and still priced by pp's typo term, and no correction can unpress it
    /// (<see cref="ComboRestoreParityTest"/> holds the combo rule itself).</para>
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
            Assert.That(Dict(fixedRun, "statistics").GetValueOrDefault("great"), Is.EqualTo(14),
                "...and the judgement agrees with the screen now: every cell judged, the fixed one capped");
            Assert.That(Dict(fixedRun, "statistics").GetValueOrDefault("ok"), Is.EqualTo(1),
                "the corrected cell, capped at Ok by backlog 210 however well the retype was timed");

            Assert.That(fixedRun.GetProperty("completion").GetDouble(), Is.EqualTo(1).Within(1e-12));
            Assert.That(fixedRun.GetProperty("rank").GetString(), Is.EqualTo("X"));

            Assert.That(fixedRun.GetProperty("engineMaxCombo").GetInt32(), Is.EqualTo(15),
                "the HUD combo resumes the streak the typo broke and runs the map out");
            Assert.That(fixedRun.GetProperty("maxCombo").GetInt32(), Is.EqualTo(15), "and so does the SUBMITTED combo");

            // The identical play with the typo left alone loses that cell's completion, and its rank
            // with it, as well as the cell's judgement: the fix is worth a capped Ok (100) and
            // leaving it is worth the typo tier (50, re-weighted), so completion, rank, accuracy,
            // total score and the combo the retype earns are all strictly better for going back for
            // it. Backlog 210 narrowed that margin, deliberately, and did not close it: the ordering
            // clean 300 > corrected 100 > unfixed typo 50 > miss 0 is what the cap is chosen to
            // produce.
            Assert.That(leftRun.GetProperty("completion").GetDouble(), Is.EqualTo(14.0 / 15.0).Within(1e-12));
            Assert.That(leftRun.GetProperty("rank").GetString(), Is.EqualTo("A"));
            Assert.That(fixedRun.GetProperty("completion").GetDouble(),
                Is.GreaterThan(leftRun.GetProperty("completion").GetDouble()));
            Assert.That(fixedRun.GetProperty("accuracy").GetDouble(), Is.GreaterThan(leftRun.GetProperty("accuracy").GetDouble()));
            Assert.That(fixedRun.GetProperty("totalScore").GetInt64(), Is.GreaterThan(leftRun.GetProperty("totalScore").GetInt64()));
            Assert.That(fixedRun.GetProperty("maxCombo").GetInt32(), Is.GreaterThan(leftRun.GetProperty("maxCombo").GetInt32()));

            // What the fix does not buy back: the keypress, which pp still prices, and since backlog
            // 210 the top tier on the cell it spoiled. The combo multiset IS fully restored (backlog
            // 140), so the shortfall against the clean run is the cap's alone, 200 of the capped
            // cell's 300 in accuracy and its share of the total.
            Assert.That(Dict(fixedRun, "statistics")[mistype_key], Is.EqualTo(1));
            Assert.That(fixedRun.GetProperty("totalScore").GetInt64(),
                Is.LessThan(root.GetProperty("clean").GetProperty("totalScore").GetInt64()),
                "a fixed typo no longer scores identically to a clean run");
            Assert.That(fixedRun.GetProperty("accuracy").GetDouble(),
                Is.EqualTo((14 * 300 + 100) / (15 * 300.0)).Within(1e-12));
        });
    }

    /// <summary>
    /// Backlog 126 stated as the case that forced it, on the browser side of the seam: a run typed
    /// almost entirely WRONG. Twelve of the map's fifteen cells end holding a wrong character (the
    /// three word gaps are typed correctly, which is what leaves the run three typed cells); the
    /// play finished every cell and typed three of them.
    ///
    /// <para>Between backlog 124 and 126 this submitted completion 1 and an X, because every one of
    /// those cells resolved as a HIT and completion counted hits. It now reads 3/15 and a D on both
    /// sides of the seam. The MISS COUNT is still zero throughout, which is the property that has to
    /// survive: pp prices this play by the mistype term, not the cleanliness term.</para>
    /// </summary>
    [Test]
    public void ARunTypedEntirelyWrongIsNotAnX()
    {
        var root = Harness();
        var run = root.GetProperty("everyLetterTypedWrong");

        Assert.Multiple(() =>
        {
            Assert.That(Dict(run, "statistics"),
                Is.EquivalentTo(new Dictionary<string, int> { ["great"] = 3, ["good"] = 12, ["combo_break"] = 12 }));

            Assert.That(run.GetProperty("completion").GetDouble(), Is.EqualTo(3 / 15.0).Within(1e-12));
            Assert.That(run.GetProperty("rank").GetString(), Is.EqualTo("D"));

            // The server reaches the same two numbers off the same dictionary.
            var recomputed = Recompute(run);
            Assert.That(recomputed.StatisticsValid, Is.True);
            Assert.That(recomputed.Completion, Is.EqualTo(3 / 15.0).Within(1e-12));
            Assert.That(recomputed.Rank, Is.EqualTo("D"));

            // Not a single MISS, so pp still prices this through the mistype term: the player did
            // reach and finish every character, they just got twelve of them wrong.
            Assert.That(Dict(run, "statistics"), Does.Not.ContainKey("miss"));
            var counts = PerformancePoints.CountNotes(Dict(run, "statistics"));
            Assert.That(counts.Notes, Is.EqualTo(15));
            Assert.That(counts.Misses, Is.Zero);
            Assert.That(counts.Typos, Is.EqualTo(12));
        });
    }

    /// <summary>
    /// The last half of the carve-out to move, and backlog 184 moved it: the SPACE KEY on a lyric
    /// character. With no word to skip (the browser hardcodes that setting off) the press means
    /// nothing but a wrong character, so it takes the same type-through path every other wrong
    /// character takes: the cell holds it, the caret advances, and backspace takes it back.
    ///
    /// <para>Two knock-ons are asserted here rather than left implied. The mash-fail streak is
    /// UNTOUCHED, because the type-through path never feeds it, so the browser now has no route into
    /// the 13-in-a-row guard at all (that guard belongs to Gatekeeper, which /play cannot select).
    /// And the cell keeps rendering its own EXPECTED character in the error red, since the browser's
    /// cellGlyph substitutes the typed char for word GAPS only, which is what makes an invisible red
    /// space a non-problem.</para>
    /// </summary>
    [Test]
    public void TheSpaceKeyIsTypedThroughOnALyricCell()
    {
        var root = Harness();
        var run = root.GetProperty("spaceKeyOnLetter");
        var probe = run.GetProperty("probe");

        Assert.Multiple(() =>
        {
            Assert.That(probe.GetProperty("state").GetString(), Is.EqualTo("wrong"), "the cell took the space");
            Assert.That(probe.GetProperty("typedChar").GetString(), Is.EqualTo(" "));
            Assert.That(probe.GetProperty("consecutiveWrongKeys").GetInt32(), Is.Zero,
                "a typed-through key never feeds the mash guard");
            Assert.That(probe.GetProperty("mistypes").GetInt32(), Is.EqualTo(1));

            // The caret moved on, exactly as it does for a wrong letter: the harness therefore fixes
            // the cell with a backspace before typing it, which is the run the totals below describe.
            Assert.That(probe.GetProperty("caretIndex").GetInt32(), Is.EqualTo(1));

            var stats = Dict(run, "statistics");
            Assert.That(stats.GetValueOrDefault("great"), Is.EqualTo(14), "the fix earns the cell back");
            Assert.That(stats.GetValueOrDefault("ok"), Is.EqualTo(1),
                "...at the capped tier, because the cell held a wrong character before it was judged (backlog 210)");
            Assert.That(stats, Does.Not.ContainKey("miss"));
            Assert.That(stats[mistype_key], Is.EqualTo(1));
        });
    }

    /// <summary>
    /// Backlog 181: a wrong LETTER pressed on the word gap is typed THROUGH, exactly as one pressed
    /// on a lyric character is. It was the one wrong letter the browser rejected, so this is the
    /// same fixture cell asserting the opposite of what it used to.
    ///
    /// <para>Unconditional here, and that is the port rather than an omission: the C# gates it on
    /// <c>TypingEngine.WrongInputOnWordGaps</c>, an ERA flag that live play sets for every mod stack
    /// (Hard Rock included) and a stored replay carries in its own CONFIG frame, and the browser
    /// only ever plays live. The flag's other arm is reachable from the game repo alone, which is
    /// where it is pinned (the game's SpaceTypoTest, and the sweep's era guard in
    /// Typebeat.WireCompat.EngineFuzzLiveParityTest).</para>
    ///
    /// <para>Every particular of the type-through is the lyric cell's: the gap holds the typo, the
    /// caret moves past it, the streak is gone, the mistype is counted, the cell resolves NOTHING at
    /// the keypress, and the mash-fail streak (a rejection-path guard) is untouched. The seal then
    /// resolves it as an unfixed typo, so it costs completion like a miss without being one, and the
    /// fix cycle earns the cell back along with the streak the typo broke.</para>
    /// </summary>
    [Test]
    public void AWrongLetterOnTheWordGapIsTypedThrough()
    {
        var root = Harness();
        var typed = root.GetProperty("wordGapTypedWrong");
        var fixedRun = root.GetProperty("wordGapWrongThenFixed");
        var probe = typed.GetProperty("probe");

        Assert.Multiple(() =>
        {
            Assert.That(probe.GetProperty("state").GetString(), Is.EqualTo("wrong"), "the gap took the character");
            Assert.That(probe.GetProperty("typedChar").GetString(), Is.EqualTo("z"));
            Assert.That(probe.GetProperty("caretIndex").GetInt32(), Is.EqualTo(4), "the caret advanced past the gap");
            Assert.That(probe.GetProperty("consecutiveWrongKeys").GetInt32(), Is.Zero,
                "type-through never feeds the mash-fail streak");
            Assert.That(probe.GetProperty("mistypes").GetInt32(), Is.EqualTo(1));

            // The backlog-109 pair, on a cell that could not reach it before: nothing was handed to
            // the processor, yet the submitted combo has already broken.
            Assert.That(probe.GetProperty("judged").GetBoolean(), Is.False);
            Assert.That(probe.GetProperty("processorCombo").GetInt32(), Is.Zero);
            Assert.That(probe.GetProperty("processorJudged").GetInt32(), Is.EqualTo(3), "only the three cells before it");

            // Left alone, it seals as an unfixed typo: a HIT, so no miss is counted, and 14 of 15
            // cells are typed, which costs the rank exactly as a miss would.
            var stats = Dict(typed, "statistics");
            Assert.That(stats, Is.EquivalentTo(new Dictionary<string, int>
            {
                ["great"] = 14, ["good"] = 1, [mistype_key] = 1
            }));
            Assert.That(typed.GetProperty("completion").GetDouble(), Is.EqualTo(14 / 15.0).Within(1e-12));
            Assert.That(typed.GetProperty("rank").GetString(), Is.EqualTo("A"));

            // ...and the server recomputes the same thing off the submitted dictionaries.
            var recomputed = Recompute(typed);
            Assert.That(recomputed.Completion, Is.EqualTo(14 / 15.0).Within(1e-12));
            Assert.That(recomputed.Rank, Is.EqualTo("A"));
            Assert.That(recomputed.StatisticsValid, Is.True);

            // Fixed instead: backspace clears the WRONG space and the corrected space earns the
            // cell's own judgement plus the streak the typo broke, so the combo multiset is the clean
            // run's 1..15 and the whole map is typed. Two traces survive: the mistype, which is what
            // pp prices the mistake with, and the CAP on the corrected cell (backlog 210), which is
            // what accuracy prices it with. A corrected word gap is capped like any other corrected
            // cell, even though a space is judged on a zeroed delta: the cap is a min over the tier,
            // and the untimed space simply arrives at the top of the ladder.
            Assert.That(Dict(fixedRun, "statistics"), Is.EquivalentTo(new Dictionary<string, int>
            {
                ["great"] = 14, ["ok"] = 1, [mistype_key] = 1
            }));
            Assert.That(fixedRun.GetProperty("maxCombo").GetInt32(), Is.EqualTo(15), "the streak came back at the fix");
            Assert.That(fixedRun.GetProperty("completion").GetDouble(), Is.EqualTo(1));
            Assert.That(fixedRun.GetProperty("rank").GetString(), Is.EqualTo("X"),
                "the cap costs accuracy, and completion and rank are untouched by it");
            Assert.That(fixedRun.GetProperty("totalScore").GetInt64(),
                Is.LessThan(root.GetProperty("clean").GetProperty("totalScore").GetInt64()));
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
    /// <item><c>lastCellSkipped</c> (and <c>lastCellWrongThenErased</c>, which it must equal, both
    /// leaving the cell EMPTY): greats at combo 1..14 then a miss contributing 300·0^0.5 = 0.
    /// Portion 300·36.596213 = 10978.863976, comboProgress 0.904297993. Judged accuracy is
    /// 14·300/(15·300) = 0.933333, accuracyProgress 15/15 = 1, so total =
    /// round(500000·0.933333·0.904297993 + 500000·0.933333^5) = 776129.</item>
    /// <item><c>lastCellTypedWrong</c>: the same run, except the last cell resolves as an unfixed
    /// typo. That result is applied COMBO-NEUTRAL, so it is weighted by the combo it found (0) and
    /// the portion is unchanged at 10978.863976; what moves is accuracy, to (14·300 + 50)/4500 =
    /// 0.944444, giving total = round(500000·0.944444·0.904297993 + 500000·0.944444^5) = 802739.
    /// One typo instead of one Miss is worth 26610 here, and nothing else moves. Backlog 126 leaves
    /// this number alone on purpose: the typo tier carries the Meh weight of 50 precisely so that
    /// only completion, rank and health move.</item>
    /// <item><c>midCellTypedWrong</c>: greats at combo 1..5, the typo (a hand-written break, no
    /// result), greats at combo 1..9, and only THEN the seal's typo result, combo-neutral at combo 9
    /// and so contributing 300·√9 = 900. Portion 300·(Σ(1..5)√i + Σ(1..9)√i) + 900 = 9206.499862,
    /// comboProgress 0.758313370, accuracy 0.944444, total = 733802. Weighting that same result at
    /// 10, i.e. letting it extend the run, would read 735695 and max_combo 10.</item>
    /// <item><c>midCellWrongThenFixed</c>: greats at combo 1..5, the typo, and then the fix, which
    /// since backlog 140 RESUMES the streak the wrong key broke BEFORE the retype is judged. The
    /// retype (the cell's own first and only result) therefore lands at combo 6 rather than 1, and
    /// the rest of the line runs 7..15, so the combo multiset is exactly the clean run's 1..15: the
    /// portion is the full 12140.758980 and comboProgress is 1. ACCURACY is where the fix now pays,
    /// since backlog 210 caps a corrected cell at Ok: the retype was struck dead on target and would
    /// have been a Great, so the cell is worth 100 of 300 and accuracy is (14·300 + 100)/4500 =
    /// 0.955556, giving total = round(500000·0.955556·1 + 500000·0.955556^5) = 876114. Until the cap
    /// this line read a flat accuracy of 1 and the clean run's 1000000 exactly, which is the equality
    /// backlog 210 exists to remove. Under the pre-140 combo rule the same keystrokes read a portion
    /// of 300·(Σ(1..5)√i + Σ(1..10)√i) = 9255.183160, i.e. comboProgress 0.762323276, which is what a
    /// restore applied AFTER the judgement would still produce.</item>
    /// <item><c>wordGapTypedWrong</c>: the same shape as <c>midCellTypedWrong</c>, on the WORD GAP
    /// (cell 3), which used to reject the key instead (backlog 181). Greats at combo 1..3, the typo
    /// (a hand-written break, no result), greats at combo 1..11, and then the seal's typo result,
    /// combo-neutral at combo 11 and so contributing 300·√11. Portion
    /// 300·(Σ(1..3)√i + Σ(1..11)√i + √11) = 9974.337641, comboProgress 0.821557998, accuracy
    /// (14·300 + 50)/4500 = 0.944444, total = 763667. Under the pre-181 rejection the identical
    /// keystrokes read max_combo 12 and 912601 off a portion of 300·(Σ(1..3)√i + Σ(1..12)√i) =
    /// 10018.580688 with a flat accuracy of 1, because the caret was held and the gap was then typed
    /// correctly.</item>
    /// <item><c>wordGapWrongThenFixed</c>: the fix cycle on that same gap. The restore puts the
    /// streak of 3 back BEFORE the corrected space is judged, so the combo multiset is the clean
    /// run's 1..15 and the total is 876114, exactly as <c>midCellWrongThenFixed</c> is: same combo
    /// portion, same one capped cell. The cap reaches a corrected GAP like any other corrected cell,
    /// even though the space itself is judged on a zeroed delta (backlog 148), because the cap is a
    /// min over the tier and an untimed space simply arrives at the top of the ladder.</item>
    /// <item><c>spaceKeyOnLetter</c>: a space typed through into the opening cell (backlog 184) and
    /// then fixed. The break lands on an already-zero combo and the restore puts nothing back, so the
    /// run is still 1..15 and the combo portion is still the clean run's; the corrected opening cell
    /// is capped, so the total is 876114 as well. A wrong keypress on the opening cell costs combo
    /// nothing; it costs the cell's top tier, and pp through the mistype count.</item>
    /// </list>
    /// </summary>
    [Test]
    public void TheSubmittedTotalsMatchTheHandDerivedCsharpModel()
    {
        var root = Harness();

        (string Run, int MaxCombo, long TotalScore, int Mistypes)[] expected =
        [
            ("clean", 15, 1_000_000, 0),
            ("lastCellTypedWrong", 14, 802_739, 1),
            ("lastCellSkipped", 14, 776_129, 0),
            ("lastCellWrongThenErased", 14, 776_129, 1),
            ("midCellTypedWrong", 9, 733_802, 1),
            ("midCellWrongThenFixed", 15, 876_114, 1),
            ("wordGapTypedWrong", 11, 763_667, 1),
            ("wordGapWrongThenFixed", 15, 876_114, 1),
            ("spaceKeyOnLetter", 15, 876_114, 1),
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
