using System.Text;
using System.Text.Json;
using typebeat.Game.Beatmaps;
using typebeat.Game.IO;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.TypeBeat;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.UI;

namespace Typebeat.WireCompat;

/// <summary>
/// The cross-repo pin on the DISPLAY of holds in the browser player: <c>typebeat-player.js</c>'s port
/// of the desktop's hold presentation held against the GAME's own public statics, with no tolerance.
/// The scoring half is <see cref="HoldLiveParityTest"/>'s; this is the half a player sees.
///
/// <para>What is held: the track's length (<see cref="LyricLineDisplay.HoldTrackWidth(double, float)"/>
/// and its paced overload), the pace into a hold (<see cref="LyricLineDisplay.HoldPaceLettersPerSecond"/>),
/// the lifted letter's travel (<see cref="LyricLineDisplay.HoldTravelOffset"/>), the playhead's offset
/// across a held cell (<see cref="LyricLineDisplay.HeldSungOffset"/>), the tail's result colour
/// (<see cref="LyricLineDisplay.HoldResultColour"/>, as the tone the stylesheet maps onto the page's
/// palette), every hold constant the browser carries that the game makes public, and the sung
/// polyline across held cells: <see cref="TypingLine.SungPositionAt"/> and
/// <see cref="TypingLine.SweepEndTime"/> on maps with holds, which is what moves the sung caret, the
/// underline fill and the pace bands' last span.</para>
///
/// <para>The JS works in the desktop's single precision (Math.fround wherever the C# rounds to float),
/// so the float statics compare exactly once each probe is passed as the same float. The private
/// pieces (LyricLineDisplay's holdPaceAt loop and CellLefts, LyricStage's holdFillAt, the cell fade
/// and the shadow factor, which are internal or private) cannot be reached from here; the display
/// harness pins them on golden values (WebplayDisplayTest).</para>
///
/// <para>The maps are timing.json documents written through the game's own
/// <see cref="LyricOsuFormat.GenerateOsu"/>, read by both loaders as <see cref="HoldLiveParityTest"/>
/// reads them (which already pins the two loaders' hold cells against each other).</para>
/// </summary>
[TestFixture]
public class HoldDisplayParityTest
{
    #region Fixtures

    private static readonly Dictionary<string, string> timing = new Dictionary<string, string>
    {
        // HoldLiveParityTest's "lovesong": a hold with ticks handing off, a word's last letter held to
        // its end, a first-letter hold on a capital, ignored holds, a hold before a hyphen and one
        // inside "don't".
        ["lovesong"] = """
            {"version": 2, "song_end_ms": 16000, "lines": [
              {"text": "love me tender", "start_ms": 1000, "end_ms": 4600, "words": [
                {"text": "love", "start_ms": 1000, "end_ms": 2200, "score": 1, "holds": [{"char": 1, "start_ms": 1300, "end_ms": 1900, "ticks": [1450, 1600.5, 1750, 1950]}]},
                {"text": "me", "start_ms": 2200, "end_ms": 3000, "score": 1, "holds": [{"char": 1, "start_ms": 2600, "end_ms": 3000}]},
                {"text": "tender", "start_ms": 3000, "end_ms": 4600, "score": 1, "holds": [{"char": 9, "start_ms": 4300, "end_ms": 4500}, {"char": 2, "start_ms": 3400, "end_ms": 4200, "ticks": [3700]}, {"char": 5, "start_ms": 4200, "end_ms": 4320}]}]},
              {"text": "Oh hold on", "start_ms": 6000, "end_ms": 9000, "words": [
                {"text": "Oh", "start_ms": 6000, "end_ms": 6800, "score": 1, "holds": [{"char": 0, "start_ms": 6000, "end_ms": 6500, "ticks": [6200]}]},
                {"text": "hold", "start_ms": 6800, "end_ms": 8000, "score": 1, "holds": [{"char": 1, "start_ms": 7000, "end_ms": 7050}, {"char": 2, "start_ms": 7100, "end_ms": 7900}, {"char": 3, "start_ms": 7500, "end_ms": 7800}]},
                {"text": "on", "start_ms": 8000, "end_ms": 9000, "score": 1, "holds": [{"char": 1, "start_ms": 8400, "end_ms": 9000, "ticks": [8700, 8800]}]}]},
              {"text": "bad-cat don't", "start_ms": 10000, "end_ms": 13000, "words": [
                {"text": "bad-cat", "start_ms": 10000, "end_ms": 11400, "score": 1, "holds": [{"char": 2, "start_ms": 10400, "end_ms": 10690}]},
                {"text": "don't", "start_ms": 11400, "end_ms": 13000, "score": 1, "holds": [{"char": 2, "start_ms": 12000, "end_ms": 12500, "ticks": [12100]}]}]}
            ]}
            """,

        // SUBDIVIDED words carrying holds, so the polyline crosses aligned subdivision targets.
        ["subdividedTicks"] = """
            {"version": 2, "song_end_ms": 9000, "lines": [
              {"text": "beautiful tonight", "start_ms": 1000, "end_ms": 4600, "words": [
                {"text": "beautiful", "start_ms": 1000, "end_ms": 3000, "score": 1, "syllables": [{"start_ms": 1000}, {"start_ms": 1800}, {"start_ms": 2400}],
                 "holds": [{"char": 0, "start_ms": 1000, "end_ms": 1250, "ticks": [1000, 1100]}, {"char": 3, "start_ms": 1300, "end_ms": 1800, "ticks": [1400, 1533, 1600, 1700]}]},
                {"text": "tonight", "start_ms": 3000, "end_ms": 4600, "score": 1, "syllables": [{"start_ms": 3000}, {"start_ms": 3500}, {"start_ms": 4000}],
                 "holds": [{"char": 5, "start_ms": 4100, "end_ms": 4500, "ticks": [4200, 4300]}]}]},
              {"text": "forever", "start_ms": 5000, "end_ms": 6500, "words": [
                {"text": "forever", "start_ms": 5000, "end_ms": 6500, "score": 1, "holds": [{"char": 3, "start_ms": 5600, "end_ms": 6200, "ticks": [5700, 5800, 5900, 6000]}]}]}
            ]}
            """,

        // A held LAST letter of a line, to the line's own end, so the polyline's closing anchor is the
        // hold's; and a hold that starts before its letter's own target.
        ["lineEndHold"] = """
            {"version": 2, "song_end_ms": 8000, "lines": [
              {"text": "hey", "start_ms": 1000, "end_ms": 2000, "words": [{"text": "hey", "start_ms": 1000, "end_ms": 2000, "score": 1, "holds": [{"char": 2, "start_ms": 1600, "end_ms": 2000, "ticks": [1700, 1800, 1900]}]}]},
              {"text": "so long", "start_ms": 2000, "end_ms": 4000, "words": [
                {"text": "so", "start_ms": 2000, "end_ms": 2800, "score": 1, "holds": [{"char": 0, "start_ms": 2000, "end_ms": 2500, "ticks": [2250]}]},
                {"text": "long", "start_ms": 3000, "end_ms": 4000, "score": 1, "holds": [{"char": 3, "start_ms": 3600, "end_ms": 4000}]}]}
            ]}
            """,

        // HoldLiveParityTest's "pausedHold": holds either side of a rest.
        ["pausedHold"] = """
            {"version": 2, "song_end_ms": 30000, "lines": [
              {"text": "please go", "start_ms": 1000, "end_ms": 5000, "words": [
                {"text": "please", "start_ms": 1000, "end_ms": 4000, "score": 1, "pauses": [{"start_ms": 2000, "end_ms": 2500, "split": 3}],
                 "holds": [{"char": 2, "start_ms": 1700, "end_ms": 2000}, {"char": 3, "start_ms": 2500, "end_ms": 2900, "ticks": [2700]}]},
                {"text": "go", "start_ms": 4000, "end_ms": 5000, "score": 1}]}
            ]}
            """,
    };

    // HoldTrackWidth(duration, ref): durations either side of both clamps, and references that are
    // and are not exact in single precision.
    private static readonly double[][] track_probes =
    [
        [100, 25], [250, 25], [500, 25], [1234.5, 23.7], [2000, 31.25], [3000, 19.203125], [5000, 25], [777, 0.1], [0, 25],
    ];

    // HoldTrackWidth(duration, ref, pace, held), a NaN pace standing for null.
    private static readonly double[][] paced_probes =
    [
        [500, 25, 3, 20], [2000, 20, 4, 20], [10000, 20, 8, 20], [400, 25, 8, 25], [1234.5, 23.7, 10d / 3, 22.1],
        [600, 19.5, 1, 19.5], [300, 25, double.NaN, 25], [750, 24.3, 7.77, 18.9], [400, 22.6, 5.123456789, 0],
    ];

    // HoldPaceLettersPerSecond(start, previous, leadIn, ref), a NaN previous standing for null.
    private static readonly double[][] pace_probes =
    [
        [2600, 2500, 30, 25], [2600, 2000, 60, 30], [1000, double.NaN, 30, 25], [1000, 1000, 30, 25], [3000, 1000, 10, 25],
        [1000, 900, 30, 0], [1234.5, 1000.25, 47.3, 23.7], [5000, 4999.5, 1, 25], [2000, 1500, 88.88, 22.2], [900, 1000, 30, 25],
    ];

    // HoldTravelOffset(fill, track).
    private static readonly double[][] travel_probes =
    [
        [0.5, 50], [-1, 50], [2, 50], [0, 50], [0.3333, 55.1], [0.999, 123.4], [1.0000001, 10], [0.7, 0],
    ];

    // HeldSungOffset(fraction, advance, track).
    private static readonly double[][] held_sung_probes =
    [
        [0.5, 25, 50], [1.5, 25, 50], [-0.2, 25, 50], [0.37, 23.7, 55.1], [0.999999, 21.3, 0], [1d / 3, 19.203125, 47.25],
    ];

    private static readonly Lazy<Dictionary<string, string>> osu = new Lazy<Dictionary<string, string>>(() =>
        timing.ToDictionary(kvp => kvp.Key, kvp => LyricOsuFormat.GenerateOsu("Artist", "Title", "audio.mp3", "mapper", kvp.Value)));

    private static Beatmap decode(string name)
    {
        LyricBeatmapDecoder.Register();

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(osu.Value[name]));
        using var reader = new LineBufferedReader(stream);
        return typebeat.Game.Beatmaps.Formats.Decoder.GetDecoder<Beatmap>(reader).Decode(reader);
    }

    /// <summary>The live era's lines, as <see cref="HoldLiveParityTest"/> builds them: the playable map's lyric lines, with aligned targets.</summary>
    private static IReadOnlyList<TypingLine> typingLines(string name)
        => new FlatWorkingBeatmap(decode(name)).GetPlayableBeatmap(new TypeBeatRuleset().RulesetInfo, Array.Empty<Mod>())
                                                 .HitObjects.OfType<TypeBeatHitObject>().OrderBy(h => h.LineIndex)
                                                 .Select(h => TypingLine.FromLyricLine(h.Line, alignSubdivisionTargets: true, holdLeadIn: true)).ToArray();

    #endregion

    #region The browser side

    private static string json(double[][] probes)
        => "[" + string.Join(",", probes.Select(p => "[" + string.Join(",", p.Select(v => double.IsNaN(v) ? "null" : v.ToString("R", System.Globalization.CultureInfo.InvariantCulture))) + "]")) + "]";

    private static readonly Lazy<JsonElement> browser = new Lazy<JsonElement>(() =>
    {
        string payload = "{\"maps\":" + JsonSerializer.Serialize(osu.Value)
                                       + ",\"track\":" + json(track_probes)
                                       + ",\"paced\":" + json(paced_probes)
                                       + ",\"pace\":" + json(pace_probes)
                                       + ",\"travel\":" + json(travel_probes)
                                       + ",\"heldSung\":" + json(held_sung_probes) + "}";
        string path = Path.Combine(Path.GetTempPath(), $"typebeat-holddisplay-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, payload, new UTF8Encoding(false));

        try
        {
            return NodeHarness.Run("PlayerHoldParityHarness.cjs", path);
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

    private static double?[] nullableDoubles(JsonElement e)
        => e.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Null ? (double?)null : x.GetDouble()).ToArray();

    #endregion

    /// <summary>
    /// Every hold constant the browser carries that the game makes public: the track's letters per
    /// second and clamps, the pace clamps, the track's and a tick's sizes, the lift, the ghost, the
    /// pick-up and set-down lengths, and <see cref="HoldBar"/>'s blur and tick pop. Float constants
    /// compare as floats, which is what the desktop holds them in.
    /// </summary>
    [Test]
    public void TheHoldConstantsAreTheDesktops()
    {
        var k = browser.Value.GetProperty("constants");
        float single(string key) => (float)k.GetProperty(key).GetDouble();
        double dbl(string key) => k.GetProperty(key).GetDouble();

        Assert.Multiple(() =>
        {
            Assert.That(single("HOLD_TRACK_LETTERS_PER_SECOND"), Is.EqualTo(LyricLineDisplay.HOLD_TRACK_LETTERS_PER_SECOND));
            Assert.That(single("HOLD_TRACK_MIN_LETTERS"), Is.EqualTo(LyricLineDisplay.HOLD_TRACK_MIN_LETTERS));
            Assert.That(single("HOLD_TRACK_MAX_LETTERS"), Is.EqualTo(LyricLineDisplay.HOLD_TRACK_MAX_LETTERS));
            Assert.That(single("HOLD_TRACK_PACED_MIN_LETTERS"), Is.EqualTo(LyricLineDisplay.HOLD_TRACK_PACED_MIN_LETTERS));
            Assert.That(single("HOLD_PACE_MIN_LETTERS_PER_SECOND"), Is.EqualTo(LyricLineDisplay.HOLD_PACE_MIN_LETTERS_PER_SECOND));
            Assert.That(single("HOLD_PACE_MAX_LETTERS_PER_SECOND"), Is.EqualTo(LyricLineDisplay.HOLD_PACE_MAX_LETTERS_PER_SECOND));
            Assert.That(single("HOLD_TRACK_HEIGHT"), Is.EqualTo(LyricLineDisplay.HOLD_TRACK_HEIGHT));
            Assert.That(single("HOLD_TICK_WIDTH"), Is.EqualTo(LyricLineDisplay.HOLD_TICK_WIDTH));
            Assert.That(single("HOLD_TICK_HEIGHT"), Is.EqualTo(LyricLineDisplay.HOLD_TICK_HEIGHT));
            Assert.That(single("HOLD_LIFT_FRACTION"), Is.EqualTo(LyricLineDisplay.HOLD_LIFT_FRACTION));
            Assert.That(single("HOLD_LIFT_SCALE"), Is.EqualTo(LyricLineDisplay.HOLD_LIFT_SCALE));
            Assert.That(single("HOLD_GHOST_ALPHA"), Is.EqualTo(LyricLineDisplay.HOLD_GHOST_ALPHA));
            Assert.That(dbl("HOLD_PICK_UP_MS"), Is.EqualTo(LyricLineDisplay.HOLD_PICK_UP_MS));
            Assert.That(dbl("HOLD_SET_DOWN_MS"), Is.EqualTo(LyricLineDisplay.HOLD_SET_DOWN_MS));
            Assert.That(dbl("HOLD_BLUR_DURATION_MS"), Is.EqualTo(HoldBar.HOLD_BLUR_DURATION_MS));
            Assert.That(single("HOLD_BLUR_SIGMA"), Is.EqualTo(HoldBar.HOLD_BLUR_SIGMA));
            Assert.That(single("HOLD_TICK_POP_SCALE"), Is.EqualTo(HoldBar.TICK_POP_SCALE));
            Assert.That(dbl("HOLD_TICK_POP_MS"), Is.EqualTo(HoldBar.TICK_POP_MS));
            // The lifted shadow is TEXT_SHADOW_OFFSET grown by the (internal) lift factor of 1.75.
            Assert.That(dbl("HOLD_LIFT_SHADOW_EM"), Is.EqualTo(TypeBeatStyle.TEXT_SHADOW_OFFSET.Y * 1.75).Within(1e-6));
        });
    }

    /// <summary>
    /// The track's length, both overloads, the pace into a hold, the letter's travel and the playhead's
    /// offset across a held cell, probe for probe against the game's statics, exactly.
    /// </summary>
    [Test]
    public void TheHoldStaticsAreTheDesktops()
    {
        var root = browser.Value;
        var track = root.GetProperty("track");
        var paced = root.GetProperty("paced");
        var pace = nullableDoubles(root.GetProperty("pace"));
        var travel = root.GetProperty("travel");
        var heldSung = root.GetProperty("heldSung");

        Assert.Multiple(() =>
        {
            for (int i = 0; i < track_probes.Length; i++)
            {
                double[] p = track_probes[i];
                Assert.That(track[i].GetDouble(), Is.EqualTo((double)LyricLineDisplay.HoldTrackWidth(p[0], (float)p[1])), $"HoldTrackWidth({p[0]}, {p[1]})");
            }

            for (int i = 0; i < paced_probes.Length; i++)
            {
                double[] p = paced_probes[i];
                double? lettersPerSecond = double.IsNaN(p[2]) ? null : p[2];
                Assert.That(paced[i].GetDouble(), Is.EqualTo((double)LyricLineDisplay.HoldTrackWidth(p[0], (float)p[1], lettersPerSecond, (float)p[3])),
                    $"HoldTrackWidth({p[0]}, {p[1]}, {lettersPerSecond}, {p[3]})");
            }

            for (int i = 0; i < pace_probes.Length; i++)
            {
                double[] p = pace_probes[i];
                double? previous = double.IsNaN(p[1]) ? null : p[1];
                Assert.That(pace[i], Is.EqualTo(LyricLineDisplay.HoldPaceLettersPerSecond(p[0], previous, (float)p[2], (float)p[3])),
                    $"HoldPaceLettersPerSecond({p[0]}, {previous}, {p[2]}, {p[3]})");
            }

            for (int i = 0; i < travel_probes.Length; i++)
            {
                double[] p = travel_probes[i];
                Assert.That(travel[i].GetDouble(), Is.EqualTo((double)LyricLineDisplay.HoldTravelOffset((float)p[0], (float)p[1])), $"HoldTravelOffset({p[0]}, {p[1]})");
            }

            for (int i = 0; i < held_sung_probes.Length; i++)
            {
                double[] p = held_sung_probes[i];
                Assert.That(heldSung[i].GetDouble(), Is.EqualTo((double)LyricLineDisplay.HeldSungOffset(p[0], (float)p[1], (float)p[2])), $"HeldSungOffset({p[0]}, {p[1]}, {p[2]})");
            }
        });
    }

    /// <summary>
    /// The tail's result colour, as the tone the stylesheet maps: <see cref="LyricLineDisplay.HoldResultColour"/>
    /// gives Great and Ok the typed colour, Meh the caret's and Miss the error red.
    /// </summary>
    [Test]
    public void TheHoldResultTonesAreTheDesktopsColours()
    {
        static string toneOf(JudgementType type)
        {
            var colour = LyricLineDisplay.HoldResultColour(type);

            if (colour == TypeBeatStyle.TypedChar)
                return "typed";

            if (colour == TypeBeatStyle.Caret)
                return "meh";

            return colour == TypeBeatStyle.ErrorChar ? "miss" : "unmapped";
        }

        var tones = browser.Value.GetProperty("tones").EnumerateArray().Select(e => e.GetString()).ToArray();

        Assert.That(tones, Is.EqualTo(new[] { JudgementType.Great, JudgementType.Ok, JudgementType.Meh, JudgementType.Miss }.Select(toneOf).ToArray()));
    }

    /// <summary>
    /// The sung polyline across held cells: at every probe time the browser picked (each anchor, either
    /// side of it, and a grid over the line), the browser's sung position is
    /// <see cref="TypingLine.SungPositionAt"/>, and its closing anchor is <see cref="TypingLine.SweepEndTime"/>,
    /// on every line of every map. Each map must actually hold something, so the pin is never vacuous.
    /// </summary>
    [Test]
    public void TheSungPolylineCrossesHeldCellsAsTheDesktops()
    {
        var maps = browser.Value.GetProperty("maps");

        Assert.Multiple(() =>
        {
            foreach (string name in timing.Keys)
            {
                var lines = typingLines(name);
                var browserLines = maps.GetProperty(name).GetProperty("lines");
                int held = 0;

                Assert.That(browserLines.GetArrayLength(), Is.EqualTo(lines.Count), $"{name}: line count");

                for (int i = 0; i < lines.Count && i < browserLines.GetArrayLength(); i++)
                {
                    var line = lines[i];
                    var browserLine = browserLines[i];

                    held += browserLine.GetProperty("held").GetInt32();
                    Assert.That(browserLine.GetProperty("held").GetInt32(), Is.EqualTo(line.Cells.Count(c => c is { HoldStart: double s, HoldEnd: double e } && e - s > 0)),
                        $"{name}[{i}]: held cells");
                    Assert.That(browserLine.GetProperty("sweepEnd").GetDouble(), Is.EqualTo(line.SweepEndTime), $"{name}[{i}]: SweepEndTime");

                    var times = browserLine.GetProperty("times").EnumerateArray().Select(e => e.GetDouble()).ToArray();
                    var positions = browserLine.GetProperty("positions").EnumerateArray().Select(e => e.GetDouble()).ToArray();

                    for (int k = 0; k < times.Length; k++)
                        Assert.That(positions[k], Is.EqualTo(line.SungPositionAt(times[k])), $"{name}[{i}]: SungPositionAt({times[k]})");
                }

                Assert.That(held, Is.GreaterThan(0), $"{name}: the map holds something");
            }
        });
    }
}
