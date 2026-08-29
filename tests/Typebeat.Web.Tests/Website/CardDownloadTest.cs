using System.Net;
using Newtonsoft.Json.Linq;

namespace Typebeat.Web.Tests.Website;

/// <summary>
/// The beatmapset card's download control (Pages/Shared/_BeatmapsetCard.cshtml), which has three
/// shapes: a set WITH a video expands into two options ("with video" / "audio only, no video"), a
/// set without one keeps the plain link that downloads on the first click exactly as it always
/// has, and a packageless set keeps its inert "available in-game only" span.
///
/// <para>The gate is beatmapsets.has_video, which reaches the card through a column appended to
/// <see cref="Typebeat.Web.Pages.BeatmapsetCardSql"/> (Dapper binds those POSITIONALLY, so
/// CardWebplayTest.EveryCardSqlConsumer_StillRendersCards is the other half of this coverage).</para>
///
/// <para>Also the panel's size labels: GET /beatmapsets/{id}/download-sizes, which the panel fetches
/// once when it first opens.</para>
/// </summary>
public class CardDownloadTest
{
    [OneTimeSetUp]
    public Task OneTimeSetUp() => PublicSiteSeed.EnsureSeededAsync();

    [Test]
    public async Task VideoSetCard_ExpandsIntoTwoOptions()
    {
        string html = await GetHtml("/beatmapsets?q=Clip");

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain($"data-set-id=\"{PublicSiteSeed.VideoSetId}\""));

            // The control is a button, not a link: the first click opens the panel.
            Assert.That(html, Does.Contain("data-dl-toggle"));
            Assert.That(html, Does.Contain("aria-expanded=\"false\""));
            Assert.That(html, Does.Not.Contain($"<a class=\"rail-btn\" href=\"/beatmapsets/{PublicSiteSeed.VideoSetId}/download\""));

            // Both packages, and where the panel goes for the numbers to label them with.
            Assert.That(html, Does.Contain($"href=\"/beatmapsets/{PublicSiteSeed.VideoSetId}/download\""));
            Assert.That(html, Does.Contain($"href=\"/beatmapsets/{PublicSiteSeed.VideoSetId}/download?noVideo=1\""));
            Assert.That(html, Does.Contain($"data-dl-sizes=\"/beatmapsets/{PublicSiteSeed.VideoSetId}/download-sizes\""));

            Assert.That(html, Does.Contain("with video"));
            Assert.That(html, Does.Contain("audio only, no video"));
        });
    }

    [Test]
    public async Task NonVideoSetCard_KeepsThePlainDownloadLink()
    {
        // "Covered In Neon": ranked, packaged, has_video false like every other seeded set.
        string html = await GetHtml("/beatmapsets?q=Covered");

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain($"data-set-id=\"{PublicSiteSeed.CoveredSetId}\""));
            Assert.That(html, Does.Contain($"<a class=\"rail-btn\" href=\"/beatmapsets/{PublicSiteSeed.CoveredSetId}/download\""));

            // No expand at all: no button, no panel, no second href. The animation never happens
            // and the click downloads, which is the whole "unchanged for maps without video" rule.
            Assert.That(html, Does.Not.Contain("data-dl-toggle"));
            Assert.That(html, Does.Not.Contain("data-dl-panel"));
            Assert.That(html, Does.Not.Contain("noVideo=1"));
        });
    }

    [Test]
    public async Task PackagelessCard_StaysInert()
    {
        // "Editor Era Classic": pre-M3 shape, no set_versions row, so there is nothing to download
        // in either shape.
        string html = await GetHtml("/beatmapsets?q=Editor");

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain($"data-set-id=\"{PublicSiteSeed.PackagelessId}\""));
            Assert.That(html, Does.Contain("rail-btn--disabled"));
            Assert.That(html, Does.Not.Contain("data-dl-toggle"));
        });
    }

    /// <summary>
    /// On /play the rail launches the browser player instead of downloading, so there is no
    /// download control to expand even for a set that has a video.
    /// </summary>
    [Test]
    public async Task PlayPicker_RendersNoDownloadExpand()
    {
        string html = await GetHtml("/play");

        Assert.Multiple(() =>
        {
            // The video set really is on the picker, so this is not vacuous.
            Assert.That(html, Does.Contain($"data-set-id=\"{PublicSiteSeed.VideoSetId}\""));
            Assert.That(html, Does.Contain("class=\"rail-btn tb-play\""));
            Assert.That(html, Does.Not.Contain("data-dl-toggle"));
            Assert.That(html, Does.Not.Contain("data-dl-panel"));
        });
    }

    [Test]
    public async Task DownloadSizes_ReportTheVideoSaving()
    {
        var body = await GetJson($"/beatmapsets/{PublicSiteSeed.VideoSetId}/download-sizes");

        Assert.Multiple(() =>
        {
            Assert.That((bool)body["audioOnlyAvailable"]!, Is.True);
            // The seed's clip is the only entry the .osu names in [Events] Video.
            Assert.That((long)body["full"]! - (long)body["audioOnly"]!, Is.EqualTo(65536));
            Assert.That((long)body["audioOnly"]!, Is.GreaterThan(0), "the .osu and the audio still ship");
        });
    }

    /// <summary>
    /// "Mp4 Single Anthem": one file serving as both the audio and the video (the pre-234 import
    /// shape). An audio-only package of it would be silent, so the card is told there is no choice
    /// to offer even though the set really does have a video.
    /// </summary>
    [Test]
    public async Task DownloadSizes_WithdrawTheChoiceWhenTheVideoIsAlsoTheAudio()
    {
        var body = await GetJson($"/beatmapsets/{PublicSiteSeed.Mp4AsAudioSetId}/download-sizes");

        Assert.Multiple(() =>
        {
            Assert.That((bool)body["audioOnlyAvailable"]!, Is.False);
            Assert.That((long)body["audioOnly"]!, Is.EqualTo((long)body["full"]!));
        });
    }

    /// <summary>A set whose difficulties name no video has nothing to leave out.</summary>
    [Test]
    public async Task DownloadSizes_OfferNothingForASetWithoutAVideo()
    {
        // "Twin Peaks Typing" is the other seeded set with real stored files behind its manifest.
        var body = await GetJson($"/beatmapsets/{PublicSiteSeed.MultiDiffSetId}/download-sizes");

        Assert.Multiple(() =>
        {
            Assert.That((bool)body["audioOnlyAvailable"]!, Is.False);
            Assert.That((long)body["audioOnly"]!, Is.EqualTo((long)body["full"]!));
        });
    }

    [Test]
    public async Task DownloadSizes_404ForUnpublishedAndPackagelessSets()
    {
        using var hidden = await WebsiteFixture.Client.GetAsync($"/beatmapsets/{PublicSiteSeed.HiddenId}/download-sizes");
        using var removed = await WebsiteFixture.Client.GetAsync($"/beatmapsets/{PublicSiteSeed.RemovedId}/download-sizes");
        using var packageless = await WebsiteFixture.Client.GetAsync($"/beatmapsets/{PublicSiteSeed.PackagelessId}/download-sizes");
        using var missing = await WebsiteFixture.Client.GetAsync("/beatmapsets/987654321/download-sizes");

        Assert.Multiple(() =>
        {
            Assert.That(hidden.StatusCode, Is.EqualTo(HttpStatusCode.NotFound), "hidden");
            Assert.That(removed.StatusCode, Is.EqualTo(HttpStatusCode.NotFound), "removed");
            Assert.That(packageless.StatusCode, Is.EqualTo(HttpStatusCode.NotFound), "no package");
            Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.NotFound), "no such set");
        });
    }

    private static async Task<JObject> GetJson(string url)
    {
        using var response = await WebsiteFixture.Client.GetAsync(url);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), url);
        return JObject.Parse(await response.Content.ReadAsStringAsync());
    }

    private static async Task<string> GetHtml(string url)
    {
        using var response = await WebsiteFixture.Client.GetAsync(url);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), url);
        return await response.Content.ReadAsStringAsync();
    }
}
