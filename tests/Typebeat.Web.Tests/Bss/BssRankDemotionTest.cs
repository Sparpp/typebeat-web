using System.Net;
using System.Net.Http.Headers;
using Dapper;
using Newtonsoft.Json.Linq;

namespace Typebeat.Web.Tests.Bss;

/// <summary>
/// The automatic ranked -> pending demotion (030_gameplay_fingerprint.sql, backlog 173) driven
/// through the REAL endpoints, because the thing that has to hold is that BOTH upload routes carry
/// it: the full-package PUT and the delta PATCH. They converge on the same Parse/Validate/Ingest
/// tail inside PackageIngest's per-set advisory-lock scope, and this is the test that says so out
/// loud rather than by inspection.
///
/// <para>
/// The exhaustive in/out matrix (which edits demote, which keep the rank, what the audit row says,
/// what happens to scores, and the backfill) lives on the ingest itself in
/// <see cref="PackageIngestDbTest"/>, where it runs without a host.
/// </para>
/// </summary>
[TestFixture]
[NonParallelizable]
public class BssRankDemotionTest
{
    private const string username = "demotion mapper";

    private string bearer = null!;

    /// <summary>The fixture lyric with every time shifted: same words, different gameplay.</summary>
    private const string retimed_lyrics =
        """
        {"version":2,"song_end_ms":4000,"granularity":"Word"}
        {"text":"ab cd","start_ms":1500,"end_ms":3800,"words":[{"text":"ab","start_ms":1500,"end_ms":2600,"score":1},{"text":"cd","start_ms":2600,"end_ms":3800,"score":1}]}
        """;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        (_, bearer) = await BssFixture.CreateUserAsync(username, verified: true);
    }

    [Test]
    public async Task FullPackagePut_OnARankedSet_DemotesOnAGameplayChangeOnly()
    {
        var (setId, diffId) = await CreateRankedSetAsync();

        // 1. A metadata-only re-upload. New version, same gameplay, rank survives.
        await UploadAsync(setId, Package(setId, diffId, title: "Neon Nights (Deluxe)", tags: "remastered"));

        Assert.That(await StatusAsync(setId), Is.EqualTo("ranked"), "a tag and title edit is not a gameplay change");
        Assert.That(await AutoUnranksAsync(setId), Is.Zero);

        // 2. The same map, retimed. Demoted, and audited.
        await UploadAsync(setId, Package(setId, diffId, title: "Neon Nights (Deluxe)", tags: "remastered", lyrics: retimed_lyrics));

        Assert.That(await StatusAsync(setId), Is.EqualTo("pending"), "the PUT route carries the demotion");
        Assert.That(await AutoUnranksAsync(setId), Is.EqualTo(1));

        // The set really is off the ranked surface now: the APIv2 read every client uses says so.
        using var api = await BssSubmissionFlowTest.SendAsync(HttpMethod.Get, $"/api/v2/beatmapsets/{setId}", bearer);
        var body = JObject.Parse(await api.Content.ReadAsStringAsync());

        Assert.That((string)body["status"]!, Is.EqualTo("pending"));
    }

    [Test]
    public async Task DeltaPatch_OnARankedSet_DemotesOnAGameplayChangeOnly()
    {
        var (setId, diffId) = await CreateRankedSetAsync();

        // 1. PATCH in an unrelated file. Cuts a version, touches no gameplay, keeps the rank.
        using (var body = new MultipartFormDataContent())
        {
            var added = new ByteArrayContent(SyntheticPackage.Utf8("liner notes"));
            added.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            body.Add(added, "filesChanged", "extra/readme.txt");

            using var response = await BssSubmissionFlowTest.SendAsync(HttpMethod.Patch, $"/bss/beatmapsets/{setId}", bearer, body);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        }

        Assert.That(await StatusAsync(setId), Is.EqualTo("ranked"));
        Assert.That(await AutoUnranksAsync(setId), Is.Zero);

        // 2. PATCH the audio blob only, keeping its filename and every timing byte. This is the
        //    backlog-171 hole: without the audio arm of the fingerprint the map would keep its rank
        //    on a recording its stored timings no longer match.
        using (var body = new MultipartFormDataContent())
        {
            var swapped = new ByteArrayContent(BssSubmissionFlowTest.MakeWav(seconds: 3));
            swapped.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            body.Add(swapped, "filesChanged", "audio.mp3");

            using var response = await BssSubmissionFlowTest.SendAsync(HttpMethod.Patch, $"/bss/beatmapsets/{setId}", bearer, body);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        }

        Assert.That(await StatusAsync(setId), Is.EqualTo("pending"), "the PATCH route carries the demotion too");
        Assert.That(await AutoUnranksAsync(setId), Is.EqualTo(1));
    }

    [Test]
    public async Task ARemovedSetIsStillRefused_AndNeverReachesTheDemotionPath()
    {
        var (setId, diffId) = await CreateRankedSetAsync();

        await using (var conn = await BssFixture.OpenDbAsync())
            await conn.ExecuteAsync("UPDATE beatmapsets SET status = 'removed' WHERE id = @setId", new { setId });

        using var response = await BssSubmissionFlowTest.SendAsync(
            HttpMethod.Put, $"/bss/beatmapsets/{setId}", bearer,
            BssSubmissionFlowTest.PackageBody(Package(setId, diffId, lyrics: retimed_lyrics)));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.UnprocessableEntity),
            "a takedown is final from the submission side, gameplay change or not");

        Assert.Multiple(async () =>
        {
            Assert.That(await StatusAsync(setId), Is.EqualTo("removed"), "the demotion must never resurrect a taken-down set as pending");
            Assert.That(await AutoUnranksAsync(setId), Is.Zero);
        });
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------------

    /// <summary>Creates a one-difficulty set, publishes it with a first upload, then ranks it.</summary>
    private async Task<(long SetId, long DiffId)> CreateRankedSetAsync()
    {
        using var create = await BssSubmissionFlowTest.SendAsync(HttpMethod.Put, "/bss/beatmapsets", bearer,
            BssSubmissionFlowTest.JsonBody(new
            {
                beatmapset_id = (long?)null,
                beatmaps_to_create = 1,
                beatmaps_to_keep = Array.Empty<long>(),
                target = "Pending",
                notify_on_discussion_replies = false,
            }));

        Assert.That(create.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var body = JObject.Parse(await create.Content.ReadAsStringAsync());
        long setId = (long)body["beatmapset_id"]!;
        long diffId = body["beatmap_ids"]!.Select(t => (long)t).Single();

        await UploadAsync(setId, Package(setId, diffId));

        await using (var conn = await BssFixture.OpenDbAsync())
        {
            // What the reviewer's button does (Pages/Beatmapsets/Set.cshtml.cs), minus the page.
            await conn.ExecuteAsync("UPDATE beatmapsets SET status = 'ranked' WHERE id = @setId", new { setId });
        }

        return (setId, diffId);
    }

    private async Task UploadAsync(long setId, byte[] zipBytes)
    {
        using var response = await BssSubmissionFlowTest.SendAsync(
            HttpMethod.Put, $"/bss/beatmapsets/{setId}", bearer, BssSubmissionFlowTest.PackageBody(zipBytes));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
    }

    private static byte[] Package(
        long setId,
        long diffId,
        string title = "Neon Nights",
        string tags = "typebeat lyrics typing",
        string lyrics = SyntheticPackage.PaceRegressionLyrics)
    {
        var osu = SyntheticPackage.Utf8(SyntheticPackage.OsuText(
            title: title, titleUnicode: title, creator: username, tags: tags,
            beatmapId: diffId, beatmapSetId: setId, lyrics: lyrics));

        using var zip = SyntheticPackage.Zip(
            ("map.osu", osu),
            ("audio.mp3", BssSubmissionFlowTest.MakeWav(seconds: 2)),
            ("bg.jpg", SyntheticPackage.TinyPng()));

        return zip.ToArray();
    }

    private static async Task<string?> StatusAsync(long setId)
    {
        await using var conn = await BssFixture.OpenDbAsync();
        return await conn.ExecuteScalarAsync<string?>("SELECT status FROM beatmapsets WHERE id = @setId", new { setId });
    }

    private static async Task<int> AutoUnranksAsync(long setId)
    {
        await using var conn = await BssFixture.OpenDbAsync();

        return await conn.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM moderation_actions WHERE set_id = @setId AND action = 'auto_unrank'",
            new { setId });
    }
}
