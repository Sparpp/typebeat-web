using System.Globalization;
using System.Text;
using System.Text.Json;
using typebeat.Game.Beatmaps;
using typebeat.Game.IO;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Objects;

namespace Typebeat.WireCompat;

/// <summary>
/// The cross-repo pin on PR 5's ALIGNED subdivision targets (<see cref="TypingEngine.AlignSubdivisionTargets"/>,
/// extended CONFIG bit 3, set by every live stack). Under it a subdivided word with NO valid
/// <c>split_chars</c> times its cells on the DERIVED letter cut (the one its judgement groups are
/// built from) instead of spreading them evenly by index across the segments, and a paused
/// stretch with no authored cell cut times its cells on its own char cuts. The browser plays live
/// only, so <c>tokenCellTargets</c> in <c>typebeat-core.js</c> takes the rule unconditionally.
///
/// <para>Each fixture is decoded by the production <see cref="LyricBeatmapDecoder"/> and built
/// through the public factory with <c>alignSubdivisionTargets: true</c>, and by the browser through
/// <c>parseLyricOsu</c> + <c>buildBeatmap</c> in <c>CoreAlignedTargetsHarness.cjs</c>. The two are
/// held against each other cell for cell, the aligned reading against worked numbers, and the
/// stored-era reading (<c>alignSubdivisionTargets: false</c>) against the legacy numbers, so the
/// fixtures provably sit where the two eras differ.</para>
/// </summary>
[TestFixture]
public class AlignedSubdivisionTargetsParityTest
{
    private sealed record Fixture(string Name, string Osu, int FirstCell, double[] Aligned, double[]? Legacy);

    /// <summary>A one-line map around the given word JSON, written by the production format writer.</summary>
    private static string Map(string lineText, double lineStart, double lineEnd, double songEnd, params string[] words)
        => LyricOsuFormat.GenerateOsu("a", "t", "a.mp3", "c",
            "{\"version\":2,\"song_end_ms\":" + Ms(songEnd) + ",\"lines\":[{\"text\":" + JsonSerializer.Serialize(lineText)
            + ",\"start_ms\":" + Ms(lineStart) + ",\"end_ms\":" + Ms(lineEnd) + ",\"words\":[" + string.Join(",", words) + "]}]}");

    /// <summary>One word's JSON; <paramref name="extra"/> is spliced in verbatim (a leading comma, or empty).</summary>
    private static string Word(string text, double start, double end, double[] boundaries, string extra = "")
    {
        string syllables = boundaries.Length == 0
            ? ""
            : ",\"syllables\":[" + string.Join(",", new[] { start }.Concat(boundaries).Select(b => "{\"start_ms\":" + Ms(b) + "}")) + "]";

        return "{\"text\":" + JsonSerializer.Serialize(text) + ",\"start_ms\":" + Ms(start) + ",\"end_ms\":" + Ms(end) + ",\"score\":1" + syllables + extra + "}";
    }

    private static string Ms(double ms) => ms.ToString("R", CultureInfo.InvariantCulture);

    private const double overdone_start = 184008.333333;
    private const double overdone_end = 190633.333333;
    private const double overdone_b1 = 185008.333333;
    private const double overdone_b2 = 186008.333333;

    private static Fixture[] Fixtures()
    {
        // "It's overdoooooooooone": the word cells are 4..20 (the default stream drops the
        // apostrophe, so "its" is cells 0..2 and the gap is cell 3). Derived split o|ver|doooooooooone
        // ([1, 4]): one cell before the first boundary, three before the second, thirteen after.
        var overdoneAligned = new double[17];
        overdoneAligned[0] = overdone_start;
        overdoneAligned[1] = overdone_b1;
        overdoneAligned[2] = overdone_b1 + (overdone_b2 - overdone_b1) / 3;
        overdoneAligned[3] = overdone_b1 + 2 * (overdone_b2 - overdone_b1) / 3;

        for (int n = 0; n <= 12; n++)
            overdoneAligned[4 + n] = overdone_b2 + n * (overdone_end - overdone_b2) / 13;

        // The index-even spread of the stored era: 17 cells over 3 segments, 17/3 cells each.
        var overdoneLegacy = new double[17];
        const double per_segment = 17d / 3;

        for (int j = 0; j < 17; j++)
        {
            int s = Math.Min(2, (int)Math.Floor(j * 3d / 17));
            double lo = s == 0 ? overdone_start : s == 1 ? overdone_b1 : overdone_b2;
            double hi = s == 0 ? overdone_b1 : s == 1 ? overdone_b2 : overdone_end;
            overdoneLegacy[j] = lo + (j - s * per_segment) / per_segment * (hi - lo);
        }

        return
        [
            new Fixture("overdone",
                Map("It's overdoooooooooone", 183000, 191500, 192000,
                    Word("It's", 183000, overdone_start, []),
                    Word("overdoooooooooone", overdone_start, overdone_end, [overdone_b1, overdone_b2])),
                4, overdoneAligned, overdoneLegacy),

            // A rest [3500, 4500] after "pro" with boundaries 2000 and 6000, one in each stretch:
            // pr|o and ba|bly on the aligned era, an even spread of each stretch on the stored one.
            new Fixture("probablyPaused",
                Map("probably", 1000, 9500, 10000,
                    Word("probably", 1000, 9000, [2000, 6000], ",\"pauses\":[{\"start_ms\":3500,\"end_ms\":4500,\"split\":3}]")),
                0, [1000, 1500, 2000, 4500, 5250, 6000, 7000, 8000], [1000, 1000 + 2000d / 3, 2500, 4500, 5100, 5700, 6600, 7800]),

            // A one-letter word cannot be cut, so its derived split is empty and every boundary drops.
            new Fixture("singleLetter",
                Map("a", 1000, 9500, 10000, Word("a", 1000, 9000, [2000, 6000])),
                0, [1000], null),

            // The derived cuts beau|tiful and to|ni|ght, the ones the groups were always built from.
            new Fixture("beautiful",
                Map("beautiful", 1000, 2500, 3000, Word("beautiful", 1000, 1900, [1450])),
                0, [1000, 1112.5, 1225, 1337.5, 1450, 1540, 1630, 1720, 1810], null),
            new Fixture("tonight",
                Map("tonight", 1000, 2500, 3000, Word("tonight", 1000, 1900, [1300, 1600])),
                0, [1000, 1150, 1300, 1450, 1600, 1700, 1800], null),
        ];
    }

    private static readonly Fixture[] all_fixtures = Fixtures();

    private static readonly Lazy<JsonElement> browser = new Lazy<JsonElement>(() =>
    {
        string payload = JsonSerializer.Serialize(new { cases = all_fixtures.Select(f => new { name = f.Name, osu = f.Osu }) });
        string path = Path.Combine(Path.GetTempPath(), $"typebeat-aligned-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, payload, new UTF8Encoding(false));

        try
        {
            return NodeHarness.Run("CoreAlignedTargetsHarness.cjs", path);
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

    private static TypingLine[] Decode(string osu, bool aligned)
    {
        LyricBeatmapDecoder.Register();

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(osu));
        using var reader = new LineBufferedReader(stream);
        var decoded = typebeat.Game.Beatmaps.Formats.Decoder.GetDecoder<Beatmap>(reader).Decode(reader);

        return decoded.HitObjects.OfType<TypeBeatHitObject>().OrderBy(h => h.LineIndex)
                      .Select(h => TypingLine.FromLyricLine(h.Line, alignSubdivisionTargets: aligned)).ToArray();
    }

    private static JsonElement BrowserCase(string name)
    {
        foreach (var one in browser.Value.GetProperty("cases").EnumerateArray())
        {
            if (one.GetProperty("name").GetString() == name)
                return one;
        }

        throw new AssertionException($"the harness emitted no case named {name}");
    }

    /// <summary>The browser builds every cell of every fixture exactly as the aligned C# factory does.</summary>
    [Test]
    public void TheBrowserTimesEveryCellAsTheAlignedFactoryDoes()
    {
        Assert.Multiple(() =>
        {
            foreach (var fixture in all_fixtures)
            {
                var lines = Decode(fixture.Osu, aligned: true);
                var browserLines = BrowserCase(fixture.Name).GetProperty("lines");

                Assert.That(browserLines.GetArrayLength(), Is.EqualTo(lines.Length), $"{fixture.Name}: line count");

                for (int i = 0; i < lines.Length && i < browserLines.GetArrayLength(); i++)
                {
                    var cells = browserLines[i];
                    Assert.That(cells.GetArrayLength(), Is.EqualTo(lines[i].Cells.Count), $"{fixture.Name}[{i}]: cell count");

                    for (int c = 0; c < lines[i].Cells.Count && c < cells.GetArrayLength(); c++)
                    {
                        Assert.That(cells[c].GetProperty("expected").GetString(), Is.EqualTo(lines[i].Cells[c].Expected.ToString()), $"{fixture.Name}[{i}][{c}]: expected");
                        Assert.That(cells[c].GetProperty("target").GetDouble(), Is.EqualTo(lines[i].Cells[c].TargetTime), $"{fixture.Name}[{i}][{c}]: target");
                    }
                }
            }
        });
    }

    /// <summary>
    /// The aligned readings land on the worked numbers on both sides, and the stored-era readings
    /// on the legacy ones, so every fixture that carries a legacy row provably sits where the two
    /// eras differ.
    /// </summary>
    [Test]
    public void TheWordsLandOnTheWorkedNumbersInBothEras()
    {
        Assert.Multiple(() =>
        {
            foreach (var fixture in all_fixtures)
            {
                var aligned = Decode(fixture.Osu, aligned: true)[0].Cells.Skip(fixture.FirstCell).Take(fixture.Aligned.Length).Select(c => c.TargetTime).ToArray();
                Assert.That(aligned, Is.EqualTo(fixture.Aligned).Within(1e-6), $"{fixture.Name}: aligned targets");

                var browserTargets = BrowserCase(fixture.Name).GetProperty("lines")[0].EnumerateArray()
                                                              .Skip(fixture.FirstCell).Take(fixture.Aligned.Length)
                                                              .Select(c => c.GetProperty("target").GetDouble()).ToArray();
                Assert.That(browserTargets, Is.EqualTo(fixture.Aligned).Within(1e-6), $"{fixture.Name}: browser targets");

                if (fixture.Legacy == null)
                    continue;

                var legacy = Decode(fixture.Osu, aligned: false)[0].Cells.Skip(fixture.FirstCell).Take(fixture.Legacy.Length).Select(c => c.TargetTime).ToArray();
                Assert.That(legacy, Is.EqualTo(fixture.Legacy).Within(1e-6), $"{fixture.Name}: stored-era targets");
                Assert.That(legacy, Is.Not.EqualTo(aligned), $"{fixture.Name}: the two eras must differ on this fixture");
            }
        });
    }
}
