using Typebeat.Web.Packages.Lyrics;

namespace Typebeat.Web.Tests;

/// <summary>
/// The pace + difficulty arithmetic against known values. Pace anchors on the game's own
/// regression test (typebeat-osu typebeat.Game.Rulesets.TypeBeat.Tests/NonVisual/
/// LyricPaceStatisticsTest.cs): "ab cd" sung for 3000 ms -> 5 cells, 2 words, CPM 100, WPM 20
/// (CPM/5), 2.5 cells per word.
///
/// <para>TWO THINGS MOVED UNDER THIS FILE AT THE DIFFICULTY REWORK (LyricPace v21), and every
/// restated number below is one of them. THE PACE: <see cref="LyricPace.PaceStatistics.AverageCpm"/>
/// is now the WHOLE-MAP rate (total cells over the summed SUNG windows, breaks dropped) rather than
/// the unweighted mean of the per-line rates, which survives beside it as
/// <see cref="LyricPace.PaceStatistics.LineAverageCpm"/>; a cell is counted by
/// <see cref="Typeability.IsTypeable"/> rather than <see cref="Typeability.IsCell"/>, so freestyle
/// slots are out of it; and the target is no longer a selection over lines at all. THE STARS:
/// <see cref="LyricDifficulty"/>'s shipped reading is the CHUNKED ENDURANCE axis
/// (<see cref="LyricDifficulty.Live"/>), so EVERY star figure in this file is a value of that axis
/// unless it names <see cref="LyricDifficulty.EnduranceAxis.Envelope"/>, the model that used to
/// ship and that the target figure still reads.</para>
///
/// <para>WHAT THIS FILE CAN NO LONGER LOCK. The envelope numbers the game's LyricDifficultyTest
/// pins are taken with typability OFF (its own NoScores source, which is internal there and
/// unreachable here), and the public API this mirror has to call applies the shipped typability
/// index. So an envelope-arm figure below is this port's own measurement WITH typability, not the
/// prototype's; the cross-port lock on the prototype value lives in the game's fixture and in
/// WireCompat, and <see cref="DifficultyRating_TheSharedAnchorMapUnderBothArms"/> says so out
/// loud.</para>
/// </summary>
public class LyricPaceTest
{
    private static LyricLine paceRegressionLine() => new()
    {
        RawText = "ab cd",
        StartTime = 1000,
        EndTime = 4000,
        SingEndTime = 3000,
        Units =
        [
            new TimedUnit { Text = "ab", StartTime = 1000, EndTime = 2000 },
            new TimedUnit { Text = "cd", StartTime = 2000, EndTime = 3000 },
        ],
    };

    [Test]
    public void ComputesSungWindowPace_MatchesGameRegressionValues()
    {
        var pace = LyricPace.Compute([paceRegressionLine()]);

        Assert.Multiple(() =>
        {
            // NEITHER COUNT MOVES under the WPM redefinition or under v21's narrowing of a cell to
            // IsTypeable: only the formula consuming them did, and this line holds no slot.
            Assert.That(pace.TypeableCellCount, Is.EqualTo(5));
            Assert.That(pace.WordCount, Is.EqualTo(2));

            // 100 CPM SURVIVES THE v21 DENOMINATOR CHANGE, and not by luck. The old figure divided
            // by the boundary window, 4000 - 1000 = 3000 ms. The new one divides by the SUNG window:
            // two 1000 ms spans plus the 1000 ms tail to the line's boundary, which is exactly the
            // break threshold and therefore still singing time, so the same 3000 ms comes out. Move
            // break_min_ms below 1000 and this line reads 150 CPM instead, which is what makes it a
            // useful place to notice the threshold.
            //
            // CPM = 5 cells / 0.05 min = 100, and WPM is that over 5 = 20. The real-word convention
            // this replaced said 2 / 0.05 = 40; the line averages 5/2 = 2.5 cells per word, exactly
            // half the 5 the unit assumes, so the new figure is exactly half the old one.
            Assert.That(pace.AverageCpm, Is.EqualTo(100.0).Within(1e-9));
            Assert.That(pace.AverageWpm, Is.EqualTo(20.0).Within(1e-9));
            Assert.That(pace.AverageCharsPerWord, Is.EqualTo(2.5).Within(1e-9));

            // One line, so the whole-map rate and the line mean are the same number by construction.
            // The fixtures below are where they come apart.
            Assert.That(pace.LineAverageCpm, Is.EqualTo(pace.AverageCpm).Within(1e-12));
            Assert.That(pace.LineAverageWpm, Is.EqualTo(pace.AverageWpm).Within(1e-12));

            // Stars from LyricDifficulty, the SHIPPED (chunked) reading. It read 0.63 under the
            // strain model, 0.59 under the feats one, 0.5911 under the envelope at the 10.6 anchor,
            // 0.6692 at the 12.0 anchor and 0.8197 on the chunked axis the rework shipped, and it
            // reads EXACTLY ZERO since v22: the line carries five cells, and the chunked axis's
            // character floor (16, the sandbox's own dial, mirrored by the envelope's
            // LyricDifficulty.MinimumWindowChars) prices a map with no window holding that many at
            // nothing, however fast its short bursts are. The pace figures above do not move.
            Assert.That(pace.DifficultyRating, Is.Zero);
        });
    }

    /// <summary>
    /// THE v21 SPLIT, and the fixture that says what each of the two figures is FOR. The whole-map
    /// rate is what the map asks per minute of singing, so a short fast line weighs less than a long
    /// one; the line mean gives every counted line one vote whatever its length. Ported from the
    /// game's WholeMapAverageWeightsLinesByTimeWhileTheLineMeanDoesNot, which is the same fixture.
    /// </summary>
    [Test]
    public void WholeMapAverage_WeightsLinesByTime_WhileTheLineMeanDoesNot()
    {
        // Line 1: "ab cd" sung 3000 ms -> 100 CPM / 20 WPM, 5 cells.
        // Line 2: "ab cd" sung 1500 ms -> 200 CPM / 40 WPM, 5 cells.
        //
        //   whole map = 10 cells / (3000 + 1500 ms) = 10 / 0.075 = 133.333 CPM = 26.667 WPM
        //   line mean = (100 + 200) / 2             =              150     CPM = 30      WPM
        //
        // Two different numbers on purpose. Before v21 this test was named AveragesPerLineRates_
        // Unweighted and pinned 150 / 30 as the MAP's pace; that figure has not been deleted, it has
        // been renamed to LineAverageCpm and demoted to the companion.
        var second = new LyricLine
        {
            RawText = "ab cd",
            StartTime = 4000,
            EndTime = 5500,
            SingEndTime = 5500,
            Units = [new TimedUnit { Text = "ab cd", StartTime = 4000, EndTime = 5500 }],
        };

        var pace = LyricPace.Compute([paceRegressionLine(), second]);

        Assert.Multiple(() =>
        {
            Assert.That(pace.TypeableCellCount, Is.EqualTo(10));
            Assert.That(pace.WordCount, Is.EqualTo(4));

            Assert.That(pace.AverageCpm, Is.EqualTo(10.0 / (4500 / 60000.0)).Within(1e-9));
            Assert.That(pace.AverageWpm, Is.EqualTo(10.0 / (4500 / 60000.0) / LyricPace.CHARS_PER_WORD).Within(1e-9));

            Assert.That(pace.LineAverageCpm, Is.EqualTo(150.0).Within(1e-9));
            Assert.That(pace.LineAverageWpm, Is.EqualTo(30.0).Within(1e-9));

            // A pure count ratio with no time in it, so nothing about the denominator can move it.
            Assert.That(pace.AverageCharsPerWord, Is.EqualTo(2.5).Within(1e-9));
        });
    }

    /// <summary>
    /// THE POINT OF THE WHOLE-MAP RATE: a break in the song is not typing time.
    ///
    /// <para>A line's <see cref="LyricLine.EndTime"/> is the next line's start, so a map with a long
    /// instrumental after a line hands that pause to the line's own boundary window. The per-line
    /// mean then charges the player for it, one pause at a time; the whole-map rate walks the word
    /// spans instead (<c>SungWindow</c>) and never sees it. Ported from the game's fixture of the
    /// same name.</para>
    /// </summary>
    [Test]
    public void TheWholeMapAverage_LeavesTheSongsBreaksOutOfTheDenominator()
    {
        // Two 5-cell lines, each sung for 4 s but bounded for 20 s (a 16 s instrumental after each):
        //   whole map = 10 cells / (4000 + 4000 ms) = 10 / 0.1333 = 75 CPM = 15 WPM
        //   line mean = each line 5 cells / 20 s    =              15 CPM =  3 WPM
        var pace = LyricPace.Compute(
        [
            sungLine("ab cd", 0, 20000, singEnd: 4000),
            sungLine("ab cd", 20000, 40000, singEnd: 24000),
        ]);

        Assert.Multiple(() =>
        {
            Assert.That(pace.TypeableCellCount, Is.EqualTo(10));
            Assert.That(pace.AverageCpm, Is.EqualTo(75.0).Within(1e-9));
            Assert.That(pace.AverageWpm, Is.EqualTo(15.0).Within(1e-9));

            // The figure it replaced reads five times slower on the same map, because every one of
            // those 16 second silences is sitting inside a line's own vote.
            Assert.That(pace.LineAverageCpm, Is.EqualTo(15.0).Within(1e-9));
            Assert.That(pace.LineAverageWpm, Is.EqualTo(3.0).Within(1e-9));

            // The two figures differ ONLY by where the windows stop, which is the point of the
            // fixture: same five cells per line either way.
            Assert.That(pace.AverageCharsPerWord, Is.EqualTo(2.5).Within(1e-9));
        });
    }

    /// <summary>
    /// THE THRESHOLD, and the difference between it being a threshold and it being a trim: a pause
    /// counts as singing time up to <c>break_min_ms</c> (1000 ms) and is dropped WHOLE beyond it.
    /// Nothing pinned this before v21, because before v21 nothing divided by a sung window.
    ///
    /// <para>Every fixture here is the same five cells, so the CPM figures encode the charged window
    /// directly: 5 cells * 60000 / CPM is the number of milliseconds that went into the denominator,
    /// so 75 means 4000 ms charged, 60 means 5000 and 50 means 6000. Ported from the game's
    /// APauseCountsUpToTheBreakThresholdAndIsDroppedWholeBeyondIt.</para>
    /// </summary>
    [Test]
    public void APause_CountsUpToTheBreakThreshold_AndIsDroppedWholeBeyondIt()
    {
        // A BREATH of exactly 1000 ms between the two 2 s spans COUNTS: the comparison is inclusive,
        // so the widest pause the constant allows is not itself a break. The 5 s tail after the last
        // span is wider than the constant and is dropped whole. Charged: 2000 + 1000 + 2000 = 5000.
        var breath = LyricPace.Compute([sungLine("ab cd", 0, 10000, singEnd: 5000, (0, 2000), (3000, 5000))]);

        // A BREAK of 1001 ms is over the line and is dropped WHOLE rather than trimmed back to the
        // constant: a trim would have charged the extra millisecond's worth and read 5000 ms (60 CPM)
        // here, so 75 (4000 ms, the two spans alone) is the number that says "dropped".
        var gone = LyricPace.Compute([sungLine("ab cd", 0, 10000, singEnd: 5001, (0, 2000), (3001, 5001))]);

        // The same rule reads the TAIL between the last span and the line's boundary: a 1000 ms one
        // counts (6000 ms charged) and a 1001 ms one does not (5000 ms).
        var shortTail = LyricPace.Compute([sungLine("ab cd", 0, 6000, singEnd: 5000, (0, 2000), (3000, 5000))]);
        var longTail = LyricPace.Compute([sungLine("ab cd", 0, 6001, singEnd: 5000, (0, 2000), (3000, 5000))]);

        Assert.Multiple(() =>
        {
            Assert.That(breath.AverageCpm, Is.EqualTo(60.0).Within(1e-9));
            Assert.That(breath.AverageWpm, Is.EqualTo(12.0).Within(1e-9));
            Assert.That(gone.AverageCpm, Is.EqualTo(75.0).Within(1e-9));
            Assert.That(shortTail.AverageCpm, Is.EqualTo(50.0).Within(1e-9));
            Assert.That(longTail.AverageCpm, Is.EqualTo(60.0).Within(1e-9));

            // THE THRESHOLD IS THE WHOLE-MAP FIGURE'S ALONE. The line mean reads the BOUNDARY window
            // and never looks inside it, so the two 10 s fixtures disagree on the whole-map rate, 60
            // against 75, while reading the same 30 CPM line mean. (The two tail fixtures are
            // shorter than 10 s, so their line means differ for that reason instead, which is
            // nothing to do with the threshold.)
            foreach (var pace in new[] { breath, gone })
            {
                Assert.That(pace.LineAverageCpm, Is.EqualTo(30.0).Within(1e-9));
                Assert.That(pace.LineAverageWpm, Is.EqualTo(6.0).Within(1e-9));
            }

            // Every fixture here is the same five cells, so no window shape can move that.
            foreach (var pace in new[] { breath, gone, shortTail, longTail })
                Assert.That(pace.TypeableCellCount, Is.EqualTo(5));
        });
    }

    /// <summary>
    /// The one identity the whole convention change rests on, and the reason the new metric counts
    /// inter-word spaces: a line whose average word is exactly 5 CELLS long has the same WPM under
    /// the typing-test convention (cells/5) as under the real-word one (words), so the change is a
    /// reweighting around 5, not an arbitrary rescaling, and AverageCharsPerWord is precisely the
    /// old CPM:WPM ratio made visible. Ported from the game's LyricPaceStatisticsTest so both sides
    /// of the mirror are pinned on it.
    ///
    /// <para>Stated at LINE granularity, and the fixture gives every line an average of exactly 5
    /// rather than only the map total: the identity is per line, so a map that averages 5 overall
    /// while its individual lines do not would not satisfy it line by line. That is why the identity
    /// is now checked against <see cref="LyricPace.PaceStatistics.LineAverageWpm"/>: since v21 it is
    /// the LINE mean that is the mean of per-line rates, and the whole-map figure weights the 1500 ms
    /// line twice as heavily as the 3000 ms one. Nothing about the conversion from cells to words
    /// changed; what changed is which windows the average is over.</para>
    /// </summary>
    [Test]
    public void FiveCellWords_MakeTheNewWpmEqualTheOldOne()
    {
        // Line 1, "abcd efghi" over 3000 ms: 2 words, 4 + 1 + 5 = 10 cells, 10/2 = 5 exactly.
        //   old WPM = 2 words / 0.05 min   = 40
        //   CPM     = 10 cells / 0.05 min  = 200
        //   new WPM = 200 / 5              = 40   (equal)
        //
        // Line 2, "abcd efgh ijkl mnopq" over 1500 ms: 4 words, 17 chars + 3 spaces = 20 cells,
        // 20/4 = 5 exactly.
        //   old WPM = 4 words / 0.025 min  = 160
        //   CPM     = 20 cells / 0.025 min = 800
        //   new WPM = 800 / 5              = 160  (equal)
        //
        // Map: line mean CPM = (200 + 800) / 2 = 500, line mean WPM = (40 + 160) / 2 = 100 = 500 / 5,
        // whole map = 30 cells / 4500 ms sung = 400 CPM = 80 WPM, and chars/word = 30 / 6 = 5.
        var lines = new[] { windowLine("abcd efghi", 1000, 4000), windowLine("abcd efgh ijkl mnopq", 4000, 5500) };

        var pace = LyricPace.Compute(lines);

        Assert.Multiple(() =>
        {
            Assert.That(pace.TypeableCellCount, Is.EqualTo(30));
            Assert.That(pace.WordCount, Is.EqualTo(6));
            Assert.That(pace.AverageCharsPerWord, Is.EqualTo(5.0).Within(1e-9));

            Assert.That(pace.AverageCpm, Is.EqualTo(400.0).Within(1e-9));
            Assert.That(pace.AverageWpm, Is.EqualTo(80.0).Within(1e-9));
            Assert.That(pace.LineAverageCpm, Is.EqualTo(500.0).Within(1e-9));
            Assert.That(pace.LineAverageWpm, Is.EqualTo(100.0).Within(1e-9));

            // And the old convention, recomputed here from the same boundary windows, agrees LINE BY
            // LINE with the cells/5 one: the mean of (words / minutes) over the two lines.
            double oldConventionWpm = (2 / (3000 / 60000.0) + 4 / (1500 / 60000.0)) / 2;

            Assert.That(pace.LineAverageWpm, Is.EqualTo(oldConventionWpm).Within(1e-9));
        });
    }

    [Test]
    public void WpmIsCpmOverFive_WhateverTheWordLength()
    {
        // The identity above is conditional on 5-cell words; THIS one is unconditional, which is the
        // point of deriving AverageWpm from AverageCpm instead of summing it separately. Lines
        // chosen to average nothing like 5: 5/2 = 2.5 and 18/2 = 9.0 cells per word, 23/4 = 5.75
        // over the map.
        var pace = LyricPace.Compute([windowLine("ab cd", 1000, 4000), windowLine("abcdefgh ijklmnopq", 4000, 9000)]);

        Assert.Multiple(() =>
        {
            Assert.That(pace.AverageWpm, Is.EqualTo(pace.AverageCpm / LyricPace.CHARS_PER_WORD).Within(1e-12));

            // The companion figure is derived the same way, for the same reason: two sums could
            // drift apart by a rounding step and these two cannot.
            Assert.That(pace.LineAverageWpm, Is.EqualTo(pace.LineAverageCpm / LyricPace.CHARS_PER_WORD).Within(1e-12));

            Assert.That(pace.AverageCharsPerWord, Is.EqualTo(23 / 4.0).Within(1e-9));
        });
    }

    [Test]
    public void CharsPerWord_CountsInterWordSpaces_AndIsZeroWithoutWords()
    {
        // "ab cd ef": 3 words, 6 chars + 2 spaces = 8 cells, so 8/3 and not 6/3. Spaces are in
        // because the 5 in "5 chars = 1 word" counts them: they are keystrokes like any other, and
        // leaving them out here would put the two metrics in different units and break the identity
        // pinned above.
        var pace = LyricPace.Compute([windowLine("ab cd ef", 1000, 4000)]);

        Assert.Multiple(() =>
        {
            Assert.That(pace.TypeableCellCount, Is.EqualTo(8));
            Assert.That(pace.WordCount, Is.EqualTo(3));
            Assert.That(pace.AverageCharsPerWord, Is.EqualTo(8 / 3.0).Within(1e-9));

            // No words to divide by: 0 rather than a NaN the set page would print as "NaN".
            Assert.That(LyricPace.Compute([]).AverageCharsPerWord, Is.Zero);
        });
    }

    /// <summary>
    /// A line whose single word span runs from its start to its vocal end, which is its boundary end
    /// unless <paramref name="singEnd"/> says otherwise.
    ///
    /// <para><paramref name="units"/> overrides the spans when a fixture needs more than one: a pause
    /// INSIDE a line is what the break threshold reads, and a single span covering the whole window
    /// cannot express one.</para>
    /// </summary>
    private static LyricLine sungLine(string text, double start, double end, double? singEnd = null, params (double Start, double End)[] units)
    {
        double vocalEnd = singEnd ?? end;

        return new LyricLine
        {
            RawText = text,
            StartTime = start,
            EndTime = end,
            SingEndTime = vocalEnd,
            Units = units.Length > 0
                ? [.. units.Select(u => new TimedUnit { Text = text, StartTime = u.Start, EndTime = u.End })]
                : [new TimedUnit { Text = text, StartTime = start, EndTime = vocalEnd }],
        };
    }

    /// <summary>A one-unit line spanning its own boundary window, for the count arithmetic.</summary>
    private static LyricLine windowLine(string text, double start, double end) => new()
    {
        RawText = text,
        StartTime = start,
        EndTime = end,
        SingEndTime = end,
        Units = [new TimedUnit { Text = text, StartTime = start, EndTime = end }],
    };

    /// <summary>
    /// <paramref name="windowsMs"/> lines of "a b c", one per boundary window given. The line holds
    /// exactly 5 cells (three tokens, three chars, two inter-word spaces) and is sung for the whole
    /// window, so its rate is 5 * 60000 / window CPM on both averages and the whole distribution is
    /// hand-computable. Lines are laid end to end with a 500 ms rest between them, which the LINE
    /// mean cannot see and the whole-map rate does not charge for (the rest sits outside every line's
    /// sung window).
    ///
    /// <para>Five cells is far too thin to interest the difficulty model, so these fixtures exercise
    /// the two AVERAGES and reach the target only through its floor;
    /// <see cref="twoPaceMap"/> is the dense fixture the target tests read.</para>
    /// </summary>
    private static LyricLine[] linesAtWindows(params double[] windowsMs) => linesAtWindowsOf("a b c", windowsMs);

    /// <summary><see cref="linesAtWindows"/> with the line text chosen.</summary>
    private static LyricLine[] linesAtWindowsOf(string text, params double[] windowsMs)
    {
        var lines = new LyricLine[windowsMs.Length];
        double at = 1000;

        for (int i = 0; i < windowsMs.Length; i++)
        {
            lines[i] = windowLine(text, at, at + windowsMs[i]);
            at += windowsMs[i] + 500;
        }

        return lines;
    }

    /// <summary>
    /// The map's six windows, chosen so every per-LINE rate is a round CPM: 500 ms -> 600 CPM
    /// (120 WPM), 600 -> 500 (100), 750 -> 400 (80), 1000 -> 300 (60), 1500 -> 200 (40),
    /// 3000 -> 100 (20). The LINE MEAN over them is (600 + 500 + 400 + 300 + 200 + 100) / 6 = 350 CPM
    /// = 70 WPM; the whole-map rate is 30 cells over 7350 ms of singing = 244.898 CPM = 48.98 WPM,
    /// which is the two averages coming apart on a fixture built to make them.
    /// </summary>
    private static readonly double[] six_windows = [500, 600, 750, 1000, 1500, 3000];

    /// <summary>The eight-token line the target fixtures are built from: 8 words, 15 cells.</summary>
    private const string dense_text = "a b c d e f g h";

    /// <summary>
    /// Twelve dense lines laid end to end, with the first <paramref name="fastLines"/> of them run at
    /// <paramref name="fastMs"/> and the rest at the pace that fills the same total duration. Both
    /// arms therefore hold the same cells over the same length, and only the DISTRIBUTION differs,
    /// which is exactly what a peak figure has to be able to see and an average must not. Ported from
    /// the game's fixture of the same name.
    /// </summary>
    private static LyricLine[] twoPaceMap(int fastLines, double fastMs, double totalMs)
    {
        const int lines = 12;
        double slowMs = (totalMs - fastLines * fastMs) / (lines - fastLines);
        var result = new LyricLine[lines];
        double at = 1000;

        for (int i = 0; i < lines; i++)
        {
            double ms = i < fastLines ? fastMs : slowMs;
            result[i] = windowLine(dense_text, at, at + ms);
            at += ms;
        }

        return result;
    }

    /// <summary>The model's own speed-window figure for a map, which the strip publishes.</summary>
    private static double modelTargetWpm(LyricLine[] lines)
        => LyricDifficulty.ComputeDetail(lines, 1, false, LyricDifficulty.EnduranceAxis.Envelope).TargetWpm;

    /// <summary>
    /// THE TARGET, REDEFINED (v21). It is the map's hardest window BY RAW SPEED re-expressed at
    /// <c>LyricDifficulty.TargetWindowSeconds</c>, floored at the whole-map average, and this file
    /// does not re-derive it: <see cref="LyricPace"/> hands the lines to the difficulty model's
    /// ENVELOPE arm and publishes what comes back, so the number the set page prints and the number
    /// the model computed cannot drift apart. That equality IS the contract, and it is asserted on
    /// every shape below.
    ///
    /// <para>WHAT THIS REPLACES. Until v21 the target was the mean of the FASTEST FIFTH of the
    /// counted lines (<c>target_line_fraction</c>, v18) restricted to lines of three words or more
    /// (<c>target_line_min_words</c>, v20). Both constants are deleted, and with them the six
    /// fixtures that pinned the selection: the fifth's rounding step, the eligibility floor at three
    /// words, the all-short fallback, and the two-word interjection that could define a map's target.
    /// A line's word count and a line's own boundary window no longer reach this figure at all.</para>
    /// </summary>
    [Test]
    public void TargetWpm_IsTheModelsOwnSpeedWindowFigure_FlooredAtTheAverage()
    {
        foreach (var lines in new[]
        {
            twoPaceMap(6, 1000, 21000),
            twoPaceMap(0, 0, 21000),
            linesAtWindows(six_windows),
            linesAtWindows(1000, 1000, 1000, 1000, 1000),
        })
        {
            var pace = LyricPace.Compute(lines);

            Assert.That(pace.TargetWpm, Is.EqualTo(Math.Max(modelTargetWpm(lines), pace.AverageWpm)).Within(1e-12),
                "the strip has to publish the model's own figure, floored at the average");
        }

        // And the two arms of that Math.Max, named rather than implied, so a failure says which one
        // broke. The peaked map publishes the MODEL's figure, its hardest window being far clear of
        // its own average (151.05 against 102.86). The six-window map, six thin lines laid end to
        // end, published its model figure too until v22 (52.26 against an average of 48.98); since
        // the 16-character floor (LyricDifficulty.MinimumWindowChars) the model's figure for it is
        // 43.05, below the average, so it now publishes the floor, 48.98 exactly.
        Assert.Multiple(() =>
        {
            Assert.That(LyricPace.Compute(twoPaceMap(6, 1000, 21000)).TargetWpm, Is.EqualTo(151.05142388501122).Within(1e-9));
            Assert.That(modelTargetWpm(linesAtWindows(six_windows)), Is.EqualTo(43.052892689999808).Within(1e-9), "the model's own figure");
            Assert.That(LyricPace.Compute(linesAtWindows(six_windows)).TargetWpm, Is.EqualTo(LyricPace.Compute(linesAtWindows(six_windows)).AverageWpm).Within(1e-12),
                "published at the floor");
            Assert.That(LyricPace.Compute(linesAtWindows(six_windows)).TargetWpm, Is.EqualTo(48.979591836734684).Within(1e-9));
        });
    }

    /// <summary>
    /// THE FLOOR, and the reason the pairing the two figures are READ as is a guarantee again. Target
    /// WPM is the pace of the map's hardest window and the average is the pace of the whole song, so
    /// the target is normally the higher of the two; on a map whose hardest stretch is SLOWER than
    /// its relentless average they invert, and the figure presented as "the pace this map asks for"
    /// comes out below the pace the map already demands everywhere. Nothing may be published under
    /// the map's own average, so the target is raised to it.
    ///
    /// <para>This invariant has now been true, false and true again, for three different reasons.
    /// v18's per-line selection gave it for free (a mean over the top fifth cannot sit below the mean
    /// over all of them). v20's three-word eligibility floor killed it, because a fast ineligible
    /// line raised the average and could not raise the target. v21 restores it as a PRESENTATION
    /// rule: <c>pace_floor_target</c>, applied after the model has spoken. It is a display decision
    /// and nothing else, and no rating reads either figure.</para>
    /// </summary>
    [Test]
    public void TargetWpm_IsNeverBelowTheWholeMapAverage()
    {
        foreach (var lines in new[]
        {
            twoPaceMap(6, 1000, 21000),
            twoPaceMap(0, 0, 21000),
            linesAtWindows(six_windows),
            linesAtWindows(1000, 1000, 1000, 1000, 1000),
            linesAtWindowsOf("ab cd", six_windows),
            linesAtWindowsOf("abcde", six_windows),
        })
        {
            var pace = LyricPace.Compute(lines);

            Assert.That(pace.TargetWpm, Is.GreaterThanOrEqualTo(pace.AverageWpm),
                "the published target may never sit under the map's whole-map average");
        }

        // The floor ENGAGING, pinned rather than left to the loop: twelve equal dense lines have no
        // hardest window worth the name, so the model asks for 99.77 WPM and the map already runs at
        // 102.857 everywhere. The published figure is the average exactly.
        var flat = twoPaceMap(0, 0, 21000);
        var flatPace = LyricPace.Compute(flat);

        Assert.Multiple(() =>
        {
            Assert.That(modelTargetWpm(flat), Is.LessThan(flatPace.AverageWpm), "this is the map where the two invert");
            Assert.That(flatPace.TargetWpm, Is.EqualTo(flatPace.AverageWpm).Within(1e-12));
            Assert.That(flatPace.TargetWpm, Is.EqualTo(102.85714285714285).Within(1e-9));
        });
    }

    /// <summary>
    /// AND IT IS A PEAK. The two arms hold the same cells over the same length, and the one that
    /// concentrates them into a fast half reads a substantially higher target. The whole-map average
    /// is the control: a per-minute rate cannot tell the two shapes apart, which is the whole reason
    /// the figure is read off a window rather than off either average. Mirrors the game's
    /// TargetWpmRisesWithThePeakWhileTheAveragesDoNot.
    /// </summary>
    [Test]
    public void TargetWpm_RisesWithThePeak_WhileTheWholeMapAverageDoesNot()
    {
        var peaked = LyricPace.Compute(twoPaceMap(6, 1000, 21000));
        var flat = LyricPace.Compute(twoPaceMap(0, 0, 21000));

        Assert.Multiple(() =>
        {
            Assert.That(peaked.TargetWpm, Is.GreaterThan(flat.TargetWpm), "concentrating the same cells has to raise the target");
            Assert.That(peaked.AverageWpm, Is.EqualTo(flat.AverageWpm).Within(1e-9), "the whole-map rate cannot see the shape");

            // The LINE mean does move, and is pinned here as the contrast rather than as a claim
            // about the map: it gives each line one vote, so a map cut into half-length and
            // double-length lines averages differently from one cut into twelve equal ones. 126 WPM
            // against 102.857, on two maps that are the same cells over the same seconds.
            Assert.That(peaked.LineAverageWpm, Is.EqualTo(126.0).Within(1e-9));
            Assert.That(flat.LineAverageWpm, Is.EqualTo(102.85714285714285).Within(1e-9));

            // The target is not either average, which is the other half of what it is for.
            Assert.That(peaked.TargetWpm, Is.Not.EqualTo(peaked.AverageWpm));
            Assert.That(peaked.TargetWpm, Is.Not.EqualTo(peaked.LineAverageWpm));
        });
    }

    /// <summary>
    /// A LINE WITH NO TYPEABLE CELL IS NOT A LINE, for both averages: "..." projects to nothing, so
    /// it contributes no cells, no vote and no sung time.
    ///
    /// <para>What this fixture USED to say is that the target's selection pool was exactly the set of
    /// lines the average counted, so a phantom line could not enter the fastest fifth as a 0 nor
    /// widen the count that decided how many lines the fifth was. There is no pool any more. The two
    /// maps below still publish the same target, but for a DIFFERENT reason, and the fixture says so:
    /// what the strip publishes is the floor in both cases. Until v22 the model's own window figures
    /// differed (30.45 WPM against 41.37, because empty lines still shape the timeline the model
    /// scans) and both sat below the average; since v22 both are exactly 0, because each map carries
    /// 15 weighted cells, one short of the 16-character floor
    /// (<c>LyricDifficulty.MinimumWindowChars</c>), so no window qualifies at all.</para>
    /// </summary>
    [Test]
    public void AnUntypeableLine_IsInvisibleToBothAverages_AndTheTargetFallsToItsFloor()
    {
        LyricLine[] withEmptyLines =
        [
            windowLine("a b c", 1000, 1500),
            windowLine("...", 2000, 2100),
            windowLine("a b c", 3000, 4000),
            windowLine("...", 5000, 5100),
            windowLine("a b c", 6000, 7000),
        ];

        var withEmpty = LyricPace.Compute(withEmptyLines);

        LyricLine[] withoutEmptyLines = linesAtWindows(500, 1000, 1000);
        var withoutEmpty = LyricPace.Compute(withoutEmptyLines);

        Assert.Multiple(() =>
        {
            // THE AVERAGES ARE THE PROPERTY, and it is exact: three counted lines, 15 cells over
            // 500 + 1000 + 1000 = 2500 ms of singing = 360 CPM = 72 WPM.
            Assert.That(withEmpty.TypeableCellCount, Is.EqualTo(withoutEmpty.TypeableCellCount));
            Assert.That(withEmpty.AverageWpm, Is.EqualTo(withoutEmpty.AverageWpm).Within(1e-12));
            Assert.That(withEmpty.LineAverageWpm, Is.EqualTo(withoutEmpty.LineAverageWpm).Within(1e-12));
            Assert.That(withEmpty.AverageWpm, Is.EqualTo(72.0).Within(1e-9));

            // THE TARGET AGREES ONLY THROUGH THE FLOOR. Both model figures are below 72 (both are 0:
            // neither map clears the character floor), so both maps publish 72.
            Assert.That(withEmpty.TargetWpm, Is.EqualTo(withoutEmpty.TargetWpm).Within(1e-12));
            Assert.That(withEmpty.TargetWpm, Is.EqualTo(withEmpty.AverageWpm).Within(1e-12));

            Assert.That(modelTargetWpm(withEmptyLines), Is.Zero, "15 cells: under the 16-character floor");
            Assert.That(modelTargetWpm(withoutEmptyLines), Is.Zero);
        });
    }

    [Test]
    public void EmptyMap_IsZero()
    {
        var pace = LyricPace.Compute([]);

        Assert.Multiple(() =>
        {
            Assert.That(pace.TypeableCellCount, Is.Zero);
            Assert.That(pace.WordCount, Is.Zero);
            Assert.That(pace.FreestyleCellCount, Is.Zero);
            Assert.That(pace.AverageWpm, Is.Zero);
            Assert.That(pace.AverageCpm, Is.Zero);

            // The companion figure has the same rule, for the same reason: no counted line, no mean.
            Assert.That(pace.LineAverageWpm, Is.Zero);
            Assert.That(pace.LineAverageCpm, Is.Zero);

            // No counted line, so no window for the model to read either, and nothing for the floor
            // to raise it to: 0, which is the only row target_wpm is NULL on for arithmetic reasons.
            Assert.That(pace.TargetWpm, Is.Zero);
            Assert.That(pace.DifficultyRating, Is.Zero);
            Assert.That(pace.AverageCharsPerWord, Is.Zero);
        });
    }

    /// <summary>
    /// THE SHARED ANCHOR MAP, under both arms, and the honest note about what this file can still
    /// lock. The four words below are the fixture the game's LyricDifficultyTest calls its anchor and
    /// pins at 1.433936415287919 since the character floor (<c>LyricDifficulty.MinimumWindowChars</c>)
    /// went to 16 (it was 1.9987321307058443 before, every digit of which came out of the prototype,
    /// docs/sr-envelope-model.js in the parent superrepo, that the envelope model is a port of): the
    /// map carries 22 cells, so the shortest stretch holding 16 of them is longer than the 1.36 s
    /// duration floor and the peak is read off that longer window instead.
    ///
    /// <para>THIS MIRROR CAN NO LONGER REACH THAT NUMBER, so it no longer claims to. The game takes
    /// it with typability OFF, through an internal score source; the public API here applies the
    /// shipped typability index, which this fixture's lines clear the gate for, so the envelope arm
    /// reads 1.4250 instead (2.0477 before v22). The prototype lock lives
    /// in the game's own fixture and in WireCompat. What this test pins is the pair the WEBSITE
    /// stores and shows: the SHIPPED chunked rating, which is what <c>difficulty_rating</c> holds,
    /// and the envelope arm beside it so a failure says which of the two moved.</para>
    /// </summary>
    [Test]
    public void DifficultyRating_TheSharedAnchorMapUnderBothArms()
    {
        LyricLine[] map =
        [
            new LyricLine
            {
                RawText = "hello brave",
                StartTime = 0,
                EndTime = 2600,
                SingEndTime = 2600,
                Units =
                [
                    new TimedUnit { Text = "hello", StartTime = 0, EndTime = 600 },
                    new TimedUnit { Text = "brave", StartTime = 700, EndTime = 1300 },
                ],
            },
            new LyricLine
            {
                RawText = "world again",
                StartTime = 2600,
                EndTime = 5200,
                SingEndTime = 5200,
                Units =
                [
                    new TimedUnit { Text = "world", StartTime = 2600, EndTime = 3400 },
                    new TimedUnit { Text = "again", StartTime = 3600, EndTime = 4600 },
                ],
            },
        ];

        Assert.Multiple(() =>
        {
            Assert.That(LyricDifficulty.Compute(map), Is.EqualTo(1.3022384832687535), "the shipped (chunked) reading");
            Assert.That(LyricDifficulty.Compute(map, 1, false, LyricDifficulty.EnduranceAxis.Envelope), Is.EqualTo(1.4250280885191169),
                "the envelope arm, with the typability the public API applies");

            // The default argument IS the shipped axis, which is what makes every unqualified
            // Compute call in this file a chunked reading.
            Assert.That(LyricDifficulty.Live, Is.EqualTo(LyricDifficulty.EnduranceAxis.Chunked));
        });
    }

    /// <summary>
    /// THE SHORT-MAP RULE, RESTATED THE OTHER WAY UP. Until the rework this test was called
    /// AMapShorterThanTheSmallestWindowRatesExactlyZero and it pinned exactly that: windows are
    /// scheduled in real seconds and never clamped down to the map, so a map whose whole sung
    /// timeline is under the smallest scheduled window (1.36 s) had no peak ratio and therefore no
    /// range to fill.
    ///
    /// <para>THE CHUNKED AXIS RATES IT. It scores the map's own windows directly, so a map that is
    /// one short burst is simply a map with one short window: six words in a second reads as a
    /// fast burst. The old claim is not deleted here, it is relocated: the envelope arm still
    /// returns exactly zero, and that is asserted below so the property has somewhere to fail
    /// from.</para>
    ///
    /// <para>THE FIXTURE CARRIES 23 CELLS SINCE v22, so the DURATION is the only thing under test.
    /// "cat cat" over 800 ms was this file's anchor for six backlog items, but the character floor
    /// (16, the sandbox's dial, which <c>LyricDifficulty.MinimumWindowChars</c> mirrors on the
    /// envelope arm) prices its 7 cells at zero on BOTH arms whatever its length, so the claim needs
    /// a map that clears the floor to be about the duration at all. The same six words the game's
    /// own test uses; and "cat cat" is kept, to say that 7 cells is under the floor however long
    /// the map runs.</para>
    /// </summary>
    [Test]
    public void DifficultyRating_AMapShorterThanTheSmallestWindow_RatesZeroOnlyOnTheEnvelopeArm()
    {
        static LyricLine cats(int count, double end)
        {
            var units = new TimedUnit[count];

            for (int i = 0; i < count; i++)
                units[i] = new TimedUnit { Text = "cat", StartTime = Math.Round(end * i / count), EndTime = Math.Round(end * (i + 1) / count) };

            return new LyricLine
            {
                RawText = string.Join(' ', Enumerable.Repeat("cat", count)),
                StartTime = 0,
                EndTime = end,
                SingEndTime = end,
                Units = units,
            };
        }

        // Six "cat"s: 18 letters and the 5 spaces between them, which clears the 16-cell floor.
        var tooShort = cats(6, 1000);
        var longEnough = cats(6, 3000);
        var belowTheFloor = cats(2, 3000);

        Assert.Multiple(() =>
        {
            Assert.That(LyricPace.Compute([tooShort]).DifficultyRating, Is.EqualTo(4.887130687171009), "1.0 s of singing is one fast burst");
            Assert.That(LyricPace.Compute([longEnough]).DifficultyRating, Is.EqualTo(2.4949068839517472), "3 s of the same six words is a slow one");

            // The ORDER is the part worth saying out loud: the short map outrates the long one,
            // where the envelope model has it at exactly nothing.
            Assert.That(LyricPace.Compute([tooShort]).DifficultyRating, Is.GreaterThan(LyricPace.Compute([longEnough]).DifficultyRating));

            Assert.That(LyricDifficulty.Compute([tooShort], 1, false, LyricDifficulty.EnduranceAxis.Envelope), Is.Zero,
                "1.0 s of singing still fits no scheduled window at all");
            Assert.That(LyricDifficulty.Compute([longEnough], 1, false, LyricDifficulty.EnduranceAxis.Envelope), Is.GreaterThan(0),
                "and 3 s of it does");

            Assert.That(LyricPace.Compute([belowTheFloor]).DifficultyRating, Is.Zero, "and 3 s of \"cat cat\" rates nothing: 7 cells is under the floor");
            Assert.That(LyricDifficulty.Compute([belowTheFloor], 1, false, LyricDifficulty.EnduranceAxis.Envelope), Is.Zero, "on either arm");
        });
    }

    /// <summary>
    /// AN INTER-WORD SPACE IS A CELL (backlog 269) and it belongs to its LINE: a word whose
    /// successor is on the same line carries the spacebar press after it, a word ending its line
    /// does not. So the same words at the same times rate differently depending on whether the
    /// author put them on one line or several, which is right, because across line breaks the
    /// player really does type fewer keystrokes. Mirrors the game's test of the same name.
    ///
    /// <para>FOUR WORDS RATHER THAN TWO SINCE v22, exactly as the game's test moved: the comparison
    /// has to clear the 16-character floor to be readable at all ("aaa bbb" carries 7 cells and now
    /// rates zero on either layout). One line of four carries 3 spaces over the same timeline that
    /// four lines of one carry none, which is the whole of the difference between the two
    /// ratings below.</para>
    /// </summary>
    [Test]
    public void DifficultyRating_AnInterWordSpaceIsACellAndBelongsToItsLine()
    {
        static LyricLine line(double start, double end, params (string Text, double Start, double End)[] words) => new()
        {
            RawText = string.Join(' ', words.Select(w => w.Text)),
            StartTime = start,
            EndTime = end,
            SingEndTime = end,
            Units = words.Select(w => new TimedUnit { Text = w.Text, StartTime = w.Start, EndTime = w.End }).ToArray(),
        };

        LyricLine[] oneLine = [line(0, 4000, ("flame", 0, 1000), ("river", 1000, 2000), ("cider", 2000, 3000), ("amber", 3000, 4000))];

        LyricLine[] fourLines =
        [
            line(0, 1000, ("flame", 0, 1000)),
            line(1000, 2000, ("river", 1000, 2000)),
            line(2000, 3000, ("cider", 2000, 3000)),
            line(3000, 4000, ("amber", 3000, 4000)),
        ];

        Assert.Multiple(() =>
        {
            Assert.That(LyricDifficulty.Compute(oneLine), Is.EqualTo(2.1219807076449064), "23 cells: four words + the 3 spaces between them");
            Assert.That(LyricDifficulty.Compute(fourLines), Is.EqualTo(1.2898243645309204), "20 cells: no space over a line break");

            Assert.That(LyricDifficulty.Compute(oneLine), Is.GreaterThan(LyricDifficulty.Compute(fourLines)),
                "the extra keystrokes have to be worth something, whatever the axis");
        });
    }

    /// <summary>
    /// The DT/HT TRIPLE, pinned exactly rather than by inequality, because these three numbers are
    /// what a beatmap row stores as <c>difficulty_rating</c>, <c>sr_dt</c> and <c>sr_ht</c> and what
    /// PerformancePoints prices a rate play from. All three moved to the chunked axis at the rework
    /// (they read 6.2607 / 8.1643 / 11.7630 on the envelope), and again at v22's overlapping profile
    /// (7.4381 / 9.6646 / 13.9120 before it). The rate PREMIUMS moved less than the level did:
    /// x1.4657 for Double Time and x0.7883 for Half Time now, against x1.4394 and x0.7696 on the
    /// v21 chunked axis and x1.4409 and x0.7668 on the envelope.
    /// </summary>
    [Test]
    public void DifficultyRating_TheRateTripleIsPinned()
    {
        var map = denseMap(lineCount: 12, wordsPerLine: 6, lineMs: 1800);

        Assert.Multiple(() =>
        {
            Assert.That(LyricDifficulty.Compute(map, 0.75), Is.EqualTo(6.8819518298858675), "sr_ht");
            Assert.That(LyricDifficulty.Compute(map), Is.EqualTo(8.7299529590602383), "difficulty_rating");
            Assert.That(LyricDifficulty.Compute(map, 1.50), Is.EqualTo(12.79553147510407), "sr_dt");
        });
    }

    [Test]
    public void DifficultyRating_RateAdjustedRatingIsNotTruncatedAtTheTop()
    {
        // backlog 118, and the fixture is shared with the game's LyricDifficultyTest. LyricDifficulty
        // used to end in a flat clamp to 10 stars, chosen to keep a star BADGE sane, and it truncated
        // the rate-adjusted ratings with it. That reached stored data: sr_dt is what
        // PerformancePoints prices a Double Time play from, and it is never a badge.
        //
        // The fixture was chosen to sit clear of 10 at 1.00x and pass it at 1.50x, which was the
        // asymmetry the live catalogue had. The v21 chunked axis passed 10 at BOTH rates; v22's
        // overlapping profile brings the base back under it (9.82), so the asymmetry is back in this
        // shape. What the pins say is the thing the test exists for, that neither figure is
        // truncated anywhere.
        var map = denseMap(lineCount: 40, wordsPerLine: 6, lineMs: 2000);

        Assert.Multiple(() =>
        {
            Assert.That(LyricDifficulty.Compute(map), Is.EqualTo(9.8175567027913466).Within(1e-9), "clear of 10 at 1.00x");
            Assert.That(LyricDifficulty.Compute(map, 1.50), Is.EqualTo(14.173383552005099).Within(1e-9), "under the old ceiling this read exactly 10.00");
        });
    }

    /// <summary>
    /// HOW A DIFFICULTY IS CHOPPED UP, on the axis that ships. Four maps whose hardest stretch runs
    /// at the same 0.75 of record pace FOR ITS OWN DURATION; what separates them is how the material
    /// is distributed.
    ///
    /// <para>THE CLAIM IN THE OLD NAME IS DEAD ON THIS FIXTURE. Under the envelope model (backlog
    /// 273) a 120 second sustain beat the same pace split into four 15 second sections, which was the
    /// headline property: the v17 feats model had the bursts AHEAD of the sustain, because eight
    /// non-overlapping windows filled eight decaying slots where one long sustain filled one, and how
    /// a difficulty happens to be chopped up is not a difficulty. The chunked axis scores fixed
    /// chunks of about 1.35 s and takes a decay-weighted mean with the hardest chunks weighted most,
    /// so "the same 0.75 of record pace" is no longer the same chunk difficulty: 0.75 of the 15
    /// second record is a FASTER raw pace than 0.75 of the 120 second one, and the four-section map's
    /// hard chunks are individually harder than any of the sustain's. It wins by 1.7% (1.9% on the
    /// v21 grid; v22 reads the same map through overlapping windows rather than one anchored grid,
    /// and every one of the four figures moved with it).</para>
    ///
    /// <para>WHAT SURVIVES, and what the ordering below is now about, is quantity: chopping the same
    /// pace into eight 1.5 second bursts and then into one costs a great deal, because a
    /// decay-weighted mean over a handful of hard chunks cannot reach what forty of them reach.</para>
    /// </summary>
    [Test]
    public void DifficultyRating_ChoppingADifficultyIntoBurstsLowersIt_ButTheFirstCutNoLongerDoes()
    {
        var sustain = new[] { envelopeSection(0, 120, envelopeWordsFor(120, 0.75)) };

        var fourSections = new List<LyricLine>();
        double t = 0;

        for (int k = 0; k < 4; k++)
        {
            fourSections.Add(envelopeEasySection(t * 1000, 15));
            t += 15;
            fourSections.Add(envelopeSection(t * 1000, 15, envelopeWordsFor(15, 0.75)));
            t += 15;
        }

        double sustainSr = LyricDifficulty.Compute(sustain);
        double fourSr = LyricDifficulty.Compute([.. fourSections]);
        double eightSr = LyricDifficulty.Compute(envelopeBurstMap(8));
        double oneSr = LyricDifficulty.Compute(envelopeBurstMap(1));

        Assert.Multiple(() =>
        {
            Assert.That(sustainSr, Is.EqualTo(9.1177770599330294));
            Assert.That(fourSr, Is.EqualTo(9.2732283200415395));
            Assert.That(eightSr, Is.EqualTo(8.5110182710289966));
            Assert.That(oneSr, Is.EqualTo(4.0847007214061071));

            Assert.That(fourSr, Is.GreaterThan(sustainSr), "four 15 s sections now edge past the sustain, which the envelope had the other way round");
            Assert.That(sustainSr, Is.GreaterThan(eightSr), "and both beat the same pace split into eight bursts");
            Assert.That(eightSr, Is.GreaterThan(oneSr), "which beat one");

            // The reason the top pair inverted, as arithmetic rather than as a story: the fixtures
            // are built to a RATIO of the capability curve, and the curve is a function of the
            // stretch's duration, so a 15 second section at 0.75 asks for more raw WPM than a 120
            // second one at 0.75. A fixed-length chunk reads raw pace, so it sees that difference.
            Assert.That(LyricDifficulty.Capability(15), Is.GreaterThan(LyricDifficulty.Capability(120)));
        });
    }

    /// <summary>
    /// LENGTH IS A SOFT SIGNAL AND NOTHING ELSE (backlog 273 deleted backlog 152's flat
    /// <c>0.12 * log10(cells/100)</c>, and nothing on the chunked axis brought a term of that size
    /// back). Sixty seconds of 60 WPM singing bolted onto an already saturated 123 second sustain is
    /// worth about a sixteenth of one percent (an eighth on the v21 grid), where the old term paid it
    /// a flat 0.02 of a star on top,
    /// and POSITIVE is the claim rather than non-negative: easy padding is worth a little rather than
    /// exactly nothing.
    /// </summary>
    [Test]
    public void DifficultyRating_PaddingAHardMapWithEasySingingAddsALittleAndNotNothing()
    {
        var sustain = envelopeSection(0, 123, envelopeWordsFor(123, 0.75));
        LyricLine[] padded = [sustain, envelopeEasySection(123000, 60)];

        double bare = LyricDifficulty.Compute([sustain]);
        double withPadding = LyricDifficulty.Compute(padded);

        Assert.Multiple(() =>
        {
            Assert.That(bare, Is.EqualTo(9.0960401994831059));
            Assert.That(withPadding, Is.EqualTo(9.1017823146088102));

            Assert.That(withPadding, Is.GreaterThan(bare), "easy padding is worth a little, never nothing");
            Assert.That(withPadding / bare - 1, Is.EqualTo(0.000631).Within(5e-6),
                "and a little means a sixteenth of a percent (0.1162% on the v21 grid, 0.0856% on the envelope)");
        });
    }

    /// <summary>
    /// A LONG INSTRUMENTAL GAP, and a property that changed sides. The old version of this test was
    /// called AMapWithALongInstrumentalGapIsRatedOnItsSingingAlone and its exact value pinned the
    /// envelope's <c>min(1, env/ratio_0)</c> CLAMP, which the chunked axis does not have (it has no
    /// envelope sweep at all).
    ///
    /// <para>ON THE SHIPPED AXIS THE TAIL IS NOT FREE. Bolting ten easy two-word lines onto the dense
    /// half, 45 seconds after it ends, LOWERS the rating: the gap and the easy tail are chunks like
    /// any other, and a decay-weighted mean over more chunks of which many are easy sits below the
    /// mean over the dense half alone. The envelope arm still has the opposite ordering, by a hair,
    /// and both are pinned so a failure says which model moved.</para>
    /// </summary>
    [Test]
    public void DifficultyRating_ALongInstrumentalGapIsNotFreeOnTheChunkedAxis()
    {
        LyricLine[] denseHalf = denseMap(lineCount: 10, wordsPerLine: 6, lineMs: 2000);
        LyricLine[] gapped = [.. denseHalf, .. gapTail()];

        Assert.Multiple(() =>
        {
            Assert.That(LyricDifficulty.Compute(gapped), Is.EqualTo(7.4303440228201012));
            Assert.That(LyricDifficulty.Compute(gapped, 1.50), Is.EqualTo(10.111577663275279), "sr_dt");
            Assert.That(LyricDifficulty.Compute(denseHalf), Is.EqualTo(7.7944375322526236), "the dense half alone");

            Assert.That(LyricDifficulty.Compute(gapped), Is.LessThan(LyricDifficulty.Compute(denseHalf)),
                "the chunked axis charges for the easy tail behind the gap");

            Assert.That(LyricDifficulty.Compute(gapped, 1, false, LyricDifficulty.EnduranceAxis.Envelope),
                Is.GreaterThan(LyricDifficulty.Compute(denseHalf, 1, false, LyricDifficulty.EnduranceAxis.Envelope)),
                "where the envelope arm reads the same pair the other way round");
        });
    }

    /// <summary>The easy half of the gap fixture: ten two-word lines starting 45 s after the dense half ends.</summary>
    private static LyricLine[] gapTail()
    {
        var shifted = denseMap(lineCount: 10, wordsPerLine: 2, lineMs: 2000);

        return
        [
            .. shifted.Select(l => new LyricLine
            {
                RawText = l.RawText,
                StartTime = l.StartTime + 65000,
                EndTime = l.EndTime + 65000,
                SingEndTime = l.SingEndTime + 65000,
                Units = [.. l.Units.Select(u => new TimedUnit { Text = u.Text, StartTime = u.StartTime + 65000, EndTime = u.EndTime + 65000 })],
            }),
        ];
    }

    /// <summary>The WPM the fastest humans sustain for <paramref name="seconds"/>, restated so the fixtures can be built to a ratio of it.</summary>
    private static double envelopeCapability(double seconds) => 220 + 221 * Math.Pow(1.35 / seconds, 0.35);

    /// <summary>Four-character words (five cells each with the space) to run at <paramref name="ratio"/> of record pace.</summary>
    private static int envelopeWordsFor(double seconds, double ratio) => (int)Math.Round(ratio * envelopeCapability(seconds) * (seconds / 60));

    /// <summary>One LINE of <paramref name="words"/> identical four-character words, evenly filling its span.</summary>
    private static LyricLine envelopeSection(double startMs, double seconds, int words)
    {
        double step = seconds * 1000.0 / words;
        var units = new TimedUnit[words];

        for (int i = 0; i < words; i++)
            units[i] = new TimedUnit { Text = "abcd", StartTime = startMs + i * step, EndTime = startMs + (i + 1) * step };

        return new LyricLine
        {
            RawText = string.Join(" ", units.Select(u => u.Text)),
            StartTime = startMs,
            EndTime = startMs + seconds * 1000,
            SingEndTime = startMs + seconds * 1000,
            Units = units,
        };
    }

    /// <summary>The same builder at an EASY 60 WPM, i.e. one five-cell word a second.</summary>
    private static LyricLine envelopeEasySection(double startMs, double seconds)
        => envelopeSection(startMs, seconds, Math.Max(1, (int)Math.Round(60.0 * seconds / 60)));

    /// <summary><paramref name="n"/> bursts of 1.5 s at ratio 0.75, spaced evenly through an easy 120 s map.</summary>
    private static LyricLine[] envelopeBurstMap(int n)
    {
        var lines = new List<LyricLine>();
        double t = 0;
        double easy = (120.0 - n * 1.5) / n;

        for (int k = 0; k < n; k++)
        {
            lines.Add(envelopeEasySection(t * 1000, easy));
            t += easy;
            lines.Add(envelopeSection(t * 1000, 1.5, envelopeWordsFor(1.5, 0.75)));
            t += 1.5;
        }

        return [.. lines];
    }

    /// <summary>
    /// A uniform map of varied words, mirroring the game's LyricDifficultyTest.buildMap so the two
    /// ports can be pinned against the same shape.
    /// </summary>
    private static LyricLine[] denseMap(int lineCount, int wordsPerLine, double lineMs)
    {
        string[] pool = ["flame", "river", "cider", "amber", "otter", "nudge", "vivid", "query", "zebra", "month", "proxy", "blitz"];
        var lines = new List<LyricLine>();
        double t = 0;
        int wordIndex = 0;

        for (int l = 0; l < lineCount; l++)
        {
            double wordMs = lineMs / wordsPerLine;
            var units = new TimedUnit[wordsPerLine];

            for (int w = 0; w < wordsPerLine; w++)
            {
                double ws = t + w * wordMs;
                units[w] = new TimedUnit { Text = pool[wordIndex++ % pool.Length], StartTime = ws, EndTime = ws + wordMs };
            }

            lines.Add(new LyricLine
            {
                RawText = string.Join(" ", units.Select(u => u.Text)),
                StartTime = t,
                EndTime = t + lineMs,
                SingEndTime = t + lineMs,
                Units = units,
            });

            t += lineMs;
        }

        return [.. lines];
    }

    [Test]
    public void MinimumLineWindow_GuardsDegenerateBoundaries()
    {
        // A 100 ms window clamps to the 500 ms floor:
        // 5 cells / (500 ms / 60000) = 600 CPM; WPM = 600 / 5 = 120.
        //
        // Unmoved by the WPM redefinition, and not by luck: "abcde" is 5 cells over 1 word, exactly
        // the 5 the unit assumes, which is the equality FiveCellWords_MakeTheNewWpmEqualTheOldOne
        // pins. Unmoved by v21 either, because the SAME floor guards both windows: the sung window is
        // 100 ms here and the boundary window is 100 ms here, so both clamp to 500 and the two
        // averages agree.
        var line = new LyricLine
        {
            RawText = "abcde",
            StartTime = 1000,
            EndTime = 1100,
            SingEndTime = 1100,
            Units = [new TimedUnit { Text = "abcde", StartTime = 1000, EndTime = 1100 }],
        };

        var pace = LyricPace.Compute([line]);

        Assert.Multiple(() =>
        {
            Assert.That(pace.AverageWpm, Is.EqualTo(120.0).Within(1e-9));
            Assert.That(pace.AverageCpm, Is.EqualTo(600.0).Within(1e-9));
            Assert.That(pace.LineAverageWpm, Is.EqualTo(120.0).Within(1e-9));
            Assert.That(pace.LineAverageCpm, Is.EqualTo(600.0).Within(1e-9));
        });
    }

    [Test]
    public void ParseSection_HeaderAndLines_RoundTrip()
    {
        var (header, lines) = LyricTiming.ParseSection(
        [
            """{"version":2,"song_end_ms":4000,"beatdrop_ms":800,"granularity":"Word"}""",
            """{"text":"ab cd","start_ms":1000,"end_ms":3000,"words":[{"text":"ab","start_ms":1000,"end_ms":2000,"score":1},{"text":"cd","start_ms":2000,"end_ms":3000,"score":1}]}""",
        ]);

        Assert.Multiple(() =>
        {
            Assert.That(header.SongEndMs, Is.EqualTo(4000));
            Assert.That(header.BeatdropMs, Is.EqualTo(800));
            Assert.That(lines, Has.Count.EqualTo(1));
            Assert.That(lines[0].RawText, Is.EqualTo("ab cd"));
            Assert.That(lines[0].StartTime, Is.EqualTo(1000));
            Assert.That(lines[0].EndTime, Is.EqualTo(4000));   // min(song_end, end_ms + 3000 tail)
            Assert.That(lines[0].SingEndTime, Is.EqualTo(3000));
        });

        // The parsed section reproduces the regression pace exactly.
        var pace = LyricPace.Compute(lines);
        Assert.That(pace.AverageWpm, Is.EqualTo(20.0).Within(1e-9));
    }

    [Test]
    public void ParseSection_BackingVocalOnlyLines_AreDropped_WhenStripping()
    {
        // The PRE-V2 read of a stored map (BeatmapPackageParser passes this for a file below
        // LiteralBracketsFromVersion): a bracket is a backing vocal, so a whole bracketed line
        // yields nothing to type and is dropped, and the previous line extends over its span.
        var (_, lines) = LyricTiming.ParseSection(
        [
            """{"version":2}""",
            """{"text":"(ooh aah)","start_ms":0,"end_ms":500}""",
            """{"text":"real line","start_ms":1000,"end_ms":2000}""",
        ], stripBackingVocals: true);

        Assert.Multiple(() =>
        {
            Assert.That(lines, Has.Count.EqualTo(1));
            Assert.That(lines[0].RawText, Is.EqualTo("real line"));
        });
    }

    [Test]
    public void ParseSection_BracketedLines_SurviveByDefault()
    {
        // The map-format contract since backlog 255, and the default: a bracket in a stored
        // [Lyrics] line is a literal lyric mark, so the line is kept with its brackets and the
        // previous line no longer swallows its span.
        var (_, lines) = LyricTiming.ParseSection(
        [
            """{"version":2}""",
            """{"text":"(ooh aah)","start_ms":0,"end_ms":500}""",
            """{"text":"real line","start_ms":1000,"end_ms":2000}""",
        ]);

        Assert.Multiple(() =>
        {
            Assert.That(lines.Select(l => l.RawText), Is.EqualTo(new[] { "(ooh aah)", "real line" }));

            // Kept in the AUTHOR'S form, deleted from the stream the player actually types.
            Assert.That(Typeability.ToDefaultStream(lines[0].RawText), Is.EqualTo("ooh aah"));
            Assert.That(lines[0].EndTime, Is.EqualTo(1000), "the next line's start is still the hard seal");
        });
    }

    [Test]
    public void ParseSection_MalformedJsonLines_AreSkipped()
    {
        var (_, lines) = LyricTiming.ParseSection(
        [
            "not json at all {{{",
            """{"text":"kept","start_ms":100,"end_ms":600}""",
        ]);

        Assert.That(lines, Has.Count.EqualTo(1));
    }

    [Test]
    public void ParseSection_NoWords_FallsBackToInterpolation()
    {
        // Line granularity: no words[] -> char-weighted interpolation over [start, singEnd]
        // (LrcParser.InterpolateUnits). "ab cd": weights 3+3 over [0,600] -> units [0,300],[300,600].
        var (_, lines) = LyricTiming.ParseSection(
        [
            """{"version":2,"song_end_ms":600}""",
            """{"text":"ab cd","start_ms":0,"end_ms":600}""",
        ]);

        Assert.Multiple(() =>
        {
            Assert.That(lines[0].Units, Has.Count.EqualTo(2));
            Assert.That(lines[0].Units[0].StartTime, Is.EqualTo(0).Within(1e-9));
            Assert.That(lines[0].Units[0].EndTime, Is.EqualTo(300).Within(1e-9));
            Assert.That(lines[0].Units[1].EndTime, Is.EqualTo(600).Within(1e-9));
        });
    }

    /// <summary>
    /// THE PAUSE CONTRACT on the server's parse alone (LyricPace v22; the cross-repo pin is
    /// WireCompat's LyricParserParityTest, which feeds the game's loader the same bytes). A word's
    /// rests are read from the <c>pauses</c> array, or from the older single <c>pause</c> object
    /// when there is no array, and kept only where <c>PausedWord.UsableRests</c> accepts them
    /// against the CLAMPED word.
    /// </summary>
    [Test]
    public void ParseSection_WordPauses_KeepOnlyTheRestsThatCutTheClampedWord()
    {
        var (_, lines) = LyricTiming.ParseSection(
        [
            """{"version":2,"song_end_ms":9000}""",
            """{"text":"together remember wonderful beautiful","start_ms":1000,"end_ms":4600,"words":[""" +
            // Two valid rests, a third missing its split, and an entry that is not an object.
            """{"text":"together","start_ms":1000,"end_ms":1900,"pauses":[{"start_ms":1250,"end_ms":1350,"split":2},{"start_ms":1550,"end_ms":1650,"split":5.0},{"start_ms":1750,"end_ms":1800},4]},""" +
            // Every rest ignored bar the first: no cell before the split, outside the word, inverted,
            // and one whose split contradicts the time order of the rest before it.
            """{"text":"remember","start_ms":1900,"end_ms":2800,"pauses":[{"start_ms":2000,"end_ms":2050,"split":5},{"start_ms":2100,"end_ms":2200,"split":0},{"start_ms":2600,"end_ms":2850,"split":4},{"start_ms":2500,"end_ms":2400,"split":3},{"start_ms":2150,"end_ms":2250,"split":3}]},""" +
            // The legacy single object.
            """{"text":"wonderful","start_ms":2800,"end_ms":3700,"pause":{"start_ms":3200,"end_ms":3300,"split":5}},""" +
            // A fractional split is dropped, and with an array present the object is never read.
            """{"text":"beautiful","start_ms":3700,"end_ms":4600,"pauses":[{"start_ms":3900,"end_ms":4000,"split":6.5}],"pause":{"start_ms":4200,"end_ms":4300,"split":3}}""" +
            """]}""",
        ]);

        var units = lines[0].Units;

        Assert.Multiple(() =>
        {
            Assert.That(units[0].Pauses, Is.EqualTo(new[] { new WordPause(1250, 1350, 2), new WordPause(1550, 1650, 5) }));
            Assert.That(units[1].Pauses, Is.EqualTo(new[] { new WordPause(2000, 2050, 5) }));
            Assert.That(units[2].Pauses, Is.EqualTo(new[] { new WordPause(3200, 3300, 5) }));
            Assert.That(units[3].Pauses, Is.Empty);
        });
    }

    /// <summary>
    /// A rest that fits the word's RAW span but not its CLAMPED one is dropped, exactly as a
    /// syllable boundary is: the word below overruns its line's hard end (the next line's start),
    /// is clamped back to it, and the rest past that point no longer sits inside the word.
    /// </summary>
    [Test]
    public void ParseSection_WordPauses_AreValidatedAgainstTheClampedWord()
    {
        var (_, lines) = LyricTiming.ParseSection(
        [
            """{"version":2,"song_end_ms":9000}""",
            """{"text":"forever","start_ms":1000,"end_ms":2000,"words":[{"text":"forever","start_ms":1000,"end_ms":2400,"pauses":[{"start_ms":1300,"end_ms":1400,"split":3},{"start_ms":2100,"end_ms":2200,"split":5}]}]}""",
            """{"text":"after","start_ms":2000,"end_ms":3000}""",
        ]);

        Assert.Multiple(() =>
        {
            Assert.That(lines[0].Units[0].EndTime, Is.EqualTo(2000), "the premise: the word is clamped to the next line's start");
            Assert.That(lines[0].Units[0].Pauses, Is.EqualTo(new[] { new WordPause(1300, 1400, 3) }));
        });
    }

    /// <summary>
    /// SYLLABLES ARE OBJECTS on the wire (<c>{text, start_ms, end_ms}</c>, what the game's encoder,
    /// SynthesizedTimingJson and the aligner all write), and each one's start inside the word
    /// becomes a boundary. Until LyricPace v22 this parse read bare NUMBERS, a shape no writer
    /// produces, so every stored subdivided map was rated here without its boundaries.
    /// </summary>
    [Test]
    public void ParseSection_SyllableObjects_BecomeBoundaries()
    {
        var (_, lines) = LyricTiming.ParseSection(
        [
            """{"version":2,"song_end_ms":9000}""",
            """{"text":"wonderful","start_ms":1000,"end_ms":1900,"words":[{"text":"wonderful","start_ms":1000,"end_ms":1900,"syllables":[{"text":"won","start_ms":1000,"end_ms":1300},{"text":"der","start_ms":1300,"end_ms":1600},{"text":"ful","start_ms":1600,"end_ms":1900},1450]}]}""",
        ]);

        Assert.That(lines[0].Units[0].SyllableBoundaries, Is.EqualTo(new[] { 1300.0, 1600.0 }),
            "the first syllable starts on the word and adds nothing, and a bare number is not a syllable");
    }

    [Test]
    public void Normalize_MatchesGameAuthority()
    {
        Assert.Multiple(() =>
        {
            // Diacritics stripped, whitespace collapsed, SUPPORTED punctuation kept: the stored
            // line is the author's form (Typeability.Normalize in the game).
            Assert.That(Typeability.Normalize("Héllo,  wörld!"), Is.EqualTo("Hello, world!"));
            Assert.That(Typeability.Normalize("don’t stop"), Is.EqualTo("don't stop"));

            // The marks backlog 202 added survive normalization like any other supported mark
            // (they used to vanish here), while chars still outside the set vanish outright.
            Assert.That(Typeability.Normalize("a*b/c"), Is.EqualTo("a*b/c"));
            Assert.That(Typeability.Normalize("50% of $9 x^2 <hey>"), Is.EqualTo("50% of $9 x^2 <hey>"));
            // '_' and '~' joined the set in backlog 255, so they survive here now; what is left
            // outside it still vanishes outright.
            Assert.That(Typeability.Normalize("a#b@c_d~e"), Is.EqualTo("abc_d~e"));
            Assert.That(Typeability.Normalize("a#b@c`d"), Is.EqualTo("abcd"));

            // Brackets are supported marks too, and Normalize has never been the thing that
            // removed a backing vocal: that is StripBackingVocals, which still exists and is still
            // exactly what the pre-v2 read of a stored map composes in front of it.
            Assert.That(Typeability.Normalize("hey (oh) now"), Is.EqualTo("hey (oh) now"));
            Assert.That(Typeability.StripBackingVocals("go (ooh) now [aah]"), Is.EqualTo("go  now "));
            Assert.That(Typeability.Normalize(Typeability.StripBackingVocals("(all backing)")), Is.Empty);
        });
    }

    [Test]
    public void ToDefaultStream_MatchesGameAuthority()
    {
        Assert.Multiple(() =>
        {
            // Golden strings shared with the game's LiteratePunctuationTest: the normative example,
            // and the rules around it (Typeability.ToDefaultStream).
            Assert.That(Typeability.ToDefaultStream("The bad-cat sat."), Is.EqualTo("the bad cat sat"));
            Assert.That(Typeability.ToDefaultStream("don't stop"), Is.EqualTo("dont stop"));
            Assert.That(Typeability.ToDefaultStream("Hello, world!"), Is.EqualTo("hello world"));

            // Every mark except the hyphen simply disappears; the hyphen is a WORD BREAK.
            Assert.That(Typeability.ToDefaultStream("a,b.c'd?e!f;g:h(i)j[k]l\"m"), Is.EqualTo("abcdefghijklm"));
            Assert.That(Typeability.ToDefaultStream("a-b"), Is.EqualTo("a b"));

            // A hyphen next to a space collapses the run; at either edge it separates nothing.
            Assert.That(Typeability.ToDefaultStream("a - b"), Is.EqualTo("a b"));
            Assert.That(Typeability.ToDefaultStream("-a-"), Is.EqualTo("a"));

            // Stronger than idempotence, and the reason no stored row moves: for any line with no
            // hyphen and no mark, which is every line the game's encoder ever wrote, the derivation
            // is exactly ToLowerInvariant.
            foreach (string s in new[] { "the bad cat sat", "me & you", "Neon SKYLINE glowing", "" })
                Assert.That(Typeability.ToDefaultStream(s), Is.EqualTo(s.ToLowerInvariant()));
        });
    }

    private static LyricLine paceLine(string text) => new LyricLine
    {
        RawText = text,
        StartTime = 0,
        EndTime = 3000,
        SingEndTime = 3000,
        Units = [new TimedUnit { Text = text, StartTime = 0, EndTime = 3000 }],
    };

    [Test]
    public void Pace_MeasuresTheDefaultStreamNotTheAuthoredLine()
    {
        // "The bad-cat sat." is 3 authored tokens but 4 words / 15 cells in the stream the player
        // types, and the pace has to describe the play everyone shares.
        var punctuated = LyricPace.Compute([paceLine("The bad-cat sat.")]);

        Assert.Multiple(() =>
        {
            Assert.That(punctuated.WordCount, Is.EqualTo(4));
            Assert.That(punctuated.TypeableCellCount, Is.EqualTo("the bad cat sat".Length));

            // A mark-free line counts exactly as it always did (the v8 rows cannot move).
            var plain = LyricPace.Compute([paceLine("The bad cat sat")]);

            Assert.That(plain.WordCount, Is.EqualTo(4));
            Assert.That(plain.TypeableCellCount, Is.EqualTo(15));
        });
    }

    [Test]
    public void Typeability_FreestyleMarkerIsACellButNeverTypeable()
    {
        Assert.Multiple(() =>
        {
            // The marker stays outside the typeable surface (nothing can be typed to produce it)
            // and outside default normalization; it is still a CELL.
            Assert.That(Typeability.IsTypeable(Typeability.FREESTYLE_MARKER), Is.False);
            Assert.That(Typeability.IsFreestyle(Typeability.FREESTYLE_MARKER), Is.True);
            Assert.That(Typeability.IsCell(Typeability.FREESTYLE_MARKER), Is.True);

            // Golden strings shared with the browser core's harness (FreestyleParityTest) and the
            // game's FreestyleCharTest: opted out, the markers vanish (the ampersand is NOT one of
            // the supported marks, so it is still stripped outright).
            Assert.That(Typeability.Normalize("R&B rock & roll"), Is.EqualTo("RB rock roll"));
            Assert.That(Typeability.Normalize("R&B rock & roll", keepFreestyleMarkers: true), Is.EqualTo("R&B rock & roll"));
            Assert.That(Typeability.Normalize("  hey,   &you!  ", keepFreestyleMarkers: true), Is.EqualTo("hey, &you!"));
            Assert.That(Typeability.ToDefaultStream("hey, &you!"), Is.EqualTo("hey &you"));

            // Typeability.TypeableCount, the SCORING side's count, still follows IsCell, so a kept
            // marker is a cell there. The PACE parted company with it at v21 and follows IsTypeable
            // instead (see FreestyleSlot_IsNoLongerACellInThePace), which is why these two counts are
            // asserted here and not through LyricPace: a keypress the engine judges is not the same
            // thing as a speed the map can ask for.
            Assert.That(Typeability.TypeableCount("a&b"), Is.EqualTo(3));
            Assert.That(Typeability.TypeableCount("ab cd"), Is.EqualTo(5));
        });
    }

    [Test]
    public void ParseSection_FlaggedLine_KeepsTheSlotOutOfTheCellCount()
    {
        // Same fixture the browser core's harness builds ("me & you" flagged, explicit words).
        var (_, lines) = LyricTiming.ParseSection(
        [
            """{"granularity":"word","version":2,"song_end_ms":20000}""",
            """{"text":"me & you","start_ms":1000,"end_ms":4000,"freestyle":true,"words":[{"text":"me","start_ms":1000,"end_ms":2000},{"text":"&","start_ms":2000,"end_ms":3000},{"text":"you","start_ms":3000,"end_ms":4000}]}""",
        ]);

        var pace = LyricPace.Compute(lines);

        Assert.Multiple(() =>
        {
            Assert.That(lines, Has.Count.EqualTo(1));
            Assert.That(lines[0].RawText, Is.EqualTo("me & you"));

            // The token count survived, so the explicit word times align one-to-one.
            Assert.That(lines[0].Units, Has.Count.EqualTo(3));
            Assert.That(lines[0].Units[1].Text, Is.EqualTo("&"));
            Assert.That(lines[0].Units[1].StartTime, Is.EqualTo(2000));

            // m e _ & _ y o u: 6 letters and 2 inter-word spaces, and since v21 the slot itself is
            // NOT among them, so this reads 7 where it read 8 from v6 to v20. The slot is published
            // separately instead, as an addition to the count rather than a subset of it, and the
            // middle token is no longer a WORD either: it has no typeable character in it.
            Assert.That(pace.TypeableCellCount, Is.EqualTo(7));
            Assert.That(pace.WordCount, Is.EqualTo(2));
            Assert.That(pace.FreestyleCellCount, Is.EqualTo(1));

            // Sung window 4000 - 1000 = 3000 ms (the 3000 ms tail out to the line's 7000 ms boundary
            // is a break and is dropped whole): 7 cells / 0.05 min = 140 CPM, WPM = 140/5 = 28.
            Assert.That(pace.AverageCpm, Is.EqualTo(140.0).Within(1e-9));
            Assert.That(pace.AverageWpm, Is.EqualTo(28.0).Within(1e-9));
        });
    }

    [Test]
    public void ParseSection_UnflaggedAmpersand_StaysLyricPunctuation()
    {
        // Back-compat pin: a line whose lyrics genuinely contain "&" ingests exactly as it always
        // did, marker stripped, no extra cell.
        var (_, lines) = LyricTiming.ParseSection(
        [
            """{"version":2,"song_end_ms":20000}""",
            """{"text":"me & you","start_ms":1000,"end_ms":4000}""",
        ]);

        var pace = LyricPace.Compute(lines);

        Assert.Multiple(() =>
        {
            Assert.That(lines[0].RawText, Is.EqualTo("me you"));
            Assert.That(pace.TypeableCellCount, Is.EqualTo(6));
            Assert.That(pace.WordCount, Is.EqualTo(2));
            Assert.That(pace.FreestyleCellCount, Is.Zero);
            // Sung window 3000 ms (the tail out to 7000 is a break): 6 cells / 0.05 min = 120 CPM,
            // WPM = 120/5 = 24. The CELL AND WORD COUNTS are what this back-compat pin is actually
            // about, and neither moved at v21 either: there is no slot here to stop counting.
            Assert.That(pace.AverageCpm, Is.EqualTo(120.0).Within(1e-9));
            Assert.That(pace.AverageWpm, Is.EqualTo(24.0).Within(1e-9));
        });

        // "freestyle" must be strictly true; anything else is the legacy path.
        var (_, truthy) = LyricTiming.ParseSection(
        [
            """{"version":2,"song_end_ms":20000}""",
            """{"text":"me & you","start_ms":1000,"end_ms":4000,"freestyle":1}""",
            """{"text":"you & me","start_ms":5000,"end_ms":6000,"freestyle":"true"}""",
        ]);

        Assert.That(truthy.Select(l => l.RawText), Is.EqualTo(new[] { "me you", "you me" }));
    }

    [Test]
    public void InterpolatedUnits_WeightFreestyleSlotsLikeLetters()
    {
        // No words[], so the line falls back to char-weighted interpolation. Weights are
        // (cells + 1): "a&b" -> 4, "c" -> 2, total 6 over a 600 ms span -> the split lands at 400.
        var (_, lines) = LyricTiming.ParseSection(
        [
            """{"version":2,"song_end_ms":600}""",
            """{"text":"a&b c","start_ms":0,"end_ms":600,"freestyle":true}""",
        ]);

        Assert.Multiple(() =>
        {
            Assert.That(lines[0].Units, Has.Count.EqualTo(2));
            Assert.That(lines[0].Units[0].EndTime, Is.EqualTo(400).Within(1e-9)); // 360 if the slot were dropped
            Assert.That(lines[0].Units[1].EndTime, Is.EqualTo(600).Within(1e-9));
        });
    }

    /// <summary>
    /// THE SPLIT BETWEEN THE TWO SIDES OF A SLOT, and it reversed at v21. A freestyle slot used to be
    /// a keypress the pace counted whole (v6) and, from backlog 211, a quarter cell to the star
    /// model; the pace no longer counts it at all, on the argument that no map can ask for a
    /// particular SPEED in a slot that takes any key. The rating still prices it, because a slot is a
    /// cell with a real deadline and merely no letter to find.
    ///
    /// <para>So the two maps below are now indistinguishable to every pace figure and still separable
    /// by the rating, which is the cleanest statement of the split there is: identical timings,
    /// identical cell counts, identical CPM, and only the slot count and the stars tell them
    /// apart.</para>
    /// </summary>
    [Test]
    public void FreestyleSlot_IsNoLongerACellInThePace_AndStillRaisesTheRating()
    {
        // Three six-letter words over 1.4 s. Backlog 269 measures a window's PACE, so the map has to
        // be fast enough to register against S(t), and since v22 it also has to clear the
        // 16-character floor or both maps rate exactly zero and the comparison below proves
        // nothing: 18 letters and 2 spaces is 20 cells, where the single "abcdef" it used to be is 6.
        static LyricLine line(string first) => new()
        {
            RawText = first + " ghijkl mnopqr",
            StartTime = 1000,
            EndTime = 5000,
            SingEndTime = 2400,
            Units =
            [
                new TimedUnit { Text = first, StartTime = 1000, EndTime = 1467 },
                new TimedUnit { Text = "ghijkl", StartTime = 1467, EndTime = 1933 },
                new TimedUnit { Text = "mnopqr", StartTime = 1933, EndTime = 2400 },
            ],
        };

        var freestyle = line("abc&def");
        var plain = line("abcdef");

        var freePace = LyricPace.Compute([freestyle]);
        var plainPace = LyricPace.Compute([plain]);

        Assert.Multiple(() =>
        {
            // TWENTY either way, where the freestyle map read one more from v6 to v20.
            Assert.That(freePace.TypeableCellCount, Is.EqualTo(20));
            Assert.That(plainPace.TypeableCellCount, Is.EqualTo(20));
            Assert.That(freePace.AverageCpm, Is.EqualTo(plainPace.AverageCpm).Within(1e-12), "no pace figure can tell the two maps apart");

            // And the slot is still published, as an addition to that count rather than a subset.
            Assert.That(freePace.FreestyleCellCount, Is.EqualTo(1), "the map still says it holds a slot");
            Assert.That(plainPace.FreestyleCellCount, Is.Zero, "and a map with no markers says so");

            Assert.That(plainPace.DifficultyRating, Is.GreaterThan(0), "the premise: the map clears the character floor");
            Assert.That(freePace.DifficultyRating, Is.GreaterThan(plainPace.DifficultyRating),
                "the slot used to be free to the rating too, which is what backlog 211 fixed");
        });
    }

    #region Freestyle slots, priced at a quarter (backlog 211)

    private const char marker = Typeability.FREESTYLE_MARKER;

    /// <summary>
    /// The model's own weighted CELL MASS for a map: every typeable character, every inter-word
    /// space, and every freestyle slot at its quarter. Both arms build the same word list, so the
    /// figure is the same on either and the envelope arm is asked for only because it is the one
    /// whose <c>Cells</c> readout the sandbox documents. Mirrors the game's CellMass helper.
    /// </summary>
    private static double cellMass(LyricLine[] lines, bool literate = false)
        => LyricDifficulty.ComputeDetail(lines, 1, literate, LyricDifficulty.EnduranceAxis.Envelope).Cells;

    /// <summary>
    /// THE BROAD REGRESSION PIN. Every value here is an exact double rather than a tolerance, so any
    /// change to the model at all has to come through this test and be argued for. It also carries
    /// the backlog 211 claim it was written for: none of these fixtures holds a single freestyle
    /// marker, and the freestyle weight is a cell COUNT, so all of them are bit-identical whatever
    /// that weight is set to.
    ///
    /// <para>The values themselves were re-taken at v22 (PR 2's overlapping chunk profile and
    /// sandbox dials) and are the SHIPPED (chunked) readings; on the v21 chunked grid the same six
    /// fixtures read 22.0419 / 31.9733 / 5.6213 / 7.4381 / 9.6646 / 13.9120. The claim did not move,
    /// only the baseline.</para>
    /// </summary>
    [Test]
    public void DifficultyRating_AMapWithNoFreestyleSlotsRatesBitIdenticallyToBeforeTheyWerePriced()
    {
        var big = denseMap(lineCount: 40, wordsPerLine: 8, lineMs: 1200);
        var realistic = denseMap(lineCount: 40, wordsPerLine: 4, lineMs: 2400);
        var mid = denseMap(lineCount: 12, wordsPerLine: 6, lineMs: 1800);

        Assert.Multiple(() =>
        {
            Assert.That(LyricDifficulty.Compute(big), Is.EqualTo(21.082086838738505));
            Assert.That(LyricDifficulty.Compute(big, 1.50), Is.EqualTo(30.949092598971315));
            Assert.That(LyricDifficulty.Compute(realistic), Is.EqualTo(5.1650041718866406));
            Assert.That(LyricDifficulty.Compute(mid, 0.75), Is.EqualTo(6.8819518298858675));
            Assert.That(LyricDifficulty.Compute(mid), Is.EqualTo(8.7299529590602383));
            Assert.That(LyricDifficulty.Compute(mid, 1.50), Is.EqualTo(12.79553147510407));
            // No mark and no capital in the pool, so the Literate stream is the same stream.
            Assert.That(LyricDifficulty.Compute(mid, 1, literate: true), Is.EqualTo(8.7299529590602383));
        });
    }

    /// <summary>
    /// THE PRICE, stated as an exact identity rather than as an inequality (the game's
    /// FourFreestyleSlotsWeighExactlyOneCell, ported): FOUR freestyle slots weigh exactly ONE
    /// ordinary cell, so a map of "a&amp;&amp;&amp;&amp;," words must carry BIT-identically the same
    /// cell mass as the same map written "ab,".
    ///
    /// <para>IT IS THE CELL MASS AND NO LONGER THE STARS. Until the rework the two maps rated
    /// bit-identically as well, and this test asserted that; the shipped axis reads more about a cell
    /// than its weight (a slot is a STRETCH cell in the rhythm arm's press intervals, so the two maps
    /// present different press streams), and the ratings come out a few percent apart. The quarter
    /// itself is unchanged, which is exactly what the surviving equality says: price a slot at
    /// anything else and these two numbers separate.</para>
    ///
    /// <para>Everything else about the pair is held equal BY CONSTRUCTION: the two maps occupy the
    /// same timeline word for word and they are cut into lines at the same places, so they carry the
    /// same inter-word spaces and the same 170 priced cells plain (230 under Literate). Two spacings,
    /// because the model reads a word's SPAN as well as its onset: LOOSE (400 ms step, 350 ms span)
    /// leaves a gap between words, TIGHT (80 ms step, 60 ms span) puts several words inside a single
    /// 50 ms timeline bin, which is where the uniform spread and the partial-bin proration actually
    /// do something.</para>
    /// </summary>
    [TestCase(400, 350, false, TestName = "AFreestyleSlotIsExactlyAQuarterCell(loose, plain)")]
    [TestCase(400, 350, true, TestName = "AFreestyleSlotIsExactlyAQuarterCell(loose, literate)")]
    [TestCase(80, 60, false, TestName = "AFreestyleSlotIsExactlyAQuarterCell(tight bins, plain)")]
    [TestCase(80, 60, true, TestName = "AFreestyleSlotIsExactlyAQuarterCell(tight bins, literate)")]
    public void FourFreestyleSlotsWeighExactlyOneCell(double stepMs, double spanMs, bool literate)
    {
        // "a&&&&," : one fixed key (two under Literate, the mark) plus four quarter-cells.
        var free = uniformMap(tokens(60, i => letters(i, 1) + new string(marker, 4) + ","), wordsPerLine: 6, stepMs, spanMs);
        // "ab," : the same weight written entirely in fixed keys.
        var full = uniformMap(tokens(60, i => letters(i, 2) + ","), wordsPerLine: 6, stepMs, spanMs);

        Assert.Multiple(() =>
        {
            Assert.That(cellMass(free, literate), Is.EqualTo(cellMass(full, literate)), "four quarter-cells have to weigh exactly one whole one");

            // And the thing that used to be equal here, named rather than quietly dropped: the two
            // maps no longer RATE the same, because the shipped axis can see that one of those cells
            // takes any key.
            Assert.That(LyricDifficulty.Compute(free, 1, literate), Is.Not.EqualTo(LyricDifficulty.Compute(full, 1, literate)));
        });
    }

    /// <summary>
    /// And a quarter is BETWEEN the two prices it could have had, which is the decision itself: the
    /// slots used to be worth nothing (a freestyle section was an accuracy and combo farm the rating
    /// could not see) and they are not worth a whole cell either, since there is no letter to find.
    /// The "excluded" map is not an approximation of the old behaviour, it IS the old number: the
    /// pre-211 code stripped every marker before measuring anything. Unchanged by the rework: this
    /// pair of inequalities holds on the shipped axis exactly as it held on the envelope.
    /// </summary>
    [TestCase(false)]
    [TestCase(true)]
    public void FreestyleRatesAboveTheOldFreePriceAndBelowAFullCell(bool literate)
    {
        var excluded = uniformMap(tokens(60, i => letters(i, 1) + ","), wordsPerLine: 6, stepMs: 400, spanMs: 350);
        var freestyle = uniformMap(tokens(60, i => letters(i, 1) + new string(marker, 4) + ","), wordsPerLine: 6, stepMs: 400, spanMs: 350);
        var fixedKeys = uniformMap(tokens(60, i => letters(i, 5) + ","), wordsPerLine: 6, stepMs: 400, spanMs: 350);

        double freestyleSr = LyricDifficulty.Compute(freestyle, 1, literate);

        Assert.Multiple(() =>
        {
            Assert.That(freestyleSr, Is.GreaterThan(LyricDifficulty.Compute(excluded, 1, literate)), "pricing the slots has to raise the rating");
            Assert.That(freestyleSr, Is.LessThan(LyricDifficulty.Compute(fixedKeys, 1, literate)), "a slot is not a letter");
        });
    }

    /// <summary>
    /// A word of NOTHING BUT slots is a word. It used to be dropped from the map outright (its
    /// stream was empty, so it never became a word at all), which is how a whole mashable freestyle
    /// section could rate exactly 0.00: this fixture is that section, and it now weighs exactly what
    /// the same map of one-key words weighs, four slots to the cell.
    ///
    /// <para>The claim is stated on the CELL MASS for the reason
    /// <see cref="FourFreestyleSlotsWeighExactlyOneCell"/> gives: the two maps rated identically on
    /// the envelope and no longer do on the shipped axis, which reads how a word's timing is
    /// subdivided as well as what it weighs. The mashed map rates ABOVE its one-key twin there
    /// (2.258 against 2.130), which is a statement about press intervals and not about the
    /// quarter.</para>
    /// </summary>
    [Test]
    public void AWordOfNothingButFreestyleSlotsIsStillAWord()
    {
        var mashed = uniformMap(tokens(60, _ => new string(marker, 4)), wordsPerLine: 6, stepMs: 400, spanMs: 350);
        var oneKeyWords = uniformMap(tokens(60, _ => "a"), wordsPerLine: 6, stepMs: 400, spanMs: 350);

        Assert.Multiple(() =>
        {
            Assert.That(cellMass(mashed), Is.GreaterThan(0), "before 211 this map had no words in it at all");
            Assert.That(cellMass(mashed), Is.EqualTo(cellMass(oneKeyWords)), "a mashable word weighs what the same run of fixed keys weighs");
            Assert.That(LyricDifficulty.Compute(mashed), Is.GreaterThan(0), "and it rates something on the shipped axis too");
        });
    }

    /// <summary>
    /// A map of UNIFORM words (the game's helper of the same name): every token gets the same span
    /// and the same step from the last, laid end to end and cut into lines. Uniform is what makes
    /// the fixtures above exact: two maps built this way occupy an identical timeline, so they
    /// differ in NOTHING but what their tokens weigh.
    /// </summary>
    private static LyricLine[] uniformMap(string[] words, int wordsPerLine, double stepMs, double spanMs)
    {
        var lines = new List<LyricLine>();
        double t = 0;

        for (int i = 0; i < words.Length; i += wordsPerLine)
        {
            int count = Math.Min(wordsPerLine, words.Length - i);
            var units = new TimedUnit[count];

            for (int w = 0; w < count; w++)
            {
                double ws = t + w * stepMs;
                units[w] = new TimedUnit { Text = words[i + w], StartTime = ws, EndTime = ws + spanMs };
            }

            lines.Add(new LyricLine
            {
                RawText = string.Join(" ", units.Select(u => u.Text)),
                StartTime = t,
                EndTime = t + count * stepMs,
                SingEndTime = t + count * stepMs,
                Units = units,
            });

            t += count * stepMs;
        }

        return [.. lines];
    }

    private const string alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";

    /// <summary>
    /// <paramref name="count"/> tokens built from <paramref name="shape"/>, which is handed the
    /// word's index and takes its letters from <see cref="alphabet"/>.
    /// </summary>
    private static string[] tokens(int count, Func<int, string> shape) => Enumerable.Range(0, count).Select(shape).ToArray();

    private static string letters(int i, int n)
    {
        var sb = new System.Text.StringBuilder(n);

        for (int k = 0; k < n; k++)
            sb.Append(alphabet[(i + k) % alphabet.Length]);

        return sb.ToString();
    }

    #endregion

    /// <summary>
    /// Optional integration anchor: a real lyriclab timing.json from the local map sources
    /// (read-only). Ignored when the assets are not on this machine.
    /// </summary>
    [Test]
    public void RealTimingJson_ProducesFinitePlausiblePace()
    {
        const string path = @"C:\Users\Mingda\Documents\type!beat\maps\Siames - The Wolf\timing.json";

        if (!File.Exists(path))
            Assert.Ignore("Real map source assets not present on this machine.");

        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        Assert.That(root.GetProperty("version").GetInt32(), Is.EqualTo(LyricTiming.SUPPORTED_VERSION));

        double? songEnd = root.TryGetProperty("song_end_ms", out var se) && se.ValueKind == System.Text.Json.JsonValueKind.Number
            ? se.GetDouble()
            : null;

        var raw = new List<LyricTiming.RawLine>();

        foreach (var el in root.GetProperty("lines").EnumerateArray())
        {
            if (LyricTiming.TryParseRawLine(el, out var line))
                raw.Add(line);
        }

        var lines = LyricTiming.BuildLines(raw, songEnd);
        var pace = LyricPace.Compute(lines);

        TestContext.WriteLine($"Real map -> WPM {pace.AverageWpm:0.0} (line mean {pace.LineAverageWpm:0.0}), CPM {pace.AverageCpm:0.0}, target {pace.TargetWpm:0.0}, chars/word {pace.AverageCharsPerWord:0.00}, stars {pace.DifficultyRating:0.00}");

        Assert.Multiple(() =>
        {
            Assert.That(lines, Is.Not.Empty);
            Assert.That(pace.TypeableCellCount, Is.GreaterThan(100));
            // A real song sits in a sane human WPM band (and stars stay on the 0..10 scale).
            Assert.That(pace.AverageWpm, Is.InRange(10, 400));
            Assert.That(pace.LineAverageWpm, Is.InRange(10, 400));
            Assert.That(pace.DifficultyRating, Is.InRange(0.1, 10));
            // The published target is the map's hardest window, floored at its own average, so it can
            // never read under the figure above it.
            Assert.That(pace.TargetWpm, Is.GreaterThanOrEqualTo(pace.AverageWpm));
            // And a real English lyric averages a bit under the 5 cells the unit assumes: the five
            // shipped maps measure 4.11 to 4.57, which is why the set page prints ONE DECIMAL and
            // not a whole number (every one of them would round to "4").
            Assert.That(pace.AverageCharsPerWord, Is.InRange(3.0, 7.0));
        });
    }
}
