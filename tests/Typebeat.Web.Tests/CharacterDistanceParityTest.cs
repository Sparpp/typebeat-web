using System.Text.Json;
using Typebeat.Web.Scoring;

namespace Typebeat.Web.Tests;

/// <summary>
/// Fidelity guard for backlog 133's CHARACTER-DISTANCE judgement across the browser/server seam.
///
/// <para>The game stopped grading a keypress on milliseconds off its cell's target and started
/// grading it on how many CHARACTERS it is from the character the playhead is on, in FOUR quality
/// tiers (perfect/great/ok/meh) rather than three. Because /play scores land on the SAME
/// leaderboards as desktop ones, <c>wwwroot/js/typebeat-core.js</c> has to measure and grade
/// identically or the two clients submit different dictionaries for the same performance.</para>
///
/// <para>Nothing here is a JS opinion checked against a C# opinion. The distances, the ladders and
/// the mash run below are the golden values from the GAME's own suite
/// (<c>TypingEngineTest.CharacterDistanceInterpolatesBetweenTargetsAndExtrapolatesPastTheEnds</c>,
/// <c>.WindowsAreCharacterDistancesAndTheMillisecondLadderStillExists</c> and
/// <c>.MashingAWholeLineAheadWalksDownEveryTierAndThenEarnsNothing</c>), restated here as literals
/// because the web test project cannot reference the ruleset assembly. The Node harness drives the
/// SHIPPED script; the dictionaries it emits are then run through the server's own
/// <see cref="ScoringContract"/> and <see cref="PerformancePoints"/>, which is the code that will
/// judge them in production.</para>
/// </summary>
public class CharacterDistanceParityTest
{
    private static JsonElement Harness() => JsHarness.Run("CoreCharacterDistanceHarness.cjs");

    private static double Num(JsonElement e, string key) => e.GetProperty(key).GetDouble();

    private static Dictionary<string, int> Dict(JsonElement run, string key)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var property in run.GetProperty(key).EnumerateObject())
            result[property.Name] = property.Value.GetInt32();

        return result;
    }

    private static ScoringContract.Recomputed Recompute(JsonElement run)
        => ScoringContract.Recompute(Dict(run, "statistics"), Dict(run, "maximumStatistics"), run.GetProperty("maxCombo").GetInt32());

    /// <summary>
    /// The axis judgement is measured on: a line's TYPEABLE cell targets in display order, spaces
    /// included. "ab cd" gives targets 1000, 1500, 2000, 2000, 2500, so the mean typeable spacing
    /// is (2500 - 1000) / 4 = 375 ms.
    /// </summary>
    [Test]
    public void TheBrowserMeasuresCharacterDistanceExactlyAsTheGameDoes()
    {
        var root = Harness();

        Assert.Multiple(() =>
        {
            Assert.That(JsHarness.Doubles(root, "cellTargets"), Is.EqualTo(new double[] { 1000, 1500, 2000, 2000, 2500 }));
            Assert.That(JsHarness.Doubles(root, "cellPositions"), Is.EqualTo(new double[] { 0, 1, 2, 3, 4 }));
            Assert.That(Num(root, "extrapolationSpacing"), Is.EqualTo(375));

            // Dead on your own target is 0 characters out, always. That is the contract the whole
            // measure rests on.
            Assert.That(JsHarness.Doubles(root, "distanceOnOwnTarget"), Is.EqualTo(new double[] { 0, 0, 0, 0, 0 }));

            // INSIDE: exact linear interpolation between the bracketing targets, so where spacing is
            // locally uniform the distance is just the millisecond delta over that spacing.
            Assert.That(Num(root, "distanceHalfwayAToB"), Is.EqualTo(0.5).Within(1e-12));      // 250 of the 500 ms a->b
            Assert.That(Num(root, "distanceHalfwayAToBFromB"), Is.EqualTo(-0.5).Within(1e-12)); // ...and 'b' is that far ahead
            Assert.That(Num(root, "distanceThreeFifthsBToSpace"), Is.EqualTo(0.6).Within(1e-12));

            // Pressing a cell at a completely different cell's target: the plain index difference.
            Assert.That(Num(root, "distanceAcrossCells"), Is.EqualTo(-3).Within(1e-12));
        });
    }

    /// <summary>
    /// THE PLAYHEAD IS A SPAN, NOT A POINT. Where several cells share one target time, which is what
    /// every boundary between two contiguous words looks like (the word gap takes its unit's end and
    /// the next word's first letter takes the next unit's start, the same millisecond), a press dead
    /// on that time is 0 characters out for ALL of them. Picking either end of the run instead would
    /// charge a rhythm-perfect player a whole character for the other one, which is the difference
    /// between a 100% sync play and a 98.75% one on the fixture below.
    /// </summary>
    [Test]
    public void CellsSharingATargetAreAllZeroCharactersFromAPressOnIt()
    {
        var root = Harness();

        Assert.Multiple(() =>
        {
            Assert.That(Num(root, "distanceTiedSpace"), Is.Zero, "the word gap at 2000");
            Assert.That(Num(root, "distanceTiedC"), Is.Zero, "the next word's first letter, also at 2000");

            // Measured from the run's FAR end for a cell after it, and from its NEAR end for one
            // before it: a span, not an average.
            Assert.That(Num(root, "distanceTiedFromAfter"), Is.EqualTo(-1).Within(1e-12));
            Assert.That(Num(root, "distanceTiedFromBefore"), Is.EqualTo(2).Within(1e-12));

            // ...and the play that proves it end to end: every cell typed dead on its own target is
            // a Perfect with sync quality exactly 1, the two tied cells included.
            Assert.That(JsHarness.Strings(root, "onTargetTypes"),
                Is.EqualTo(new[] { "Perfect", "Perfect", "Perfect", "Perfect", "Perfect" }));
            Assert.That(JsHarness.Doubles(root, "onTargetSync"), Is.EqualTo(new double[] { 1, 1, 1, 1, 1 }));
        });
    }

    /// <summary>
    /// The line's ends EXTRAPOLATE at its mean typeable spacing rather than clamping. Clamping is
    /// what the sung caret sweep does, correctly, because a caret must not leave its line; doing it
    /// here would make every early press on a line's first character a distance of exactly 0, i.e. a
    /// Perfect however early it was, which is the one answer that must not come out.
    /// </summary>
    [Test]
    public void TheEndsOfTheAxisExtrapolateRatherThanClamp()
    {
        var root = Harness();

        Assert.Multiple(() =>
        {
            Assert.That(Num(root, "distanceTwoSpacingsEarly"), Is.EqualTo(-2).Within(1e-12));
            Assert.That(Num(root, "distanceTwoSpacingsLate"), Is.EqualTo(2).Within(1e-12));

            // The fallback chain, for data that offers no spacing at all: one typeable cell sung
            // over [1000, 1600] falls back to that 600 ms span over its 1 cell, so a press 600 ms
            // either side is exactly one character out. Never a division by zero.
            Assert.That(Num(root, "singleExtrapolationSpacing"), Is.EqualTo(600));
            Assert.That(Num(root, "distanceSingleEarly"), Is.EqualTo(-1).Within(1e-12));
            Assert.That(Num(root, "distanceSingleLate"), Is.EqualTo(1).Within(1e-12));
            Assert.That(Num(root, "fallbackSpacing"), Is.EqualTo(200), "the last resort, when even the sung span is empty");
        });
    }

    /// <summary>
    /// The two window ladders. The CHARACTER one is live: geometric, every tier exactly 1.6x
    /// late-biased and exactly double the tier inside it. The MILLISECOND one is backlog 135's
    /// Rhythmic mod and nothing selects it yet, but its Great/Ok/Meh rows are EXACTLY the windows
    /// this game judged in before backlog 133, so it has to be here and has to be right.
    /// </summary>
    [Test]
    public void BothWindowLaddersMatchTheGamesTuningPoint()
    {
        var root = Harness();

        // pe, pl, ge, gl, oe, ol, me, ml.
        double[] line = [1.25, 2.00, 2.50, 4.00, 5.00, 8.00, 10.00, 16.00];

        Assert.Multiple(() =>
        {
            Assert.That(JsHarness.Doubles(root, "characterLine"), Is.EqualTo(line));
            Assert.That(JsHarness.Doubles(root, "characterWord"), Is.EqualTo(line.Select(v => v * 0.6).ToArray()).Within(1e-12));
            Assert.That(JsHarness.Doubles(root, "characterSyllable"), Is.EqualTo(line.Select(v => v * 0.45).ToArray()).Within(1e-12));
            Assert.That(JsHarness.Doubles(root, "millisecondLine"), Is.EqualTo(new double[] { 125, 200, 250, 400, 600, 1000, 1200, 2000 }));

            // Nested asymmetric ranges, tested Perfect -> Great -> Ok -> Meh, every edge inclusive;
            // outside Meh the sign decides Premature (ahead) from Lagging (behind).
            Assert.That(JsHarness.Strings(root, "characterClassify"), Is.EqualTo(new[]
            {
                "Perfect",  //  0
                "Perfect",  // -1.25, the early edge
                "Great",    // -1.26
                "Perfect",  //  2.00, the late edge
                "Great",    //  2.01
                "Great",    // -2.50
                "Ok",       // -2.51
                "Great",    //  4.00
                "Ok",       //  4.01
                "Ok",       // -5.00
                "Meh",      // -5.01
                "Ok",       //  8.00
                "Meh",      //  8.01
                "Meh",      // -10.00
                "Premature",// -10.01
                "Meh",      //  16.00
                "Lagging",  //  16.01
            }));

            Assert.That(JsHarness.Strings(root, "millisecondClassify"), Is.EqualTo(new[]
            {
                "Perfect",   //  200, the tier backlog 133 added above the old top
                "Great",     //  201
                "Great",     // -250, the old Perfect window
                "Great",     //  400
                "Ok",        //  401, the old Good window
                "Ok",        //  1000
                "Meh",       //  1001, the old Ok window
                "Meh",       //  2000
                "Lagging",   //  2001
                "Premature", // -1201
            }));
        });
    }

    /// <summary>
    /// The four tiers are worth 300 / 200 / 100 / 50 and map onto the osu results they are NAMED
    /// for. The identity is backlog 133's other half: before it the two vocabularies were offset by
    /// one rung, so "Perfect" meant two different things depending on which side you read.
    /// </summary>
    [Test]
    public void TheFourTiersAreWorthTheirOsuResultsAndAreNamedForThem()
    {
        var root = Harness();

        Assert.Multiple(() =>
        {
            // Perfect, Great, Ok, Meh, then everything that scores nothing.
            Assert.That(JsHarness.Doubles(root, "basePoints"), Is.EqualTo(new double[] { 300, 200, 100, 50, 0, 0, 0, 0 }));
            Assert.That(JsHarness.Strings(root, "hitResults"),
                Is.EqualTo(new[] { "perfect", "great", "ok", "meh", "miss", "miss", "miss" }));

            // Sync quality runs over the WIDEST scoring window, so it is 1 on the playhead and
            // exactly 0 at both Meh edges: every offset a correct keypress can still score at lands
            // somewhere on the ramp, and everything past it sits on the floor.
            Assert.That(Num(root, "syncOnPlayhead"), Is.EqualTo(1));
            Assert.That(Num(root, "syncHalfLate"), Is.EqualTo(0.5).Within(1e-12));
            Assert.That(Num(root, "syncAtMehLate"), Is.Zero);
            Assert.That(Num(root, "syncAtMehEarly"), Is.Zero);
            Assert.That(Num(root, "syncPastMeh"), Is.Zero);
        });
    }

    /// <summary>
    /// The premise change, stated as a play. 14 presses at t = 1000 on a line paced 2000 ms per
    /// character: under the old millisecond measure every press but the first was thousands of ms
    /// early, i.e. Premature. Measured in CHARACTERS the k'th press is exactly k characters ahead
    /// whatever the tempo, so the ladder is walked down one rung at a time. That is the point of the
    /// change: how far ahead a player may be is capped in characters (10) rather than in
    /// milliseconds, so a slow line is not a harder line. Mashing still cannot pay.
    ///
    /// <para>Score, combo and break count are the game suite's literals:
    /// <c>points_i = round(base_i * (1 + comboBefore/50))</c> gives
    /// <c>300, 306 | 208 | 106, 108, 110 | 56, 57, 58, 59, 60 = 1428</c>.</para>
    /// </summary>
    [Test]
    public void MashingAWholeLineAheadWalksDownEveryTierExactlyAsTheGameDoes()
    {
        var root = Harness();

        Assert.Multiple(() =>
        {
            Assert.That(JsHarness.Strings(root, "mashTypes"), Is.EqualTo(new[]
            {
                "Perfect", "Perfect",                       // distance 0 and -1, inside the 1.25 early edge
                "Great",                                    // -2, inside 2.50
                "Ok", "Ok", "Ok",                           // -3, -4, -5, edge inclusive
                "Meh", "Meh", "Meh", "Meh", "Meh",          // -6 .. -10, edge inclusive
                "Premature", "Premature", "Premature",      // -11, -12, -13: past every window
            }));

            Assert.That(Num(root, "mashScore"), Is.EqualTo(1428));
            Assert.That(Num(root, "mashMaxCombo"), Is.EqualTo(11));
            Assert.That(Num(root, "mashComboBreaks"), Is.EqualTo(3));
        });
    }

    /// <summary>
    /// A press one notch outside the SCALED Perfect window, and the split the measure introduced:
    /// the judgement is derived from the character distance, while the delta the timing read-out
    /// shows stays in milliseconds. Word granularity, "ab" over [1000, 2000], so targets 1000 and
    /// 1500 and a mean spacing of 500 ms. A press at 1601 is (1601 - 1500) / 500 = 0.202 past the
    /// last target, i.e. 1.202 characters behind the playhead, against a scaled PerfectLate of
    /// 2.00 * 0.6 = 1.20.
    /// </summary>
    [Test]
    public void TheJudgementComesFromTheDistanceWhileTheDeltaStaysMilliseconds()
    {
        var run = Harness().GetProperty("wordJustOutsidePerfect");

        Assert.Multiple(() =>
        {
            Assert.That(run.GetProperty("judgeType").GetString(), Is.EqualTo("Great"));
            Assert.That(Num(run, "judgedOffset"), Is.EqualTo(1.202).Within(1e-12));
            Assert.That(Num(run, "judgedDelta"), Is.EqualTo(601));
        });
    }

    /// <summary>
    /// A scoring-inert retype replays the judgement the cell already earned, and under the character
    /// measure that means replaying the banked OFFSET rather than re-deriving a distance at the
    /// retype's time. Twenty backspace-retype cycles seconds later leave the original Perfect, its
    /// offset, its delta, the score and the combo all exactly where the first press put them:
    /// otherwise backspace-retype re-grades a cell as well as farming it.
    /// </summary>
    [Test]
    public void AnInertRetypeReplaysTheBankedOffsetRatherThanReJudging()
    {
        var run = Harness().GetProperty("inertRetype");

        Assert.Multiple(() =>
        {
            Assert.That(run.GetProperty("firstJudgeType").GetString(), Is.EqualTo("Perfect"));
            Assert.That(run.GetProperty("judgeType").GetString(), Is.EqualTo("Perfect"));
            Assert.That(Num(run, "judgedOffset"), Is.Zero);
            Assert.That(Num(run, "judgedDelta"), Is.Zero);
            Assert.That(Num(run, "score"), Is.EqualTo(300));
            Assert.That(Num(run, "maxCombo"), Is.EqualTo(1));
        });
    }

    /// <summary>
    /// THE UNRANK LANDMINE. The cell judgement's MaxResult was raised from Great to Perfect to free
    /// the enum slot the fourth tier needed, so <c>maximum_statistics</c> is now one <b>perfect</b>
    /// per cell. Every classifier in <see cref="ScoringContract"/> has to know that key: if
    /// <c>AffectsAccuracy</c> did not, <c>accuracyMax</c> would be 0 and the denominator not
    /// positive, and EVERY submitted play would unrank.
    /// </summary>
    [Test]
    public void APerfectBrowserPlaySubmitsPerfectsAndTheContractRanksThem()
    {
        var root = Harness();
        var run = root.GetProperty("onTarget");

        var statistics = Dict(run, "statistics");
        var maximums = Dict(run, "maximumStatistics");
        var recomputed = Recompute(run);

        Assert.Multiple(() =>
        {
            Assert.That(statistics, Is.EquivalentTo(new Dictionary<string, int> { ["perfect"] = 5 }));
            Assert.That(maximums, Is.EquivalentTo(new Dictionary<string, int> { ["perfect"] = 5 }),
                "one MaxResult per cell, and the MaxResult is Perfect since backlog 133");

            Assert.That(recomputed.StatisticsValid, Is.True, "a clean play must rank");
            Assert.That(recomputed.Accuracy, Is.EqualTo(1));
            Assert.That(recomputed.JudgedAccuracy, Is.EqualTo(1));
            Assert.That(recomputed.Completion, Is.EqualTo(1));
            Assert.That(recomputed.Rank, Is.EqualTo("X"));
            Assert.That(recomputed.TheoreticalMaxCombo, Is.EqualTo(5), "perfect must be combo-increasing too");

            // A full combo hits the ceiling exactly, which is the whole-pipeline statement that the
            // per-cell maximum did not move when the fourth tier arrived.
            Assert.That(run.GetProperty("totalScore").GetInt64(), Is.EqualTo(1_000_000));
            Assert.That(ScoringContract.TotalScoreWithinBounds(run.GetProperty("totalScore").GetInt64(), recomputed), Is.True);
            Assert.That(recomputed.TotalScoreCeiling, Is.EqualTo(1_000_000));
        });
    }

    /// <summary>
    /// A dictionary carrying all four quality keys at once, recomputed by the server exactly as the
    /// browser computed it. The mash play judges 2 perfect, 1 great, 3 ok, 5 meh and seals 3 misses
    /// over a 14 cell map, so accuracy is
    /// <c>(2*300 + 1*200 + 3*100 + 5*50) / (14*300) = 1350/4200</c> and completion is
    /// <c>11/14</c>. If <c>great</c> were still priced at 300 the accuracy would come out at
    /// 1450/4200 and every browser play with a great in it would submit a number the server
    /// disagreed with.
    /// </summary>
    [Test]
    public void TheServerRecomputesAFourTierDictionaryToTheBrowsersOwnNumbers()
    {
        var root = Harness();
        var run = root.GetProperty("mash");

        var recomputed = Recompute(run);

        Assert.Multiple(() =>
        {
            Assert.That(Dict(run, "statistics"), Is.EquivalentTo(new Dictionary<string, int>
            {
                ["perfect"] = 2, ["great"] = 1, ["ok"] = 3, ["meh"] = 5, ["miss"] = 3,
            }));
            Assert.That(Dict(run, "maximumStatistics"), Is.EquivalentTo(new Dictionary<string, int> { ["perfect"] = 14 }));

            Assert.That(recomputed.StatisticsValid, Is.True);
            Assert.That(recomputed.Accuracy, Is.EqualTo(1350d / 4200).Within(1e-12));
            Assert.That(recomputed.Accuracy, Is.EqualTo(Num(run, "accuracy")).Within(1e-12),
                "the browser showed the player the number the server then stores");
            Assert.That(recomputed.Completion, Is.EqualTo(11d / 14).Within(1e-12));
            Assert.That(recomputed.Completion, Is.EqualTo(Num(run, "completion")).Within(1e-12));
            Assert.That(recomputed.Rank, Is.EqualTo("C"));
            Assert.That(ScoringContract.TotalScoreWithinBounds(run.GetProperty("totalScore").GetInt64(), recomputed), Is.True);
        });
    }

    /// <summary>
    /// The two numeric weights the ruleset overrides, stated directly rather than through a play.
    /// <c>great</c> is 200 of the cell's 300 and <c>good</c> (the uncorrected typo) is 50, while the
    /// per-cell MAXIMUM stays 300 for every one of them. That last part is why the accuracy
    /// denominator did not move: the tier was made by moving Great DOWN, because the base game
    /// already scores a Perfect at 300.
    /// </summary>
    [Test]
    public void GreatIsTwoThirdsOfACellAndThePerCellMaximumIsStillThreeHundred()
    {
        var perfectMax = new Dictionary<string, int> { ["perfect"] = 15 };

        var allGreat = ScoringContract.Recompute(new Dictionary<string, int> { ["great"] = 15 }, perfectMax, 15);
        var allPerfect = ScoringContract.Recompute(new Dictionary<string, int> { ["perfect"] = 15 }, perfectMax, 15);
        var allOk = ScoringContract.Recompute(new Dictionary<string, int> { ["ok"] = 15 }, perfectMax, 15);
        var allMeh = ScoringContract.Recompute(new Dictionary<string, int> { ["meh"] = 15 }, perfectMax, 15);
        var allTypo = ScoringContract.Recompute(new Dictionary<string, int> { ["good"] = 15 }, perfectMax, 15);

        Assert.Multiple(() =>
        {
            // The 300 / 200 / 100 / 50 ladder, read off the accuracy each tier alone produces
            // against a per-cell maximum of 300.
            Assert.That(allPerfect.Accuracy, Is.EqualTo(1));
            Assert.That(allGreat.Accuracy, Is.EqualTo(200d / 300).Within(1e-12));
            Assert.That(allOk.Accuracy, Is.EqualTo(100d / 300).Within(1e-12));
            Assert.That(allMeh.Accuracy, Is.EqualTo(50d / 300).Within(1e-12));

            // The typo is re-weighted to the meh value, so it still pays the most accuracy a judged
            // cell can pay, and it is still NOT typed, so it still costs rank exactly as a miss does.
            Assert.That(allTypo.Accuracy, Is.EqualTo(allMeh.Accuracy));
            Assert.That(allTypo.Completion, Is.Zero);
            Assert.That(allTypo.Rank, Is.EqualTo("D"));

            // ...and all four quality tiers ARE typed, so any of them alone is a full completion.
            Assert.That(allGreat.Completion, Is.EqualTo(1));
            Assert.That(allMeh.Completion, Is.EqualTo(1));
            Assert.That(allMeh.Rank, Is.EqualTo("X"), "rank is graded on cells typed, never on timing");
        });
    }

    /// <summary>
    /// A SCORE STORED BEFORE BACKLOG 133 STILL READS EXACTLY AS IT WAS SUBMITTED, which is what the
    /// era discriminator is for. Such a row carries <c>great</c> as its TOP tier, worth 300 at the
    /// time, and there is no way to tell that <c>great</c> from a post-133 second-tier one from the
    /// key alone. Its own <c>maximum_statistics</c> is the stamp: the MaxResult moved from Great to
    /// Perfect in the same change that re-weighted <c>great</c>, so a maximum keyed <c>great</c> can
    /// only be a score judged under the old ladder.
    ///
    /// <para>Without this every stored row would read at 2/3 of the accuracy it was submitted with,
    /// and its stored total would fall outside the ceiling its own statistics justify: a
    /// pre-133 SS would look tampered to <c>GateRefund.Qualifies</c>, the one path that re-runs this
    /// contract over rows the database already holds. At the time of writing EVERY row in production
    /// is a pre-133 one, because neither half of this change may ship without the other.</para>
    /// </summary>
    [Test]
    public void AScoreStoredBeforeTheFourthTierRecomputesToWhatItWasSubmittedWith()
    {
        var oldClean = ScoringContract.Recompute(
            new Dictionary<string, int> { ["great"] = 15 },
            new Dictionary<string, int> { ["great"] = 15 },
            maxCombo: 15);

        // The same play under the new ladder: 15 SECOND-tier cells against a 300 maximum.
        var newGreats = ScoringContract.Recompute(
            new Dictionary<string, int> { ["great"] = 15 },
            new Dictionary<string, int> { ["perfect"] = 15 },
            maxCombo: 15);

        // A mixed old row, where the eras genuinely disagree: (300*12 + 100*2) / (300*15).
        var oldMixed = ScoringContract.Recompute(
            new Dictionary<string, int> { ["great"] = 12, ["ok"] = 2, ["miss"] = 1 },
            new Dictionary<string, int> { ["great"] = 15 },
            maxCombo: 12);

        Assert.Multiple(() =>
        {
            Assert.That(oldClean.StatisticsValid, Is.True, "an old row must never become unrankable");
            Assert.That(oldClean.Accuracy, Is.EqualTo(1));
            Assert.That(oldClean.JudgedAccuracy, Is.EqualTo(1));
            Assert.That(oldClean.Completion, Is.EqualTo(1));
            Assert.That(oldClean.Rank, Is.EqualTo("X"));
            Assert.That(oldClean.TheoreticalMaxCombo, Is.EqualTo(15));

            // The stored total of a pre-133 full combo is still exactly at its ceiling, which is the
            // test GateRefund applies to a row before it will re-rank it.
            Assert.That(oldClean.TotalScoreCeiling, Is.EqualTo(1_000_000));

            Assert.That(oldMixed.Accuracy, Is.EqualTo(3800d / 4500).Within(1e-12));

            // ...and the discriminator is doing real work: the identical statistics under a
            // post-133 maximum price the greats as the second tier they now are.
            Assert.That(newGreats.Accuracy, Is.EqualTo(200d / 300).Within(1e-12));
            Assert.That(newGreats.Accuracy, Is.Not.EqualTo(oldClean.Accuracy));
        });
    }

    /// <summary>
    /// pp counts a cell of the map per NOTE key, and the fourth tier is a note like any other.
    /// Leaving <c>perfect</c> out of that list would make every play submitted after backlog 133
    /// read as a map with almost no notes at all, shrinking the length bonus and inflating the combo
    /// ratio. No stored score carries the key, so adding it reprices nothing and needs no version
    /// bump.
    /// </summary>
    [Test]
    public void PpCountsAPerfectAsANoteOfTheMap()
    {
        var root = Harness();

        var clean = PerformancePoints.CountNotes(Dict(root.GetProperty("onTarget"), "statistics"));
        var mash = PerformancePoints.CountNotes(Dict(root.GetProperty("mash"), "statistics"));

        Assert.Multiple(() =>
        {
            Assert.That(clean.Notes, Is.EqualTo(5));
            Assert.That(clean.Misses, Is.Zero);

            Assert.That(mash.Notes, Is.EqualTo(14), "every judged cell is a note, whichever tier it landed in");
            Assert.That(mash.Misses, Is.EqualTo(3));
            Assert.That(mash.Mistypes, Is.Zero, "mashing the RIGHT character is not a mistype");
        });
    }
}
