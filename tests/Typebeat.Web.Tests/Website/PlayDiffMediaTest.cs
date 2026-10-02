using System.Net;
using Newtonsoft.Json.Linq;

namespace Typebeat.Web.Tests.Website;

/// <summary>
/// The /play media routes once the player can name a DIFFICULTY (backlog 230):
/// <c>GET /play/map/{setId}/diffs</c>, and the <c>?diff={beatmapId}</c> parameter on
/// <c>/osu</c> and <c>/audio</c>.
///
/// <para>Two things are pinned here. First that a named difficulty really is served as itself,
/// down to the audio: the audio route re-reads AudioFilename out of THAT difficulty's .osu, so a
/// set whose two diffs point at different files must hand back different bytes. Second the TAMPER
/// BOUND that naming a beatmap introduced: the routes' design note says "we serve the exact map",
/// which stops being true by construction once the client picks, so every beatmap id is resolved
/// as (id AND set_id AND filename IS NOT NULL AND filename LIKE '%.osu') and anything else is a
/// 404 rather than someone else's map.</para>
///
/// <para>"Twin Peaks Typing" is the one seeded set with real stored files behind it
/// (PublicSiteSeed.StoreTwinPeaksFilesAsync), because these routes walk beatmaps.filename ->
/// version_files -> the blob store the way a real upload wrote it.</para>
/// </summary>
public class PlayDiffMediaTest
{
    [OneTimeSetUp]
    public Task OneTimeSetUp() => PublicSiteSeed.EnsureSeededAsync();

    private static long SetId => PublicSiteSeed.MultiDiffSetId;

    [Test]
    public async Task Diffs_ListsTheLiveDifficultiesHardestFirst_WithTheSiteStarColour()
    {
        var diffs = (JArray)(await GetJson($"/play/map/{SetId}/diffs"))["diffs"]!;

        Assert.That(diffs, Has.Count.EqualTo(2), "the dropped (filename NULL) row is not a difficulty");

        Assert.Multiple(() =>
        {
            // Hardest first, the same order the set page's own selector uses.
            Assert.That((long)diffs[0]["id"]!, Is.EqualTo(PublicSiteSeed.MultiDiffHardId));
            Assert.That((string?)diffs[0]["version_name"], Is.EqualTo("twin hard"));
            Assert.That((double)diffs[0]["stars"]!, Is.EqualTo(6.0).Within(1e-9));
            // The pill's pace is the TARGET WPM since backlog 272, with the stored average as the
            // fallback for a row the LyricPace v18 backfill has not reached. The hard diff carries
            // both (target 165, average 180) and the easy one carries only the average (60), so
            // this one response covers both arms: a pill reading 180 here would be reading the
            // wrong column.
            Assert.That((double)diffs[0]["wpm"]!, Is.EqualTo(165).Within(1e-9));
            // The plain average rides beside it as avg_wpm (PR 5): /play's underline pace hue draws
            // its map-relative bands against it, as the desktop does by default. It is never the
            // target, which would recolour every band against the fastest fifth of the map.
            Assert.That((double)diffs[0]["avg_wpm"]!, Is.EqualTo(180).Within(1e-9), "the average, not the target");

            Assert.That((long)diffs[1]["id"]!, Is.EqualTo(PublicSiteSeed.MultiDiffEasyId));
            Assert.That((string?)diffs[1]["version_name"], Is.EqualTo("twin easy"));
            Assert.That((double)diffs[1]["stars"]!, Is.EqualTo(2.0).Within(1e-9));
            Assert.That((double)diffs[1]["wpm"]!, Is.EqualTo(60).Within(1e-9), "no target stored, so the average stands in");
            Assert.That((double)diffs[1]["avg_wpm"]!, Is.EqualTo(60).Within(1e-9));

            // The colour is the site's one ramp, not a second one ported into the player script.
            Assert.That((string?)diffs[0]["colour"], Is.EqualTo(Typebeat.Web.DifficultyColour.ForStars(6.0)));
            Assert.That((string?)diffs[1]["colour"], Is.EqualTo(Typebeat.Web.DifficultyColour.ForStars(2.0)));
        });
    }

    [Test]
    public async Task Diffs_OnAnUnpublishedSet_Is404()
    {
        // The same media gate the two byte-serving routes carry: a hidden set advertises nothing.
        using var response = await WebsiteFixture.Client.GetAsync($"/play/map/{PublicSiteSeed.HiddenId}/diffs");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    /// <summary>
    /// The whole point of (a): the second difficulty is served as itself, and its audio is the file
    /// ITS .osu names. Without the ?diff= plumbing both requests would return the primary diff, so
    /// picking "twin hard" would have played "twin easy"'s map to "twin easy"'s audio.
    /// </summary>
    [Test]
    public async Task EachDifficulty_ServesItsOwnOsu_AndTheAudioThatOsuNames()
    {
        string primaryOsu = await GetText($"/play/map/{SetId}/osu");
        string easyOsu = await GetText($"/play/map/{SetId}/osu?diff={PublicSiteSeed.MultiDiffEasyId}");
        string hardOsu = await GetText($"/play/map/{SetId}/osu?diff={PublicSiteSeed.MultiDiffHardId}");

        string primaryAudio = await GetText($"/play/map/{SetId}/audio");
        string easyAudio = await GetText($"/play/map/{SetId}/audio?diff={PublicSiteSeed.MultiDiffEasyId}");
        string hardAudio = await GetText($"/play/map/{SetId}/audio?diff={PublicSiteSeed.MultiDiffHardId}");

        Assert.Multiple(() =>
        {
            Assert.That(easyOsu, Does.Contain("Version:twin easy"));
            Assert.That(easyOsu, Does.Contain("AudioFilename: " + PublicSiteSeed.MultiDiffEasyAudio));
            Assert.That(hardOsu, Does.Contain("Version:twin hard"));
            Assert.That(hardOsu, Does.Contain("AudioFilename: " + PublicSiteSeed.MultiDiffHardAudio));

            Assert.That(easyAudio, Is.EqualTo("easy-audio-bytes"));
            Assert.That(hardAudio, Is.EqualTo("hard-audio-bytes"));

            // No ?diff= is still the set's primary (lowest-id) difficulty, which is what /play was
            // hard-wired to before the picker existed and what a set-only request still means.
            Assert.That(primaryOsu, Is.EqualTo(easyOsu));
            Assert.That(primaryAudio, Is.EqualTo(easyAudio));
        });
    }

    /// <summary>
    /// The bundled lyric font route (backlog 291): /play/map/{setId}/font streams the file the
    /// chosen difficulty's own .osu names in [General] LyricFontFile, with the content type its
    /// extension implies, exactly as /audio streams that .osu's AudioFilename. A difficulty that
    /// names no font is a 404, which is nearly every map, and the browser player does not consume
    /// the route yet (its layout runs on JetBrains Mono's fixed advance); it exists so the
    /// per-glyph rework can adopt it without a server change.
    /// </summary>
    [Test]
    public async Task Font_ServedForTheDifficultyThatBundlesOne_404ForTheOneThatDoesNot()
    {
        using var hard = await WebsiteFixture.Client.GetAsync($"/play/map/{SetId}/font?diff={PublicSiteSeed.MultiDiffHardId}");
        string hardBytes = await hard.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(hard.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(hard.Content.Headers.ContentType?.MediaType, Is.EqualTo("font/ttf"),
                "content type follows the bundled file's extension");
            Assert.That(hardBytes, Is.EqualTo("fake-font-bytes"), "the manifest blob the .osu names");
        });

        // The easy diff (and therefore the no-?diff= primary) bundles no font: 404, never a 500
        // and never the other difficulty's font.
        await AssertNotFound($"/play/map/{SetId}/font?diff={PublicSiteSeed.MultiDiffEasyId}", "a difficulty with no font");
        await AssertNotFound($"/play/map/{SetId}/font", "the primary difficulty has no font either");

        // Same media gate as /osu and /audio: an unpublished set advertises nothing.
        await AssertNotFound($"/play/map/{PublicSiteSeed.HiddenId}/font", "hidden set");
    }

    /// <summary>
    /// THE NEGATIVE PIN. A beatmap id the caller was not served is not addressable through a set
    /// whose media they may see: another set's difficulty, a dropped one, and an id that is not a
    /// difficulty at all are all 404, on every byte route.
    ///
    /// <para>The load-bearing case is "off the record hard", whose archive filename deliberately
    /// COLLIDES with this set's own hard.osu. Every other foreign id fails twice over (its filename
    /// is not in this set's version manifest either), so it would stay a 404 even with the bound
    /// removed; this one is refused by the bound ALONE. Drop <c>set_id = @setId</c> from
    /// ResolveOsuFilenameAsync and it serves "twin hard" for a beatmap id belonging to another set.</para>
    /// </summary>
    [Test]
    public async Task NamingABeatmapTheSetDoesNotHave_Is404_OnBothMediaRoutes()
    {
        long colliding = PublicSiteSeed.UnrankedCollidingDiffId; // another set's diff, filename hard.osu
        long foreign = PublicSiteSeed.LeaderboardBeatmapId;      // another set's diff, filename map.osu
        long dropped = PublicSiteSeed.MultiDiffDroppedId;        // this set's row, filename NULL

        // "font" rides the same bound: the colliding case would otherwise resolve THIS set's
        // hard.osu, which really does bundle a font, and serve it for another set's beatmap id.
        foreach (string route in new[] { "osu", "audio", "font" })
        {
            await AssertNotFound($"/play/map/{SetId}/{route}?diff={colliding}", $"{route}: another set's difficulty, colliding filename");
            await AssertNotFound($"/play/map/{SetId}/{route}?diff={foreign}", $"{route}: another set's difficulty");
            await AssertNotFound($"/play/map/{SetId}/{route}?diff={dropped}", $"{route}: a dropped difficulty");
            await AssertNotFound($"/play/map/{SetId}/{route}?diff=987654321", $"{route}: not a difficulty at all");
        }
    }

    /// <summary>
    /// And the reverse direction of the same bound: the difficulty is real and live, but the SET in
    /// the path is a different one, so it must not resolve through that set either.
    /// </summary>
    [Test]
    public async Task NamingAValidDifficultyUnderTheWrongSet_Is404()
    {
        await AssertNotFound(
            $"/play/map/{PublicSiteSeed.UnrankedSetId}/osu?diff={PublicSiteSeed.MultiDiffHardId}",
            "a live difficulty routed through a set it does not belong to");
    }

    private static async Task AssertNotFound(string url, string because)
    {
        using var response = await WebsiteFixture.Client.GetAsync(url);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound), because);
    }

    private static async Task<string> GetText(string url)
    {
        using var response = await WebsiteFixture.Client.GetAsync(url);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), url);
        return await response.Content.ReadAsStringAsync();
    }

    private static async Task<JObject> GetJson(string url)
        => JObject.Parse(await GetText(url));
}
