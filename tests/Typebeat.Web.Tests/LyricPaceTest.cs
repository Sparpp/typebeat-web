using Typebeat.Web.Packages.Lyrics;

namespace Typebeat.Web.Tests;

/// <summary>
/// The pace + difficulty arithmetic against known values. Pace anchors on the game's own
/// regression test (typebeat-osu typebeat.Game.Rulesets.TypeBeat.Tests/NonVisual/
/// LyricPaceStatisticsTest.cs): "ab cd" over a 3000 ms boundary window -> 5 cells, 2 words,
/// WPM 40, CPM 100. Stars follow <see cref="LyricDifficulty"/> (strain-based); the hand-computed
/// "cat cat" -> 0.79 anchor is shared with the game's LyricDifficultyTest to lock the two ports.
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
    public void ComputesBoundaryWindowPace_MatchesGameRegressionValues()
    {
        var pace = LyricPace.Compute([paceRegressionLine()]);

        Assert.Multiple(() =>
        {
            Assert.That(pace.TypeableCellCount, Is.EqualTo(5));
            Assert.That(pace.WordCount, Is.EqualTo(2));
            // Boundary window 4000 - 1000 = 3000 ms: WPM = 2 / 0.05 min, CPM = 5 / 0.05 min.
            Assert.That(pace.AverageWpm, Is.EqualTo(40.0).Within(1e-9));
            Assert.That(pace.AverageCpm, Is.EqualTo(100.0).Within(1e-9));
            // stars from LyricDifficulty (per-word strain sum + power remap).
            Assert.That(pace.DifficultyRating, Is.EqualTo(0.63).Within(0.01));
        });
    }

    [Test]
    public void AveragesPerLineRates_Unweighted()
    {
        // Line 1: "ab cd" over 3000 ms -> 40 WPM / 100 CPM.
        // Line 2: "ab cd" over 1500 ms -> 80 WPM / 200 CPM.
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
            Assert.That(pace.AverageWpm, Is.EqualTo(60.0).Within(1e-9));
            Assert.That(pace.AverageCpm, Is.EqualTo(150.0).Within(1e-9));
        });
    }

    [Test]
    public void EmptyMap_IsZero()
    {
        var pace = LyricPace.Compute([]);

        Assert.Multiple(() =>
        {
            Assert.That(pace.TypeableCellCount, Is.Zero);
            Assert.That(pace.AverageWpm, Is.Zero);
            Assert.That(pace.DifficultyRating, Is.Zero);
        });
    }

    [Test]
    public void DifficultyRating_MatchesGameAnchor()
    {
        // Anchor shared with the game's LyricDifficultyTest ("cat cat" -> 0.79), locking the two
        // ports together on the per-word strain formula.
        var line = new LyricLine
        {
            RawText = "cat cat",
            StartTime = 0,
            EndTime = 800,
            SingEndTime = 800,
            Units =
            [
                new TimedUnit { Text = "cat", StartTime = 0, EndTime = 400 },
                new TimedUnit { Text = "cat", StartTime = 400, EndTime = 800 },
            ],
        };

        Assert.That(LyricPace.Compute([line]).DifficultyRating, Is.EqualTo(0.79).Within(0.01));
    }

    [Test]
    public void DifficultyRating_RateAdjustedRatingIsNotTruncatedAtTheTop()
    {
        // backlog 118, and the anchor for it is shared with the game's LyricDifficultyTest exactly
        // as the "cat cat" one above is. LyricDifficulty used to end in a flat clamp to 10 stars,
        // chosen to keep a star BADGE sane, and it truncated the rate-adjusted ratings with it.
        // That reached stored data: sr_dt is what PerformancePoints prices a Double Time play from,
        // and it is never a badge. The shape below stays clear of 10 at 1.00x and passes it at
        // 1.50x, which is the same asymmetry the live catalogue has (no base rating has ever
        // reached 10, while sr_dt reached it on 3 of the 5 real reference maps).
        var map = denseMap(lineCount: 40, wordsPerLine: 8, lineMs: 1200);

        Assert.Multiple(() =>
        {
            // Both figures carry the backlog-152 length bonus, which is 0.1445 on this 1600-cell
            // fixture (0.12 * log10(16)) and is the SAME on both rates, since the bonus reads the
            // cell count and a clock change adds no cells. Before it they read 6.1622 and 10.5567.
            Assert.That(LyricDifficulty.Compute(map), Is.EqualTo(6.3067).Within(0.001));
            Assert.That(LyricDifficulty.Compute(map, 1.50), Is.EqualTo(10.7012).Within(0.001), "under the old ceiling this read exactly 10.00");
        });
    }

    /// <summary>
    /// The additive per-decade length bonus (backlog 152), stated as its own quantity and shared
    /// with the game's LyricDifficultyTest exactly as the two anchors above are. Every word
    /// <see cref="denseMap"/> emits is a 5-character pool word, so the cell count is exactly
    /// <c>lineCount * wordsPerLine * 5</c> and the bonus is a number this test can write out rather
    /// than read back off the thing under test. The other half of each expectation is the STRAIN
    /// rating, which is the value the same fixture rated before this term existed.
    /// </summary>
    [TestCase(40, 8, 1200, 1600, 6.16224)] // the RateAdjusted fixture above, pinned pre-152 at 6.1622
    [TestCase(40, 4, 2400, 800, 3.43140)]
    public void DifficultyRating_TheLengthBonusIsAddedFlatOnTopOfTheStrainRating(int lineCount, int wordsPerLine, double lineMs, int cells, double strainOnly)
    {
        var map = denseMap(lineCount, wordsPerLine, lineMs);

        double bonus = 0.12 * Math.Log10(cells / 100.0);

        Assert.That(bonus, Is.GreaterThan(0), "the fixture has to be over the pivot for this to test anything");
        Assert.That(LyricDifficulty.Compute(map), Is.EqualTo(strainOnly + bonus).Within(1e-5));
    }

    /// <summary>
    /// AND IT IS EXACTLY ZERO BELOW 100 CELLS, which is what the <c>max(0, .)</c> clamp is for and
    /// is not a rounding claim: the raw term is NEGATIVE under the pivot, so without the clamp every
    /// short fixture would LOSE stars (0.0122 at 90 cells, and 0.147 on the 6-cell "cat cat" anchor
    /// above). Every synthetic-map regression constant on both sides, the 0.79 anchor above and the
    /// 0.63s in <c>PackageParserTest</c> and this file, is a short fixture, so the clamp is the
    /// reason they all rate byte-identically across this change.
    /// </summary>
    [TestCase(3, 6, 1800, 90, 4.189181)] // under the pivot: the raw term is negative
    [TestCase(4, 5, 2000, 100, 3.195837)] // AT the pivot: log10(1) is exactly 0
    public void DifficultyRating_TheLengthBonusIsExactlyNothingAtOrBelowTheHundredCellPivot(int lineCount, int wordsPerLine, double lineMs, int cells, double strainOnly)
    {
        var map = denseMap(lineCount, wordsPerLine, lineMs);

        double raw = 0.12 * Math.Log10(cells / 100.0);

        Assert.Multiple(() =>
        {
            Assert.That(raw, Is.LessThanOrEqualTo(0), "the clamp cannot be tested where the raw term is positive");
            // The expectation is the STRAIN rating alone, i.e. what the fixture rated before this
            // term existed (verified by setting length_stars to 0 and re-running). Drop the clamp
            // and the 90-cell case reads 4.183690 instead, which this catches.
            Assert.That(LyricDifficulty.Compute(map), Is.EqualTo(strainOnly).Within(1e-5));
        });
    }

    /// <summary>
    /// A uniform map of varied words, mirroring the game's LyricDifficultyTest.buildMap so the two
    /// ports can be pinned against the same shape. The word pool is varied on purpose: repeating one
    /// word saturates LyricDifficulty's repetition factor and flattens the rating.
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
        // A 100 ms boundary window clamps to the 500 ms floor:
        // 1 word / (500 ms / 60000) = 120 WPM; 5 cells -> 600 CPM.
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
        Assert.That(pace.AverageWpm, Is.EqualTo(40.0).Within(1e-9));
    }

    [Test]
    public void ParseSection_BackingVocalOnlyLines_AreDropped()
    {
        var (_, lines) = LyricTiming.ParseSection(
        [
            """{"version":2}""",
            """{"text":"(ooh aah)","start_ms":0,"end_ms":500}""",
            """{"text":"real line","start_ms":1000,"end_ms":2000}""",
        ]);

        Assert.Multiple(() =>
        {
            Assert.That(lines, Has.Count.EqualTo(1));
            Assert.That(lines[0].RawText, Is.EqualTo("real line"));
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

    [Test]
    public void Normalize_MatchesGameAuthority()
    {
        Assert.Multiple(() =>
        {
            // Diacritics stripped, whitespace collapsed, SUPPORTED punctuation kept: the stored
            // line is the author's form (Typeability.Normalize in the game).
            Assert.That(Typeability.Normalize("Héllo,  wörld!"), Is.EqualTo("Hello, world!"));
            Assert.That(Typeability.Normalize("don’t stop"), Is.EqualTo("don't stop"));

            // Unsupported chars still vanish outright.
            Assert.That(Typeability.Normalize("a*b/c"), Is.EqualTo("abc"));

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

            // Counting follows IsCell, so a kept marker is a cell; a default-normalized text has
            // no markers to count and is byte-identical to the historical typeable-only count.
            Assert.That(Typeability.TypeableCount("a&b"), Is.EqualTo(3));
            Assert.That(Typeability.TypeableCount("ab cd"), Is.EqualTo(5));
        });
    }

    [Test]
    public void ParseSection_FlaggedLine_CountsMarkersAsCells()
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

            // m e _ & _ y o u: 6 letters, the slot, 2 inter-word spaces (the browser core's
            // flattening reaches the same 8 cells).
            Assert.That(pace.TypeableCellCount, Is.EqualTo(8));
            Assert.That(pace.WordCount, Is.EqualTo(3));
            // Boundary window 7000 - 1000 = 6000 ms: 3 words / 0.1 min, 8 cells / 0.1 min.
            Assert.That(pace.AverageWpm, Is.EqualTo(30.0).Within(1e-9));
            Assert.That(pace.AverageCpm, Is.EqualTo(80.0).Within(1e-9));
        });
    }

    [Test]
    public void ParseSection_UnflaggedAmpersand_StaysLyricPunctuation()
    {
        // Back-compat pin: a line whose lyrics genuinely contain "&" ingests exactly as it always
        // did, marker stripped, no extra cell. This is why the v6 backfill is value-identical for
        // every map in prod today.
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
            Assert.That(pace.AverageWpm, Is.EqualTo(20.0).Within(1e-9));
            Assert.That(pace.AverageCpm, Is.EqualTo(60.0).Within(1e-9));
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

    [Test]
    public void FreestyleSlot_AddsACellButNoKeystrokeCost()
    {
        // The game's split, mirrored: a freestyle slot is a keypress (pace counts it), but it
        // carries no fixed key, so it adds no finger travel to the strain model (LyricDifficulty
        // stays on IsTypeable). Identical timings, so the stars must match exactly.
        var freestyle = new LyricLine
        {
            RawText = "a&b",
            StartTime = 1000,
            EndTime = 5000,
            SingEndTime = 4000,
            Units = [new TimedUnit { Text = "a&b", StartTime = 1000, EndTime = 4000 }],
        };

        var plain = new LyricLine
        {
            RawText = "ab",
            StartTime = 1000,
            EndTime = 5000,
            SingEndTime = 4000,
            Units = [new TimedUnit { Text = "ab", StartTime = 1000, EndTime = 4000 }],
        };

        var freePace = LyricPace.Compute([freestyle]);
        var plainPace = LyricPace.Compute([plain]);

        Assert.Multiple(() =>
        {
            Assert.That(freePace.TypeableCellCount, Is.EqualTo(3));
            Assert.That(plainPace.TypeableCellCount, Is.EqualTo(2));
            Assert.That(freePace.DifficultyRating, Is.EqualTo(plainPace.DifficultyRating).Within(1e-12));
        });
    }

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

        TestContext.WriteLine($"Real map -> WPM {pace.AverageWpm:0.0}, CPM {pace.AverageCpm:0.0}, stars {pace.DifficultyRating:0.00}");

        Assert.Multiple(() =>
        {
            Assert.That(lines, Is.Not.Empty);
            Assert.That(pace.TypeableCellCount, Is.GreaterThan(100));
            // A real song sits in a sane human WPM band (and stars stay on the 0..10 scale).
            Assert.That(pace.AverageWpm, Is.InRange(10, 400));
            Assert.That(pace.DifficultyRating, Is.InRange(0.1, 10));
        });
    }
}
