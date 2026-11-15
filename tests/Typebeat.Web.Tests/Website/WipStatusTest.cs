using System.Net;
using System.Net.Http.Headers;
using Dapper;
using Newtonsoft.Json.Linq;
using Npgsql;
using Typebeat.Web.Auth;
using Typebeat.Web.Data;
using Typebeat.Web.Endpoints;
using Typebeat.Web.Pages;
using Typebeat.Web.Scoring;

namespace Typebeat.Web.Tests.Website;

/// <summary>
/// Backlog 476: 'wip' as a published beatmapset status of its own. It is published like 'pending'
/// (listed, searchable, on its mapper's profile, readable by the game API), shown as a WIP pill
/// everywhere a status shows, filtered on its own by the website and by the API's s=wip, kept in
/// the game overlay's combined "Pending &amp; WIP" category (s=pending), and, as on osu!, serves
/// no leaderboard and cannot be ranked until its mapper submits it as Pending. It can still be
/// loved (Respected), as osu-web loves any not-yet-approved map.
/// </summary>
[TestFixture]
[NonParallelizable]
public class WipStatusTest
{
    private const string tag = "wipstatusfixture";

    private const string reviewer_name = "wip reviewer";
    private const string reviewer_password = "wippass-1234567";

    private static NpgsqlDataSource dataSource = null!;
    private static long mapperId;
    private static long wipSetId, wipBeatmapId, pendingSetId;
    private static string viewerBearer = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        await PublicSiteSeed.EnsureSeededAsync();

        dataSource = NpgsqlDataSource.Create(WebsiteFixture.ConnectionString);
        await using var conn = await dataSource.OpenConnectionAsync();

        mapperId = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO users (username, email, password_hash, country_code)
            VALUES ('wip mapper', 'wip.mapper@example.com', 'x', 'US')
            RETURNING id
            """);

        long viewerId = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO users (username, email, password_hash, country_code)
            VALUES ('wip viewer', 'wip.viewer@example.com', 'x', 'US')
            RETURNING id
            """);
        viewerBearer = (await new TokenService(new Db(dataSource)).IssueAsync(viewerId)).AccessToken;

        await conn.ExecuteAsync(
            """
            INSERT INTO users (username, email, password_hash, country_code, map_reviewer)
            VALUES (@name, 'wip.reviewer@example.com', @hash, 'US', true)
            """,
            new { name = reviewer_name, hash = new PasswordService().Hash(reviewer_password) });

        (wipSetId, wipBeatmapId) = await insertSetAsync(conn, "Draft Song", "wip");
        (pendingSetId, _) = await insertSetAsync(conn, "Ready Song", "pending");

        // A passed play on the WIP map: stored unranked, as on any non-ranked set.
        await conn.ExecuteAsync(
            """
            INSERT INTO scores
                (user_id, beatmap_id, total_score, accuracy, completion, max_combo, rank, passed, ranked,
                 mods, statistics, maximum_statistics, ended_at)
            VALUES
                (@viewerId, @wipBeatmapId, 650000, 0.95, 1.0, 40, 'A', true, false,
                 '[]'::jsonb, '{"great":40}'::jsonb, '{"great":42}'::jsonb, now())
            """,
            new { viewerId, wipBeatmapId });
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (dataSource != null)
            await dataSource.DisposeAsync();
    }

    [Test]
    public void StatusHelpers_KnowWip()
    {
        Assert.Multiple(() =>
        {
            Assert.That(BeatmapsetEndpoints.IsPublished("wip"), Is.True);
            Assert.That(BeatmapsetEndpoints.PublishedSql, Does.Contain("'wip'"));
            Assert.That(BeatmapsetEndpoints.StatusString("wip"), Is.EqualTo("wip"));
            Assert.That(BeatmapsetDisplay.StatusLabel("wip"), Is.EqualTo("WIP"));
            Assert.That(BeatmapsetDisplay.PillClass("wip"), Is.EqualTo("pill--wip"));
            Assert.That(BeatmapLeaderboard.BoardFor("wip"), Is.Null, "a WIP map serves no board, as on osu!");
        });
    }

    // ---- website listing ----

    [Test]
    public async Task Listing_ShowsWipUnderAnyAndUnderItsOwnPill_ButNotUnderPending()
    {
        var (client, _) = WebsiteFixture.CreateBrowser();
        using var _c = client;

        string any = await client.GetStringAsync($"/beatmapsets?q=title%3D{tag}");
        string wip = await client.GetStringAsync($"/beatmapsets?q=title%3D{tag}&status=wip");
        string pending = await client.GetStringAsync($"/beatmapsets?q=title%3D{tag}&status=pending");

        Assert.Multiple(() =>
        {
            Assert.That(any, Does.Contain($"{tag} Draft Song"));
            Assert.That(any, Does.Contain($"{tag} Ready Song"));
            Assert.That(any, Does.Contain("pill--wip"));
            Assert.That(any, Does.Contain("status=wip"), "the status row offers a WIP pill");

            Assert.That(wip, Does.Contain($"{tag} Draft Song"));
            Assert.That(wip, Does.Not.Contain($"{tag} Ready Song"));

            Assert.That(pending, Does.Contain($"{tag} Ready Song"));
            Assert.That(pending, Does.Not.Contain($"{tag} Draft Song"), "the site's pills are one status each");
        });
    }

    // ---- game API search (the listing overlay) ----

    [Test]
    public async Task ApiSearch_PendingIsPendingAndWip_WipIsWipAlone()
    {
        var pending = await searchTitlesAsync("s=pending");
        var wip = await searchTitlesAsync("s=wip");
        var any = await searchTitlesAsync("");

        Assert.Multiple(() =>
        {
            Assert.That(pending, Is.EquivalentTo(new[] { "Draft Song", "Ready Song" }), "the client's \"Pending & WIP\" category");
            Assert.That(wip, Is.EqualTo(new[] { "Draft Song" }));
            Assert.That(any, Is.EquivalentTo(new[] { "Draft Song", "Ready Song" }));
        });
    }

    [Test]
    public async Task ApiSearch_SendsTheWipStatusString()
    {
        var result = await getObjectAsync($"/api/v2/beatmapsets/search?q=title%3D{tag}&s=wip");
        var set = result["beatmapsets"]!.Single();

        Assert.Multiple(() =>
        {
            Assert.That((string)set["status"]!, Is.EqualTo("wip"));
            Assert.That((string)set["beatmaps"]![0]!["status"]!, Is.EqualTo("wip"));
        });
    }

    // ---- game API: set, lookup, board ----

    [Test]
    public async Task GameApi_ServesTheWipSetPublicly_AsWip()
    {
        var (client, _) = WebsiteFixture.CreateBrowser();
        using var _c = client;

        using var response = await client.GetAsync($"/api/v2/beatmapsets/{wipSetId}");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var body = JObject.Parse(await response.Content.ReadAsStringAsync());
        Assert.That((string)body["status"]!, Is.EqualTo("wip"));
    }

    [Test]
    public async Task GameApi_BeatmapLookup_ReportsWip()
    {
        var body = await getObjectAsync($"/api/v2/beatmaps/lookup?id={wipBeatmapId}");
        Assert.That((string)body["status"]!, Is.EqualTo("wip"));
    }

    [Test]
    public async Task GameApi_WipMap_ServesNoBoard_EvenWithAPassedPlay()
    {
        var board = await getObjectAsync($"/api/v2/beatmaps/{wipBeatmapId}/scores");

        Assert.Multiple(() =>
        {
            Assert.That((JArray)board["scores"]!, Is.Empty);
            Assert.That((int)board["score_count"]!, Is.EqualTo(0));
        });
    }

    // ---- profile ----

    [Test]
    public async Task Profile_PendingSectionAndCount_IncludeWip()
    {
        var user = await getObjectAsync($"/api/v2/users/{mapperId}");
        var pending = JArray.Parse(await getBodyAsync($"/api/v2/users/{mapperId}/beatmapsets/pending"));

        Assert.Multiple(() =>
        {
            Assert.That((int)user["pending_beatmapset_count"]!, Is.EqualTo(2), "pending + WIP, as osu-web counts it");
            Assert.That(pending.Select(s => (string?)s["title"]), Is.EquivalentTo(new[] { $"{tag} Draft Song", $"{tag} Ready Song" }));
        });
    }

    [Test]
    public async Task ProfilePage_ShowsTheWipPill()
    {
        var (client, _) = WebsiteFixture.CreateBrowser();
        using var _c = client;

        string page = await client.GetStringAsync($"/users/{mapperId}");

        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain($"{tag} Draft Song"));
            Assert.That(page, Does.Contain("pill--wip"));
        });
    }

    // ---- set page ----

    [Test]
    public async Task SetPage_ShowsTheWipPill_AndNoBoard()
    {
        var (client, _) = WebsiteFixture.CreateBrowser();
        using var _c = client;

        using var response = await client.GetAsync($"/beatmapsets/{wipSetId}");
        string page = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), "a WIP set is public");
            Assert.That(page, Does.Contain("<span class=\"pill pill--wip\">WIP</span>"));
            Assert.That(page, Does.Contain("Work in progress"));
            Assert.That(page, Does.Not.Contain("lb-tabs"), "no board tabs on a WIP set");
        });
    }

    [Test]
    public async Task ReviewerSetPage_OffersRespectedButNotRankOnWip_AndSaysWhy()
    {
        using var client = await signedInReviewerAsync();

        string page = await client.GetStringAsync($"/beatmapsets/{wipSetId}");

        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("review-controls__note"));
            Assert.That(page, Does.Not.Contain("Rank this map"));
            Assert.That(page, Does.Contain("Mark as Respected"), "osu-web lets a WIP map be loved");
        });
    }

    [Test]
    public async Task RankLever_DoesNothingToAWipSet()
    {
        using var client = await signedInReviewerAsync();

        await postReviewAsync(client, wipSetId, "Rank");

        Assert.That(await statusOfAsync(wipSetId), Is.EqualTo("wip"), "Rank must not move a WIP set");
    }

    [Test]
    public async Task LoveLever_RespectsAWipSet_AndUnloveReturnsItToWip()
    {
        // Its own set, owner and untagged title, so the shared WIP set stays WIP and neither the
        // tag searches nor the mapper's profile assertions in this fixture ever see this one.
        long setId;
        await using (var conn = await dataSource.OpenConnectionAsync())
        {
            (setId, _) = await insertSetAsync(conn, "Respected Draft", "wip");
            long otherMapperId = await conn.ExecuteScalarAsync<long>(
                """
                INSERT INTO users (username, email, password_hash, country_code)
                VALUES ('wip love mapper', 'wip.love.mapper@example.com', 'x', 'US')
                RETURNING id
                """);
            await conn.ExecuteAsync(
                "UPDATE beatmapsets SET owner_id = @otherMapperId, title = 'wip love lever draft' WHERE id = @setId",
                new { otherMapperId, setId });
        }

        using var client = await signedInReviewerAsync();

        await postReviewAsync(client, setId, "Love");
        string? loved = await statusOfAsync(setId);

        await postReviewAsync(client, setId, "Unlove");
        string? unloved = await statusOfAsync(setId);

        Assert.Multiple(() =>
        {
            Assert.That(loved, Is.EqualTo("loved"), "a WIP set can be loved, as on osu-web");
            Assert.That(unloved, Is.EqualTo("wip"), "Unlove returns it to its mapper's intended WIP");
        });
    }

    // ---- helpers ----

    private static async Task<(long SetId, long BeatmapId)> insertSetAsync(NpgsqlConnection conn, string title, string status)
    {
        long setId = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmapsets (owner_id, title, artist, status, intended_status, submitted_at, updated_at)
            VALUES (@mapperId, @title, 'WIP Artist', @status, @status, now() - interval '2 days', now() - interval '2 days')
            RETURNING id
            """,
            new { mapperId, title = $"{tag} {title}", status });

        long beatmapId = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmaps (set_id, version_name, checksum_md5, total_length_s, drain_length_s, difficulty_rating, filename, lyrics)
            VALUES (@setId, 'only', @checksum, 60, 0, 2.5, 'only.osu', 'some lyrics here')
            RETURNING id
            """,
            new { setId, checksum = Guid.NewGuid().ToString("N") });

        return (setId, beatmapId);
    }

    private static async Task postReviewAsync(HttpClient client, long setId, string handler)
    {
        string token = await WebsiteFixture.GetAntiforgeryTokenAsync(client, $"/beatmapsets/{setId}");
        using (await client.PostAsync($"/beatmapsets/{setId}?handler={handler}",
                   new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }))) { }
    }

    private static async Task<string?> statusOfAsync(long setId)
    {
        await using var conn = await dataSource.OpenConnectionAsync();
        return await conn.ExecuteScalarAsync<string>("SELECT status FROM beatmapsets WHERE id = @setId", new { setId });
    }

    private static async Task<HttpClient> signedInReviewerAsync()
    {
        var (client, _) = WebsiteFixture.CreateBrowser();
        using (await WebsiteFixture.LoginAndVerifyAsync(client, reviewer_name, reviewer_password)) { }
        return client;
    }

    private static async Task<List<string>> searchTitlesAsync(string query)
    {
        var result = await getObjectAsync($"/api/v2/beatmapsets/search?q=title%3D{tag}&{query}");
        return result["beatmapsets"]!.Select(s => ((string)s["title"]!)[(tag.Length + 1)..]).ToList();
    }

    private static async Task<string> getBodyAsync(string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", viewerBearer);

        using var response = await WebsiteFixture.Client.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), $"{url}: {body}");
        return body;
    }

    private static async Task<JObject> getObjectAsync(string url) => JObject.Parse(await getBodyAsync(url));
}
