using Typebeat.Web.Packages.Lyrics;

namespace Typebeat.Web.Tests;

/// <summary>
/// The pace + difficulty arithmetic against known values. Pace anchors on the game's own
/// regression test (typebeat-osu typebeat.Game.Rulesets.TypeBeat.Tests/NonVisual/
/// LyricPaceStatisticsTest.cs): "ab cd" over a 3000 ms boundary window -> 5 cells, 2 words,
/// WPM 40, CPM 100. Stars follow <see cref="LyricDifficulty"/> (strain-based); the hand-computed
/// "cat cat" -> 0.7608 anchor is shared with the game's LyricDifficultyTest to lock the two ports.
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
            // stars from LyricDifficulty: "ab cd" lands one word in each of two 1 s sections,
            // each load 3 -> Dmax 3, agg = 3/4 + ln(2) = 1.443, stars = 0.108 * 1.443^1.5 = 0.19.
            Assert.That(pace.DifficultyRating, Is.EqualTo(0.19).Within(0.01));
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
        // The hand-computed anchor shared with the game's LyricDifficultyTest: "cat cat" — both
        // 400 ms words fall in section 0; loads 4 + 3.4 (2nd repeats) = 7.4, agg = 7.4/4 + ln(1),
        // stars = 0.108 * 1.85^1.5 = 0.27. Locks the web port to the game byte-for-byte.
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

        Assert.That(LyricPace.Compute([line]).DifficultyRating, Is.EqualTo(0.27).Within(0.01));
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
            // Diacritics stripped, punctuation removed, whitespace collapsed (LyricBeatmap.cs:90-149).
            Assert.That(Typeability.Normalize("Héllo,  wörld!"), Is.EqualTo("Hello world"));
            Assert.That(Typeability.Normalize("don’t stop"), Is.EqualTo("dont stop"));
            Assert.That(Typeability.StripBackingVocals("go (ooh) now [aah]"), Is.EqualTo("go  now "));
            Assert.That(Typeability.Normalize(Typeability.StripBackingVocals("(all backing)")), Is.Empty);
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
