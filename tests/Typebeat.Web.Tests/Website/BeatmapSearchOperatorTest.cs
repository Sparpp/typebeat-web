using System.Net;
using System.Text.RegularExpressions;

namespace Typebeat.Web.Tests.Website;

/// <summary>
/// The typed <c>key:value</c> search operators on /beatmapsets, exercised end-to-end against the
/// test database via the two fixed-fingerprint fixtures (<see cref="PublicSiteSeed.OpAlphaId"/> /
/// <see cref="PublicSiteSeed.OpBravoId"/>, both tagged "operatorset"). Free text scopes to the
/// pair; the operator narrows to one, proving real SQL filtering.
/// </summary>
public class BeatmapSearchOperatorTest
{
    [OneTimeSetUp]
    public Task OneTimeSetUp() => PublicSiteSeed.EnsureSeededAsync();

    [Test]
    public async Task Title_Operator_MatchesOneSet()
    {
        string html = await GetHtml("/beatmapsets?q=title:bravo");

        Assert.Multiple(() =>
        {
            Assert.That(Ids(html), Does.Contain(PublicSiteSeed.OpBravoId));
            Assert.That(Ids(html), Does.Not.Contain(PublicSiteSeed.OpAlphaId));
        });
    }

    [Test]
    public async Task Artist_Operator_MatchesOneSet()
    {
        var ids = Ids(await GetHtml("/beatmapsets?q=" + Enc("artist:piano")));

        Assert.Multiple(() =>
        {
            Assert.That(ids, Does.Contain(PublicSiteSeed.OpBravoId));
            Assert.That(ids, Does.Not.Contain(PublicSiteSeed.OpAlphaId));
        });
    }

    [Test]
    public async Task StarRange_NarrowsWithinScope()
    {
        var high = Ids(await GetHtml("/beatmapsets?q=" + Enc("operatorset star:>6")));
        var low = Ids(await GetHtml("/beatmapsets?q=" + Enc("operatorset star:4-5")));

        Assert.Multiple(() =>
        {
            Assert.That(high, Does.Contain(PublicSiteSeed.OpBravoId));
            Assert.That(high, Does.Not.Contain(PublicSiteSeed.OpAlphaId));
            Assert.That(low, Does.Contain(PublicSiteSeed.OpAlphaId));
            Assert.That(low, Does.Not.Contain(PublicSiteSeed.OpBravoId));
        });
    }

    [Test]
    public async Task Wpm_ComparatorFilters()
    {
        var fast = Ids(await GetHtml("/beatmapsets?q=" + Enc("operatorset wpm:>150")));
        var slow = Ids(await GetHtml("/beatmapsets?q=" + Enc("operatorset wpm:<150")));

        Assert.Multiple(() =>
        {
            Assert.That(fast, Does.Contain(PublicSiteSeed.OpBravoId));
            Assert.That(fast, Does.Not.Contain(PublicSiteSeed.OpAlphaId));
            Assert.That(slow, Does.Contain(PublicSiteSeed.OpAlphaId));
            Assert.That(slow, Does.Not.Contain(PublicSiteSeed.OpBravoId));
        });
    }

    [Test]
    public async Task Cpm_DerivedFromStoredWpm_Filters()
    {
        // Since LyricPace v15 a stored wpm IS cpm/5, so cpm: is wpm*5 and needs no counts at all:
        // Alpha cpm = 100*5 = 500, Bravo cpm = 200*5 = 1000.
        var ids = Ids(await GetHtml("/beatmapsets?q=" + Enc("operatorset cpm:>600")));

        // NON-VACUITY, and the reason this test is worth having: the old derivation was
        // wpm * char_count / word_count, which was the pre-v15 identity exactly. Bravo's seeded
        // counts make its average word 700/100 = 7 cells, so that expression reads 1400 for it
        // while the truth is 1000. A 1200 cut therefore separates the two derivations outright,
        // and this assertion fails on the old one. Alpha cannot show the bug: its average word is
        // exactly 5 cells, the one length at which the two expressions agree.
        var overTwelveHundred = Ids(await GetHtml("/beatmapsets?q=" + Enc("operatorset cpm:>1200")));

        Assert.Multiple(() =>
        {
            Assert.That(ids, Does.Contain(PublicSiteSeed.OpBravoId));
            Assert.That(ids, Does.Not.Contain(PublicSiteSeed.OpAlphaId));
            Assert.That(overTwelveHundred, Does.Not.Contain(PublicSiteSeed.OpBravoId));
            Assert.That(overTwelveHundred, Does.Not.Contain(PublicSiteSeed.OpAlphaId));
        });
    }

    [Test]
    public async Task Target_FiltersOnItsOwnColumn_NotTheStoredAverage()
    {
        // Alpha's target is 180 against an average of 100; Bravo's is 130 against an average of
        // 200. The two orderings are therefore OPPOSITE, which is the whole point of the fixture:
        // target:>150 has to pick Alpha where wpm:>150 picks Bravo, so an implementation that
        // quietly read b.wpm (or coalesced onto it) fails here rather than passing by luck.
        var fastTarget = Ids(await GetHtml("/beatmapsets?q=" + Enc("operatorset target:>150")));
        var slowTarget = Ids(await GetHtml("/beatmapsets?q=" + Enc("operatorset target:<150")));

        Assert.Multiple(() =>
        {
            Assert.That(fastTarget, Does.Contain(PublicSiteSeed.OpAlphaId));
            Assert.That(fastTarget, Does.Not.Contain(PublicSiteSeed.OpBravoId));
            Assert.That(slowTarget, Does.Contain(PublicSiteSeed.OpBravoId));
            Assert.That(slowTarget, Does.Not.Contain(PublicSiteSeed.OpAlphaId));
        });
    }

    [Test]
    public async Task Length_MmSs_Filters()
    {
        // Alpha 90s, Bravo 240s.
        var ids = Ids(await GetHtml("/beatmapsets?q=" + Enc("operatorset length:>3:00")));

        Assert.Multiple(() =>
        {
            Assert.That(ids, Does.Contain(PublicSiteSeed.OpBravoId));
            Assert.That(ids, Does.Not.Contain(PublicSiteSeed.OpAlphaId));
        });
    }

    [Test]
    public async Task Date_Year_And_Comparator_Filter()
    {
        var in2024 = Ids(await GetHtml("/beatmapsets?q=" + Enc("operatorset date:2024")));
        var before2023 = Ids(await GetHtml("/beatmapsets?q=" + Enc("operatorset date:<2023")));

        Assert.Multiple(() =>
        {
            // Alpha submitted 2024-03-15, Bravo 2022-11-01.
            Assert.That(in2024, Does.Contain(PublicSiteSeed.OpAlphaId));
            Assert.That(in2024, Does.Not.Contain(PublicSiteSeed.OpBravoId));
            Assert.That(before2023, Does.Contain(PublicSiteSeed.OpBravoId));
            Assert.That(before2023, Does.Not.Contain(PublicSiteSeed.OpAlphaId));
        });
    }

    [Test]
    public async Task Lyrics_Operator_MatchesTheMapSingingTheWord()
    {
        var ids = Ids(await GetHtml("/beatmapsets?q=" + Enc("operatorset lyrics:skyline")));

        Assert.Multiple(() =>
        {
            Assert.That(ids, Does.Contain(PublicSiteSeed.OpAlphaId));
            Assert.That(ids, Does.Not.Contain(PublicSiteSeed.OpBravoId));
        });
    }

    [Test]
    public async Task Lyrics_Operator_IsCaseInsensitive()
    {
        var ids = Ids(await GetHtml("/beatmapsets?q=" + Enc("operatorset lyrics:SKYLINE")));

        Assert.Multiple(() =>
        {
            Assert.That(ids, Does.Contain(PublicSiteSeed.OpAlphaId));
            Assert.That(ids, Does.Not.Contain(PublicSiteSeed.OpBravoId));
        });
    }

    [Test]
    public async Task Lyrics_MultiWordValue_RequiresEveryWord()
    {
        // Both fixtures sing "night"; only Bravo also sings "rain". A pair that no single
        // fixture sings together must match neither, proving the words AND rather than OR.
        var night = Ids(await GetHtml("/beatmapsets?q=" + Enc("operatorset lyrics:night")));
        var bravoOnly = Ids(await GetHtml("/beatmapsets?q=" + Enc("operatorset lyrics:\"night rain\"")));
        var neither = Ids(await GetHtml("/beatmapsets?q=" + Enc("operatorset lyrics:\"skyline rain\"")));

        Assert.Multiple(() =>
        {
            Assert.That(night, Does.Contain(PublicSiteSeed.OpAlphaId));
            Assert.That(night, Does.Contain(PublicSiteSeed.OpBravoId));
            Assert.That(bravoOnly, Does.Contain(PublicSiteSeed.OpBravoId));
            Assert.That(bravoOnly, Does.Not.Contain(PublicSiteSeed.OpAlphaId));
            Assert.That(neither, Is.Empty);
        });
    }

    [Test]
    public async Task Lyrics_CombinesWithNumericOperators_OnTheSameDifficulty()
    {
        // Bravo sings "night" AND has stars 7.0; Alpha sings "night" but sits at 4.5 stars.
        var ids = Ids(await GetHtml("/beatmapsets?q=" + Enc("operatorset lyrics:night star:>6")));

        Assert.Multiple(() =>
        {
            Assert.That(ids, Does.Contain(PublicSiteSeed.OpBravoId));
            Assert.That(ids, Does.Not.Contain(PublicSiteSeed.OpAlphaId));
        });
    }

    [Test]
    public async Task MultipleOperators_CombineWithAnd()
    {
        // star:>6 AND wpm:>150 both point at Bravo only.
        var ids = Ids(await GetHtml("/beatmapsets?q=" + Enc("operatorset star:>6 wpm:>150")));

        Assert.Multiple(() =>
        {
            Assert.That(ids, Does.Contain(PublicSiteSeed.OpBravoId));
            Assert.That(ids, Has.Count.EqualTo(1));
        });
    }

    // ---- the game's operator syntax (backlog 338) over the "staropset" ratings ----
    // Index order: 3.99, 4.00, 4.0712, 4.50, 4.10 (PublicSiteSeed.StarOpRatings).

    [TestCase("stars>4", new[] { 2, 3, 4 })]
    [TestCase("stars<=4", new[] { 0, 1 })]
    [TestCase("stars=4.07", new[] { 2 })]
    [TestCase("stars!=4.07", new[] { 0, 1, 3, 4 })]
    [TestCase("sr>=4.5", new[] { 3 })]
    [TestCase("stars:4.07", new[] { 2 })]
    [TestCase("stars=4", new[] { 1 })]
    [TestCase("stars:>4", new[] { 2, 3, 4 })]
    [TestCase("stars=4.1", new[] { 4 })]
    [TestCase("stars=4.10", new[] { 4 })]
    [TestCase("stars!=4.1", new[] { 0, 1, 2, 3 })]
    public async Task GameStarSyntax_SelectsExactlyTheRatingsItNames(string op, int[] expected)
    {
        var ids = Ids(await GetHtml("/beatmapsets?q=" + Enc("staropset " + op)));
        var want = expected.Select(i => PublicSiteSeed.StarOpIds[i]).ToList();

        // stars:4.07 is the colon spelling of the same equality: it used to compare at full
        // precision and find nothing, while the card printed "4.07" on the third fixture.
        // stars=4.1 on the fifth (rated exactly 4.10, printed "4.1") is backlog 342: the floor
        // read it as 4.09 until the epsilon went in.
        Assert.That(ids, Is.EquivalentTo(want), op);
    }

    [Test]
    public async Task GameTextSyntax_EqualsIsTheColon_AndNotEqualsExcludes()
    {
        var bravo = Ids(await GetHtml("/beatmapsets?q=" + Enc("operatorset title=bravo")));
        var notBravo = Ids(await GetHtml("/beatmapsets?q=" + Enc("operatorset title!=bravo")));
        var notPiano = Ids(await GetHtml("/beatmapsets?q=" + Enc("operatorset artist!:piano")));

        Assert.Multiple(() =>
        {
            Assert.That(bravo, Is.EquivalentTo(new[] { PublicSiteSeed.OpBravoId }));
            Assert.That(notBravo, Is.EquivalentTo(new[] { PublicSiteSeed.OpAlphaId }));
            Assert.That(notPiano, Is.EquivalentTo(new[] { PublicSiteSeed.OpAlphaId }));
        });
    }

    [Test]
    public async Task GameSyntax_WholeNumberAndLengthEquality()
    {
        // Alpha: wpm 100, 90 s. Bravo: wpm 200, 240 s. length=2m is the game's 90 to 150 window.
        var wpm = Ids(await GetHtml("/beatmapsets?q=" + Enc("operatorset wpm=100")));
        var clock = Ids(await GetHtml("/beatmapsets?q=" + Enc("operatorset length=1:30")));
        var minutes = Ids(await GetHtml("/beatmapsets?q=" + Enc("operatorset length=4m")));

        Assert.Multiple(() =>
        {
            Assert.That(wpm, Is.EquivalentTo(new[] { PublicSiteSeed.OpAlphaId }));
            Assert.That(clock, Is.EquivalentTo(new[] { PublicSiteSeed.OpAlphaId }));
            Assert.That(minutes, Is.EquivalentTo(new[] { PublicSiteSeed.OpBravoId }));
        });
    }

    [Test]
    public async Task UnknownKey_TreatedAsPlainText_NoError()
    {
        // Must not 500; the token becomes free text and simply matches nothing here.
        using var response = await WebsiteFixture.Client.GetAsync("/beatmapsets?q=" + Enc("colour:blue"));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task RawQuery_RoundTripsInSearchBox()
    {
        string raw = "operatorset star:>6";
        string html = await GetHtml("/beatmapsets?q=" + Enc(raw));

        // The search input keeps the full typed query (operators included) so it's editable.
        Assert.That(html, Does.Contain($"value=\"{WebUtility.HtmlEncode(raw)}\""));
    }

    [Test]
    public async Task Guide_IsRenderedNearSearchBox()
    {
        string html = await GetHtml("/beatmapsets");

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("search-guide"));
            Assert.That(html, Does.Contain("star:"));
            Assert.That(html, Does.Contain("length:"));
            Assert.That(html, Does.Contain("lyrics:"));

            // The game's spellings (backlog 338), with the item's three examples.
            Assert.That(html, Does.Contain("<code>stars&gt;4</code>"));
            Assert.That(html, Does.Contain("<code>stars&lt;=4</code>"));
            Assert.That(html, Does.Contain("<code>stars=4.07</code>"));
            Assert.That(html, Does.Contain("<code>stars=4.06</code>"), "the rounded-display note");
        });
    }

    [Test]
    public async Task InfoIcon_QuickHelpTooltip_RendersWithExamples()
    {
        string html = await GetHtml("/beatmapsets");

        Assert.Multiple(() =>
        {
            // Focusable, labelled trigger + a role="tooltip" the search box points at.
            Assert.That(html, Does.Contain("search-info__trigger"));
            Assert.That(html, Does.Contain("aria-label=\"How to search\""));
            Assert.That(html, Does.Contain("role=\"tooltip\" id=\"search-info-tip\""));
            Assert.That(html, Does.Contain("aria-describedby=\"search-info-tip search-guide\""));

            // Concrete, valid-syntax examples in the quick reference.
            Assert.That(html, Does.Contain("star:&gt;4"));
            Assert.That(html, Does.Contain("length:&lt;2:30"));
            Assert.That(html, Does.Contain("creator:neon"));
        });
    }

    [Test]
    public async Task FreeTextStillWorks_WithNoOperators()
    {
        // Regression: a plain query behaves exactly as before.
        var ids = Ids(await GetHtml("/beatmapsets?q=Rhapsody"));
        Assert.That(ids, Does.Contain(PublicSiteSeed.SearchSetId));
    }

    private static string Enc(string q) => Uri.EscapeDataString(q);

    private static async Task<string> GetHtml(string url)
    {
        using var response = await WebsiteFixture.Client.GetAsync(url);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), url);
        return await response.Content.ReadAsStringAsync();
    }

    private static List<long> Ids(string html)
        => Regex.Matches(html, "data-set-id=\"(\\d+)\"").Select(m => long.Parse(m.Groups[1].Value)).ToList();
}
