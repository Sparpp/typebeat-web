using Typebeat.Web.Packages.Lyrics;

namespace Typebeat.Web.Tests;

/// <summary>
/// The rolling-window pace curve against known values. Ported alongside
/// <see cref="LyricWpmCurve"/> itself from the game's own regression test (typebeat-osu
/// typebeat.Game.Rulesets.TypeBeat.Tests/NonVisual/LyricWpmCurveTest.cs), including the two
/// hand-computed anchors (600 CPM / 120 WPM for a single 30-char word, and the halved window span
/// the inter-word space cells produce), so the two ports are pinned to the same numbers the way
/// <see cref="LyricPace"/>'s "cat cat" anchor pins the star rating.
/// </summary>
public class LyricWpmCurveTest
{
    /// <summary>A single-token line whose one unit spans the whole line.</summary>
    private static LyricLine singleWordLine(string text, double start, double end) => new()
    {
        RawText = text,
        StartTime = start,
        EndTime = end,
        SingEndTime = end,
        Units = [new TimedUnit { Text = text, StartTime = start, EndTime = end }],
    };

    /// <summary>
    /// <paramref name="count"/> one-character words, word m sung over
    /// [start + m*step, start + (m+1)*step].
    /// </summary>
    private static LyricLine evenWordsLine(int count, double start, double step)
    {
        var units = new List<TimedUnit>();

        for (int m = 0; m < count; m++)
            units.Add(new TimedUnit { Text = "a", StartTime = start + m * step, EndTime = start + (m + 1) * step });

        return new LyricLine
        {
            RawText = string.Join(' ', Enumerable.Repeat("a", count)),
            StartTime = start,
            EndTime = start + count * step,
            SingEndTime = start + count * step,
            Units = units,
        };
    }

    [Test]
    public void SingleThirtyCharacterWordIsHandComputable()
    {
        // One 30-char word over [0, 3000]: k = 30, so typeable char j targets
        // 0 + j*3000/30 = 100j. That is exactly 30 cells at 0, 100, ... 2900, with no
        // inter-word space cell (there is only one token), so the map holds one window.
        //
        //   spanMs = 2900 - 0 = 2900
        //   cpm    = 29 / (2900/60000) = 29 * 60000 / 2900   = 600
        //   wpm    = 600 / 5                                 = 120
        //
        // The 30-character word does NOT make this window worth one word: under the typing-test
        // convention a word is 5 keystrokes whatever the text says, so the map's own word length is
        // reported separately (LyricPace.PaceStatistics.AverageCharsPerWord) instead of being baked
        // into the WPM. The real-word convention this replaced said 29/30 of a word over the same
        // span, i.e. 20 WPM, a number the HUD could never have shown.
        var curve = LyricWpmCurve.Compute([singleWordLine(new string('a', 30), 0, 3000)]);

        Assert.Multiple(() =>
        {
            Assert.That(curve.IsEmpty, Is.False);
            Assert.That(curve.PeakCpm, Is.EqualTo(600.0).Within(1e-9));
            Assert.That(curve.PeakWpm, Is.EqualTo(120.0).Within(1e-9));
            Assert.That(curve.StartTime, Is.EqualTo(0).Within(1e-9));
            Assert.That(curve.EndTime, Is.EqualTo(2900).Within(1e-9));

            // The single window starts at the map's first cell, so it lands in bucket 0.
            Assert.That(curve.Curve, Has.Count.EqualTo(LyricWpmCurve.DEFAULT_CURVE_POINTS));
            Assert.That(curve.Curve[0], Is.EqualTo(120.0).Within(1e-9));
            Assert.That(curve.Curve.Skip(1).Max(), Is.EqualTo(0.0).Within(1e-9));
        });
    }

    [Test]
    public void InterWordSpaceCellsHalveTheWindowSpan()
    {
        // 30 one-char words 100 ms apart. The flattening interleaves an inter-word space cell at
        // each unit end, so the map holds 30 chars + 29 spaces = 59 cells whose targets run
        // 0, 100, 100, 200, 200, ... 2800, 2900, 2900: cell i is char m at 100m for even i = 2m,
        // and the space after word m at 100(m+1) for odd i = 2m+1.
        //
        //   even-start window: span 1500 ms, cpm 29*60000/1500 = 1160,        wpm 232
        //   odd-start  window: span 1400 ms, cpm 29*60000/1400 = 1242.857..., wpm 248.571...
        //
        // The odd-start window wins BOTH now. Under the old real-word convention the even and odd
        // windows tied on WPM (15 whole words vs 14 words and 2 halves) while the odd one won on
        // CPM, so the two peaks sat in different windows and had to be maximised independently.
        // Every cell being worth 1/5 of a word removes that possibility: the peaks are
        // proportional, and the assertion below is the whole point of this fixture.
        var curve = LyricWpmCurve.Compute([evenWordsLine(30, 0, 100)]);

        Assert.Multiple(() =>
        {
            Assert.That(curve.PeakCpm, Is.EqualTo(1740000.0 / 1400.0).Within(1e-9));
            Assert.That(curve.PeakWpm, Is.EqualTo(1740000.0 / 1400.0 / 5.0).Within(1e-9));
            Assert.That(curve.PeakWpm, Is.EqualTo(248.571428571428).Within(1e-9));
            Assert.That(curve.StartTime, Is.EqualTo(0).Within(1e-9));
            Assert.That(curve.EndTime, Is.EqualTo(2900).Within(1e-9));
        });
    }

    [Test]
    public void PeakWpmIsPeakCpmOverFive()
    {
        // The curve carries its own copy of the 5 so that it stays mirrorable with no dependencies
        // (see the constant's doc), which means nothing but this stops the copy drifting from the
        // map averages' one. The third copy, the browser core's literal in its live WPM counter, is
        // not reachable from here without standing up a whole run.
        Assert.That(LyricWpmCurve.CHARS_PER_WORD, Is.EqualTo(LyricPace.CHARS_PER_WORD));

        // Holds for every map, whatever its word lengths, which is what makes the in-game HUD's
        // rolling counter and this map figure the same quantity: both average over 30 presses, both
        // divide characters by 5.
        foreach (var curve in new[]
                 {
                     LyricWpmCurve.Compute([evenWordsLine(40, 0, 100)]),
                     LyricWpmCurve.Compute([singleWordLine(new string('a', 60), 0, 3000)]),
                     LyricWpmCurve.Compute([evenWordsLine(40, 0, 200), evenWordsLine(40, 8000, 60)]),
                 })
        {
            Assert.That(curve.IsEmpty, Is.False);
            Assert.That(curve.PeakWpm, Is.EqualTo(curve.PeakCpm / 5.0).Within(1e-9));
        }
    }

    /// <summary>
    /// A 30-character word over [0, 3000] (cells at 0, 100, ... 2900) followed by
    /// <paramref name="tails"/> one-character lines, each contributing exactly one cell at its own
    /// instant. That makes every window's span hand-computable: window i spans
    /// targets[i + 29] - targets[i], so window 0 is the word itself and window i &gt; 0 reaches from
    /// the word's cell i to tail cell i - 1.
    /// </summary>
    private static IReadOnlyList<LyricLine> wordThenTails(params double[] tails)
    {
        var lines = new List<LyricLine> { singleWordLine(new string('a', 30), 0, 3000) };

        foreach (double t in tails)
            lines.Add(singleWordLine("a", t, t));

        return lines;
    }

    [Test]
    public void TargetWpmIsTheEightiethPercentileOfTheWindowReadings()
    {
        // Five windows, hand-computed. cpm = 29 * 60000 / span = 1740000 / span, wpm = cpm / 5:
        //
        //   w0: 2900 - 0     =  2900 -> 600 cpm -> 120 wpm
        //   w1: 5900 - 100   =  5800 -> 300 cpm ->  60 wpm
        //   w2: 8900 - 200   =  8700 -> 200 cpm ->  40 wpm
        //   w3: 11900 - 300  = 11600 -> 150 cpm ->  30 wpm
        //   w4: 17800 - 400  = 17400 -> 100 cpm ->  20 wpm
        //
        // Sorted ascending that is [20, 30, 40, 60, 120] with n = 5, and the nearest-rank index is
        // floor(0.8 * 4 + 0.5) = floor(3.7) = 3, i.e. 60. Same fixture and same numbers as the
        // game's own LyricWpmCurveTest, which is the point of porting it.
        var lines = wordThenTails(5900, 8900, 11900, 17800);
        var curve = LyricWpmCurve.Compute(lines);

        // Non-vacuity: the three figures a map advertises have to be three DIFFERENT numbers here,
        // or an implementation that reported the peak (or the average) as the target would pass
        // everything below. The average is the per-line mean: the 30-cell word runs 600 cpm and
        // each one-cell tail runs 1 cell over the 500 ms floor = 120 cpm, so the mean is
        // (600 + 4 * 120) / 5 = 216 cpm = 43.2 wpm.
        double average = LyricPace.Compute(lines).AverageWpm;

        Assert.Multiple(() =>
        {
            Assert.That(curve.IsEmpty, Is.False);
            Assert.That(curve.PeakWpm, Is.EqualTo(120.0).Within(1e-9));
            Assert.That(curve.TargetWpm, Is.EqualTo(60.0).Within(1e-9));

            Assert.That(average, Is.EqualTo(43.2).Within(1e-9));
            Assert.That(curve.TargetWpm, Is.Not.EqualTo(curve.PeakWpm));
            Assert.That(curve.TargetWpm, Is.Not.EqualTo(average));
            Assert.That(curve.PeakWpm, Is.Not.EqualTo(average));
        });
    }

    [Test]
    public void TargetWpmRoundsTheRankToNearestRatherThanTruncatingIt()
    {
        // The index rule at a boundary. Seven windows, same arithmetic as above:
        //
        //   w0: 2900          -> 120 wpm      w4: 9100 - 400  =  8700 -> 40 wpm
        //   w1: 3580 - 100    = 3480 -> 100   w5: 12100 - 500 = 11600 -> 30 wpm
        //   w2: 4550 - 200    = 4350 ->  80   w6: 18000 - 600 = 17400 -> 20 wpm
        //   w3: 6100 - 300    = 5800 ->  60
        //
        // Sorted: [20, 30, 40, 60, 80, 100, 120], n = 7, and 0.8 * (7 - 1) = 4.8 sits between two
        // ranks. Nearest rank takes index 5 (100 wpm); truncating the same product would take
        // index 4 (80 wpm), and a percentile that quietly reports the rank BELOW the one it claims
        // understates every map. This fixture is what tells the two apart.
        var curve = LyricWpmCurve.Compute(wordThenTails(3580, 4550, 6100, 9100, 12100, 18000));

        Assert.Multiple(() =>
        {
            Assert.That(curve.IsEmpty, Is.False);
            Assert.That(curve.PeakWpm, Is.EqualTo(120.0).Within(1e-9));
            Assert.That(curve.TargetWpm, Is.EqualTo(100.0).Within(1e-9));
        });
    }

    [Test]
    public void TargetWpmIsNeverAboveThePeakNorBelowTheSlowestWindow()
    {
        // It is one of the readings, not an interpolation between two of them and not a mean, so it
        // is always a pace some window of the map really asks for.
        foreach (var curve in new[]
                 {
                     LyricWpmCurve.Compute([evenWordsLine(40, 0, 100)]),
                     LyricWpmCurve.Compute([evenWordsLine(40, 0, 200), evenWordsLine(40, 8000, 60)]),
                     LyricWpmCurve.Compute(wordThenTails(5900, 8900, 11900, 17800)),
                 })
        {
            Assert.That(curve.IsEmpty, Is.False);
            Assert.That(curve.TargetWpm, Is.LessThanOrEqualTo(curve.PeakWpm));
            Assert.That(curve.TargetWpm, Is.GreaterThan(0));
        }
    }

    [Test]
    public void PeakIsTheMaximumOfTheCurve()
    {
        // Every window starts at some cell time inside [StartTime, EndTime], so every window
        // lands in some bucket and the curve's maximum is exactly the peak WPM.
        var curve = LyricWpmCurve.Compute([
            evenWordsLine(40, 0, 200),
            evenWordsLine(40, 8000, 60),
        ]);

        Assert.That(curve.IsEmpty, Is.False);
        Assert.That(curve.Curve.Max(), Is.EqualTo(curve.PeakWpm).Within(1e-9));

        // The second line is typed more than three times as fast as the first, and buckets are
        // laid out on map time, so the peak has to sit in the back half of the curve.
        int peakBucket = curve.Curve.Select((v, i) => (v, i)).OrderByDescending(x => x.v).First().i;
        Assert.That(peakBucket, Is.GreaterThan(curve.Curve.Count / 2));
    }

    [Test]
    public void CurvePointCountIsRespected()
    {
        var curve = LyricWpmCurve.Compute([evenWordsLine(40, 0, 100)], 8);

        Assert.Multiple(() =>
        {
            Assert.That(curve.Curve, Has.Count.EqualTo(8));
            Assert.That(curve.Curve.Max(), Is.EqualTo(curve.PeakWpm).Within(1e-9));
        });
    }

    [Test]
    public void MapShorterThanTheWindowIsEmpty()
    {
        // 10 one-char words = 10 chars + 9 spaces = 19 cells, under the 30-cell window.
        var curve = LyricWpmCurve.Compute([evenWordsLine(10, 0, 100)]);

        Assert.Multiple(() =>
        {
            Assert.That(curve.IsEmpty, Is.True);
            Assert.That(curve.Curve, Is.Empty);
            Assert.That(curve.PeakWpm, Is.Zero);
            Assert.That(curve.PeakCpm, Is.Zero);
            Assert.That(curve.TargetWpm, Is.Zero);
        });
    }

    [Test]
    public void EmptyMapIsZero()
    {
        var curve = LyricWpmCurve.Compute([]);

        Assert.Multiple(() =>
        {
            Assert.That(curve.IsEmpty, Is.True);
            Assert.That(curve.PeakWpm, Is.Zero);
            Assert.That(curve.PeakCpm, Is.Zero);
            Assert.That(curve.TargetWpm, Is.Zero);
            Assert.That(curve.StartTime, Is.Zero);
            Assert.That(curve.EndTime, Is.Zero);
        });
    }

    [Test]
    public void ZeroSpanMapIsEmpty()
    {
        // Every cell of a zero-length unit targets the same instant: no span anywhere, so there
        // is nothing to divide by and nothing to report.
        var curve = LyricWpmCurve.Compute([singleWordLine(new string('a', 40), 1000, 1000)]);

        Assert.Multiple(() =>
        {
            Assert.That(curve.IsEmpty, Is.True);
            Assert.That(curve.PeakWpm, Is.Zero);
            Assert.That(curve.PeakCpm, Is.Zero);
            Assert.That(curve.TargetWpm, Is.Zero);
        });
    }

    [Test]
    public void NonPositivePointCountIsEmpty()
    {
        var curve = LyricWpmCurve.Compute([evenWordsLine(40, 0, 100)], 0);

        Assert.That(curve.IsEmpty, Is.True);
    }

    [Test]
    public void PunctuationTakesNoCell()
    {
        // Punctuation is not IsCell, so "abc," and "abc" flatten to the same cells at the same
        // times: adding marks must not move the curve.
        var plain = LyricWpmCurve.Compute([evenWordsLine(40, 0, 100)]);

        var units = new List<TimedUnit>();

        for (int m = 0; m < 40; m++)
            units.Add(new TimedUnit { Text = "a", StartTime = m * 100, EndTime = (m + 1) * 100 });

        var punctuated = LyricWpmCurve.Compute([
            new LyricLine
            {
                RawText = string.Join(' ', Enumerable.Repeat("a,", 40)),
                StartTime = 0,
                EndTime = 4000,
                SingEndTime = 4000,
                Units = units,
            },
        ]);

        Assert.Multiple(() =>
        {
            Assert.That(punctuated.PeakWpm, Is.EqualTo(plain.PeakWpm).Within(1e-9));
            Assert.That(punctuated.PeakCpm, Is.EqualTo(plain.PeakCpm).Within(1e-9));
            Assert.That(punctuated.TargetWpm, Is.EqualTo(plain.TargetWpm).Within(1e-9));
            Assert.That(punctuated.Curve, Is.EqualTo(plain.Curve));
        });
    }
}
