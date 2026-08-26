using System.Text.Json;
using Typebeat.Web.Scoring;

namespace Typebeat.Web.Tests;

/// <summary>
/// Fidelity guard for the COMBO RESTORE rule across the browser/server seam (backlog 140, web half
/// 143). Backspacing and correctly retyping a cell a wrong keypress spoiled RESUMES the streak that
/// keypress broke: the snapshot plus everything earned since, applied BEFORE the retype is judged so
/// the retype is priced at the resumed streak. Without it, browser <c>/play</c> and desktop judge
/// combo under different rules on the SAME leaderboards.
///
/// <para><b>Why this fixture is shaped differently from its siblings.</b> Every other JS parity test
/// here pins the browser against golden literals transcribed once from the game suite. When the game
/// half of backlog 140 landed and <c>typebeat-core.js</c> did not restore combo at all, all twenty of
/// those tests still passed, because the literals had been transcribed from a game that did not
/// restore either and nothing in them reaches the live C#. A wire change fails
/// <see cref="Typebeat.Web.Scoring.ScoringContract"/> loudly; a BEHAVIOUR change on a shared
/// leaderboard fails nothing. So this one is driven by the combo-restore SEQUENCES rather than by
/// pre-existing transcribed values, which is the shape of test that would have caught it.</para>
///
/// <para>The sequences and the numbers are the game's own pins, transcribed:
/// <c>NonVisual/ComboRestoreTest.cs</c> (the plain fix, the intervening break, the repeated
/// wrong/fix cycle) and
/// <c>NonVisual/TypeBeatReplayScorerTest.AFixedTypoResumesTheStreakOnlyUnderTheLiveRule</c>, whose
/// thirteen-cell map re-derives <c>max_combo</c> 13 under the live rule and 11 under the pre-140
/// one. The browser is permanently live (it has no mods payload, no replay input and never
/// re-derives a stored score), so it has no counterpart to <c>ComboRestoreRule.Never</c> and 13 is
/// the only answer available to it. 11 is what it produced before this landed, which is what makes
/// the replay-shaped case the non-vacuity proof as well as the pin.</para>
///
/// <para>Backlog 176 narrowed the rule that decides WHICH break holds the claim, and the narrowing
/// is mirrored here by the last three cases: a break takes ownership of the streak only if it HAS a
/// streak to own, so a wrong key or a skip landing on a run something else already zeroed leaves the
/// outstanding claim alone instead of replacing it with an empty one. Their sequences and numbers are
/// the game's own pins again (<c>ComboRestoreTest</c>'s three backlog 176 shapes), and each is pinned
/// there twice, once live and once under <c>ComboClaimRule.LatestBreakWins</c>. The browser has no
/// counterpart to that second arm, for the same reason it has no <c>ComboRestoreRule.Never</c>: it
/// only ever plays live and never re-derives a stored row.</para>
///
/// <para>Backlog 199 took an off-time press OUT of the set of breaks that discard a claim, and
/// <see cref="AnOffTimePressBetweenATypoAndItsFixKeepsTheClaim"/> is that case: a right character
/// struck outside the outermost Meh window is a hit worth no points, so it no longer costs a
/// pending fix its restore. Same pattern again, the game pinning both arms and the browser only the
/// live one.</para>
///
/// <para>Both combo accounts are asserted throughout, because the class of bug lives in them
/// disagreeing: the engine's own <c>combo</c> is what the HUD counts up, and the score processor
/// mirror's <c>highestCombo</c> is what is submitted as <c>max_combo</c>. They are kept equal by
/// mirroring every move, never by one overwriting the other.</para>
/// </summary>
public class ComboRestoreParityTest
{
    private const string mistype_key = "combo_break";

    private static JsonElement Harness() => JsHarness.Run("CoreComboRestoreHarness.cjs");

    private static int Int(JsonElement run, string key) => run.GetProperty(key).GetInt32();

    private static int[] Ints(JsonElement run, string key)
    {
        var list = new List<int>();

        foreach (var element in run.GetProperty(key).EnumerateArray())
            list.Add(element.GetInt32());

        return list.ToArray();
    }

    private static Dictionary<string, int> Dict(JsonElement run, string key)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var property in run.GetProperty(key).EnumerateObject())
            result[property.Name] = property.Value.GetInt32();

        return result;
    }

    private static ScoringContract.Recomputed Recompute(JsonElement run)
        => ScoringContract.Recompute(Dict(run, "statistics"), Dict(run, "maximumStatistics"), Int(run, "maxCombo"));

    /// <summary>
    /// The rule itself (<c>ComboRestoreTest.FixingATypoResumesTheStreakItBroke</c>). A streak of 3, a
    /// typo, two cells rebuilt while the wrong character sits there, then the fix: the run resumes at
    /// the snapshot PLUS what was earned since (3 + 2), and the corrected retype is judged on top of
    /// that, so it ends at 6 rather than at 3.
    ///
    /// <para>That ORDERING is the whole difference between fixing a typo being worth score and being
    /// worth only accuracy: the combo portion of the total weights every judgement by the combo AFTER
    /// it, so a restore applied after the retype would leave the retype priced at the broken streak.
    /// 6 is only reachable if the restore landed first.</para>
    /// </summary>
    [Test]
    public void FixingATypoResumesTheStreakItBroke()
    {
        var run = Harness().GetProperty("fixResumesTheStreak");

        Assert.Multiple(() =>
        {
            Assert.That(Int(run, "comboBeforeTypo"), Is.EqualTo(3));
            Assert.That(Int(run, "comboAfterTypo"), Is.Zero, "the wrong key breaks the run at the keypress");
            Assert.That(Int(run, "comboBeforeFix"), Is.EqualTo(2), "two cells rebuilt while cell 3 sat wrong");

            Assert.That(Ints(run, "restored"), Is.EqualTo(new[] { 3 }), "the streak the wrong key broke, once");
            Assert.That(Int(run, "combo"), Is.EqualTo(6), "3 restored + 2 earned since + the fix itself");
            Assert.That(Int(run, "maxCombo"), Is.EqualTo(6));

            // The submitted account has to reach the identical number by its own route, which is a
            // hand-mirrored delta plus a hand-pushed watermark, not a copy of the engine's value.
            Assert.That(Int(run, "processorCombo"), Is.EqualTo(6));
            Assert.That(Int(run, "processorHighestCombo"), Is.EqualTo(6));

            // The typo deferred the cell's result, so the retype IS that result, earned at the
            // resumed streak.
            Assert.That(run.GetProperty("fixedCellJudgeType").GetString(), Is.EqualTo("Great"));

            // Exactly one break, at the keypress, and it is not un-counted: the typo stat counts the
            // KEYPRESS, and no correction can unpress it.
            Assert.That(Int(run, "breaks"), Is.EqualTo(1));
            Assert.That(Int(run, "mistypes"), Is.EqualTo(1));
        });
    }

    /// <summary>
    /// The bound on the rule
    /// (<c>ComboRestoreTest.AnInterveningBreakOwnsTheStreakSoTheOlderFixRestoresNothing</c>). An
    /// intervening break OWNS the streak: the run the player was on when they typed the first wrong
    /// character has been lost to something else since, and going back to fix the older cell cannot
    /// un-lose it. Only the newest wrong cell holds a claim, so fixing them in the order they
    /// happened restores nothing for the first and everything for the second.
    ///
    /// <para>Exactly one snapshot is ever outstanding, which is what makes "a second wrong key
    /// discards the first cell's claim" fall out of the same rule as every other break rather than
    /// needing a case of its own.</para>
    /// </summary>
    [Test]
    public void AnInterveningBreakOwnsTheStreakSoTheOlderFixRestoresNothing()
    {
        var run = Harness().GetProperty("interveningBreak");

        Assert.Multiple(() =>
        {
            Assert.That(Int(run, "comboAfterSecondTypo"), Is.Zero);

            Assert.That(Ints(run, "restoredAfterOlderFix"), Is.Empty, "cell 3's streak died with the second wrong key");
            Assert.That(Int(run, "comboAfterOlderFix"), Is.EqualTo(1), "the fix earns its own cell and nothing more");

            // Cell 6 is the one still holding a claim, and it is redeemed normally.
            Assert.That(Ints(run, "restored"), Is.EqualTo(new[] { 2 }), "the newer cell's snapshot survived");
            Assert.That(Int(run, "combo"), Is.EqualTo(4), "2 restored + the 1 the older fix earned + this fix");
            Assert.That(Int(run, "processorCombo"), Is.EqualTo(4));
            Assert.That(Int(run, "processorHighestCombo"), Is.EqualTo(4));

            Assert.That(Int(run, "breaks"), Is.EqualTo(2));
            Assert.That(Int(run, "mistypes"), Is.EqualTo(2));
        });
    }

    /// <summary>
    /// Repeated wrong/fix cycles on ONE cell break and restore each time
    /// (<c>ComboRestoreTest.RepeatedWrongFixCyclesOnOneCellBreakAndRestoreEachTime</c>), each cycle
    /// snapshotting whatever the run has grown back to. The second fix is a scoring-inert retype (the
    /// cell was already judged correct by the first fix, so it earns no points and no combo of its
    /// own), which is exactly why the restore is not folded into the judgement: the streak belongs to
    /// the FIX, not to the cell's result. Two wrong keypresses, two typos: the count is of keypresses,
    /// and fixing is not a refund.
    /// </summary>
    [Test]
    public void RepeatedWrongFixCyclesOnOneCellBreakAndRestoreEachTime()
    {
        var run = Harness().GetProperty("repeatedCycles");

        Assert.Multiple(() =>
        {
            Assert.That(Int(run, "comboAfterFirstCycle"), Is.EqualTo(3), "2 restored + the fix");
            Assert.That(Int(run, "comboAfterSecondTypo"), Is.Zero);

            Assert.That(Ints(run, "restored"), Is.EqualTo(new[] { 2, 3 }), "each cycle restores what it broke");
            Assert.That(Int(run, "combo"), Is.EqualTo(3), "the retype is inert, so the run is exactly what was resumed");
            Assert.That(Int(run, "maxCombo"), Is.EqualTo(3));
            Assert.That(Int(run, "processorHighestCombo"), Is.EqualTo(3));

            Assert.That(Int(run, "breaks"), Is.EqualTo(2));
            Assert.That(Int(run, "mistypes"), Is.EqualTo(2));
        });
    }

    /// <summary>
    /// The half the osu-side mirror has to do for itself, and the half that is easiest to leave out.
    /// NOT transcribed: the game's pins do not isolate it, so the sequence and its numbers are
    /// derived here and stated as literals so a reviewer can re-derive them.
    ///
    /// <para>The restore is the LAST thing that moves combo, it takes the run PAST anything the play
    /// had reached before, and the retype carrying it is INERT (the cell was already judged by an
    /// earlier fix), so no result follows that could raise the maximum on its behalf. Cells 0 and 1
    /// correct (run 2, watermark 2); a typo on cell 2 and an immediate fix (2 restored, the retype
    /// earns, run 3, watermark 3); backspace and typo cell 2 again on that run of 3; cells 3 and 4
    /// correct (run 2, watermark still 3); backspace back and fix cell 2. The snapshot of 3 plus the
    /// 2 earned since is 5, which is past the watermark of 3, on a retype that applies no result.</para>
    ///
    /// <para>So <c>max_combo</c> reads 5 only if the restore pushed the watermark itself, at the
    /// moment it happened. Leaving it to the next result submits 3, and the player watches a HUD
    /// counting 5. Five judgements, not six, is the assertion that says the retype really was
    /// inert.</para>
    /// </summary>
    [Test]
    public void ARestoreThatPassesTheWatermarkRaisesItItself()
    {
        var run = Harness().GetProperty("restoreBeyondTheWatermark");

        Assert.Multiple(() =>
        {
            Assert.That(Int(run, "watermarkBeforeFix"), Is.EqualTo(3), "the highest run the play had held");

            Assert.That(Ints(run, "restored"), Is.EqualTo(new[] { 2, 3 }));
            Assert.That(Int(run, "combo"), Is.EqualTo(5), "3 restored + the 2 earned since, and the retype adds nothing");
            Assert.That(Int(run, "maxCombo"), Is.EqualTo(5));

            Assert.That(Int(run, "judgementCount"), Is.EqualTo(5),
                "five cells resolved: the retype applied no result, so nothing after the restore could raise the maximum");
            Assert.That(Int(run, "processorCombo"), Is.EqualTo(5));
            Assert.That(Int(run, "processorHighestCombo"), Is.EqualTo(5),
                "the submitted max_combo has to reach the HUD's 5, which only the restore can put there");
        });
    }

    /// <summary>
    /// BACKLOG 176, and the shape a real submitted run took
    /// (<c>ComboRestoreTest.TwoWrongKeysOnAdjacentCellsKeepTheStreakWhenBothAreFixed</c>): score 6212
    /// on "Joji - PIXELATED KISSES [Insane]", 447 combo deep into "if you never hear from me", where
    /// the player typed 'a' onto the 'm' cell and then 'm' onto the 'e' cell, backspaced twice and
    /// typed "me" out correctly. The second wrong key breaks a run of ZERO, because the first one
    /// already took the streak, so it has nothing to take the claim with and the 'm' cell keeps it:
    /// fixing that cell resumes the run.
    ///
    /// <para>Deliberately stronger than
    /// <see cref="AnInterveningBreakOwnsTheStreakSoTheOlderFixRestoresNothing"/>, which is the same
    /// two wrong keys with a run REBUILT between them, so there the second break really does cost
    /// something and really does take the claim. Nothing is lost between these two, which is exactly
    /// why the older claim survives.</para>
    ///
    /// <para>This is the case the browser got wrong for as long as its two snapshot sites wrote
    /// unconditionally, which they did until this landed: the same keystrokes restored nothing and
    /// ended the run at 2 rather than 6.</para>
    /// </summary>
    [Test]
    public void TwoWrongKeysOnAdjacentCellsKeepTheStreakWhenBothAreFixed()
    {
        var run = Harness().GetProperty("adjacentTyposBothFixed");

        Assert.Multiple(() =>
        {
            Assert.That(Int(run, "comboBeforeTypos"), Is.EqualTo(4));

            Assert.That(Ints(run, "restored"), Is.EqualTo(new[] { 4 }), "the empty break left cell 4's claim alone");
            Assert.That(Int(run, "combo"), Is.EqualTo(6), "4 restored + the two fixes");
            Assert.That(Int(run, "maxCombo"), Is.EqualTo(6));
            Assert.That(Int(run, "processorCombo"), Is.EqualTo(6));
            Assert.That(Int(run, "processorHighestCombo"), Is.EqualTo(6));

            // The two wrong KEYPRESSES are still spent: the fix buys back the streak, never the typo
            // count, and both breaks were taken when they happened.
            Assert.That(Int(run, "breaks"), Is.EqualTo(2));
            Assert.That(Int(run, "mistypes"), Is.EqualTo(2));
        });
    }

    /// <summary>
    /// The same-cell sibling
    /// (<c>ComboRestoreTest.ASecondWrongKeyOnTheSameCellKeepsTheStreakTheFirstOneSnapshotted</c>):
    /// fumble cell 2, erase it, fumble it AGAIN, then correct it. It differs from
    /// <see cref="RepeatedWrongFixCyclesOnOneCellBreakAndRestoreEachTime"/> only in that no successful
    /// fix separates the two wrong keys, so there is no second streak to snapshot and the first one's
    /// claim is the only one there has ever been.
    /// </summary>
    [Test]
    public void ASecondWrongKeyOnTheSameCellKeepsTheStreakTheFirstOneSnapshotted()
    {
        var run = Harness().GetProperty("sameCellFumbledTwice");

        Assert.Multiple(() =>
        {
            Assert.That(Ints(run, "restored"), Is.EqualTo(new[] { 2 }), "the first wrong key's claim survived the second");
            Assert.That(Int(run, "combo"), Is.EqualTo(3), "2 restored + the fix");
            Assert.That(Int(run, "maxCombo"), Is.EqualTo(3));
            Assert.That(Int(run, "processorHighestCombo"), Is.EqualTo(3));

            Assert.That(Int(run, "breaks"), Is.EqualTo(2));
            Assert.That(Int(run, "mistypes"), Is.EqualTo(2));
        });
    }

    /// <summary>
    /// The OTHER redeemable break under the same rule
    /// (<c>ComboRestoreTest.AWordSkipOverATypoLeavesThatTyposSnapshotAlone</c>), on the two-word map:
    /// type "ab", fumble 'c', give up on the word with a space (abandoning 'd'), then backspace into
    /// it and type both cells out. The skip's own break costs nothing, because the typo already
    /// zeroed the run, so it has no streak to claim the cell with, and backlog 167's promise survives
    /// in the case it is worth most: the player who fumbles, gives up, then goes back and types the
    /// whole word out has undone everything they did wrong.
    ///
    /// <para>The browser reaches this path with <c>spaceSkipsWord</c> set on the engine directly.
    /// <c>/play</c> has no settings payload, so a live browser play is permanently non-skipping, but
    /// the two snapshot sites share one write site on both sides and this is the half of it the typo
    /// cases cannot reach.</para>
    /// </summary>
    [Test]
    public void AWordSkipOverATypoLeavesThatTyposSnapshotAlone()
    {
        var run = Harness().GetProperty("wordSkippedOverATypo");

        Assert.Multiple(() =>
        {
            Assert.That(Int(run, "comboBeforeTypo"), Is.EqualTo(2));
            Assert.That(Int(run, "caretAfterSkip"), Is.EqualTo(5), "the space landed on the word gap");
            Assert.That(Int(run, "caretAfterBackspaces"), Is.EqualTo(2), "one press reclaims 'd', the next erases the typo");

            Assert.That(Ints(run, "restored"), Is.EqualTo(new[] { 2 }), "the skip had no streak to take the claim with");
            Assert.That(Int(run, "combo"), Is.EqualTo(5), "the space, 2 restored at the 'c', then both cells");
            Assert.That(Int(run, "maxCombo"), Is.EqualTo(5));
            Assert.That(Int(run, "processorHighestCombo"), Is.EqualTo(5));

            // Two breaks, the typo's and the skip's, and one mistyped KEYPRESS: the skip is not one.
            Assert.That(Int(run, "breaks"), Is.EqualTo(2));
            Assert.That(Int(run, "mistypes"), Is.EqualTo(1));
        });
    }

    /// <summary>
    /// BACKLOG 199, and the browser half of
    /// <c>ComboRestoreTest.AnOffTimePressBetweenATypoAndItsFixKeepsTheClaim</c>: an off-time press
    /// between a typo and its fix does NOT discard the claim. Only a BREAK takes a claim away, and a
    /// right character struck outside the outermost Meh window is no longer a break: it earns no
    /// points, keeps the run, raises no combo break, and leaves the older cell redeemable, so
    /// fumbling the beat while a typo is sitting there no longer costs the fix its restore.
    ///
    /// <para>The game pins the same keystrokes under <c>OffTimeRule.BreaksCombo</c> as well, where
    /// the mistimed press loses the snapshot and the fix restores nothing at all. The browser has no
    /// counterpart to that arm, for the reason it has none for <c>ComboRestoreRule.Never</c>: it only
    /// ever plays live. So the pin here is the live numbers, and its non-vacuity is that a break
    /// would produce different ones (an empty <c>restored</c> and a run of 3 rather than 4).</para>
    ///
    /// <para>The press TIMES are the browser's rather than the C# fixture's, exactly as
    /// <see cref="UntimedSpaceParityTest.ALyricCharacterPressedJustAsLateIsStillLagging"/> records
    /// for its 4100: the game's fixture drives a bare engine judged on each cell's own point target,
    /// while the browser only ever plays live and judges a cell against its SYLLABLE's sung span.
    /// The tiers, their order and the rule under test are the same.</para>
    /// </summary>
    [Test]
    public void AnOffTimePressBetweenATypoAndItsFixKeepsTheClaim()
    {
        var run = Harness().GetProperty("offTimePressBetweenATypoAndItsFix");

        Assert.Multiple(() =>
        {
            // The fixture is only about the claim if the mistimed press really was off the ladder,
            // and only interesting if the press after it was not.
            Assert.That(run.GetProperty("offTimeJudgeType").GetString(), Is.EqualTo("Lagging"));
            Assert.That(run.GetProperty("followUpJudgeType").GetString(), Is.EqualTo("Meh"));

            Assert.That(Int(run, "comboBeforeTypo"), Is.EqualTo(1));
            Assert.That(Int(run, "comboBeforeFix"), Is.EqualTo(2), "both presses after the typo extended the run, the off-time one included");

            Assert.That(Ints(run, "restored"), Is.EqualTo(new[] { 1 }), "the mistimed press took nothing away");
            Assert.That(Int(run, "combo"), Is.EqualTo(4), "1 restored + the 2 earned since + the fix itself");
            Assert.That(Int(run, "maxCombo"), Is.EqualTo(4));
            Assert.That(Int(run, "processorHighestCombo"), Is.EqualTo(4));

            // One break and one mistyped keypress, both the typo's: the off-time press is neither.
            Assert.That(Int(run, "breaks"), Is.EqualTo(1));
            Assert.That(Int(run, "mistypes"), Is.EqualTo(1));
        });
    }

    /// <summary>
    /// The whole rule as a SUBMITTED account, on the game's own replay-scorer map transcribed cell
    /// for cell (twelve cells on line 0, one on line 1, every one strikeable dead on its target).
    /// The same thirteen keystrokes plus a typo on cell 2 that is immediately fixed.
    ///
    /// <para><c>AFixedTypoResumesTheStreakOnlyUnderTheLiveRule</c> re-derives this replay at
    /// <c>max_combo</c> 13 under <c>ComboRestoreRule.OnFix</c> (live play) and 11 under
    /// <c>Never</c> (every score stored before backlog 140): the wrong key on cell 2 broke a streak
    /// of 2, and either that resumes before the retype is judged and the map runs unbroken to
    /// thirteen, or it does not and the run is the retype plus cells 3..11 plus line 1, eleven. The
    /// browser has no <c>Never</c> path to build, so 13 is the only answer available to it, and 11 is
    /// precisely what it produced before this landed.</para>
    ///
    /// <para>The restored run is worth SCORE and not only <c>max_combo</c>, which is why the restore
    /// goes before the judgement: the fixed run's combo multiset is exactly the clean run's, so it
    /// scores the identical 1000000, where the unrestored one scored 929151. What the fix does NOT
    /// buy back is the wrong keypress, which is still counted and still priced by pp's typo term:
    /// the two runs' statistics differ by the <c>combo_break</c> key and by nothing else.</para>
    /// </summary>
    [Test]
    public void AFixedTypoResumesTheStreakOnTheSubmittedAccountToo()
    {
        var root = Harness();
        var clean = root.GetProperty("replayClean");
        var fixedRun = root.GetProperty("replayFixedTypo");

        Assert.Multiple(() =>
        {
            Assert.That(Int(clean, "totalCells"), Is.EqualTo(13), "twelve cells on line 0 plus one on line 1");

            Assert.That(Int(fixedRun, "maxCombo"), Is.EqualTo(13),
                "the streak of 2 resumes before the retype is judged, so nothing is ever broken");
            Assert.That(Int(fixedRun, "engineMaxCombo"), Is.EqualTo(13), "and the HUD combo agrees");
            Assert.That(Int(clean, "maxCombo"), Is.EqualTo(13));

            // Restoring BEFORE the retype's judgement is what makes the fix worth score: every cell
            // from the fix onwards is weighted by a streak two higher, so the combo multiset is the
            // clean run's and the total is identical.
            Assert.That(fixedRun.GetProperty("totalScore").GetInt64(), Is.EqualTo(1_000_000));
            Assert.That(fixedRun.GetProperty("totalScore").GetInt64(),
                Is.EqualTo(clean.GetProperty("totalScore").GetInt64()));

            // The rule moves combo and nothing else: the keypress is still a typo, the cell is still
            // recovered, and the two runs agree on every other count.
            var cleanStats = Dict(clean, "statistics");
            var fixedStats = Dict(fixedRun, "statistics");

            Assert.That(fixedStats[mistype_key], Is.EqualTo(1));
            fixedStats.Remove(mistype_key);
            Assert.That(fixedStats, Is.EquivalentTo(cleanStats), "thirteen cells resolved either way");

            Assert.That(fixedRun.GetProperty("completion").GetDouble(), Is.EqualTo(1));
            Assert.That(fixedRun.GetProperty("accuracy").GetDouble(), Is.EqualTo(clean.GetProperty("accuracy").GetDouble()));
            Assert.That(fixedRun.GetProperty("rank").GetString(), Is.EqualTo("X"));

            // ...and the server agrees, recomputing the browser's own dictionary through the contract
            // that judges it in production: a max_combo of 13 on a thirteen-cell map is in bounds, and
            // so is the total the restored run submits.
            var recomputed = Recompute(fixedRun);
            Assert.That(recomputed.StatisticsValid, Is.True);
            Assert.That(recomputed.Completion, Is.EqualTo(1));
            Assert.That(recomputed.Rank, Is.EqualTo("X"));
            Assert.That(ScoringContract.TotalScoreWithinBounds(fixedRun.GetProperty("totalScore").GetInt64(), recomputed), Is.True);
        });
    }
}
