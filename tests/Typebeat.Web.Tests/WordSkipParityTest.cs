using System.Text.Json;

namespace Typebeat.Web.Tests;

/// <summary>
/// Fidelity guard for the RECLAIMABLE WORD SKIP in the browser engine (backlog 167, web half 168).
/// A space pressed inside a word abandons the rest of it into a PHANTOM state instead of missing it
/// on the spot: one backspace re-enters the word, re-typing the cells earns their ordinary
/// judgements and the streak the skip broke, and a skip nobody goes back for resolves at the seal as
/// the misses it turned out to be. The desktop client has done this since backlog 167; until this
/// landed the browser missed the word on the spot, so the identical performance scored LOWER in the
/// browser on the SAME leaderboards.
///
/// <para>The sequences and the numbers are the game's own pins, transcribed cell for cell from
/// <c>NonVisual/SpaceSkipWordTest.cs</c>, which is that feature's behaviour spec. Everything here is
/// the browser's ENGINE state: cell states, the caret, the live combo, the points a press earns.
/// The SUBMITTED account is pinned separately, and against the live C# rather than against a
/// literal, in <c>Typebeat.WireCompat.WordSkipLiveParityTest</c>: that is the only project that
/// compiles both repos, and a shared leaderboard is what the two clients disagreeing costs.</para>
///
/// <para>Both accounts are read throughout, because the class of bug lives in them disagreeing: the
/// engine's own <c>combo</c> is what the HUD counts up, and the score processor mirror's
/// <c>highestCombo</c> is what is submitted as <c>max_combo</c>. Misses are read off the PROCESSOR:
/// the engine's own <c>counts</c> dict is the scored-keypress dict in this mirror and has never
/// recorded seal misses, where the C# <c>ResultsSummary.Counts</c> does.</para>
/// </summary>
public class WordSkipParityTest
{
    private static JsonElement Harness() => JsHarness.Run("CoreWordSkipHarness.cjs");

    private static JsonElement Run(string scenario) => Harness().GetProperty(scenario);

    private static int Int(JsonElement run, string key) => run.GetProperty(key).GetInt32();

    private static bool Bool(JsonElement run, string key) => run.GetProperty(key).GetBoolean();

    private static double Double(JsonElement run, string key) => run.GetProperty(key).GetDouble();

    private static string?[] Strings(JsonElement run, string key) => JsHarness.Strings(run, key);

    private static int[] Ints(JsonElement run, string key)
    {
        var list = new List<int>();

        foreach (var element in run.GetProperty(key).EnumerateArray())
            list.Add(element.GetInt32());

        return list.ToArray();
    }

    /// <summary>
    /// <c>SpaceInsideAWordIsStillRejectedWhenTheSettingIsOff</c>, as backlog 184 leaves it on the
    /// live arm: with the setting off there is no word for the press to skip, so it is nothing but a
    /// wrong character and is TYPED THROUGH as one. The cell takes it, the caret advances, the press
    /// is a mistype, and no rejection is announced. Also the pin that nothing the skip added reaches
    /// a run with the setting off, which is every browser <c>/play</c> today.
    ///
    /// <para>The game's own fixture asserts the rejection still, because a bare engine there is on
    /// the CLASSIC space era (<c>StrictSpaces</c> false) that every stored replay carries; the
    /// browser has no era axis and plays the live rule unconditionally, exactly as it does for the
    /// word-gap type-through.</para>
    /// </summary>
    [Test]
    public void SpaceInsideAWordIsTypedThroughWhenTheSettingIsOff()
    {
        var run = Run("settingOff");

        Assert.Multiple(() =>
        {
            Assert.That(Strings(run, "rejected"), Is.Empty, "not a rejection any more");
            Assert.That(Int(run, "caretIndex"), Is.EqualTo(2), "the caret moved on, as it does for any typo");
            Assert.That(Strings(run, "states"), Is.EqualTo(new[] { "correct", "wrong", "untyped", "untyped", "untyped", "untyped", "untyped" }));
            Assert.That(Int(run, "processorMisses"), Is.Zero);
            Assert.That(Int(run, "mistypes"), Is.EqualTo(1), "the typed-through space is a mistype");
        });
    }

    /// <summary>
    /// The feature itself (<c>SpaceInsideAWordAbandonsTheRestOfItAndLandsOnTheNextWord</c>): the rest
    /// of the word enters the phantom state and the caret lands on the next word, with the word gap
    /// judged exactly like a typed space. NOTHING is resolved yet, because the player can still come
    /// back for those cells: the miss arrives at the seal, and only for the cells nobody returned to.
    ///
    /// <para>The gap's delta is 2600 - 3000 = -400, which the millisecond ladder would grade Ok, but
    /// the spacebar has been outside the timing challenge since backlog 148, so it takes the top
    /// tier whatever the clock said. 600 is 300 for 'c' plus 300 for the space, the latter at combo
    /// 0 after the skip's break, i.e. at a x1.00 multiplier.</para>
    /// </summary>
    [Test]
    public void SpaceInsideAWordAbandonsTheRestOfItAndLandsOnTheNextWord()
    {
        var run = Run("skipAbandonsTheWord");

        Assert.Multiple(() =>
        {
            Assert.That(Strings(run, "rejected"), Is.Empty, "the space is consumed by the skip, not rejected");
            Assert.That(Int(run, "mistypes"), Is.Zero, "abandoning a word is a deliberate action, not a mistype");

            Assert.That(Strings(run, "states"), Is.EqualTo(new[]
            {
                "correct",   // 'c' keeps what it earned
                "abandoned", // 'a' given up, not lost
                "abandoned", // 't' given up, not lost
                "correct",   // the word gap took the space
                "untyped", "untyped", "untyped"
            }));

            Assert.That(Int(run, "caretIndex"), Is.EqualTo(4), "past the gap, on the first character of the NEXT word");

            Assert.That(Int(run, "processorMisses"), Is.Zero, "the miss is the cell's resolution, and the line has not run out of time");
            Assert.That(Int(run, "ok"), Is.Zero);
            Assert.That(Int(run, "great"), Is.EqualTo(2));
            Assert.That(Int(run, "score"), Is.EqualTo(600));
            Assert.That(Double(run, "accuracy"), Is.EqualTo(1.0), "the skip itself is not a keypress");
            Assert.That(Int(run, "maxCombo"), Is.EqualTo(1), "'c' made it 1, the skip broke it, the space rebuilt it to 1");
            Assert.That(Int(run, "breaks"), Is.EqualTo(1), "two cells given up, one break");

            Assert.That(run.GetProperty("stateOfDAfterTheNextPress").GetString(), Is.EqualTo("correct"),
                "typing carries straight on from the next word");
        });
    }

    /// <summary>
    /// <c>AWrongCharInTheAbandonedWordIsNotGivenUp</c>. A cell the player FINISHED is not given up,
    /// and since backlog 124 that group is the correct cells AND the wrong ones: a Great cannot be
    /// revoked, and a typo is not a miss, so abandoning the word cannot turn it into one. The wrong
    /// cell keeps its red and its deferred result, which the seal decides.
    /// </summary>
    [Test]
    public void AWrongCharInTheAbandonedWordIsNotGivenUp()
    {
        var run = Run("wrongCharIsNotGivenUp");

        Assert.Multiple(() =>
        {
            Assert.That(Strings(run, "states"), Is.EqualTo(new[]
            {
                "correct", "wrong", "abandoned", "correct", "untyped", "untyped", "untyped"
            }));

            Assert.That(run.GetProperty("typedCharOfCellOne").GetString(), Is.EqualTo("x"));
            Assert.That(Int(run, "processorMisses"), Is.Zero, "'t' is deferred, not missed");
            Assert.That(Int(run, "mistypes"), Is.EqualTo(1), "'x' still counted once");
        });
    }

    /// <summary>
    /// <c>ACellTypedCorrectlyAndThenBackspacedIsGivenUpLikeAnyUntypedCell</c>. The other side of the
    /// same rule: backspacing puts the cell back to untyped, so the skip gives it up like any other
    /// unresolved cell.
    /// </summary>
    [Test]
    public void ACellTypedCorrectlyAndThenBackspacedIsGivenUpLikeAnyUntypedCell()
    {
        var run = Run("correctThenBackspacedIsGivenUp");

        Assert.Multiple(() =>
        {
            Assert.That(Bool(run, "backspaced"), Is.True);
            Assert.That(Int(run, "caretAfterBackspace"), Is.EqualTo(1));
            Assert.That(Strings(run, "states").Take(3), Is.EqualTo(new[] { "correct", "abandoned", "abandoned" }));
            Assert.That(Int(run, "processorMisses"), Is.Zero);
        });
    }

    /// <summary>
    /// <c>SpaceOnAWordGapIsUnchanged</c>. A space pressed ON the word gap keeps its ordinary meaning,
    /// setting or no setting: it is the character the cell expects, so it is simply typed. 918 is
    /// 300 + 306 + 312, three Greats on an unbroken run.
    /// </summary>
    [Test]
    public void SpaceOnAWordGapIsUnchanged()
    {
        var run = Run("spaceOnAWordGap");

        Assert.Multiple(() =>
        {
            Assert.That(Strings(run, "states").Take(3), Is.EqualTo(new[] { "correct", "correct", "correct" }));
            Assert.That(Int(run, "caretIndex"), Is.EqualTo(3));
            Assert.That(Int(run, "processorMisses"), Is.Zero);
            Assert.That(Int(run, "great"), Is.EqualTo(3));
            Assert.That(Int(run, "score"), Is.EqualTo(918));
            Assert.That(Int(run, "maxCombo"), Is.EqualTo(3), "never broken");
            Assert.That(Int(run, "breaks"), Is.Zero);
        });
    }

    /// <summary>
    /// <c>SkippingTheLastWordOfALineCompletesTheLine</c>. The last word of a line has no gap after
    /// it, so the caret lands at the end of the line and the cells stay reclaimable until the line's
    /// own deadline, plus the flexible caret's <c>FLETCHER_DRAG_GRACE_MS</c> since backlog 208 (the
    /// caret is still on the line, and an abandoned cell is a cell still owed).
    /// When nobody comes back the seal resolves them, and it does so WITHOUT a second
    /// combo break: that break was taken at the skip, and charging it again would cost a run the
    /// player rebuilt through the rest of the line.
    /// </summary>
    [Test]
    public void SkippingTheLastWordOfALineCompletesTheLineAndSealsWithoutASecondBreak()
    {
        var run = Run("skippingTheLastWordOfALine");
        var afterSkip = run.GetProperty("afterSkip");
        var afterSeal = run.GetProperty("afterSeal");

        Assert.Multiple(() =>
        {
            Assert.That(Strings(afterSkip, "states").Skip(3), Is.EqualTo(new[] { "abandoned", "abandoned" }));
            Assert.That(Int(afterSkip, "caretIndex"), Is.EqualTo(5), "the line is complete");
            Assert.That(Int(afterSkip, "breaks"), Is.EqualTo(1));

            Assert.That(Strings(afterSeal, "states").Skip(3), Is.EqualTo(new[] { "missed", "missed" }));
            Assert.That(Int(afterSeal, "processorMisses"), Is.EqualTo(2), "the cells resolve here, as the misses they turned out to be");
            Assert.That(Int(afterSeal, "breaks"), Is.EqualTo(1), "and WITHOUT a second combo break");
            Assert.That(Int(afterSeal, "maxCombo"), Is.EqualTo(3));
            Assert.That(Bool(afterSeal, "finished"), Is.True);
        });
    }

    /// <summary>
    /// <c>GatekeeperRejectsSpaceInsteadOfSkippingUnderInputEra2</c> (PR 3's second input era), which
    /// replaced <c>TheSkipWorksUnderGatekeeperToo</c> for every live run: the skip needs wrong input
    /// allowed, so under Gatekeeper a space inside a word is a rejected wrong key like any other. The
    /// browser cannot select Gatekeeper (it has no mods payload), so this is a pin on the mirror
    /// rather than on a reachable run.
    /// </summary>
    [Test]
    public void GatekeeperRejectsSpaceInsteadOfSkipping()
    {
        var run = Run("underGatekeeper");

        Assert.Multiple(() =>
        {
            Assert.That(Strings(run, "rejected"), Is.EqualTo(new[] { "q", " " }), "the wrong letter is rejected, and so is the space");
            Assert.That(Int(run, "caretAfterRejection"), Is.EqualTo(1));
            Assert.That(Int(run, "streakAfterRejection"), Is.EqualTo(1));

            Assert.That(Strings(run, "states").Take(4), Is.EqualTo(new[] { "correct", "untyped", "untyped", "untyped" }), "nothing was skipped");
            Assert.That(Int(run, "caretIndex"), Is.EqualTo(1));
            Assert.That(Int(run, "processorMisses"), Is.Zero);
            Assert.That(Int(run, "consecutiveWrongKeys"), Is.EqualTo(2), "the rejected space feeds the mash-fail streak like any wrong key");
            Assert.That(Bool(run, "canUndoWordSkip"), Is.False, "and there is no skip to undo");
        });
    }

    /// <summary>
    /// <c>MashingLeavesNothingToSkip</c>. Mashing rewrites every press into the character the caret
    /// expects BEFORE the skip is reached, so with both on there is no word left to abandon.
    /// </summary>
    [Test]
    public void MashingLeavesNothingToSkip()
    {
        var run = Run("mashingLeavesNothingToSkip");

        Assert.Multiple(() =>
        {
            Assert.That(Strings(run, "states").Take(2), Is.EqualTo(new[] { "correct", "correct" }));
            Assert.That(run.GetProperty("typedCharOfCellOne").GetString(), Is.EqualTo("a"));
            Assert.That(Int(run, "caretIndex"), Is.EqualTo(2));
            Assert.That(Int(run, "processorMisses"), Is.Zero);
        });
    }

    /// <summary>
    /// THE PROPERTY (<c>OneBackspaceUndoesTheSkipAndItsGapUnderInputEra2</c>, PR 3's second input era).
    /// From the next word, ONE backspace undoes the whole skip: the abandoned letters go back to
    /// untyped, the space the skip typed on the gap is erased, and the caret lands on the FIRST
    /// abandoned cell with the correctly typed prefix intact. Before PR 3
    /// (<c>OneBackspaceFromTheGapReOpensTheWholeSkippedWord</c>) the first press took the gap alone and
    /// the second crossed the abandoned run and erased the last character actually typed.
    /// </summary>
    [Test]
    public void OneBackspaceUndoesTheSkipAndItsGap()
    {
        var run = Run("oneBackspaceReOpensTheWord");

        Assert.Multiple(() =>
        {
            Assert.That(Bool(run, "undoableBefore"), Is.True, "canUndoWordSkip, with the caret past the typed gap");
            Assert.That(Bool(run, "undone"), Is.True);
            Assert.That(Int(run, "caretIndex"), Is.EqualTo(1), "the caret lands on the first skipped character");
            Assert.That(Strings(run, "states").Take(4), Is.EqualTo(new[] { "correct", "untyped", "untyped", "untyped" }),
                "the prefix is preserved, the word re-opened and the skip's space erased too");
            Assert.That(Bool(run, "undoableAfter"), Is.False);
        });
    }

    /// <summary>
    /// <c>RetypingAReclaimedWordEarnsRealJudgements</c>. The cells really are earnable again:
    /// re-typing them produces ordinary judgements with ordinary points, not the scoring-inert retype
    /// an already-earned cell produces. That is the whole point of withholding the osu result at the
    /// skip, since a cell takes only its first result.
    /// </summary>
    [Test]
    public void RetypingAReclaimedWordEarnsRealJudgements()
    {
        var run = Run("retypingEarnsRealJudgements");

        Assert.Multiple(() =>
        {
            Assert.That(Int(run, "pointsForTheInertRetype"), Is.Zero, "the gap was already earned before the undo erased it");
            Assert.That(Int(run, "pointsForTheFirstReclaimedCell"), Is.GreaterThan(0),
                "a reclaimed cell scores; an inert retype would not");

            Assert.That(Strings(run, "states").Take(3), Is.EqualTo(new[] { "correct", "correct", "correct" }));
            Assert.That(Int(run, "great"), Is.EqualTo(4), "c, the gap, a, t, each counted once");
            Assert.That(Int(run, "processorMisses"), Is.Zero);
            Assert.That(Double(run, "accuracy"), Is.EqualTo(1.0));
        });
    }

    /// <summary>
    /// <c>AReclaimedSkipGivesTheComboBackToWhereItWouldHaveBeen</c>. The combo the skip broke comes
    /// back, on the cell the skip abandoned FIRST, through the same snapshot machinery a corrected
    /// typo redeems (backlog 140). Typing the line out after a skip and a full reclaim therefore ends
    /// on exactly the combo, and the exact max combo, that typing it straight through would have.
    ///
    /// <para>The three intermediate reads are the ordering: nothing is restored by the skip itself,
    /// nothing by the erase (an erase alone fixes nothing), and everything by the retype of the
    /// snapshot cell.</para>
    /// </summary>
    [Test]
    public void AReclaimedSkipGivesTheComboBackToWhereItWouldHaveBeen()
    {
        var run = Run("reclaimedSkipGivesTheComboBack");
        var straight = run.GetProperty("straight");
        var reclaimed = run.GetProperty("reclaimed");

        Assert.Multiple(() =>
        {
            Assert.That(Ints(reclaimed, "restoredAfterTheSkip"), Is.Empty);
            Assert.That(Ints(reclaimed, "restoredAfterTheErase"), Is.Empty, "the erase alone restores nothing");
            Assert.That(Ints(reclaimed, "restoredAfterTheSnapshotCell"), Is.EqualTo(new[] { 1 }),
                "the streak of 1 the skip broke, resumed on the cell it was snapshotted against");

            Assert.That(Int(straight, "combo"), Is.EqualTo(7));
            Assert.That(Int(reclaimed, "combo"), Is.EqualTo(7), "the run ends where it would have without the skip");
            Assert.That(Int(reclaimed, "maxCombo"), Is.EqualTo(Int(straight, "maxCombo")));
            Assert.That(Int(reclaimed, "processorHighestCombo"), Is.EqualTo(Int(straight, "processorHighestCombo")),
                "and the SUBMITTED max_combo agrees, by its own hand-mirrored route");
            Assert.That(Int(reclaimed, "great"), Is.EqualTo(Int(straight, "great")));
            Assert.That(Int(reclaimed, "processorMisses"), Is.Zero);
        });
    }

    /// <summary>
    /// <c>TheFirstWordOfALineIsReclaimableTooUnderInputEra2</c>. A word abandoned at the very START of
    /// a line has no keypress behind it, and the ordinary "nothing to erase" answer would make it the
    /// one unreclaimable word on the map. Since PR 3 ONE backspace erases the gap and re-opens the
    /// word, parking the caret at the head of the line; before it a first press took the gap and a
    /// second reclaimed the word.
    /// </summary>
    [Test]
    public void TheFirstWordOfALineIsReclaimableToo()
    {
        var run = Run("firstWordOfALineIsReclaimable");

        Assert.Multiple(() =>
        {
            Assert.That(Int(run, "caretAfterSkip"), Is.EqualTo(4), "nothing typed at all, so the whole of \"cat\" goes");

            Assert.That(Bool(run, "reclaim"), Is.True, "one press erases the gap and reclaims the word");
            Assert.That(Int(run, "caretAfterReclaim"), Is.Zero);
            Assert.That(Strings(run, "statesAfterReclaim").Take(3), Is.EqualTo(new[] { "untyped", "untyped", "untyped" }));

            Assert.That(Strings(run, "states")[0], Is.EqualTo("correct"), "and the head of the word is typeable again");
            Assert.That(Int(run, "score"), Is.GreaterThan(0));
        });
    }

    /// <summary>
    /// <c>BackspaceAtTheHeadOfALineIsStillInert</c>. With nothing abandoned behind it, a backspace at
    /// the head of a line does nothing, which is the pin that the reclaim branch did not widen
    /// "nothing to erase" for everyone else.
    /// </summary>
    [Test]
    public void BackspaceAtTheHeadOfALineIsStillInert()
    {
        var run = Run("backspaceAtTheHeadIsInert");

        Assert.Multiple(() =>
        {
            Assert.That(Bool(run, "erased"), Is.False);
            Assert.That(Int(run, "caretIndex"), Is.Zero);
        });
    }

    /// <summary>
    /// <c>EveryAbandonedCellLeavesThePhantomStateExactlyOnce</c>, read off the CELLS (this mirror
    /// raises none of the three C# events, because they carry health, which the browser models as a
    /// derived read rather than as an account). Skip "cat", come back for it, then skip "dog" and
    /// never return: both exits from the phantom state happen in one play, no cell may still be
    /// phantom after the seal, and only the word nobody came back for is missed.
    /// </summary>
    [Test]
    public void EveryAbandonedCellLeavesThePhantomStateExactlyOnce()
    {
        var run = Run("everyAbandonedCellLeavesExactlyOnce");

        Assert.Multiple(() =>
        {
            Assert.That(Strings(run, "statesBeforeSeal"), Is.EqualTo(new[]
            {
                "correct", "correct", "correct", "correct", "correct", "abandoned", "abandoned"
            }));

            Assert.That(Strings(run, "states"), Has.None.EqualTo("abandoned"), "no cell may still be phantom after the seal");
            Assert.That(Int(run, "processorMisses"), Is.EqualTo(2), "only the word nobody came back for");
            Assert.That(Int(run, "maxCombo"), Is.EqualTo(5));
        });
    }

    /// <summary>
    /// <c>AnAbandonedCellHoldsTheLineOpenLikeAnUntypedOne</c>. A line holds its seal open for an
    /// abandoned cell exactly as it does for an untyped one, so the reclaim window runs to the line's
    /// own deadline. Without that, a skip near the end of a line would trip the EARLY seal ("nothing
    /// left to type, do not hold the next line up") and close the window in the very grace period
    /// that exists for finishing.
    ///
    /// <para>Since backlog 208 that window is WIDER, and by the same argument stated once more: an
    /// abandoned cell is a cell the player is still owed, so the flexible caret's DRAG GRACE holds
    /// the line for <c>FLETCHER_DRAG_GRACE_MS</c> beyond its hard deadline too, exactly as it does
    /// for a player still typing cells they have not reached. Only the CLOCK moved; the seal
    /// resolves the same cells the same way, which is why the assertions below did not.</para>
    ///
    /// <para>The fixture is the game's rebuilt around the LOADER: the C# one hands the engine a line
    /// whose word units overrun its end with no grace of its own, which neither loader produces (both
    /// clamp a word to its line), so the same JSON gives a 700 ms overrun grace on both sides instead
    /// of the C# fixture's 250 ms boundary bump. The property is identical.</para>
    ///
    /// <para>Since PR 3 (<c>AnAbandonedCellHoldsTheLineOpenUnderInputEra2</c>) the one backspace undoes
    /// the skip of the line's last word outright, so the retype starts on 'c' with no space before
    /// it.</para>
    /// </summary>
    [Test]
    public void AnAbandonedCellHoldsTheLineOpenLikeAnUntypedOne()
    {
        var run = Run("abandonedCellHoldsTheLineOpen");

        Assert.Multiple(() =>
        {
            Assert.That(Int(run, "sealGraceMs"), Is.EqualTo(700), "the vocals overrun the boundary, so there is a window to come back into");
            Assert.That(Int(run, "activeInsideTheGrace"), Is.Zero, "the line must still be open to come back into");

            Assert.That(Bool(run, "reclaim"), Is.True);
            Assert.That(run.GetProperty("stateOfC").GetString(), Is.EqualTo("correct"));

            // ...and the grace is still bounded: past it the line seals whatever is left.
            Assert.That(Int(run, "activeLineIndex"), Is.EqualTo(-1));
            Assert.That(Int(run, "processorMisses"), Is.EqualTo(1), "only the 'd' nobody got back to");
        });
    }

    /// <summary>
    /// <c>ASkipNeverReturnedToCostsWhatItAlwaysCost</c>, as the browser's own account. The pin the
    /// whole design rests on: moving the miss from the keypress to the seal is what makes the cells
    /// earnable, and it must cost a player who never comes back exactly nothing extra.
    ///
    /// <para>Stated as literals here so a reviewer can re-derive them, and held against the LIVE C#
    /// in <c>Typebeat.WireCompat.WordSkipLiveParityTest.ASkipNeverReturnedToAgrees</c>, which is the
    /// assertion that actually pins the two clients together. Five of the seven cells typed, two
    /// missed: completion 5/7, which is a C.</para>
    /// </summary>
    [Test]
    public void ASkipNeverReturnedToCostsWhatItAlwaysCost()
    {
        var run = Run("skipNeverReturnedTo");
        var submitted = run.GetProperty("submitted");

        Assert.Multiple(() =>
        {
            Assert.That(Strings(run, "states"), Is.EqualTo(new[]
            {
                "correct", "missed", "missed", "correct", "correct", "correct", "correct"
            }));

            Assert.That(Int(run, "breaks"), Is.EqualTo(1), "one break for the whole word, and none at the seal");
            Assert.That(Int(run, "processorMisses"), Is.EqualTo(2));
            Assert.That(Int(submitted, "maxCombo"), Is.EqualTo(4),
                "the run rebuilt after the skip: the seal's combo-neutral misses do not break it a second time");
            Assert.That(submitted.GetProperty("totalScore").GetInt64(), Is.EqualTo(282336));
            Assert.That(Double(submitted, "completion"), Is.EqualTo(5 / 7.0));
            Assert.That(submitted.GetProperty("rank").GetString(), Is.EqualTo("C"));
        });
    }

    /// <summary>
    /// The seal's COMBO-NEUTRAL marks, isolated. The same never-reclaimed skip, on a line the play
    /// carries on past: the abandoned cells resolve as Misses while the player is still holding the
    /// run they rebuilt after the skip, so those Misses must leave the submitted combo exactly where
    /// they find it. Their break was taken at the skip, and taking it again would wipe a run the
    /// player rebuilt through the rest of the line while the HUD combo kept it.
    ///
    /// <para>This is the ONE shape in which the marks are observable, which is why it exists
    /// alongside the single-line pins: when the seal is the last thing that happens, max_combo is
    /// already banked and no judgement follows, so a second break there costs nothing and a browser
    /// that marked nothing would still look right. Held against the live C# as
    /// <c>ASkipOnALineThePlayCarriesOnPastAgrees</c>.</para>
    ///
    /// <para>RE-TIMED by backlog 218's rush bound, which moved WHEN the seal lands without moving a
    /// single press or anything in the submitted account. The 'g' walks the caret off the end of line
    /// 0 at 4333, before entry into line 1 opens at 4500, so the roll is refused and the caret PARKS
    /// on line 0: it is still there when line 0's deadline arrives, and an ABANDONED cell is an
    /// untyped one (backlog 167), so the drag grace protects the reclaim window and holds the seal
    /// off exactly as it does for any player still on a line with cells owed. The line-start snap
    /// takes the caret onto line 1 on that same frame, and the seal follows once the caret has left.
    /// So the two Misses now land AFTER "hi" is typed, against a run of 6 rather than 4, which is a
    /// stronger reading of the same rule: there is more for a spurious break to destroy.</para>
    /// </summary>
    [Test]
    public void TheSealsMissesDoNotBreakARunTheSkipAlreadyBroke()
    {
        var run = Run("skipThenTheNextLine");
        var afterTheSnap = run.GetProperty("afterTheSnap");
        var afterSeal = run.GetProperty("afterSeal");
        var submitted = run.GetProperty("submitted");

        Assert.Multiple(() =>
        {
            // Since backlog 307 the browser runs the MANUAL NEWLINE, under which no snap exists: the
            // caret is still parked on line 0 at 6000, and it is the player's own 'h' (the typed-through
            // newline) that hands it on and lands on line 1's first cell. Nothing in the account moves.
            Assert.That(Int(afterTheSnap, "activeLineIndex"), Is.EqualTo(0), "the manual newline: nothing but the player's own press moves the caret on");
            Assert.That(Int(afterTheSnap, "processorMisses"), Is.Zero,
                "and line 0 is still unsealed, the rush bound having kept the caret on it through its own deadline");

            // EARLY FINISH (PR 13): line 1 is the map's FINAL line and both its cells are typed by
            // 6500, so it seals on the same 6501 update that seals line 0, and the run finishes right
            // there rather than waiting for line 1's own end at 10000. The caret is parked nowhere,
            // which is why activeLineIndex reads -1; the judgement is neutral, so the account below is
            // the one a late seal would have written.
            Assert.That(Int(afterSeal, "activeLineIndex"), Is.EqualTo(-1), "the fully typed final line ended the run early");
            Assert.That(Bool(afterSeal, "finished"), Is.True);
            Assert.That(Int(afterSeal, "processorMisses"), Is.EqualTo(2), "the two cells nobody came back for resolved here");
            Assert.That(Int(afterSeal, "processorCombo"), Is.EqualTo(6),
                "and left the run the player rebuilt after the skip exactly where they were holding it");
            Assert.That(Int(afterSeal, "combo"), Is.EqualTo(6), "the HUD combo agrees, having taken no break here either");
            Assert.That(Int(afterSeal, "breaks"), Is.EqualTo(1), "one break for the whole play, at the skip");

            Assert.That(Strings(run, "statesLineOne"), Is.EqualTo(new[] { "correct", "correct" }));
            Assert.That(Int(submitted, "maxCombo"), Is.EqualTo(6), "4 held across the seal, plus the next line's two cells");
            Assert.That(submitted.GetProperty("totalScore").GetInt64(), Is.EqualTo(380647));
            Assert.That(submitted.GetProperty("rank").GetString(), Is.EqualTo("C"));
        });
    }

    /// <summary>
    /// And the other half of that pair: a skip the player DOES come back for costs nothing beyond the
    /// detour, so the map still ends on a perfect X with every cell typed and the full run restored.
    ///
    /// <para>The total is NOT the clean run's 1000000, and the gap is the one thing the detour really
    /// costs: the word gap was typed once, at combo 0 immediately after the break, and the retype of
    /// it on the way back through is scoring-inert, so its combo-weighted portion is the one the
    /// broken run gave it. Both clients have to agree on that too (see the WireCompat pin).</para>
    /// </summary>
    [Test]
    public void AFullyReclaimedSkipEndsOnAPerfectRun()
    {
        var clean = Run("cleanRun").GetProperty("submitted");
        var reclaimed = Run("fullyReclaimedRun").GetProperty("submitted");

        Assert.Multiple(() =>
        {
            Assert.That(clean.GetProperty("totalScore").GetInt64(), Is.EqualTo(1_000_000));

            Assert.That(Int(reclaimed, "maxCombo"), Is.EqualTo(7), "the streak the skip broke came back");
            Assert.That(Double(reclaimed, "completion"), Is.EqualTo(1));
            Assert.That(reclaimed.GetProperty("rank").GetString(), Is.EqualTo("X"));
            Assert.That(reclaimed.GetProperty("statistics").GetProperty("great").GetInt32(), Is.EqualTo(7));
            Assert.That(reclaimed.GetProperty("statistics").TryGetProperty("miss", out _), Is.False,
                "every cell was typed in the end");

            Assert.That(reclaimed.GetProperty("totalScore").GetInt64(), Is.EqualTo(984633));
        });
    }
}
