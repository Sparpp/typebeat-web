using typebeat.Game.Rulesets.TypeBeat.Configuration;

namespace Typebeat.WireCompat;

/// <summary>
/// The browser's two caret glides (the player caret and the sung playhead) are the desktop's
/// CaretSmoothing half time at its default, not a pair of constants of their own: both JS values
/// are read off the shipped player and held to the game's
/// <see cref="TypeBeatRulesetConfigManager.DEFAULT_CARET_SMOOTHING_MS"/>.
/// </summary>
[TestFixture]
public class CaretSmoothingParityTest
{
    [Test]
    public void BrowserCaretGlidesAreTheDesktopDefaultSmoothing()
    {
        var root = NodeHarness.Run("PlayerDisplayHarness.cjs");

        Assert.Multiple(() =>
        {
            Assert.That(root.GetProperty("caretDampHalfTime").GetDouble(), Is.EqualTo(TypeBeatRulesetConfigManager.DEFAULT_CARET_SMOOTHING_MS));
            Assert.That(root.GetProperty("sungDampHalfTime").GetDouble(), Is.EqualTo(TypeBeatRulesetConfigManager.DEFAULT_CARET_SMOOTHING_MS));
        });
    }
}
