using System.Net;

namespace Typebeat.Web.Tests.Website;

/// <summary>
/// The shared beatmapset card's webplay rail (Pages/Shared/_BeatmapsetCard.cshtml) and the
/// <c>/play?set={id}</c> deep link it points at.
///
/// <para>The rail is offered only when the card could actually be played in the browser: ranked
/// (browser scores land on the live leaderboards), an assembled package, and a live .osu
/// difficulty, i.e. the exact conditions /play's own picker filters on. The seed gives one fixture
/// per failure mode: "Waiting Room" (pending), "Editor Era Classic" (ranked, no package) and
/// "Hammered Keys" (ranked and packaged, but no .osu difficulty).</para>
///
/// <para>The rail's gate also rides on a column appended to <see cref="Typebeat.Web.Pages.BeatmapsetCardSql"/>,
/// which Dapper binds POSITIONALLY, so the last test re-renders every consumer of that SQL.</para>
/// </summary>
public class CardWebplayTest
{
    [OneTimeSetUp]
    public Task OneTimeSetUp() => PublicSiteSeed.EnsureSeededAsync();

    /// <summary>"Covered In Neon": ranked, packaged, one map.osu difficulty.</summary>
    private static long PlayableId => PublicSiteSeed.CoveredSetId;

    [Test]
    public async Task RankedPlayableCard_LinksTheWebplayDeepLink()
    {
        string html = await GetHtml("/beatmapsets?q=Covered");

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain($"data-set-id=\"{PlayableId}\""));
            Assert.That(html, Does.Contain("class=\"bset-card__play\""));
            Assert.That(html, Does.Contain($"href=\"/play?set={PlayableId}\""));
        });
    }

    [Test]
    public async Task PendingCard_HasNoWebplayRail()
    {
        // "Waiting Room" is published and has a difficulty and a package; only its status differs.
        string html = await GetHtml("/beatmapsets?q=Waiting");

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain($"data-set-id=\"{PublicSiteSeed.PendingId}\""));
            Assert.That(html, Does.Not.Contain("bset-card__play"));
            Assert.That(html, Does.Not.Contain("/play?set="));
        });
    }

    [Test]
    public async Task PackagelessRankedCard_HasNoWebplayRail()
    {
        // "Editor Era Classic": pre-M3 shape, live diffs but no set_versions row to serve.
        string html = await GetHtml("/beatmapsets?q=Editor");

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain($"data-set-id=\"{PublicSiteSeed.PackagelessId}\""));
            Assert.That(html, Does.Not.Contain("bset-card__play"));
        });
    }

    [Test]
    public async Task RankedSetWithNoDifficulty_HasNoWebplayRail()
    {
        // "Hammered Keys": ranked and packaged, but nothing for /play/map/{id}/osu to resolve.
        string html = await GetHtml("/beatmapsets?q=Hammered");

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain($"data-set-id=\"{PublicSiteSeed.MostPlayedId}\""));
            Assert.That(html, Does.Not.Contain("bset-card__play"));
        });
    }

    [Test]
    public async Task PlayPicker_KeepsItsInPlacePlayButton_AndAddsNoDeepLink()
    {
        // On /play the rail itself already launches the player, so the deep-link column would be
        // both redundant and a link nested in the picker's click region.
        string html = await GetHtml("/play");

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("class=\"rail-btn tb-play\""));
            Assert.That(html, Does.Not.Contain("bset-card__play"));
            Assert.That(html, Does.Not.Contain("href=\"/play?set="));
        });
    }

    [Test]
    public async Task DeepLink_HandsThePlayableSetToTheScriptAsAutoPlay()
    {
        string html = await GetHtml($"/play?set={PlayableId}");

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain($"\"setId\":{PlayableId}"));
            Assert.That(html, Does.Contain("\"title\":\"Covered In Neon\""));
            Assert.That(html, Does.Contain("\"artist\":\"The Artwork\""));
        });
    }

    [Test]
    public async Task DeepLink_UnplayableOrUnknownSet_FallsBackToThePickerWithoutAutoPlay()
    {
        string pending = await GetHtml($"/play?set={PublicSiteSeed.PendingId}");
        string packageless = await GetHtml($"/play?set={PublicSiteSeed.PackagelessId}");
        string missing = await GetHtml("/play?set=987654321");
        string junk = await GetHtml("/play?set=not-a-number");

        Assert.Multiple(() =>
        {
            Assert.That(pending, Does.Contain("autoPlay: null"));
            Assert.That(packageless, Does.Contain("autoPlay: null"));
            Assert.That(missing, Does.Contain("autoPlay: null"));
            // Unparseable ids bind to the default and must not 400 the picker away.
            Assert.That(junk, Does.Contain("autoPlay: null"));
            Assert.That(junk, Does.Contain("class=\"card-grid\""));
        });
    }

    /// <summary>
    /// Every page hydrating <see cref="Typebeat.Web.Pages.BeatmapsetCardModel"/> still renders
    /// cards: the added HasPlayableDiff column must sit at the same index in the SELECT and in the
    /// record, or Dapper's positional binding 500s all of these at once.
    /// </summary>
    [Test]
    public async Task EveryCardSqlConsumer_StillRendersCards()
    {
        string landing = await GetHtml("/");
        string listing = await GetHtml("/beatmapsets");
        string picker = await GetHtml("/play");
        string profile = await GetHtml($"/users/{PublicSiteSeed.MapperId}");

        Assert.Multiple(() =>
        {
            Assert.That(landing, Does.Contain("data-set-id="), "landing strip");
            Assert.That(listing, Does.Contain("data-set-id="), "listing");
            Assert.That(picker, Does.Contain("data-set-id="), "play picker");
            Assert.That(profile, Does.Contain("data-set-id="), "profile maps");
        });
    }

    private static async Task<string> GetHtml(string url)
    {
        using var response = await WebsiteFixture.Client.GetAsync(url);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), url);
        return await response.Content.ReadAsStringAsync();
    }
}
