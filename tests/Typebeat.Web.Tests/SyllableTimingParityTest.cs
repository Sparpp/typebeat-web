using System.Text.Json;

namespace Typebeat.Web.Tests;

/// <summary>
/// Fidelity guard for the browser scoring core's syllable-aware per-char timing. The hand-written
/// wwwroot/js/typebeat-core.js must reproduce the desktop game's TypingLine.syllableCharTarget
/// bit-for-bit, or /play scores on subdivided maps diverge from desktop and corrupt the shared
/// leaderboards. Golden values mirror typebeat-osu's TypingEngineTest syllable region:
///   "abcd" over [1000,2000] with a boundary at 1200 -> 1000/1100/1200/1600 through the bare
///   helper (index-even spread, no cut passed); a decoded "abcd" takes the derived cut a|bcd since
///   PR 5 and reads 1000/1200/1466.667/1733.333;
///   "abcdef" over [0,1200] with boundaries 300,900 -> 0/150/300/600/900/1050 (2 chars/segment);
///   an undivided word stays the flat ramp 1000/1250/1500/1750.
/// A Node harness loads the actual shipped JS and emits the computed targets; this asserts them.
/// </summary>
public class SyllableTimingParityTest
{
    private static JsonElement RunHarness() => JsHarness.Run("CoreSyllableHarness.cjs");

    private static double[] Arr(JsonElement root, string key) => JsHarness.Doubles(root, key);

    [Test]
    public void JsSyllableTargetsMatchGameGoldenValues()
    {
        var root = RunHarness();

        Assert.Multiple(() =>
        {
            // One boundary, off-centre: non-uniform ramp (caret slows in the longer 2nd syllable).
            Assert.That(Arr(root, "divided"), Is.EqualTo(new[] { 1000d, 1100d, 1200d, 1600d }));
            // No boundary: exact flat interpolation, unchanged for every existing map.
            Assert.That(Arr(root, "flat"), Is.EqualTo(new[] { 1000d, 1250d, 1500d, 1750d }));
            // Two boundaries, uneven segment durations 300/600/300; 2 chars per segment, monotonic.
            Assert.That(Arr(root, "multi"), Is.EqualTo(new[] { 0d, 150d, 300d, 600d, 900d, 1050d }));
        });

        // Monotonic non-decreasing across the whole subdivided word.
        var multi = Arr(root, "multi");
        for (int i = 1; i < multi.Length; i++)
            Assert.That(multi[i], Is.GreaterThanOrEqualTo(multi[i - 1]));
    }

    [Test]
    public void JsBuildBeatmapDecodesSyllablesFromOsuJson()
    {
        var root = RunHarness();

        Assert.Multiple(() =>
        {
            // Proves words[].syllables[] in the served .osu decodes into boundaries and warps the
            // cells (not just the direct helper); the field flows through parseLyricOsu/buildBeatmap.
            // Since PR 5 the live targets follow the word's EFFECTIVE letter cut (the C#'s
            // AlignSubdivisionTargets, extended CONFIG bit 3): "abcd" carries no split_chars, so the
            // derived split a|bcd ([1]) puts one cell before the 1200 boundary and three after it,
            // where the index-even spread of the older era gave 1000/1100/1200/1600.
            Assert.That(Arr(root, "dividedCells"), Is.EqualTo(new[] { 1000d, 1200d, 1200d + 800d / 3, 1200d + 1600d / 3 }).Within(1e-9));
            // A word with no syllables[] stays the flat ramp end-to-end.
            Assert.That(Arr(root, "flatCells"), Is.EqualTo(new[] { 1000d, 1250d, 1500d, 1750d }));
        });
    }
}
