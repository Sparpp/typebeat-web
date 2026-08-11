using Typebeat.Web.Packages.Lyrics;

namespace Typebeat.Web.Tests;

/// <summary>
/// The rolling-window pace curve against known values. Ported alongside
/// <see cref="LyricWpmCurve"/> itself from the game's own regression test (typebeat-osu
/// typebeat.Game.Rulesets.TypeBeat.Tests/NonVisual/LyricWpmCurveTest.cs), including the two
/// hand-computed anchors (600 CPM / 20 WPM for a single 30-char word, and the halved window span
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
        //   words  = 29 cells * (1/30 of a word)             = 29/30
        //   wpm    = (29/30) / (2900/60000) = 1740000/87000  = 20
        //
        // The word here is 30 characters long, so wpm = cpm/30; the HUD's chars/5 convention
        // would have called the same window 600/5 = 120 WPM instead.
        var curve = LyricWpmCurve.Compute([singleWordLine(new string('a', 30), 0, 3000)]);

        Assert.Multiple(() =>
        {
            Assert.That(curve.IsEmpty, Is.False);
            Assert.That(curve.PeakCpm, Is.EqualTo(600.0).Within(1e-9));
            Assert.That(curve.PeakWpm, Is.EqualTo(20.0).Within(1e-9));
            Assert.That(curve.StartTime, Is.EqualTo(0).Within(1e-9));
            Assert.That(curve.EndTime, Is.EqualTo(2900).Within(1e-9));

            // The single window starts at the map's first cell, so it lands in bucket 0.
            Assert.That(curve.Curve, Has.Count.EqualTo(LyricWpmCurve.DEFAULT_CURVE_POINTS));
            Assert.That(curve.Curve[0], Is.EqualTo(20.0).Within(1e-9));
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
        // A window of 30 cells therefore covers only ~15 words, not 30:
        //   even-start window: span 1500 ms, 15 word cells -> wpm 600, cpm 29*60000/1500 = 1160
        //   odd-start  window: span 1400 ms, 14 word cells -> wpm 600, cpm 29*60000/1400 = 1242.857...
        //
        // So the peak CPM comes from a different window than any peak-WPM window, which is why
        // the two are maximised independently.
        var curve = LyricWpmCurve.Compute([evenWordsLine(30, 0, 100)]);

        Assert.Multiple(() =>
        {
            Assert.That(curve.PeakWpm, Is.EqualTo(600.0).Within(1e-9));
            Assert.That(curve.PeakCpm, Is.EqualTo(1740000.0 / 1400.0).Within(1e-9));
            Assert.That(curve.StartTime, Is.EqualTo(0).Within(1e-9));
            Assert.That(curve.EndTime, Is.EqualTo(2900).Within(1e-9));
        });
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
    public void PunctuationTakesNoCellAndNoWordShare()
    {
        // Punctuation is not IsCell, so "abc," and "abc" flatten to the same cells at the same
        // times and carry the same word shares: adding marks must not move the curve.
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
