using System.Net;
using System.Text.RegularExpressions;

namespace Typebeat.Web.Tests.Website;

/// <summary>
/// The explicit-content marker end to end on the public site: the EXPLICIT badge renders beside
/// the title on the set page and on the shared set card (the /beatmapsets listing), and ONLY for
/// sets whose beatmapsets.explicit is true, plus the <c>explicit:</c> / <c>nsfw:</c> search
/// operator. Fixtures are the seeded twins <see cref="PublicSiteSeed.ExplicitSetId"/> ("Parental
/// Advisory Anthem", flagged) and <see cref="PublicSiteSeed.CleanTwinSetId"/> ("Radio Edit
/// Anthem", clean), both tagged "advisoryset".
/// </summary>
public class ExplicitBadgeTest
{
    [OneTimeSetUp]
    public Task OneTimeSetUp() => PublicSiteSeed.EnsureSeededAsync();

    [Test]
    public async Task SetPage_ShowsBadge_OnlyForExplicitSet()
    {
        string flagged = await GetHtml($"/beatmapsets/{PublicSiteSeed.ExplicitSetId}");
        string clean = await GetHtml($"/beatmapsets/{PublicSiteSeed.CleanTwinSetId}");

        Assert.Multiple(() =>
        {
            Assert.That(flagged, Does.Contain("Parental Advisory Anthem"));
            Assert.That(flagged, Does.Contain("class=\"explicit-badge\""));
            // The badge belongs to the title area, not to the stray corners of the page.
            Assert.That(flagged, Does.Contain("set-header__title"));

            Assert.That(clean, Does.Contain("Radio Edit Anthem"));
            Assert.That(clean, Does.Not.Contain("explicit-badge"));
        });
    }

    [Test]
    public async Task ListingCard_ShowsBadge_OnlyForExplicitSet()
    {
        string flagged = await GetHtml("/beatmapsets?q=" + Enc("title:\"parental advisory\""));
        string clean = await GetHtml("/beatmapsets?q=" + Enc("title:\"radio edit\""));

        Assert.Multiple(() =>
        {
            Assert.That(Ids(flagged), Is.EqualTo(new[] { PublicSiteSeed.ExplicitSetId }));
            Assert.That(flagged, Does.Contain("class=\"explicit-badge\""));

            Assert.That(Ids(clean), Is.EqualTo(new[] { PublicSiteSeed.CleanTwinSetId }));
            Assert.That(clean, Does.Not.Contain("explicit-badge"));
        });
    }

    [Test]
    public async Task ExplicitOperator_FiltersBothWays()
    {
        var yes = Ids(await GetHtml("/beatmapsets?q=" + Enc("advisoryset explicit:yes")));
        var no = Ids(await GetHtml("/beatmapsets?q=" + Enc("advisoryset explicit:no")));

        Assert.Multiple(() =>
        {
            Assert.That(yes, Does.Contain(PublicSiteSeed.ExplicitSetId));
            Assert.That(yes, Does.Not.Contain(PublicSiteSeed.CleanTwinSetId));

            Assert.That(no, Does.Contain(PublicSiteSeed.CleanTwinSetId));
            Assert.That(no, Does.Not.Contain(PublicSiteSeed.ExplicitSetId));
        });
    }

    [Test]
    public async Task NsfwAlias_MatchesExplicitOperator()
    {
        var ids = Ids(await GetHtml("/beatmapsets?q=" + Enc("advisoryset nsfw:true")));

        Assert.Multiple(() =>
        {
            Assert.That(ids, Does.Contain(PublicSiteSeed.ExplicitSetId));
            Assert.That(ids, Does.Not.Contain(PublicSiteSeed.CleanTwinSetId));
        });
    }

    [Test]
    public async Task UnparseableValue_DegradesToFreeText_NeverErrors()
    {
        // "explicit:maybe" is not a truth word, so the whole token stays free text: the page must
        // still answer 200 (and match nothing, since no set contains that string).
        string html = await GetHtml("/beatmapsets?q=" + Enc("explicit:maybe"));

        Assert.That(Ids(html), Is.Empty);
    }

    [Test]
    public async Task SearchGuide_DocumentsTheOperator()
    {
        string html = await GetHtml("/beatmapsets");

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("<code>explicit:</code>"));
            Assert.That(html, Does.Contain("explicit:no"));
        });
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
