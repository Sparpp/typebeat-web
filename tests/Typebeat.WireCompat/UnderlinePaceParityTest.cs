using System.Globalization;
using System.Text;
using System.Text.Json;
using osuTK.Graphics;
using typebeat.Game.Beatmaps;
using typebeat.Game.IO;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.UI;

namespace Typebeat.WireCompat;

/// <summary>
/// The cross-repo pin on the underline PACE HUE (backlog 317): <c>buildPaceBands</c> in
/// <c>typebeat-player.js</c> is a port of the desktop's <see cref="UnderlinePace"/>, and this holds
/// the browser's bands against the C# over the same .osu bytes, band by band: the cell range each
/// word (or, since PR 3, syllable subdivision) band covers and the colour it earned. PR 3 moved the
/// desktop's DRAWN mode to <see cref="UnderlinePace.BuildRelativeBands"/> (each band against the
/// band before it, at <see cref="UnderlinePace.DEFAULT_MAX_CHANGE_PERCENT"/>), so that is what
/// <c>buildPaceBands</c> is held against; the whole-map percentile mode
/// (<see cref="UnderlinePace.BuildBands"/>) is kept on both sides and held against
/// <c>buildRankedPaceBands</c>. PR 5 made the MAP-RELATIVE mode
/// (<see cref="UnderlinePace.BuildMapRelativeBands"/>, each band against the map's average WPM) the
/// desktop's default, and /play draws it as <c>buildMapRelativePaceBands</c> whenever the diffs
/// route serves the average, so that is held against it too, with the average both sides read
/// computed once by <see cref="LyricPaceStatistics"/> (the figure <c>beatmaps.wpm</c> stores).
///
/// <para>Display only, so a drift here moves no score; it would still show a /play player a
/// different map from the one the desktop shows, which is what the mirror table forbids. The
/// fixtures are a synthetic map whose lines are paced deliberately unevenly (always run, so the pin
/// is never vacuous) plus every real map the environment supplies: each <c>.osu</c> under
/// <c>TYPEBEAT_GAP_OSU_DIR</c> and each <c>timing.json</c> under <c>TYPEBEAT_MAPS_DIR</c>, written
/// to a .osu by the production <see cref="LyricOsuFormat"/>.</para>
///
/// <para>Colours are compared to 1e-5 per channel rather than exactly: the framework blends in
/// single precision and the browser in double, which differ in the seventh digit and nowhere a
/// screen can show.</para>
/// </summary>
[TestFixture]
public class UnderlinePaceParityTest
{
    private const double colour_tolerance = 1e-5;

    private static readonly double?[] rank_probes =
        [0, 0.01, 0.1, 0.2, 0.2499, 0.25, 0.4, 0.5, 0.75, 0.7501, 0.8, 0.9, 0.99, 1, -1, 2, null];

    /// <summary>
    /// (speed, previous speed, max change percent) triples for
    /// <see cref="UnderlinePace.ColourForPreviousSpeed"/>: no previous band, a zero previous with a
    /// positive and a zero speed, no change, rises and falls either side of the threshold, and the
    /// threshold clamped at both ends. A null max change is the default.
    /// </summary>
    private static readonly (double Speed, double? Previous, double? MaxChange)[] previous_probes =
    [
        (1, null, null), (1, 0, null), (0, 0, null), (1, 1, null),
        (1.5, 1, null), (2, 1, null), (3, 1, null), (0.75, 1, null), (0.5, 1, null), (0, 1, null),
        (1.2, 1, 10), (1.2, 1, 500), (0.9, 1, 50), (1.25, 1, 25), (2.5, 1, 150),
    ];

    /// <summary>
    /// (speed, average, max change percent, expected rank) for
    /// <see cref="UnderlinePace.ColourForMapAverage"/>, in WPM at an average of 200 (the ramp reads a
    /// RATIO, so any common unit will do, and WPM keeps the ratios exact). At p percent full green is
    /// 200 / (1 + p/100) and full red 200 * (1 + p/200): the threshold cases at 25, 50, 100 and 150,
    /// the neutral average, the clamp past both ends, a zero speed (full green) and a zero average
    /// (neutral). A null max change is the default (100). The expected rank is the
    /// <see cref="UnderlinePace.ColourForRank"/> the colour must equal, checked on the C# first.
    /// </summary>
    private static readonly (double Speed, double Average, double? MaxChange, double Rank)[] map_average_probes =
    [
        (100, 200, 100, 0), (200, 200, 100, 0.5), (300, 200, 100, 1), (50, 200, 100, 0), (400, 200, 100, 1),
        (160, 200, 25, 0), (225, 200, 25, 1), (400d / 3, 200, 50, 0), (250, 200, 50, 1), (80, 200, 150, 0), (350, 200, 150, 1),
        (0, 200, null, 0), (100, 200, null, 0), (300, 200, null, 1), (250, 200, null, 0.875), (150, 200, null, 1d / 6),
        (100, 0, null, 0.5),
    ];

    private static readonly Lazy<(List<(string Name, string Osu)> Maps, JsonElement Browser)> run = new(() =>
    {
        var maps = Fixtures();
        var payload = new StringBuilder("{\"maps\":[");

        for (int i = 0; i < maps.Count; i++)
        {
            if (i > 0)
                payload.Append(',');

            payload.Append("{\"name\":").Append(JsonSerializer.Serialize(maps[i].Name));
            payload.Append(",\"avgWpm\":").Append(Json(AverageWpmOf(Decode(maps[i].Osu))));
            payload.Append(",\"osu\":").Append(JsonSerializer.Serialize(maps[i].Osu)).Append('}');
        }

        payload.Append("],\"ranks\":[");
        payload.Append(string.Join(",", rank_probes.Select(r => r is double d ? d.ToString("R", CultureInfo.InvariantCulture) : "null")));
        payload.Append("],\"previous\":[");
        payload.Append(string.Join(",", previous_probes.Select(p => $"[{Json(p.Speed)},{Json(p.Previous)},{Json(p.MaxChange)}]")));
        payload.Append("],\"mapAverage\":[");
        payload.Append(string.Join(",", map_average_probes.Select(p => $"[{Json(p.Speed)},{Json(p.Average)},{Json(p.MaxChange)}]")));
        payload.Append("]}");

        string path = Path.Combine(Path.GetTempPath(), $"typebeat-pacebands-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, payload.ToString(), new UTF8Encoding(false));

        try
        {
            return (maps, NodeHarness.Run("PlayerPaceBandsHarness.cjs", path));
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // A leftover temp file is not worth failing a fidelity test over.
            }
        }
    });

    /// <summary>
    /// A map built to reach BOTH hued ends of the ramp and the neutral middle: sixteen lines whose
    /// words run from a slow drawl to a fast burst, so the fast and slow quartiles are populated and
    /// ties (identically paced words) share a rank.
    ///
    /// <para>"charlie" carries an AUTHORED subdivision, char|lie, cut at the middle of the word with
    /// <c>split_chars</c> [4] (backlog 363). The subdivision-cut coverage below used to be reached by
    /// the gameplay syllabifier's automatic marks on these unsubdivided words; only the mapper
    /// subdivides now, so the synthetic map has to author one or the cut would only ever be
    /// exercised by the env-supplied real maps.</para>
    /// </summary>
    private static string SyntheticOsu()
    {
        var lines = new List<string>();
        double t = 1000;
        string[] words = ["alpha", "bravo", "charlie", "delta"];

        for (int k = 0; k < 16; k++)
        {
            // Per-word duration cycles from 120 ms to 1800 ms across the lines, with some repeats.
            double wordMs = 120 + (k % 8) * 240;
            var wordJson = new List<string>();
            double lineStart = t;

            foreach (string w in words.Take(2 + k % 3))
            {
                string subdivision = w == "charlie"
                    ? $",\"syllables\":[{{\"text\":\"char\",\"start_ms\":{t.ToString(CultureInfo.InvariantCulture)},\"end_ms\":{(t + wordMs / 2).ToString(CultureInfo.InvariantCulture)}}},"
                      + $"{{\"text\":\"lie\",\"start_ms\":{(t + wordMs / 2).ToString(CultureInfo.InvariantCulture)},\"end_ms\":{(t + wordMs).ToString(CultureInfo.InvariantCulture)}}}],\"split_chars\":[4]"
                    : string.Empty;

                wordJson.Add($"{{\"text\":\"{w}\",\"start_ms\":{t.ToString(CultureInfo.InvariantCulture)},\"end_ms\":{(t + wordMs).ToString(CultureInfo.InvariantCulture)},\"score\":1{subdivision}}}");
                t += wordMs + (k % 4) * 60;
            }

            string text = string.Join(" ", words.Take(2 + k % 3));
            lines.Add($"{{\"text\":\"{text}\",\"start_ms\":{lineStart.ToString(CultureInfo.InvariantCulture)},\"end_ms\":{t.ToString(CultureInfo.InvariantCulture)},\"words\":[{string.Join(",", wordJson)}]}}");
            t += 500;
        }

        return LyricOsuFormat.GenerateOsu("a", "t", "a.mp3", "c",
            $"{{\"version\":2,\"song_end_ms\":{(t + 1000).ToString(CultureInfo.InvariantCulture)},\"lines\":[{string.Join(",", lines)}]}}");
    }

    private static List<(string Name, string Osu)> Fixtures()
    {
        var maps = new List<(string, string)> { ("synthetic", SyntheticOsu()) };

        string gapDir = Environment.GetEnvironmentVariable("TYPEBEAT_GAP_OSU_DIR") ?? string.Empty;

        if (gapDir.Length > 0 && Directory.Exists(gapDir))
        {
            foreach (string file in Directory.GetFiles(gapDir, "*.osu").OrderBy(f => f, StringComparer.Ordinal))
                maps.Add(("gap:" + Path.GetFileName(file), File.ReadAllText(file)));
        }

        string mapsDir = Environment.GetEnvironmentVariable("TYPEBEAT_MAPS_DIR") ?? string.Empty;

        if (mapsDir.Length > 0 && Directory.Exists(mapsDir))
        {
            foreach (string dir in Directory.GetDirectories(mapsDir).OrderBy(d => d, StringComparer.Ordinal))
            {
                string timing = Path.Combine(dir, "timing.json");

                if (File.Exists(timing))
                    maps.Add(("maps:" + Path.GetFileName(dir), LyricOsuFormat.GenerateOsu("a", "t", "a.mp3", "c", File.ReadAllText(timing))));
            }
        }

        return maps;
    }

    private static TypingLine[] Decode(string osu)
    {
        LyricBeatmapDecoder.Register();

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(osu));
        using var reader = new LineBufferedReader(stream);
        var decoded = typebeat.Game.Beatmaps.Formats.Decoder.GetDecoder<Beatmap>(reader).Decode(reader);

        return decoded.HitObjects.OfType<TypeBeatHitObject>()
                      .OrderBy(h => h.LineIndex)
                      .Select(h => TypingLine.FromLyricLine(h.Line, alignSubdivisionTargets: true))
                      .ToArray();
    }

    /// <summary>
    /// The map's whole-map average WPM as the desktop's lyric stack reads it for the map-relative
    /// mode (<see cref="LyricPaceStatistics"/> over the lines' sources, default stream), which is
    /// the figure the server stores as <c>beatmaps.wpm</c> and serves to /play as <c>avg_wpm</c>.
    /// </summary>
    private static double AverageWpmOf(TypingLine[] lines)
        => LyricPaceStatistics.Compute(lines.Select(l => l.Source), false).AverageWpm;

    private static string Json(double? value) => value is double d ? d.ToString("R", CultureInfo.InvariantCulture) : "null";

    private static void AssertColour(JsonElement browser, Color4 expected, string what)
    {
        Assert.That(browser.GetProperty("r").GetDouble(), Is.EqualTo(expected.R).Within(colour_tolerance), $"{what}: R");
        Assert.That(browser.GetProperty("g").GetDouble(), Is.EqualTo(expected.G).Within(colour_tolerance), $"{what}: G");
        Assert.That(browser.GetProperty("b").GetDouble(), Is.EqualTo(expected.B).Within(colour_tolerance), $"{what}: B");
        Assert.That(browser.GetProperty("a").GetDouble(), Is.EqualTo(expected.A).Within(colour_tolerance), $"{what}: A");
    }

    /// <summary>
    /// Every band of every line of every fixture, in the mode /play draws (the RELATIVE one since
    /// PR 3): same count, same cell range, same colour. Coverage is asserted alongside, so the sweep
    /// cannot pass on maps that stopped reaching either hued end, and on the subdivision cut PR 3
    /// added (a word is cut again at its syllable markers, which since backlog 363 are the mapper's
    /// own subdivisions only).
    /// </summary>
    [Test]
    public void TheBrowserBandsMatchUnderlinePaceOnEveryFixture()
        => AssertBands("lines", lines => UnderlinePace.BuildRelativeBands(lines, authoredSyllablesOnly: true));

    /// <summary>
    /// The same sweep in the whole-map percentile mode (<see cref="UnderlinePace.BuildBands"/>, the
    /// pre-PR 3 draw), which both sides keep.
    /// </summary>
    [Test]
    public void TheBrowserRankedBandsMatchBuildBandsOnEveryFixture()
        => AssertBands("ranked", UnderlinePace.BuildBands);

    /// <summary>
    /// The same sweep in the MAP-RELATIVE mode (<see cref="UnderlinePace.BuildMapRelativeBands"/>),
    /// the desktop's default since PR 5 and what /play draws when the map's average WPM is served,
    /// against the same average on both sides. This mode is neutral only on a band paced at
    /// EXACTLY the average, which no real fixture is guaranteed to hold, so the sweep does not
    /// demand a neutral band; <see cref="TheMapAverageRampMatchesColourForMapAverage"/> pins the
    /// neutral average directly.
    /// </summary>
    [Test]
    public void TheBrowserMapRelativeBandsMatchBuildMapRelativeBandsOnEveryFixture()
        => AssertBands("mapRelative", lines => UnderlinePace.BuildMapRelativeBands(lines, AverageWpmOf(lines), authoredSyllablesOnly: true), requireNeutral: false);

    private static void AssertBands(string mode, Func<TypingLine[], PaceBand[][]> build, bool requireNeutral = true)
    {
        var (maps, browser) = run.Value;
        var browserMaps = browser.GetProperty("maps");
        var neutral = UnderlinePace.NeutralColour;
        int fast = 0, slow = 0, neutralBands = 0, total = 0, markers = 0;

        Assert.That(browserMaps.GetArrayLength(), Is.EqualTo(maps.Count), "the harness read every map");

        Assert.Multiple(() =>
        {
            for (int m = 0; m < maps.Count && m < browserMaps.GetArrayLength(); m++)
            {
                var lines = Decode(maps[m].Osu);
                var expected = build(lines);
                var browserLines = browserMaps[m].GetProperty(mode);
                var browserCells = browserMaps[m].GetProperty("cellCounts");
                string name = maps[m].Name;

                Assert.That(browserLines.GetArrayLength(), Is.EqualTo(expected.Length), $"{name}: line count");

                for (int k = 0; k < expected.Length && k < browserLines.GetArrayLength(); k++)
                {
                    Assert.That(browserCells[k].GetInt32(), Is.EqualTo(lines[k].Cells.Count), $"{name}[{k}]: cell count");
                    Assert.That(browserMaps[m].GetProperty("markers")[k].GetInt32(), Is.EqualTo(lines[k].AuthoredGrouping.MarkerCells.Count), $"{name}[{k}]: subdivision cuts");
                    markers += lines[k].AuthoredGrouping.MarkerCells.Count;
                    Assert.That(browserMaps[m].GetProperty("sungEnds")[k].GetDouble(), Is.EqualTo(UnderlinePace.SungEndOf(lines[k])), $"{name}[{k}]: sung end");

                    var segments = UnderlinePace.SegmentLine(lines[k], authoredSyllablesOnly: true);
                    var browserSpeeds = browserMaps[m].GetProperty("speeds")[k];
                    Assert.That(browserSpeeds.GetArrayLength(), Is.EqualTo(segments.Length), $"{name}[{k}]: segment count");

                    for (int j = 0; j < segments.Length && j < browserSpeeds.GetArrayLength(); j++)
                        Assert.That(browserSpeeds[j].GetDouble(), Is.EqualTo(segments[j].Speed), $"{name}[{k}] segment {j}: speed");

                    var browserBands = browserLines[k];
                    Assert.That(browserBands.GetArrayLength(), Is.EqualTo(expected[k].Length), $"{name}[{k}]: band count");

                    for (int j = 0; j < expected[k].Length && j < browserBands.GetArrayLength(); j++)
                    {
                        var band = expected[k][j];
                        var browserBand = browserBands[j];
                        string what = $"{name}[{k}] band {j}";

                        Assert.That(browserBand.GetProperty("startCell").GetInt32(), Is.EqualTo(band.StartCell), $"{what}: startCell");
                        Assert.That(browserBand.GetProperty("endCellExclusive").GetInt32(), Is.EqualTo(band.EndCellExclusive), $"{what}: endCellExclusive");
                        AssertColour(browserBand, band.Colour, what);

                        total++;

                        if (band.Colour == neutral)
                            neutralBands++;
                        else if (band.Colour.R > neutral.R)
                            fast++;
                        else
                            slow++;
                    }
                }
            }
        });

        Assert.Multiple(() =>
        {
            Assert.That(total, Is.GreaterThan(0), "the fixtures produced bands");
            Assert.That(fast, Is.GreaterThan(0), "the fixtures reach the fast (red) end of the ramp");
            Assert.That(slow, Is.GreaterThan(0), "the fixtures reach the slow (green) end of the ramp");
            if (requireNeutral)
                Assert.That(neutralBands, Is.GreaterThan(0), "the fixtures keep a neutral middle");
            Assert.That(markers, Is.GreaterThan(0), "the fixtures cut at least one word at a syllable subdivision");
        });

        TestContext.Out.WriteLine($"{mode}: {maps.Count} maps, {total} bands: {fast} fast, {slow} slow, {neutralBands} neutral, {markers} subdivision cuts");
    }

    /// <summary>
    /// <see cref="UnderlinePace.ColourForPreviousSpeed"/> against the browser's
    /// <c>paceColourForPreviousSpeed</c>: the relative mode's ramp, probed where the maps may never
    /// land (no previous band, a zero previous, both thresholds clamped).
    /// </summary>
    [Test]
    public void ThePreviousSpeedRampMatchesColourForPreviousSpeed()
    {
        var probes = run.Value.Browser.GetProperty("previousProbes");

        Assert.That(probes.GetArrayLength(), Is.EqualTo(previous_probes.Length));

        Assert.Multiple(() =>
        {
            for (int i = 0; i < previous_probes.Length; i++)
            {
                var (speed, previous, maxChange) = previous_probes[i];
                var expected = maxChange is double max
                    ? UnderlinePace.ColourForPreviousSpeed(speed, previous, max)
                    : UnderlinePace.ColourForPreviousSpeed(speed, previous);

                AssertColour(probes[i], expected, $"speed {speed} after {previous?.ToString(CultureInfo.InvariantCulture) ?? "nothing"} at {maxChange?.ToString(CultureInfo.InvariantCulture) ?? "default"}");
            }
        });
    }

    /// <summary>
    /// <see cref="UnderlinePace.ColourForMapAverage"/> against the browser's
    /// <c>paceColourForMapAverage</c>, and both against the rank each threshold case must land on:
    /// the full-green and full-red thresholds at every clamp-legal percentage, the neutral average,
    /// the clamp past both ends, a zero speed and a zero average.
    /// </summary>
    [Test]
    public void TheMapAverageRampMatchesColourForMapAverage()
    {
        var probes = run.Value.Browser.GetProperty("mapAverageProbes");

        Assert.That(probes.GetArrayLength(), Is.EqualTo(map_average_probes.Length));

        Assert.Multiple(() =>
        {
            for (int i = 0; i < map_average_probes.Length; i++)
            {
                var (speed, average, maxChange, rank) = map_average_probes[i];
                var expected = maxChange is double max
                    ? UnderlinePace.ColourForMapAverage(speed, average, max)
                    : UnderlinePace.ColourForMapAverage(speed, average);
                string what = $"{speed.ToString(CultureInfo.InvariantCulture)} against {average.ToString(CultureInfo.InvariantCulture)} at {maxChange?.ToString(CultureInfo.InvariantCulture) ?? "default"}";

                var atRank = UnderlinePace.ColourForRank(rank);
                Assert.That(expected.R, Is.EqualTo(atRank.R).Within(colour_tolerance), $"{what}: C# lands on rank {rank} (R)");
                Assert.That(expected.G, Is.EqualTo(atRank.G).Within(colour_tolerance), $"{what}: C# lands on rank {rank} (G)");
                Assert.That(expected.B, Is.EqualTo(atRank.B).Within(colour_tolerance), $"{what}: C# lands on rank {rank} (B)");
                Assert.That(expected.A, Is.EqualTo(atRank.A).Within(colour_tolerance), $"{what}: C# lands on rank {rank} (A)");
                AssertColour(probes[i], expected, what);
            }
        });
    }

    /// <summary>
    /// <see cref="UnderlinePace.ColourForRank"/> against the browser's <c>paceColourForRank</c> over
    /// a sweep of ranks: both buffer edges (inclusive), both exact endpoints, the interior of each
    /// ramp, out-of-range values (clamped) and NaN (neutral).
    /// </summary>
    [Test]
    public void TheRankRampMatchesColourForRank()
    {
        var probes = run.Value.Browser.GetProperty("rankProbes");

        Assert.That(probes.GetArrayLength(), Is.EqualTo(rank_probes.Length));

        Assert.Multiple(() =>
        {
            for (int i = 0; i < rank_probes.Length; i++)
            {
                double rank = rank_probes[i] ?? double.NaN;
                AssertColour(probes[i], UnderlinePace.ColourForRank(rank), $"rank {rank.ToString(CultureInfo.InvariantCulture)}");
            }
        });
    }
}
