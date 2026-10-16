using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using Typebeat.Web.Auth;

namespace Typebeat.Web.Tests.Website;

/// <summary>
/// Backlog 406: a guest client reads what the website already shows anyone (map lookup, the
/// beatmap board, profiles and their sections), and only the writes stay signed-in. The write
/// half is pinned off the ROUTE TABLE rather than a hand list of URLs, so a write route added
/// without <c>RequireBearer</c>, or a personal read losing it, goes red here.
/// </summary>
public class GuestReadsTest
{
    /// <summary>
    /// Non-GET routes that deliberately carry no bearer filter, each with the reason. A new write
    /// route lands here only by a conscious edit; one that disappears fails as stale.
    /// </summary>
    private static readonly Dictionary<string, string> unbearered_writes = new()
    {
        ["POST /api/v2/typebeat/align"] = "the pre-287 aligner route, answers 410 to everyone (the server aligner moved to /server-align, backlog 413)",
        ["POST /oauth/token"] = "the sign-in itself",
        ["POST /users"] = "registration",
        ["POST /play/token"] = "browser play: website session cookie, checked in the handler",
        ["POST /play/submit"] = "browser play: website session cookie, checked in the handler",
        ["PUT /api/v2/ops/stems/{setId:long}"] = "operator tool behind the buddy key",
    };

    /// <summary>The GETs that stay signed-in, because they answer about the caller themself.</summary>
    private static readonly string[] personal_reads =
    [
        "GET /api/v2/me/",
        "GET /api/v2/me/{ruleset}",
        "GET /api/v2/me/beatmapset-favourites",
        "GET /api/v2/friends",
        "GET /api/v2/notifications",
        "GET /api/v2/chat/updates",
        "GET /api/v2/chat/channels",
        "GET /api/v2/blocks",
        "GET /bss/upload-sessions/{sessionId}",
        "GET /api/v2/typebeat/server-align/{id}",
    ];

    /// <summary>The reads backlog 406 opened to guests: none may carry the bearer filter again.</summary>
    private static readonly string[] guest_reads =
    [
        "GET /api/v2/beatmaps/lookup",
        "GET /api/v2/beatmaps/{beatmapId:long}/scores",
        "GET /api/v2/users/{lookup}",
        "GET /api/v2/users/{lookup}/{ruleset}",
        "GET /api/v2/users/{userId:long}/scores/{type}",
        "GET /api/v2/users/{userId:long}/beatmapsets/{type}",
        "GET /api/v2/users/{userId:long}/beatmapsets/most_played",
        "GET /api/v2/seasonal-backgrounds",
    ];

    [OneTimeSetUp]
    public Task OneTimeSetUp() => PublicSiteSeed.EnsureSeededAsync();

    [Test]
    public void TheRouteTable_KeepsEveryWriteAndEveryPersonalRead_SignedIn()
    {
        var routes = routeTable();

        var bearered = routes.Where(r => r.Bearer).Select(r => r.Key).ToHashSet();
        var unbearered = routes.Where(r => !r.Bearer).Select(r => r.Key).ToHashSet();

        var openWrites = routes.Where(r => !r.Bearer && !r.IsRead).Select(r => r.Key).ToList();
        var bearedReads = routes.Where(r => r.Bearer && r.IsRead).Select(r => r.Key).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(openWrites, Is.EquivalentTo(unbearered_writes.Keys),
                "a non-GET route without RequireBearer is either a mistake or belongs in unbearered_writes with its reason");
            Assert.That(bearedReads, Is.EquivalentTo(personal_reads),
                "the signed-in GETs are exactly the personal ones: a read losing RequireBearer, or a new one gaining it, is a decision");

            foreach (string read in guest_reads)
                Assert.That(unbearered, Does.Contain(read), $"{read} is a guest read (backlog 406)");

            // The server aligner's writes (backlog 413) are the caller's own jobs.
            Assert.That(bearered, Does.Contain("POST /api/v2/typebeat/server-align"));
            Assert.That(bearered, Does.Contain("DELETE /api/v2/typebeat/server-align/{id}"));

            // Sanity: the sweep saw the score submit, so it is reading the real table.
            Assert.That(bearered, Does.Contain("PUT /api/v2/beatmaps/{beatmapId:long}/solo/scores/{tokenId:long}"));
        });
    }

    [Test]
    public async Task EverySignedInRoute_Answers401_ToAGuest_WithTheExactEnvelope()
    {
        var targets = routeTable().Where(r => r.Bearer).ToList();
        Assert.That(targets, Has.Count.GreaterThan(20), "the sweep must be over the real route table");

        var failures = new List<string>();

        foreach (var route in targets)
        {
            using var request = new HttpRequestMessage(new HttpMethod(route.Method), concretePath(route.Pattern));
            if (!route.IsRead)
                request.Content = new StringContent(string.Empty);

            using var response = await WebsiteFixture.Client.SendAsync(request);
            string body = await response.Content.ReadAsStringAsync();

            if (response.StatusCode != HttpStatusCode.Unauthorized || body != "{\"error\":\"authentication failed\"}")
                failures.Add($"{route.Key}: {(int)response.StatusCode} {body}");
        }

        // The browser's own play routes are signed-in too, by session cookie in their handlers.
        foreach (string path in new[] { "/play/token", "/play/submit" })
        {
            using var response = await WebsiteFixture.Client.PostAsync(path, new StringContent(string.Empty));
            if (response.StatusCode != HttpStatusCode.Unauthorized)
                failures.Add($"POST {path}: {(int)response.StatusCode}");
        }

        Assert.That(failures, Is.Empty);
    }

    [Test]
    public async Task GuestReads_Answer200_WithNoBearer()
    {
        long setId = PublicSiteSeed.LeaderboardSetId;
        long beatmapId = PublicSiteSeed.LeaderboardBeatmapId;
        long userId = PublicSiteSeed.TypistOneId;

        string[] paths =
        [
            $"/api/v2/beatmaps/lookup?id={beatmapId}",
            $"/api/v2/beatmaps/{beatmapId}/scores?type=global&mode=typebeat&limit=50",
            $"/api/v2/users/{userId}?key=id",
            $"/api/v2/users/{userId}/typebeat?key=id",
            $"/api/v2/users/{userId}/scores/best?offset=0&limit=51",
            $"/api/v2/users/{userId}/scores/recent?offset=0&limit=51",
            $"/api/v2/users/{userId}/beatmapsets/most_played?offset=0&limit=51",
            $"/api/v2/users/{PublicSiteSeed.MapperId}/beatmapsets/ranked?offset=0&limit=51",
            $"/api/v2/users/{userId}/beatmapsets/favourite?offset=0&limit=51",
            "/api/v2/seasonal-backgrounds",
        ];

        var failures = new List<string>();

        foreach (string path in paths)
        {
            using var response = await WebsiteFixture.Client.GetAsync(path);
            string body = await response.Content.ReadAsStringAsync();

            if (response.StatusCode != HttpStatusCode.OK)
                failures.Add($"{path}: {(int)response.StatusCode} {body}");
        }

        Assert.That(failures, Is.Empty);

        // The board a guest reads is the public one, with no caller row.
        using var board = await WebsiteFixture.Client.GetAsync($"/api/v2/beatmaps/{beatmapId}/scores");
        var json = JObject.Parse(await board.Content.ReadAsStringAsync());

        Assert.Multiple(() =>
        {
            Assert.That(json["scores"]!.Count(), Is.GreaterThan(0), $"set {setId}'s board has seeded plays");
            Assert.That(json["user_score"]?.Type ?? JTokenType.Null, Is.EqualTo(JTokenType.Null));
        });
    }

    [TestCase("country")]
    [TestCase("friend")]
    [TestCase("team")]
    [TestCase("FRIEND")]
    public async Task AGuestsPersonalBoardScope_IsAnEmpty200_NeverA401(string type)
    {
        using var response = await WebsiteFixture.Client.GetAsync(
            $"/api/v2/beatmaps/{PublicSiteSeed.LeaderboardBeatmapId}/scores?type={type}&mode=typebeat");
        string body = await response.Content.ReadAsStringAsync();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), body);

        var json = JObject.Parse(body);
        Assert.Multiple(() =>
        {
            Assert.That(json["scores"]!.Count(), Is.EqualTo(0));
            Assert.That((int)json["score_count"]!, Is.EqualTo(0));
            Assert.That(json["user_score"]?.Type ?? JTokenType.Null, Is.EqualTo(JTokenType.Null));
        });
    }

    [Test]
    public async Task TheAnonymousReadCap_CountsTheGuestReads_ButNotTheLookup()
    {
        var (browser, _) = WebsiteFixture.CreateBrowser();
        using var _b = browser;

        for (int i = 0; i < RateLimits.AnonymousReadsPerMinute; i++)
        {
            using var ok = await browser.GetAsync("/menu-content.json");
            Assert.That(ok.StatusCode, Is.EqualTo(HttpStatusCode.OK), $"read {i + 1} is inside the cap");
        }

        using var profile = await browser.GetAsync($"/api/v2/users/{PublicSiteSeed.TypistOneId}?key=id");
        using var board = await browser.GetAsync($"/api/v2/beatmaps/{PublicSiteSeed.LeaderboardBeatmapId}/scores");

        // The lookup keeps its own per-IP token bucket, deep enough for a pack import.
        using var lookup = await browser.GetAsync($"/api/v2/beatmaps/lookup?id={PublicSiteSeed.LeaderboardBeatmapId}");

        Assert.Multiple(() =>
        {
            Assert.That(profile.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests), "a guest profile read is under the cap");
            Assert.That(board.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests), "a guest board read is under the cap");
            Assert.That(lookup.StatusCode, Is.EqualTo(HttpStatusCode.OK), "the lookup is exempt from the cap");
        });
    }

    // ---- helpers ----

    private sealed record Route(string Method, string Pattern, bool Bearer)
    {
        public string Key => $"{Method} {Pattern}";

        public bool IsRead => Method is "GET" or "HEAD";
    }

    /// <summary>Every minimal-API route (one row per method), with whether it carries RequireBearer.</summary>
    private static List<Route> routeTable()
    {
        var sources = WebsiteFixture.Services.GetServices<EndpointDataSource>();

        return sources
               .SelectMany(s => s.Endpoints)
               .OfType<RouteEndpoint>()
               .SelectMany(e =>
               {
                   var methods = e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [];
                   bool bearer = e.Metadata.GetMetadata<AuthExtensions.BearerRequiredMetadata>() is not null;
                   string pattern = e.RoutePattern.RawText ?? string.Empty;
                   if (!pattern.StartsWith('/'))
                       pattern = "/" + pattern;
                   return methods.Select(m => new Route(m, pattern, bearer));
               })
               .Distinct()
               .ToList();
    }

    /// <summary>A route pattern with every parameter filled by a value its constraint accepts.</summary>
    private static string concretePath(string pattern)
        => Regex.Replace(pattern, @"\{[^}]+\}", "1");
}
