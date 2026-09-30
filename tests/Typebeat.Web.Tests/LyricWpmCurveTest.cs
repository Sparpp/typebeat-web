using Typebeat.Web.Packages.Lyrics;

namespace Typebeat.Web.Tests;

/// <summary>
/// The rolling-window pace curve against known values. Ported alongside
/// <see cref="LyricWpmCurve"/> itself from the game's own regression test (typebeat-osu
/// typebeat.Game.Rulesets.TypeBeat.Tests/NonVisual/LyricWpmCurveTest.cs), including the two
/// hand-computed anchors (640 CPM / 128 WPM for a single 30-char word, and 1280 CPM once the
/// inter-word space cells are in the window; since LyricPace v23 the window is at least
/// <see cref="LyricWpmCurve.WINDOW_SECONDS"/> seconds and <see cref="LyricWpmCurve.MIN_WINDOW_CELLS"/>
/// cells rather than 30 cells), so the two ports are pinned to the same numbers the way
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
    public void SingleThirtyCharacterWordUsesShortestEligibleTimeWindow()
    {
        // One 30-char word over [0, 3000]: k = 30, so typeable char j targets 0 + j*3000/30 = 100j,
        // 30 cells at 0, 100, ... 2900. The shortest span carrying 16 cells is 1500 ms, which is
        // also the time floor, so the window is 1500 ms. A window starting at the first cell holds
        // the inclusive 0..1500, 16 cells:
        //
        //   cpm = 16 * 60000 / 1500 = 640
        //   wpm = 640 / 5           = 128
        //
        // The 30-character word does NOT make this window worth one word: under the typing-test
        // convention a word is 5 keystrokes whatever the text says, so the map's own word length is
        // reported separately (LyricPace.PaceStatistics.AverageCharsPerWord). Until v23 this read
        // 600 CPM / 120 WPM off the old 30-cell window (29 gaps over 2900 ms).
        var curve = LyricWpmCurve.Compute([singleWordLine(new string('a', 30), 0, 3000)]);

        Assert.Multiple(() =>
        {
            Assert.That(curve.IsEmpty, Is.False);
            Assert.That(curve.PeakCpm, Is.EqualTo(640.0).Within(1e-9));
            Assert.That(curve.PeakWpm, Is.EqualTo(128.0).Within(1e-9));
            Assert.That(curve.StartTime, Is.EqualTo(0).Within(1e-9));
            Assert.That(curve.EndTime, Is.EqualTo(2900).Within(1e-9));
            Assert.That(curve.Curve, Has.Count.EqualTo(LyricWpmCurve.DEFAULT_CURVE_POINTS));
            Assert.That(curve.Curve[0], Is.EqualTo(128.0).Within(1e-9));
            Assert.That(curve.Curve.Max(), Is.EqualTo(curve.PeakWpm).Within(1e-9));
        });
    }

    [Test]
    public void InterWordSpaceCellsCountInTimeWindow()
    {
        // 30 one-char words 100 ms apart. The flattening interleaves an inter-word space cell at
        // each unit end, so the map holds 30 chars + 29 spaces = 59 cells whose targets run
        // 0, 100, 100, 200, 200, ... 2800, 2900, 2900. A 1.5 second inclusive window beginning at
        // 100 ms (the first space cell) holds 32 cells: 32 * 60000 / 1500 = 1280 CPM = 256 WPM.
        var curve = LyricWpmCurve.Compute([evenWordsLine(30, 0, 100)]);

        Assert.Multiple(() =>
        {
            Assert.That(curve.PeakCpm, Is.EqualTo(1280.0).Within(1e-9));
            Assert.That(curve.PeakWpm, Is.EqualTo(256.0).Within(1e-9));
            Assert.That(curve.StartTime, Is.EqualTo(0).Within(1e-9));
            Assert.That(curve.EndTime, Is.EqualTo(2900).Within(1e-9));
        });
    }

    /// <summary>
    /// THE WINDOW IS IN PLAYBACK SECONDS (v23), so a rate recomputes the curve rather than scaling
    /// it. Mirrors the game's TypingPaceRateTest.ThePeakUsesAPlaybackTimeWindowAtEachClock: cells
    /// 100 ms apart; at 1x the inclusive 1.5 s window holds 16 cells, at 1.5x its 2250 map ms hold
    /// 23, and at 0.75x the 16-cell floor keeps the window at 1500 map ms.
    /// </summary>
    [TestCase(1.0, 128.0)]
    [TestCase(1.5, 184.0)]
    [TestCase(0.75, 96.0)]
    public void ThePeakUsesAPlaybackTimeWindowAtEachClock(double rate, double expectedPeak)
    {
        var curve = LyricWpmCurve.Compute([singleWordLine(new string('a', 50), 0, 5000)], rate: rate);

        Assert.Multiple(() =>
        {
            Assert.That(curve.PeakWpm, Is.EqualTo(expectedPeak).Within(1e-9));
            Assert.That(curve.Curve.Max(), Is.EqualTo(expectedPeak).Within(1e-9));
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

        // Holds for every map, regardless of word length or eligible window duration.
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
        // Eight one-char words = eight chars plus seven spaces, 15 cells, below the 16-cell floor.
        var curve = LyricWpmCurve.Compute([evenWordsLine(8, 0, 100)]);

        Assert.Multiple(() =>
        {
            Assert.That(curve.IsEmpty, Is.True);
            Assert.That(curve.Curve, Is.Empty);
            Assert.That(curve.PeakWpm, Is.Zero);
            Assert.That(curve.PeakCpm, Is.Zero);
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
            Assert.That(punctuated.Curve, Is.EqualTo(plain.Curve));
        });
    }
}
