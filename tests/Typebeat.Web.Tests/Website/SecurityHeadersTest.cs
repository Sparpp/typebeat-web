using System.Net;
using Microsoft.AspNetCore.Http;

namespace Typebeat.Web.Tests.Website;

/// <summary>
/// The baseline security headers (S1-023): nosniff and a Referrer-Policy on every response,
/// framing refused everywhere except the /play page the itch.io embed loads, HSTS only over HTTPS
/// outside Development. Runs on the shared WebsiteFixture host, which is Development, so HSTS is
/// pinned against <see cref="SecurityHeaders.Apply"/> directly.
/// </summary>
public class SecurityHeadersTest
{
    private static string? header(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values) ? string.Join(", ", values) : null;

    [TestCase("/")]
    [TestCase("/login")]
    [TestCase("/settings")]
    [TestCase("/beatmapsets")]
    [TestCase("/no-such-page")]
    [TestCase("/health")]
    [TestCase("/api/v2/me")]
    [TestCase("/menu-content.json")]
    [TestCase("/play/map/987654321/diffs")]
    public async Task EveryResponse_RefusesFraming(string url)
    {
        using var client = WebsiteFixture.CreateNoRedirectClient();
        using var response = await client.GetAsync(url);

        Assert.Multiple(() =>
        {
            Assert.That(header(response, "X-Content-Type-Options"), Is.EqualTo("nosniff"), url);
            Assert.That(header(response, "Referrer-Policy"), Is.EqualTo("strict-origin-when-cross-origin"), url);
            Assert.That(header(response, "Content-Security-Policy"), Is.EqualTo("frame-ancestors 'none'"), url);
            Assert.That(header(response, "X-Frame-Options"), Is.EqualTo("DENY"), url);
        });
    }

    [TestCase("/play")]
    [TestCase("/play/")]
    [TestCase("/play?set=1")]
    public async Task PlayPage_StaysFrameable_ForTheEmbed(string url)
    {
        using var response = await WebsiteFixture.Client.GetAsync(url);

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), url);
            Assert.That(header(response, "X-Content-Type-Options"), Is.EqualTo("nosniff"), url);
            Assert.That(header(response, "Referrer-Policy"), Is.EqualTo("strict-origin-when-cross-origin"), url);
            Assert.That(header(response, "Content-Security-Policy"), Is.Null, url);
            Assert.That(header(response, "X-Frame-Options"), Is.Null, url);
        });
    }

    [TestCase("/play", true)]
    [TestCase("/play/", true)]
    [TestCase("/PLAY", true)]
    [TestCase("/play/map/1/osu", false)]
    [TestCase("/play/token", false)]
    [TestCase("/playground", false)]
    [TestCase("/", false)]
    public void OnlyThePlayPage_IsFrameable(string path, bool expected)
        => Assert.That(SecurityHeaders.IsFrameable(new PathString(path)), Is.EqualTo(expected));

    [TestCase("https", false, true)]
    [TestCase("https", true, false)]
    [TestCase("http", false, false)]
    public void Hsts_OnlyOverHttps_OutsideDevelopment(string scheme, bool development, bool expected)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Scheme = scheme;

        SecurityHeaders.Apply(ctx, frameable: false, development);

        string? hsts = ctx.Response.Headers.StrictTransportSecurity;
        Assert.That(hsts, expected ? Is.EqualTo("max-age=31536000") : Is.Null);
        if (expected)
            Assert.That(hsts, Does.Not.Contain("includeSubDomains"));
    }

    [Test]
    public async Task TestHost_IsDevelopment_SoSendsNoHsts()
    {
        using var response = await WebsiteFixture.Client.GetAsync("/");
        Assert.That(header(response, "Strict-Transport-Security"), Is.Null);
    }
}
