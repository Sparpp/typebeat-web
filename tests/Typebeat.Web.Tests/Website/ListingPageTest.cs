using System.Net;
using System.Text.RegularExpressions;
using Dapper;
using Npgsql;

namespace Typebeat.Web.Tests.Website;

/// <summary>
/// /beatmapsets listing: full-text search + ILIKE fallback, the status filter, the three sort
/// orders, and keyset "show more" paging at 50 per page.
/// </summary>
public class ListingPageTest
{
    [OneTimeSetUp]
    public Task OneTimeSetUp() => PublicSiteSeed.EnsureSeededAsync();

    [Test]
    public async Task Search_FindsSetByTitleAndArtistLexemes()
    {
        string byTitle = await GetHtml("/beatmapsets?q=Rhapsody");
        string byArtist = await GetHtml("/beatmapsets?q=Queen");

        Assert.Multiple(() =>
        {
            Assert.That(byTitle, Does.Contain($"data-set-id=\"{PublicSiteSeed.SearchSetId}\""));
            Assert.That(byTitle, Does.Not.Contain($"data-set-id=\"{PublicSiteSeed.MostPlayedId}\""));

            Assert.That(byArtist, Does.Contain($"data-set-id=\"{PublicSiteSeed.SearchSetId}\""));
        });
    }

    [Test]
    public async Task Search_ShortQuery_FallsBackToSubstringMatch()
    {
        // "oh" is mid-word in "Bohemian"; only the ILIKE fallback can find it.
        string html = await GetHtml("/beatmapsets?q=oh");

        Assert.That(html, Does.Contain($"data-set-id=\"{PublicSiteSeed.SearchSetId}\""));
    }

    [Test]
    public async Task Search_NoResults_ShowsEmptyState()
    {
        string html = await GetHtml("/beatmapsets?q=zxqvbnrrrr");

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Not.Contain("data-set-id="));
            Assert.That(html, Does.Contain("No maps matched"));
        });
    }

    [Test]
    public async Task StatusFilter_NeverListsHiddenOrRemoved()
    {
        // Both seeded with future submitted_at: they would top the default sort if leaked.
        string any = await GetHtml("/beatmapsets");
        string ranked = await GetHtml("/beatmapsets?status=ranked");

        Assert.Multiple(() =>
        {
            Assert.That(any, Does.Not.Contain("Hidden Gem Nobody"));
            Assert.That(any, Does.Not.Contain("Removed For Reasons"));
            Assert.That(ranked, Does.Not.Contain("Hidden Gem Nobody"));
            Assert.That(ranked, Does.Not.Contain("Removed For Reasons"));
            Assert.That(ranked, Does.Contain("data-set-id=")); // the filter still lists published sets
        });
    }

    [Test]
    public async Task StatusFilter_SplitsPendingAndRanked_AnyShowsBoth()
    {
        string any = await GetHtml("/beatmapsets");
        string ranked = await GetHtml("/beatmapsets?status=ranked");
        string pending = await GetHtml("/beatmapsets?status=pending");

        Assert.Multiple(() =>
        {
            // Any = both published statuses ('Waiting Room' is recent enough for page 1).
            Assert.That(any, Does.Contain($"data-set-id=\"{PublicSiteSeed.PendingId}\""));
            Assert.That(any, Does.Contain($"data-set-id=\"{PublicSiteSeed.FreshId}\""));

            // Pending narrows to pending only…
            Assert.That(pending, Does.Contain($"data-set-id=\"{PublicSiteSeed.PendingId}\""));
            Assert.That(pending, Does.Not.Contain($"data-set-id=\"{PublicSiteSeed.FreshId}\""));

            // …and Ranked excludes it.
            Assert.That(ranked, Does.Not.Contain($"data-set-id=\"{PublicSiteSeed.PendingId}\""));
            Assert.That(ranked, Does.Contain($"data-set-id=\"{PublicSiteSeed.FreshId}\""));

            // The Pending filter pill renders and marks itself active on its own page.
            Assert.That(pending, Does.Contain(">Pending</a>"));
        });
    }

    [Test]
    public async Task Sort_ControlsFirstCard()
    {
        long newestFirst = FirstCardId(await GetHtml("/beatmapsets"));
        long playsFirst = FirstCardId(await GetHtml("/beatmapsets?s=plays"));
        long favsFirst = FirstCardId(await GetHtml("/beatmapsets?s=favs"));

        Assert.Multiple(() =>
        {
            Assert.That(newestFirst, Is.EqualTo(PublicSiteSeed.FreshId), "newest");
            Assert.That(playsFirst, Is.EqualTo(PublicSiteSeed.MostPlayedId), "most played");
            Assert.That(favsFirst, Is.EqualTo(PublicSiteSeed.MostFavedId), "most favourited");
        });
    }

    [Test]
    public async Task Paging_ShowMoreCursor_WalksTheWholeListing()
    {
        int totalPublic;
        await using (var conn = new NpgsqlConnection(WebsiteFixture.ConnectionString))
        {
            await conn.OpenAsync();
            // Match the listing's visibility: published AND not owned by a restricted mapper
            // (RestrictedOwnerTest seeds a delisted ranked set into the same database).
            totalPublic = await conn.ExecuteScalarAsync<int>(
                """
                SELECT count(*) FROM beatmapsets s
                JOIN users u ON u.id = s.owner_id
                WHERE s.status IN ('pending', 'ranked') AND NOT u.restricted
                """);
        }

        Assert.That(totalPublic, Is.GreaterThan(50), "seed must overflow one page");

        string page1 = await GetHtml("/beatmapsets");
        Assert.That(CardIds(page1), Has.Count.EqualTo(50));

        var showMore = Regex.Match(page1, "href=\"(/beatmapsets\\?[^\"]*after=[^\"]*)\"");
        Assert.That(showMore.Success, Is.True, "page 1 must link a cursor page");

        string page2 = await GetHtml(WebUtility.HtmlDecode(showMore.Groups[1].Value));
        var page2Ids = CardIds(page2);

        Assert.Multiple(() =>
        {
            Assert.That(page2Ids, Has.Count.EqualTo(totalPublic - 50));
            Assert.That(page2Ids.Intersect(CardIds(page1)), Is.Empty, "cursor pages must not overlap");
            Assert.That(Regex.IsMatch(page2, "href=\"/beatmapsets\\?[^\"]*after="), Is.False,
                "the last page must not offer another cursor link");
        });
    }

    private static async Task<string> GetHtml(string url)
    {
        using var response = await WebsiteFixture.Client.GetAsync(url);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), url);
        return await response.Content.ReadAsStringAsync();
    }

    private static long FirstCardId(string html)
    {
        var match = Regex.Match(html, "data-set-id=\"(\\d+)\"");
        Assert.That(match.Success, Is.True, "no cards rendered");
        return long.Parse(match.Groups[1].Value);
    }

    private static List<long> CardIds(string html)
        => Regex.Matches(html, "data-set-id=\"(\\d+)\"").Select(m => long.Parse(m.Groups[1].Value)).ToList();
}
