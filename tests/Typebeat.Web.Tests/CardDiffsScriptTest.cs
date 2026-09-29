using System.Text.Json;

namespace Typebeat.Web.Tests;

/// <summary>
/// The card's difficulty stack as the browser runs it (wwwroot/js/card-diffs.js, backlog 327),
/// driven through the shipped script by Js/CardDiffsHarness.cjs against a fake card. What the
/// server renders into the button is pinned in <c>Website/CardDiffStackTest</c>; this is the half
/// that rotates it: the order after one, two and N clicks, the wrap, the text, colour and label
/// swap, and the deep links that follow the difficulty on top.
/// </summary>
[TestFixture]
public class CardDiffsScriptTest
{
    private static JsonElement root;

    [OneTimeSetUp]
    public void RunHarness() => root = JsHarness.Run("CardDiffsHarness.cjs");

    private static JsonElement state(string scenario, int click) => root.GetProperty(scenario)[click - 1];

    private static int[] ints(JsonElement e, string key) => e.GetProperty(key).EnumerateArray().Select(x => x.GetInt32()).ToArray();

    private static string?[] strings(JsonElement e, string key) => e.GetProperty(key).EnumerateArray().Select(x => x.GetString()).ToArray();

    private static string str(JsonElement e, string key) => e.GetProperty(key).GetString()!;

    [Test]
    public void OneClick_BringsTheNextDifficultyForward_AndSendsTheTopStarToTheBack()
    {
        var s = state("three", 1);

        Assert.Multiple(() =>
        {
            Assert.That(s.GetProperty("index").GetInt32(), Is.EqualTo(1));
            // Star elements keep their identity (and colour) and move: the old top one to the back.
            Assert.That(ints(s, "slots"), Is.EqualTo(new[] { 2, 0, 1 }));
            Assert.That(strings(s, "starColours"), Is.EqualTo(new[] { "c11", "c12", "c13" }));
            Assert.That(str(s, "rating"), Is.EqualTo("4.1"));
            Assert.That(str(s, "colour"), Is.EqualTo("c12"));
            Assert.That(str(s, "wpm"), Is.EqualTo("195 WPM"));
            Assert.That(str(s, "label"), Is.EqualTo("L12"));
            Assert.That(str(s, "titleHref"), Is.EqualTo("/beatmapsets/42?diff=12"));
            Assert.That(str(s, "playHref"), Is.EqualTo("/play?set=42&diff=12"));
        });
    }

    [Test]
    public void TwoClicks_ReachTheThird_AndAMissingPaceEmptiesTheWpm()
    {
        var s = state("three", 2);

        Assert.Multiple(() =>
        {
            Assert.That(s.GetProperty("index").GetInt32(), Is.EqualTo(2));
            Assert.That(ints(s, "slots"), Is.EqualTo(new[] { 1, 2, 0 }));
            Assert.That(str(s, "rating"), Is.EqualTo("2.2"));
            Assert.That(str(s, "colour"), Is.EqualTo("c13"));
            Assert.That(str(s, "wpm"), Is.EqualTo(""));
            Assert.That(str(s, "titleHref"), Is.EqualTo("/beatmapsets/42?diff=13"));
            Assert.That(str(s, "playHref"), Is.EqualTo("/play?set=42&diff=13"));
        });
    }

    [Test]
    public void NClicks_WrapToTheHardest_AndDropTheDeepLinks()
    {
        var wrapped = state("three", 3);
        var again = state("three", 4);

        Assert.Multiple(() =>
        {
            Assert.That(wrapped.GetProperty("index").GetInt32(), Is.EqualTo(0));
            Assert.That(ints(wrapped, "slots"), Is.EqualTo(new[] { 0, 1, 2 }));
            Assert.That(str(wrapped, "rating"), Is.EqualTo("6.4"));
            Assert.That(str(wrapped, "colour"), Is.EqualTo("c11"));
            Assert.That(str(wrapped, "wpm"), Is.EqualTo("150 WPM"));
            Assert.That(str(wrapped, "label"), Is.EqualTo("L11"));
            // The default difficulty carries no ?diff=, so the hrefs are exactly the server's again.
            Assert.That(str(wrapped, "titleHref"), Is.EqualTo("/beatmapsets/42"));
            Assert.That(str(wrapped, "playHref"), Is.EqualTo("/play?set=42"));

            // And the cycle goes round again from there.
            Assert.That(again.GetProperty("index").GetInt32(), Is.EqualTo(1));
            Assert.That(str(again, "rating"), Is.EqualTo("4.1"));
        });
    }

    /// <summary>
    /// Six difficulties, five stars drawn: the cycle still visits all six, and the star that wraps
    /// to the back takes the colour of the difficulty that is next in line behind the others.
    /// </summary>
    [Test]
    public void MoreDifficultiesThanStars_CyclesAllOfThem_AndRecoloursTheWrappedStar()
    {
        Assert.Multiple(() =>
        {
            var one = state("six", 1);
            Assert.That(ints(one, "slots"), Is.EqualTo(new[] { 4, 0, 1, 2, 3 }));
            Assert.That(strings(one, "starColours"), Is.EqualTo(new[] { "c26", "c22", "c23", "c24", "c25" }));
            Assert.That(str(one, "rating"), Is.EqualTo("5.5"));

            var five = state("six", 5);
            Assert.That(five.GetProperty("index").GetInt32(), Is.EqualTo(5), "the sixth, undrawn at first, gets its turn on top");
            Assert.That(str(five, "rating"), Is.EqualTo("1.5"));
            Assert.That(str(five, "colour"), Is.EqualTo("c26"));
            Assert.That(strings(five, "starColours"), Is.EqualTo(new[] { "c26", "c21", "c22", "c23", "c24" }));
            Assert.That(ints(five, "slots"), Is.EqualTo(new[] { 0, 1, 2, 3, 4 }));

            var six = state("six", 6);
            Assert.That(six.GetProperty("index").GetInt32(), Is.EqualTo(0));
            Assert.That(ints(six, "slots"), Is.EqualTo(new[] { 4, 0, 1, 2, 3 }));
            Assert.That(strings(six, "starColours"), Is.EqualTo(new[] { "c25", "c21", "c22", "c23", "c24" }));
            Assert.That(str(six, "titleHref"), Is.EqualTo("/beatmapsets/42"));

            var seven = state("six", 7);
            Assert.That(ints(seven, "slots"), Is.EqualTo(new[] { 3, 4, 0, 1, 2 }));
            Assert.That(strings(seven, "starColours"), Is.EqualTo(new[] { "c25", "c26", "c22", "c23", "c24" }));
        });
    }

    [Test]
    public void DeepLinks_AddSwapAndDropTheDiffParameter()
    {
        var w = root.GetProperty("withDiff");

        Assert.Multiple(() =>
        {
            Assert.That(str(w, "title"), Is.EqualTo("/beatmapsets/42?diff=9"));
            Assert.That(str(w, "titleBack"), Is.EqualTo("/beatmapsets/42"));
            Assert.That(str(w, "play"), Is.EqualTo("/play?set=42&diff=9"));
            Assert.That(str(w, "playSwap"), Is.EqualTo("/play?set=42&diff=10"));
            Assert.That(str(w, "playBack"), Is.EqualTo("/play?set=42"));
        });
    }
}
