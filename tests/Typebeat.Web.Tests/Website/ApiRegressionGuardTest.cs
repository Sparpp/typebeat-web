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
}
