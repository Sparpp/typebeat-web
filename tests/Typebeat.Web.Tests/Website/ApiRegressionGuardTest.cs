using System.Net;

namespace Typebeat.Web.Tests.Website;

/// <summary>
/// Guards that the website build-out changed nothing about the pre-existing surface: /health
/// still answers the uptime monitor, and an APIv2 endpoint still speaks the exact WireJson
/// envelope (the game client special-cases these strings).
/// </summary>
public class ApiRegressionGuardTest
{
    [Test]
    public async Task Health_StillReturnsPlainOk()
    {
        using var response = await WebsiteFixture.Client.GetAsync("/health");
        string body = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(body, Is.EqualTo("ok"));
        });
    }

    [Test]
    public async Task ApiV2Me_WithoutBearer_StillExact401Envelope()
    {
        using var response = await WebsiteFixture.Client.GetAsync("/api/v2/me/");
        string body = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/json"));
            Assert.That(body, Is.EqualTo("{\"error\":\"authentication failed\"}"));
        });
    }

    [Test]
    public async Task MenuContent_StillEmptyImagesArray()
    {
        using var response = await WebsiteFixture.Client.GetAsync("/menu-content.json");
        string body = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(body, Is.EqualTo("{\"images\":[]}"));
        });
    }

    // ---- The website's styled error pages must never wrap a wire response (byte-exact). ----

    [Test]
    public async Task ApiV2BeatmapsetMissing_StillExact404Envelope()
    {
        using var response = await WebsiteFixture.Client.GetAsync("/api/v2/beatmapsets/999999999");
        string body = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/json"));
            Assert.That(body, Is.EqualTo("{\"error\":\"not found\"}"));
        });
    }

    [Test]
    public async Task UnmappedApiPath_StillEmptyBodied404()
    {
        using var response = await WebsiteFixture.Client.GetAsync("/api/v2/no-such-endpoint");
        string body = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(body, Is.Empty, "an unmatched /api path must not grow an HTML error body");
        });
    }

    [Test]
    public async Task BssWithoutBearer_StillExact401Envelope()
    {
        using var response = await WebsiteFixture.Client.PutAsync("/bss/beatmapsets", null);
        string body = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/json"));
            Assert.That(body, Is.EqualTo("{\"error\":\"authentication failed\"}"));
        });
    }

    [Test]
    public async Task RegistrationPost_WrongUserAgent_StillExact403Envelope()
    {
        // POST /users is the game client's registration wire route; only GET/HEAD /users/* is
        // website surface. The UA gate fires before anything else — exact envelope pinned.
        using var response = await WebsiteFixture.Client.PostAsync("/users", new FormUrlEncodedContent([]));
        string body = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/json"));
            Assert.That(body, Is.EqualTo("{\"error\":\"forbidden\"}"));
        });
    }
}
