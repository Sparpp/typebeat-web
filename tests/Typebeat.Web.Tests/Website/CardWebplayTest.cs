using System.Net;

namespace Typebeat.Web.Tests.Website;

/// <summary>
/// The shared beatmapset card's webplay rail (Pages/Shared/_BeatmapsetCard.cshtml), the /play
/// picker's own listing, and the <c>/play?set={id}[&amp;diff={beatmapId}]</c> deep link they point at.
///
/// <para>The rail is offered on every card that could actually be played in the browser: PUBLISHED
/// (pending, unranked or ranked, i.e. BeatmapsetEndpoints.IsPublished), an assembled package, and a
/// live .osu difficulty. Backlog 230 widened it from ranked-only, so the two published-but-not-ranked
/// statuses moved from the "no rail" group to the "rail" one; what is left in the no-rail group is
/// what a player would genuinely fail to load. The seed gives one fixture per case: "Waiting Room"
/// (pending), "Off The Record" (unranked), "Editor Era Classic" (ranked, no package) and "Hammered
/// Keys" (ranked and packaged, but no .osu difficulty).</para>
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

    /// <summary>
    /// The (b) half of backlog 230, on the card: a published-but-not-ranked set is playable. Both
    /// non-ranked published statuses, because they are separate values in the status domain and
    /// only one of them ('pending') used to reach the media routes at all.
    /// </summary>
    [Test]
    public async Task PendingAndUnrankedCards_AlsoOfferTheWebplayRail()
    {
        // Both are published and have a difficulty and a package; only their status differs from
        // "Covered In Neon" above.
        string pending = await GetHtml("/beatmapsets?q=Waiting");
        string unranked = await GetHtml("/beatmapsets?q=Record");

        Assert.Multiple(() =>
        {
            Assert.That(pending, Does.Contain($"data-set-id=\"{PublicSiteSeed.PendingId}\""));
            Assert.That(pending, Does.Contain($"href=\"/play?set={PublicSiteSeed.PendingId}\""));
            Assert.That(pending, Does.Contain(">Pending<"), "the status pill still says which kind of map it is");

            Assert.That(unranked, Does.Contain($"data-set-id=\"{PublicSiteSeed.UnrankedSetId}\""));
            Assert.That(unranked, Does.Contain($"href=\"/play?set={PublicSiteSeed.UnrankedSetId}\""));
            Assert.That(unranked, Does.Contain(">Unranked<"));
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

    /// <summary>
    /// The picker's own listing widened with the rail: every published status is offered, and the
    /// ranked-worded empty state went with them.
    /// </summary>
    [Test]
    public async Task PlayPicker_ListsPendingAndUnrankedSetsToo()
    {
        string html = await GetHtml("/play");

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain($"data-set-id=\"{PlayableId}\""), "ranked");
            Assert.That(html, Does.Contain($"data-set-id=\"{PublicSiteSeed.PendingId}\""), "pending");
            Assert.That(html, Does.Contain($"data-set-id=\"{PublicSiteSeed.UnrankedSetId}\""), "unranked");

            // Still only PLAYABLE ones: a package and a live .osu difficulty are unchanged conditions.
            Assert.That(html, Does.Not.Contain($"data-set-id=\"{PublicSiteSeed.PackagelessId}\""), "no package");
            Assert.That(html, Does.Not.Contain($"data-set-id=\"{PublicSiteSeed.MostPlayedId}\""), "no .osu difficulty");
            Assert.That(html, Does.Not.Contain($"data-set-id=\"{PublicSiteSeed.HiddenId}\""), "unpublished");
            Assert.That(html, Does.Not.Contain($"data-set-id=\"{PublicSiteSeed.RemovedId}\""), "unpublished");

            Assert.That(html, Does.Not.Contain("No ranked maps yet"), "the empty state is no longer ranked-worded");
            // The status pill each card already carries is what makes a non-ranked map's terms visible.
            Assert.That(html, Does.Contain("class=\"pill pill--unranked\""));
            // And the play button carries it too, so the post-submit copy can be honest about it.
            Assert.That(html, Does.Contain("data-status=\"unranked\""));
        });
    }

    /// <summary>
    /// The stage root carries the Discord invite as <c>data-discord-url</c> (backlog 288), so the
    /// player's first-clear nudge (backlog 289) reads the one constant instead of hardcoding the URL
    /// a second time in JavaScript.
    /// </summary>
    [Test]
    public async Task PlayStage_CarriesTheDiscordInvite_ForThePlayerToRead()
    {
        string html = await GetHtml("/play");

        Assert.That(html, Does.Contain($"data-discord-url=\"{SiteLinks.DISCORD_INVITE}\""));
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
            Assert.That(html, Does.Contain("\"status\":\"ranked\""));
            // No difficulty named, so the player shows its difficulty step (or auto-skips it).
            Assert.That(html, Does.Contain("\"diffId\":0"));
        });
    }

    /// <summary>
    /// <c>?set=X&amp;diff=Y</c> preselects one difficulty, on exactly the terms the set page's own
    /// selector uses (belongs to the set, and is live). A difficulty from ANOTHER set, or one whose
    /// row is no longer part of the current version, is ignored rather than honoured: the player
    /// then chooses, and nothing downstream is ever handed a beatmap the media routes would refuse.
    /// </summary>
    [Test]
    public async Task DeepLink_WithDiff_PreselectsOnlyALiveDifficultyOfThatSet()
    {
        string chosen = await GetHtml($"/play?set={PublicSiteSeed.MultiDiffSetId}&diff={PublicSiteSeed.MultiDiffHardId}");
        string foreign = await GetHtml($"/play?set={PublicSiteSeed.MultiDiffSetId}&diff={PublicSiteSeed.LeaderboardBeatmapId}");
        string dropped = await GetHtml($"/play?set={PublicSiteSeed.MultiDiffSetId}&diff={PublicSiteSeed.MultiDiffDroppedId}");
        string junk = await GetHtml($"/play?set={PublicSiteSeed.MultiDiffSetId}&diff=987654321");

        Assert.Multiple(() =>
        {
            Assert.That(chosen, Does.Contain($"\"diffId\":{PublicSiteSeed.MultiDiffHardId}"));
            Assert.That(foreign, Does.Contain("\"diffId\":0"), "a difficulty of another set");
            Assert.That(dropped, Does.Contain("\"diffId\":0"), "a dropped (filename NULL) difficulty");
            Assert.That(junk, Does.Contain("\"diffId\":0"), "an id that is not a difficulty at all");

            // The set itself still opens in every case.
            foreach (string html in new[] { chosen, foreign, dropped, junk })
                Assert.That(html, Does.Contain($"\"setId\":{PublicSiteSeed.MultiDiffSetId}"));
        });
    }

    [Test]
    public async Task DeepLink_UnplayableOrUnknownSet_FallsBackToThePickerWithoutAutoPlay()
    {
        // Unpublished (hidden / removed), unplayable (no package), unknown, unparseable. Pending and
        // unranked are deliberately NOT here any more: since backlog 230 they auto-play like any
        // other published set, which is what PendingAndUnrankedCards_AlsoOfferTheWebplayRail links.
        string hidden = await GetHtml($"/play?set={PublicSiteSeed.HiddenId}");
        string removed = await GetHtml($"/play?set={PublicSiteSeed.RemovedId}");
        string packageless = await GetHtml($"/play?set={PublicSiteSeed.PackagelessId}");
        string missing = await GetHtml("/play?set=987654321");
        string junk = await GetHtml("/play?set=not-a-number");

        Assert.Multiple(() =>
        {
            Assert.That(hidden, Does.Contain("autoPlay: null"));
            Assert.That(removed, Does.Contain("autoPlay: null"));
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
