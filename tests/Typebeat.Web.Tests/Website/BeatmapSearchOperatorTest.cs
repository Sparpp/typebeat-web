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
    public async Task Cpm_DerivedFromStoredCounts_Filters()
    {
        // Alpha cpm = 100*500/100 = 500; Bravo cpm = 200*700/100 = 1400.
        var ids = Ids(await GetHtml("/beatmapsets?q=" + Enc("operatorset cpm:>600")));

        Assert.Multiple(() =>
        {
            Assert.That(ids, Does.Contain(PublicSiteSeed.OpBravoId));
            Assert.That(ids, Does.Not.Contain(PublicSiteSeed.OpAlphaId));
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
