using System.Text.Json;

namespace Typebeat.Web.Tests;

/// <summary>
/// The map's TRACK GAIN on /play (backlog 315). PR 1 gave every map a linear gain in [0, 4], read
/// from <c>[Metadata] AudioGain</c> by the game's <c>LegacyBeatmapDecoder</c> and played on the
/// desktop as a scaled copy of the track whose samples are clamped to full scale before any volume
/// stage. The browser reads the same key in <c>parseLyricOsu</c> and bakes the gain into the decoded
/// samples with <c>applyTrackGain</c>.
///
/// <para>These are the self-contained pins: the read's default, its clamp at both ends, and its
/// all-or-nothing number syntax (C# <c>double.TryParse</c> rejects trailing junk that
/// <c>parseFloat</c> would accept), and the scale and clamp. The cross-parser pin, the same files
/// through the game's production decoder, lives in WireCompat's <c>LyricParserParityTest</c>.</para>
/// </summary>
public class AudioGainTest
{
    private static JsonElement Harness() => JsHarness.Run("CoreAudioGainHarness.cjs");

    private static double[] Doubles(JsonElement e) => e.EnumerateArray().Select(x => x.GetDouble()).ToArray();

    [Test]
    public void AnAbsentKeyReadsAsTheDefault()
    {
        var root = Harness();

        Assert.Multiple(() =>
        {
            Assert.That(root.GetProperty("defaultGain").GetDouble(), Is.EqualTo(1));
            Assert.That(root.GetProperty("maxGain").GetDouble(), Is.EqualTo(4));
            Assert.That(root.GetProperty("absent").GetDouble(), Is.EqualTo(1));
            Assert.That(root.GetProperty("builtAbsent").GetDouble(), Is.EqualTo(1), "buildBeatmap carries the default");
            Assert.That(root.GetProperty("builtBoosted").GetDouble(), Is.EqualTo(2.5), "buildBeatmap carries the gain");
            Assert.That(root.GetProperty("inGeneral").GetDouble(), Is.EqualTo(1), "a [General] AudioGain is not the map's gain");
        });
    }

    [Test]
    public void TheReadClampsAndMirrorsTryParse()
    {
        var root = Harness();
        string[] raw = root.GetProperty("raw").EnumerateArray().Select(x => x.GetString()!).ToArray();
        double[] parsed = Doubles(root.GetProperty("parsed"));

        var expected = new Dictionary<string, double>
        {
            ["2"] = 2, ["0.5"] = 0.5, ["4"] = 4, ["0"] = 0, [".5"] = 0.5, ["1."] = 1, ["+2"] = 2, ["2.5e-1"] = 0.25,
            // The clamp, at both ends.
            ["4.5"] = 4, ["1e1"] = 4, ["-1"] = 0, ["-0.25"] = 0,
            // TryParse takes the whole string or nothing, so junk leaves the default in place.
            ["2x"] = 1, ["1.5.2"] = 1, ["abc"] = 1, [""] = 1, ["1,5"] = 1, ["0x2"] = 1,
            // Infinity (spelled, or out of range) is a number to TryParse, and clamps.
            ["Infinity"] = 4, ["-Infinity"] = 0, ["1e400"] = 4,
            // NaN is refused here although TryParse accepts it (see parseAudioGain).
            ["NaN"] = 1,
            // The value is trimmed before it is read, on both sides.
            [" 3 "] = 3,
        };

        Assert.That(raw, Is.EquivalentTo(expected.Keys), "the harness and this table cover the same values");

        Assert.Multiple(() =>
        {
            for (int i = 0; i < raw.Length; i++)
                Assert.That(parsed[i], Is.EqualTo(expected[raw[i]]), $"AudioGain:{raw[i]}");

            Assert.That(root.GetProperty("goodThenJunk").GetDouble(), Is.EqualTo(2), "a failed read leaves the earlier value");
            Assert.That(root.GetProperty("junkThenGood").GetDouble(), Is.EqualTo(3));
        });
    }

    [Test]
    public void TheGainScalesAndClipsAtFullScale()
    {
        var root = Harness();
        var gain2 = root.GetProperty("gain2");
        var half = root.GetProperty("gainHalf");
        var one = root.GetProperty("gain1");
        var zero = root.GetProperty("gain0");

        Assert.Multiple(() =>
        {
            // x2: 0.75 and -0.75 clip, 0.5 lands exactly ON full scale (not a clip), 1 clips.
            Assert.That(Doubles(gain2.GetProperty("left")), Is.EqualTo(new[] { 0.5, -0.5, 1, -1 }));
            Assert.That(Doubles(gain2.GetProperty("right")), Is.EqualTo(new[] { 1d, -1, 0, 1 }));
            Assert.That(gain2.GetProperty("clipped").GetInt32(), Is.EqualTo(3));

            Assert.That(Doubles(half.GetProperty("left")), Is.EqualTo(new[] { 0.125, -0.125, 0.375, -0.375 }));
            Assert.That(Doubles(half.GetProperty("right")), Is.EqualTo(new[] { 0.25, -0.25, 0, 0.5 }));
            Assert.That(half.GetProperty("clipped").GetInt32(), Is.EqualTo(0));

            // The default gain leaves the samples untouched, full-scale sample included.
            Assert.That(Doubles(one.GetProperty("left")), Is.EqualTo(new[] { 0.25, -0.25, 0.75, -0.75 }));
            Assert.That(Doubles(one.GetProperty("right")), Is.EqualTo(new[] { 0.5, -0.5, 0, 1 }));
            Assert.That(one.GetProperty("clipped").GetInt32(), Is.EqualTo(0));

            Assert.That(Doubles(zero.GetProperty("left")), Is.EqualTo(new[] { 0d, 0, 0, 0 }));
            Assert.That(Doubles(zero.GetProperty("right")), Is.EqualTo(new[] { 0d, 0, 0, 0 }));
        });
    }
}
