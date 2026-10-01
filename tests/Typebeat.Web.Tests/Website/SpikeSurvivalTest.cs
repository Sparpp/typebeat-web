using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Npgsql;
using Typebeat.Web.Auth;

namespace Typebeat.Web.Tests.Website;

/// <summary>
/// Backlog 366 on the website host: the anonymous-only output cache (a hit for the anonymous,
/// never for a signed-in visitor or a bearer caller, evicted by the writes that change what it
/// shows) and the rate limits a browser can reach (the anonymous report brake and the global
/// anonymous read cap, whose website 429 still renders the styled error page).
/// </summary>
[TestFixture]
[NonParallelizable]
public class SpikeSurvivalTest
{
    private const string player_password = "spikepass-123456";

    [OneTimeSetUp]
    public Task OneTimeSetUp() => PublicSiteSeed.EnsureSeededAsync();

    // ---- output cache ----

    [Test]
    public async Task Listing_SecondAnonymousRead_IsServedFromTheCache_WithEdgeHeaders()
    {
        string url = "/beatmapsets?q=" + Guid.NewGuid().ToString("N");

        using var first = await WebsiteFixture.Client.GetAsync(url);
        using var second = await WebsiteFixture.Client.GetAsync(url);

        Assert.Multiple(() =>
        {
            Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(first.Headers.Age, Is.Null, "the first read renders");
            Assert.That(first.Headers.CacheControl?.ToString(), Is.EqualTo("public, max-age=0, s-maxage=30"));

            Assert.That(second.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(second.Headers.Age, Is.Not.Null, "the second read is a cache hit");
            Assert.That(second.Headers.CacheControl?.ToString(), Is.EqualTo("public, max-age=0, s-maxage=30"));
        });
    }

    [Test]
    public async Task SignedInVisitor_NeverGetsTheCachedAnonymousPage_AndIsNoStore()
    {
        string url = "/beatmapsets?q=" + Guid.NewGuid().ToString("N");
        var (browser, username) = await signedInBrowserAsync("spike nav");
        using var _ = browser;

        using (var warm = await WebsiteFixture.Client.GetAsync(url))
            Assert.That(warm.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        using var mine = await browser.GetAsync(url);
        string html = await mine.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(mine.Headers.Age, Is.Null, "not the anonymous entry");
            Assert.That(html, Does.Contain(WebUtility.HtmlEncode(username)), "the nav names the signed-in visitor");
            // no-store either way: the middleware's "private, no-store", or the "no-cache, no-store"
            // antiforgery already set for the nav's sign-out form token.
            Assert.That(mine.Headers.CacheControl?.NoStore, Is.True);
            Assert.That(mine.Headers.CacheControl?.Public, Is.False);
        });
    }

    [Test]
    public async Task BrowserPlay_IsReflectedAtOnce_InTheCachedAnonymousSetApi()
    {
        long setId = PublicSiteSeed.CoveredSetId;
        var (browser, _) = await signedInBrowserAsync("spike play");
        using var _b = browser;

        int before = await anonymousPlayCountAsync(setId);
        using (var hit = await WebsiteFixture.Client.GetAsync($"/api/v2/beatmapsets/{setId}"))
            Assert.That(hit.Headers.Age, Is.Not.Null, "the set API is cached, so only eviction can show the play");

        string csrf = await browserCsrfAsync(browser);
        long tokenId = await browserTokenAsync(browser, csrf, setId);
        await browserSubmitAsync(browser, csrf, tokenId);

        Assert.That(await anonymousPlayCountAsync(setId), Is.GreaterThan(before), "the submit evicted set:{id}");
    }

    [Test]
    public async Task UnpublishedSetMedia_ServedToItsOwner_IsNeverServedToAnonymous()
    {
        long setId = PublicSiteSeed.HiddenId;
        string bearer = await issueBearerAsync(PublicSiteSeed.MapperId);

        using (var owner = new HttpRequestMessage(HttpMethod.Get, $"/play/map/{setId}/diffs"))
        {
            owner.Headers.Add("Authorization", "Bearer " + bearer);
            using var response = await WebsiteFixture.Client.SendAsync(owner);

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), "the owner sees their hidden set");
            Assert.That(response.Headers.CacheControl?.NoStore, Is.True);
            Assert.That(response.Headers.CacheControl?.Private, Is.True);
        }

        using var anonymous = await WebsiteFixture.Client.GetAsync($"/play/map/{setId}/diffs");
        Assert.That(anonymous.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    // ---- rate limits ----

    [Test]
    public async Task AnonymousReports_ArePosted_UntilTheBrakeAnswers429()
    {
        long setId = PublicSiteSeed.SearchSetId;
        var (browser, _) = WebsiteFixture.CreateBrowser();
        using var _b = browser;

        string token = await WebsiteFixture.GetAntiforgeryTokenAsync(browser, $"/beatmapsets/{setId}");

        for (int i = 0; i < RateLimits.ReportsPerWindow; i++)
        {
            using var ok = await postReportAsync(browser, setId, token);
            Assert.That(ok.StatusCode, Is.EqualTo(HttpStatusCode.OK), $"report {i + 1} lands on the thanks page");
            Assert.That(ok.RequestMessage!.RequestUri!.Query, Does.Contain("reported=true"));
        }

        using var limited = await postReportAsync(browser, setId, token);
        string body = await limited.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(limited.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));
            Assert.That(limited.Headers.RetryAfter, Is.Not.Null);
            Assert.That((string?)JObject.Parse(body)["error"], Is.EqualTo(RateLimits.RejectionMessage));
        });

        // Only the Report POST is counted: the page itself still renders for the same visitor.
        using var page = await browser.GetAsync($"/beatmapsets/{setId}");
        Assert.That(page.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task AnonymousReadCap_Answers429_AndAWebsitePageStillRendersTheStyledError()
    {
        var (browser, _) = WebsiteFixture.CreateBrowser();
        using var _b = browser;

        for (int i = 0; i < RateLimits.AnonymousReadsPerMinute; i++)
        {
            using var ok = await browser.GetAsync("/menu-content.json");
            Assert.That(ok.StatusCode, Is.EqualTo(HttpStatusCode.OK), $"read {i + 1} is inside the cap");
        }

        using var wire = await browser.GetAsync("/menu-content.json");
        string wireBody = await wire.Content.ReadAsStringAsync();

        using var page = await browser.GetAsync("/rankings");
        string html = await page.Content.ReadAsStringAsync();

        // A different visitor is untouched: the cap is per client IP.
        var (other, _) = WebsiteFixture.CreateBrowser();
        using var _o = other;
        using var otherRead = await other.GetAsync("/menu-content.json");

        Assert.Multiple(() =>
        {
            Assert.That(wire.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));
            Assert.That(wire.Headers.RetryAfter, Is.Not.Null);
            Assert.That((string?)JObject.Parse(wireBody)["error"], Is.EqualTo(RateLimits.RejectionMessage));

            Assert.That(page.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));
            Assert.That(html, Does.Contain("Slow down"), "the /error/429 page renders, uncounted");

            Assert.That(otherRead.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        });
    }

    // ---- helpers ----

    private static async Task<(HttpClient Browser, string Username)> signedInBrowserAsync(string label)
    {
        string username = $"{label} {Guid.NewGuid().ToString("N")[..8]}";
        await WebsiteFixture.SeedUserAsync(username, username.Replace(' ', '.') + "@example.com", player_password, verified: true);

        var (browser, _) = WebsiteFixture.CreateBrowser();
        using (await WebsiteFixture.LoginAndVerifyAsync(browser, username, player_password)) { }
        return (browser, username);
    }

    private static async Task<int> anonymousPlayCountAsync(long setId)
    {
        using var response = await WebsiteFixture.Client.GetAsync($"/api/v2/beatmapsets/{setId}");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        return (int)JObject.Parse(await response.Content.ReadAsStringAsync())["play_count"]!;
    }

    private static Task<HttpResponseMessage> postReportAsync(HttpClient browser, long setId, string token)
        => browser.PostAsync($"/beatmapsets/{setId}?handler=Report", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["reason"] = "rate limit probe",
        }));

    private static async Task<string> issueBearerAsync(long userId)
    {
        string access = "spike-" + Guid.NewGuid().ToString("N");

        await using var conn = new NpgsqlConnection(WebsiteFixture.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync(
            """
            INSERT INTO oauth_tokens (user_id, access_hash, refresh_hash, access_expires_at, refresh_expires_at)
            VALUES (@userId, @accessHash, @refreshHash, now() + interval '1 day', now() + interval '30 days')
            """,
            new
            {
                userId,
                accessHash = SHA256.HashData(Encoding.UTF8.GetBytes(access)),
                refreshHash = SHA256.HashData(Encoding.UTF8.GetBytes(access + "-refresh")),
            });

        return access;
    }

    private static async Task<string> browserCsrfAsync(HttpClient client)
    {
        string page = await (await client.GetAsync("/play")).Content.ReadAsStringAsync();
        var match = Regex.Match(page, "csrf:\\s*\"([^\"]+)\"");
        Assert.That(match.Success, Is.True, "the /play page must publish an antiforgery token");
        return match.Groups[1].Value;
    }

    private static async Task<long> browserTokenAsync(HttpClient client, string csrf, long setId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/play/token");
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        request.Content = new StringContent(JsonConvert.SerializeObject(new { setId, beatmapId = 0 }), Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), "play token");
        return (long)JObject.Parse(await response.Content.ReadAsStringAsync())["id"]!;
    }

    private static async Task browserSubmitAsync(HttpClient client, string csrf, long tokenId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/play/submit");
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        request.Content = new StringContent(JsonConvert.SerializeObject(new
        {
            token = tokenId,
            passed = true,
            totalScore = 100_000,
            maxCombo = 10,
            statistics = new Dictionary<string, int> { ["great"] = 10 },
            maximumStatistics = new Dictionary<string, int> { ["great"] = 10 },
        }), Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), "play submit");
    }
}
