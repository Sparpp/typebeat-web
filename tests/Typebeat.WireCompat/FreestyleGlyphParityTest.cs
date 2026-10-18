using System.Text.Json;
using typebeat.Game.Rulesets.TypeBeat.UI;

namespace Typebeat.WireCompat;

/// <summary>
/// The cross-repo pin on the freestyle SHIMMER: the browser's <c>freestyleGlyph</c> and
/// <c>freestyleTick</c> (typebeat-core.js) are a port of the desktop's
/// <see cref="FreestyleGlyphs.Glyph"/> and <see cref="FreestyleGlyphs.TickFor"/>, and this holds the
/// browser's glyph for a grid of (tick, position) pairs, and its tick for a table of clock times,
/// against the game's own functions over the fixed-width pool the site renders with. Display only,
/// so a drift here moves no score; it would still show a /play player a different shimmer from the
/// desktop's, which the comment on the browser's copy promises it does not.
/// </summary>
[TestFixture]
public class FreestyleGlyphParityTest
{
    [Test]
    public void TheBrowserShimmersThroughTheGamesGlyphs()
    {
        var browser = NodeHarness.Run("CoreFreestyleHarness.cjs").GetProperty("shimmerGrid");

        Assert.That(browser.GetProperty("intervalMs").GetDouble(), Is.EqualTo(FreestyleGlyphs.SHIMMER_INTERVAL_MS), "one shimmer interval on both clients");

        var ticks = browser.GetProperty("ticks").EnumerateArray().Select(t => t.GetInt32()).ToArray();
        var rows = browser.GetProperty("glyphs").EnumerateArray().Select(r => r.GetString()!).ToArray();
        int positions = browser.GetProperty("positions").GetInt32();

        Assert.That(rows, Has.Length.EqualTo(ticks.Length));

        Assert.Multiple(() =>
        {
            for (int i = 0; i < Math.Min(ticks.Length, rows.Length); i++)
            {
                Assert.That(rows[i], Has.Length.EqualTo(positions), $"tick {ticks[i]}: one glyph per position");

                for (int position = 0; position < Math.Min(positions, rows[i].Length); position++)
                    Assert.That(rows[i][position], Is.EqualTo(FreestyleGlyphs.Glyph(FreestyleGlyphs.FIXED_WIDTH_POOL, ticks[i], position)), $"tick {ticks[i]}, position {position}");
            }
        });
    }

    [Test]
    public void TheBrowserTicksOnTheGamesClock()
    {
        var browser = NodeHarness.Run("CoreFreestyleHarness.cjs").GetProperty("shimmerGrid");

        var times = browser.GetProperty("times").EnumerateArray().Select(t => t.GetDouble()).ToArray();
        var ticks = browser.GetProperty("timeTicks").EnumerateArray().Select(t => t.GetInt32()).ToArray();

        Assert.That(ticks, Has.Length.EqualTo(times.Length));

        Assert.Multiple(() =>
        {
            for (int i = 0; i < Math.Min(times.Length, ticks.Length); i++)
                Assert.That(ticks[i], Is.EqualTo(FreestyleGlyphs.TickFor(times[i])), $"time {times[i]} ms");
        });
    }
}
