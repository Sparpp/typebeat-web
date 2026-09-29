using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Typebeat.Web.Pages;

namespace Typebeat.Web.Tests.Website;

/// <summary>
/// The card's difficulty stack (backlog 327, Pages/Shared/_BeatmapsetCard.cshtml): a set with two
/// or more live difficulties renders its star/WPM chip as a button drawing one star per difficulty
/// (the <see cref="BeatmapsetCardModel.StackCap"/> hardest), each in its own colour, hardest first,
/// and cycling them is card-diffs.js's job (pinned by CardDiffsScriptTest). A one-difficulty set
/// keeps the old chip byte for byte.
///
/// <para>The list rides on a column appended to <see cref="BeatmapsetCardSql"/>, which Dapper binds
/// POSITIONALLY, so the last test renders every page that hydrates a card through it.</para>
/// </summary>
public class CardDiffStackTest
{
    [OneTimeSetUp]
    public Task OneTimeSetUp() => PublicSiteSeed.EnsureSeededAsync();

    /// <summary>
    /// "Covered In Neon" (one diff, 5.0 stars, 125 WPM, no target): the chip exactly as it rendered
    /// before the stack existed, whitespace included (captured from the pre-change partial).
    /// </summary>
    [Test]
    public async Task OneDifficultySet_RendersTheOldChipVerbatim()
    {
        string card = Card(await GetHtml("/beatmapsets?q=Covered"), PublicSiteSeed.CoveredSetId);
        string colour = DifficultyColour.ForStars(5.0);

        string expected =
            "            <span class=\"chip\">\n" +
            $"                <span class=\"chip-value\" style=\"color:{colour}\">&#9733; 5.0</span>\n" +
            "                    <span class=\"chip-label\" title=\"target words per minute: the average WPM across the fastest fifth of the map's lyric lines of three words or more\">125 WPM</span>\n" +
            "            </span>\n" +
            "        </span>\n";

        Assert.Multiple(() =>
        {
            Assert.That(card.Replace("\r\n", "\n"), Does.Contain(expected));
            Assert.That(card, Does.Not.Contain("chip--diffs"));
            Assert.That(card, Does.Not.Contain("data-diff"));
        });
    }

    [Test]
    public async Task ThreeDifficultySet_RendersAButtonWithThreeStars_HardestFirst_EachInItsOwnColour()
    {
        string card = Card(await GetHtml("/beatmapsets?q=" + Uri.EscapeDataString("Triple Stack")), PublicSiteSeed.StackSetId);
        var button = Regex.Match(card, "<button type=\"button\" class=\"chip chip--diffs\"[^>]*>");

        Assert.That(button.Success, Is.True, "the multi-difficulty chip must be a real button");

        var stars = StarColours(card);
        var diffs = Diffs(button.Value);

        Assert.Multiple(() =>
        {
            Assert.That(stars, Is.EqualTo(new[]
            {
                DifficultyColour.ForStars(6.4), DifficultyColour.ForStars(4.1), DifficultyColour.ForStars(2.2),
            }), "hardest first, one colour each; the dropped 9.5 row must not be drawn");

            Assert.That(Regex.Matches(card, "style=\"--slot:(\\d)").Select(m => m.Groups[1].Value), Is.EqualTo(new[] { "0", "1", "2" }));
            Assert.That(button.Value, Does.Contain("--stack-n:3;"));
            Assert.That(button.Value, Does.Contain("data-diff-index=\"0\""));
            Assert.That(WebUtility.HtmlDecode(Attr(button.Value, "aria-label")),
                Is.EqualTo("stack hard: 6.4 stars, 150 WPM. 3 difficulties, click to cycle"));

            Assert.That(diffs.Select(d => d.GetProperty("id").GetInt64()),
                Is.EqualTo(new[] { PublicSiteSeed.StackHardId, PublicSiteSeed.StackNormalId, PublicSiteSeed.StackEasyId }));
            Assert.That(diffs.Select(d => d.GetProperty("stars").GetString()), Is.EqualTo(new[] { "6.4", "4.1", "2.2" }));
            // The per-row coalesce: normal carries a target (195) over its average (120).
            Assert.That(diffs.Select(d => d.GetProperty("wpm").GetString()), Is.EqualTo(new[] { "150", "195", "70" }));
            Assert.That(diffs.Select(d => d.GetProperty("colour").GetString()), Is.EqualTo(stars));
            Assert.That(diffs[1].GetProperty("label").GetString(),
                Is.EqualTo("stack normal: 4.1 stars, 195 WPM. 3 difficulties, click to cycle"));
        });
    }

    /// <summary>
    /// The pairing pin. The set-level rollup reads max(stars) 6.4 from "stack hard" and max(wpm)
    /// 195 from "stack normal"; the old chip printed those two side by side. The stack's default
    /// face is the hardest difficulty's OWN pair: 6.4 and 150.
    /// </summary>
    [Test]
    public async Task DefaultFace_PairsTheRatingAndWpmOfTheSameHardestDifficulty()
    {
        string card = Card(await GetHtml("/beatmapsets?q=" + Uri.EscapeDataString("Triple Stack")), PublicSiteSeed.StackSetId);

        var rating = Regex.Match(card, "data-diff-rating>([^<]*)<");
        var wpm = Regex.Match(card, "data-diff-wpm[^>]*>([^<]*)<");
        var colour = Regex.Match(card, "data-diff-colour style=\"color:([^\"]+)\"");

        Assert.Multiple(() =>
        {
            Assert.That(rating.Groups[1].Value, Is.EqualTo("6.4"));
            Assert.That(wpm.Groups[1].Value, Is.EqualTo("150 WPM"));
            Assert.That(colour.Groups[1].Value, Is.EqualTo(DifficultyColour.ForStars(6.4)));
            Assert.That(card, Does.Not.Contain(">195 WPM<"), "the fastest difficulty's pace must not ride the hardest one's rating");
            // No deep link until a click puts another difficulty on top.
            Assert.That(card, Does.Contain($"href=\"/beatmapsets/{PublicSiteSeed.StackSetId}\""));
            Assert.That(card, Does.Contain($"href=\"/play?set={PublicSiteSeed.StackSetId}\""));
        });
    }

    [Test]
    public async Task SixDifficultySet_DrawsTheFiveHardest_ButCarriesAndCountsAllSix()
    {
        string card = Card(await GetHtml("/beatmapsets?q=Staircase"), PublicSiteSeed.StaircaseSetId);
        var button = Regex.Match(card, "<button type=\"button\" class=\"chip chip--diffs\"[^>]*>").Value;
        var diffs = Diffs(button);

        Assert.Multiple(() =>
        {
            Assert.That(StarColours(card), Is.EqualTo(new[] { 6.5, 5.5, 4.5, 3.5, 2.5 }.Select(DifficultyColour.ForStars)));
            Assert.That(button, Does.Contain("--stack-n:5;"));
            Assert.That(diffs.Select(d => d.GetProperty("id").GetInt64()), Is.EqualTo(PublicSiteSeed.StaircaseDiffIds));
            Assert.That(WebUtility.HtmlDecode(Attr(button, "aria-label")),
                Is.EqualTo("step 6: 6.5 stars, 160 WPM. 6 difficulties, click to cycle"));
        });
    }

    [Test]
    public void Model_ParsesTheJsonList_AndAnEmptyOneIsNoStack()
    {
        var withDiffs = Model("""[{"id":7,"name":"b","stars":3.25,"wpm":null},{"id":3,"name":"a","stars":1,"wpm":88.6}]""");
        var none = Model("[]");

        Assert.Multiple(() =>
        {
            Assert.That(withDiffs.Diffs, Is.EqualTo(new[] { new CardDifficulty(7, "b", 3.25, null), new CardDifficulty(3, "a", 1, 88.6) }));
            Assert.That(withDiffs.HasDiffStack, Is.True);
            Assert.That(withDiffs.StarsTextWidth, Is.EqualTo(4)); // "3.25"
            Assert.That(withDiffs.WpmTextWidth, Is.EqualTo(2));   // "89"
            Assert.That(none.Diffs, Is.Empty);
            Assert.That(none.HasDiffStack, Is.False);
        });
    }

    /// <summary>Every page that hydrates cards through BeatmapsetCardSql still binds (positional).</summary>
    [Test]
    public async Task EveryCardConsumer_StillRenders()
    {
        foreach (string url in new[] { "/", "/beatmapsets", "/play", $"/users/{PublicSiteSeed.MapperId}" })
            Assert.That(await GetHtml(url), Does.Contain("class=\"bset-card\""), url);

        // /play lists by play count, capped: the stack sets are playable, so they are on it.
        Assert.That(await GetHtml("/play"), Does.Contain("chip chip--diffs"));
    }

    private static BeatmapsetCardModel Model(string diffsJson) => new(
        1, "t", "a", null, null, "c", 1, null, null, "ranked", 0, 0, 0, DateTime.UtcNow, 0, null,
        false, false, false, false, false, diffsJson);

    private static List<JsonElement> Diffs(string buttonTag)
        => JsonDocument.Parse(WebUtility.HtmlDecode(Attr(buttonTag, "data-diffs"))).RootElement.EnumerateArray().ToList();

    private static string[] StarColours(string card)
        => Regex.Matches(card, "data-diff-star style=\"--slot:\\d;color:([^\"]+)\"").Select(m => m.Groups[1].Value).ToArray();

    private static string Attr(string tag, string name)
    {
        var m = Regex.Match(tag, $"{name}=\"([^\"]*)\"");
        Assert.That(m.Success, Is.True, $"no {name} on {tag}");
        return m.Groups[1].Value;
    }

    /// <summary>The one card article for <paramref name="setId"/>.</summary>
    private static string Card(string html, long setId)
    {
        var m = Regex.Match(html, $"<article class=\"bset-card\" data-set-id=\"{setId}\">.*?</article>", RegexOptions.Singleline);
        Assert.That(m.Success, Is.True, $"no card for set {setId}");
        return m.Value;
    }

    private static async Task<string> GetHtml(string url)
    {
        using var response = await WebsiteFixture.Client.GetAsync(url);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), url);
        return await response.Content.ReadAsStringAsync();
    }
}
