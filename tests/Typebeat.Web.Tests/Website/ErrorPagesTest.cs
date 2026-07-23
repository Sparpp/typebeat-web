using System.Net;

namespace Typebeat.Web.Tests.Website;

/// <summary>
/// Website 404s render the styled error page (site chrome + a way back) instead of a 0-byte
/// blank body, while keeping the true status code. Wire-route behavior is pinned separately in
/// <see cref="ApiRegressionGuardTest"/>; these two suites together fence the UseWhen scope.
/// </summary>
public class ErrorPagesTest
{
    [TestCase("/beatmapsets/987654321")]
    [TestCase("/users/98765432")]
    [TestCase("/no-such-page")]
    public async Task Website404_IsStyled_WithSiteChrome(string url)
    {
        using var response = await WebsiteFixture.Client.GetAsync(url);
        string html = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound), url);
            Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("text/html"), url);

            // Site chrome (nav wordmark + footer) and the error copy with a way back home.
            Assert.That(html, Does.Contain("type!beat"), url);
            Assert.That(html, Does.Contain("skipped a beat"), url);
            Assert.That(html, Does.Contain("href=\"/\""), url);
            Assert.That(html, Does.Contain("href=\"/beatmapsets\""), url);
        });
    }

    [Test]
    public async Task ErrorPage_DirectVisit_KeepsTheHonestStatusCode()
    {
        using var notFound = await WebsiteFixture.Client.GetAsync("/error/404");
        using var serverError = await WebsiteFixture.Client.GetAsync("/error/500");

        Assert.Multiple(() =>
        {
            Assert.That(notFound.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(serverError.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
        });
    }
}
