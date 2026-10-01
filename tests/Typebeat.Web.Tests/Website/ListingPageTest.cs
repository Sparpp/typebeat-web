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
    public async Task CardPaceChip_ShowsTheTargetWpm_AndFallsBackToTheAverage()
    {
        // The chip is max(coalesce(target_wpm, wpm)) over the set's live difficulties (backlog
        // 272). Alpha carries a target of 180 against a stored average of 100, so a chip reading
        // 100 would be reading the wrong column; the packageless fixture carries no target at all,
        // which is the shape of every row the v18 backfill has not reached, and its chip must still
        // read its stored 60 rather than disappearing.
        string alpha = await GetHtml("/beatmapsets?q=" + Uri.EscapeDataString("Operator Alpha"));
        string packageless = await GetHtml("/beatmapsets?q=" + Uri.EscapeDataString("Editor Era"));

        Assert.Multiple(() =>
        {
            Assert.That(alpha, Does.Contain($"data-set-id=\"{PublicSiteSeed.OpAlphaId}\""));
            Assert.That(alpha, Does.Contain(">180 WPM<"));
            Assert.That(alpha, Does.Not.Contain(">100 WPM<"));

            Assert.That(packageless, Does.Contain($"data-set-id=\"{PublicSiteSeed.PackagelessId}\""));
            Assert.That(packageless, Does.Contain(">60 WPM<"));
        });
    }

    [Test]
    public async Task Search_ShortQuery_FallsBackToSubstringMatch()
    {
        // "oh" is mid-word in "Bohemian"; only the ILIKE fallback can find it.
        string html = await GetHtml("/beatmapsets?q=oh");

        Assert.That(html, Does.Contain($"data-set-id=\"{PublicSiteSeed.SearchSetId}\""));
    }

    // ---- backlog 351: prefix, substring and typo layers, tier ranking ----

    [Test]
    public async Task Search_PartialWord_FindsEveryTitleItPrefixes()
    {
        // FTS alone matched whole lexemes, so "drac" found nothing while "dr" (substring) did.
        var drac = CardIds(await GetHtml("/beatmapsets?q=drac"));
        var dra = CardIds(await GetHtml("/beatmapsets?q=dra"));

        Assert.Multiple(() =>
        {
            Assert.That(drac, Does.Contain(PublicSiteSeed.DraculaId));
            Assert.That(drac, Does.Contain(PublicSiteSeed.DraculauraId));

            // "drac" is NOT a prefix of Dragonfly (d-r-a-g); only a trigram union would return it
            // (0.6), and that union floods literal searches with look-alikes, so the typo layer is
            // a fallback. Fresh Drop is the same story at 0.4. "dra" prefixes all three.
            Assert.That(drac, Does.Not.Contain(PublicSiteSeed.DragonflyId));
            Assert.That(drac, Does.Not.Contain(PublicSiteSeed.FreshId));
            Assert.That(dra, Is.SupersetOf(new[] { PublicSiteSeed.DraculaId, PublicSiteSeed.DragonflyId, PublicSiteSeed.DraculauraId }));
        });
    }

    [Test]
    public async Task Search_WholeWord_RanksTheExactMatchAboveNewerPrefixMatches()
    {
        // Draculaura is newer than Dracula and matches "dracula" as a prefix only: the default
        // (newest) sort alone would put it first.
        var ids = CardIds(await GetHtml("/beatmapsets?q=dracula"));

        Assert.Multiple(() =>
        {
            Assert.That(ids.FirstOrDefault(), Is.EqualTo(PublicSiteSeed.DraculaId));
            Assert.That(ids, Does.Contain(PublicSiteSeed.DraculauraId));
        });
    }

    [Test]
    public async Task Search_ExplicitSort_KeepsItsOwnOrder_OverTheMatchTier()
    {
        // Same two sets on a sort the visitor chose: plays tie at 0, so id DESC decides, and
        // Draculaura (inserted after Dracula) leads exactly as it did before tiers existed.
        var ids = CardIds(await GetHtml("/beatmapsets?q=dracula&s=plays"));

        Assert.That(ids.IndexOf(PublicSiteSeed.DraculauraId), Is.LessThan(ids.IndexOf(PublicSiteSeed.DraculaId)));
    }

    [Test]
    public async Task Search_MidWordFragment_FindsBySubstring()
    {
        Assert.That(CardIds(await GetHtml("/beatmapsets?q=cula")), Does.Contain(PublicSiteSeed.DraculaId));
    }

    [Test]
    public async Task Search_SmallTypo_FindsByTrigram()
    {
        var dracla = CardIds(await GetHtml("/beatmapsets?q=dracla"));
        var washng = CardIds(await GetHtml("/beatmapsets?q=" + Uri.EscapeDataString("washng machin")));

        Assert.Multiple(() =>
        {
            Assert.That(dracla, Does.Contain(PublicSiteSeed.DraculaId));
            Assert.That(washng, Does.Contain(PublicSiteSeed.WashingMachineId));
            // Per word: "machin" alone resembles nothing else, so the typo pair stays narrow.
            Assert.That(washng, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public async Task Search_MultiWord_PrefixesTheUnfinishedWord()
    {
        var ids = CardIds(await GetHtml("/beatmapsets?q=" + Uri.EscapeDataString("washing mach")));

        Assert.That(ids, Does.Contain(PublicSiteSeed.WashingMachineId));
    }

    [Test]
    public async Task Search_EveryWordIsAPrefix_AcrossTitleAndArtist()
    {
        // Only the prefix layer can answer this: not a substring anywhere, and both words are too
        // short for the typo layer. "wa" prefixes Washing (title), "mi" prefixes Mitski (artist).
        var ids = CardIds(await GetHtml("/beatmapsets?q=" + Uri.EscapeDataString("wa mi")));

        Assert.That(ids, Does.Contain(PublicSiteSeed.WashingMachineId));
    }

    [Test]
    public async Task Search_QuotedPhrase_StillMatchesAsAPhrase()
    {
        var ids = CardIds(await GetHtml("/beatmapsets?q=" + Uri.EscapeDataString("\"washing machine\"")));

        Assert.That(ids.FirstOrDefault(), Is.EqualTo(PublicSiteSeed.WashingMachineId));
    }

    [TestCase("!!!")]
    [TestCase("&|!():*<>")]
    [TestCase("drac:* | ! (")]
    [TestCase("'\\'")]
    [TestCase("\"")]
    [TestCase("\"unclosed phrase")]
    [TestCase("x');DROP TABLE users;--")]
    [TestCase("%_\\")]
    public async Task Search_PunctuationAndOperatorCharacters_NeverError(string q)
    {
        // GetHtml asserts the 200; a tsquery syntax error would surface as a 500.
        string html = await GetHtml("/beatmapsets?q=" + Uri.EscapeDataString(q));

        Assert.That(html, Does.Contain("</html>"));
    }

    [Test]
    public async Task Search_TwoCharacters_StaysASubstringMatch()
    {
        // "dr" is inside Dracula, Dragonfly and Fresh Drop; nothing without a "dr" joins them.
        var ids = CardIds(await GetHtml("/beatmapsets?q=dr"));

        Assert.Multiple(() =>
        {
            Assert.That(ids, Does.Contain(PublicSiteSeed.DraculaId));
            Assert.That(ids, Does.Contain(PublicSiteSeed.DragonflyId));
            Assert.That(ids, Does.Contain(PublicSiteSeed.FreshId));
            Assert.That(ids, Does.Not.Contain(PublicSiteSeed.WashingMachineId));
        });
    }

    [Test]
    public async Task Search_TieredPaging_CarriesTheTierInTheCursor()
    {
        // 60 exact "filler" sets fill page 1 and start page 2; "Fillerless Night" (a prefix match,
        // but newer than the 50th filler) must close page 2 rather than lead page 1 or vanish.
        string page1 = await GetHtml("/beatmapsets?q=filler");
        var page1Ids = CardIds(page1);

        var showMore = Regex.Match(page1, "href=\"(/beatmapsets\\?[^\"]*after=[^\"]*)\"");
        Assert.That(showMore.Success, Is.True, "page 1 must link a cursor page");

        string next = WebUtility.HtmlDecode(showMore.Groups[1].Value);
        var page2Ids = CardIds(await GetHtml(next));

        Assert.Multiple(() =>
        {
            Assert.That(next, Does.Contain("after_tier=0"));
            Assert.That(page1Ids, Has.Count.EqualTo(50));
            Assert.That(page1Ids, Does.Not.Contain(PublicSiteSeed.FillerlessId));
            Assert.That(page2Ids, Has.Count.EqualTo(11));
            Assert.That(page2Ids.LastOrDefault(), Is.EqualTo(PublicSiteSeed.FillerlessId));
            Assert.That(page2Ids.Intersect(page1Ids), Is.Empty, "cursor pages must not overlap");
        });
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
            // (RestrictedOwnerTest seeds a delisted ranked set into the same database). All THREE
            // published statuses, which is what IndexModel's "any" predicate says; the list read
            // 'pending', 'ranked' while no seeded set was 'unranked', and PublicSiteSeed now has one.
            totalPublic = await conn.ExecuteScalarAsync<int>(
                """
                SELECT count(*) FROM beatmapsets s
                JOIN users u ON u.id = s.owner_id
                WHERE s.status IN ('pending', 'unranked', 'ranked') AND NOT u.restricted
                """);
        }

        Assert.That(totalPublic, Is.GreaterThan(50), "seed must overflow one page");

        // Other tests insert sets through SQL; the count above must describe the page read below.
        await WebsiteFixture.EvictAllAsync();

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

    /// <summary>
    /// Backlog 337: the Status/Sort/Show pill rows must never push the page wider than a phone.
    /// Pins the wrap rule and the phone-width hanging-label gutter.
    /// </summary>
    [Test]
    public void FilterRows_WrapInsideTheirContainer_AtPhoneWidth()
    {
        string css = File.ReadAllText(Path.Combine(JsHarness.RepoRoot(), "src", "Typebeat.Web", "wwwroot", "css", "site.css"));
        css = Regex.Replace(css, @"\s+", " ");

        Assert.Multiple(() =>
        {
            Assert.That(css, Does.Contain(".filter-row { display: flex; align-items: center; flex-wrap: wrap;"));
            Assert.That(css, Does.Contain("@media (max-width: 640px) { .filter-row { min-width: 0; padding-left: 64px; }"));
            Assert.That(css, Does.Contain(".filter-row > .filter-label { margin-left: -64px; }"));
        });
    }

    private static List<long> CardIds(string html)
        => Regex.Matches(html, "data-set-id=\"(\\d+)\"").Select(m => long.Parse(m.Groups[1].Value)).ToList();
}
