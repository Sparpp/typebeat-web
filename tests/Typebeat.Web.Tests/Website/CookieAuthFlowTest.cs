using System.Net;
using Newtonsoft.Json.Linq;

namespace Typebeat.Web.Tests.Website;

/// <summary>
/// End-to-end cookie auth: login form -> typebeat_session cookie -> authed layout, and the
/// identity-unification contract — the raw token in the cookie IS a bearer access token, so
/// the API's /api/v2/me/ must resolve it to the same user the website session shows.
/// </summary>
public class CookieAuthFlowTest
{
    [Test]
    public async Task Login_ShowsUserChip_AndCookieTokenResolvesViaBearerPath()
    {
        var (client, cookies) = WebsiteFixture.CreateBrowser();
        using var _ = client;

        string token = await WebsiteFixture.GetAntiforgeryTokenAsync(client, "/login");

        using var response = await client.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["Login"] = WebsiteFixture.SeededUsername,
            ["Password"] = WebsiteFixture.SeededPassword,
        }));

        // 302 -> "/" followed by the redirect handler.
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        string html = await response.Content.ReadAsStringAsync();
        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("user-chip"));
            Assert.That(html, Does.Contain(WebsiteFixture.SeededUsername));
            Assert.That(html, Does.Contain($"href=\"/users/{WebsiteFixture.SeededUserId}\""));
            Assert.That(html, Does.Contain("Sign out"));
        });

        // The cookie carries a raw access token; the API bearer path must agree on who it is.
        string? sessionToken = cookies.GetCookies(WebsiteFixture.BaseAddress)["typebeat_session"]?.Value;
        Assert.That(sessionToken, Is.Not.Null.And.Not.Empty, "typebeat_session cookie not set");

        using var meRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v2/me/");
        meRequest.Headers.Add("Authorization", $"Bearer {sessionToken}");

        using var meResponse = await WebsiteFixture.Client.SendAsync(meRequest);
        Assert.That(meResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var me = JObject.Parse(await meResponse.Content.ReadAsStringAsync());
        Assert.Multiple(() =>
        {
            Assert.That(me["id"]!.Value<long>(), Is.EqualTo(WebsiteFixture.SeededUserId));
            Assert.That(me["username"]!.Value<string>(), Is.EqualTo(WebsiteFixture.SeededUsername));
        });
    }

    [Test]
    public async Task Login_WrongPassword_ShowsGenericError_AndSetsNoCookie()
    {
        var (client, cookies) = WebsiteFixture.CreateBrowser();
        using var _ = client;

        string token = await WebsiteFixture.GetAntiforgeryTokenAsync(client, "/login");

        using var response = await client.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["Login"] = WebsiteFixture.SeededUsername,
            ["Password"] = "not the password",
        }));

        string html = await response.Content.ReadAsStringAsync();
        Assert.Multiple(() =>
        {
            // Re-rendered form (200), generic message, no session issued.
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(html, Does.Contain("The username or password is incorrect."));
            Assert.That(cookies.GetCookies(WebsiteFixture.BaseAddress)["typebeat_session"], Is.Null);
        });
    }

    [Test]
    public async Task Register_CreatesAccount_AndAutoLogsIn()
    {
        var (client, cookies) = WebsiteFixture.CreateBrowser();
        using var _ = client;

        string token = await WebsiteFixture.GetAntiforgeryTokenAsync(client, "/register");

        using var response = await client.PostAsync("/register", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["Username"] = "reg tester",
            ["Email"] = "reg.tester@example.com",
            ["Password"] = "registerpass1",
        }));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        string html = await response.Content.ReadAsStringAsync();
        Assert.Multiple(() =>
        {
            // Landed on "/" signed in: chip visible, no "Sign in" prompt.
            Assert.That(html, Does.Contain("user-chip"));
            Assert.That(html, Does.Contain("reg tester"));
            Assert.That(cookies.GetCookies(WebsiteFixture.BaseAddress)["typebeat_session"], Is.Not.Null);
        });
    }

    [Test]
    public async Task Register_InvalidFields_ShowsSharedValidationErrors()
    {
        var (client, _) = WebsiteFixture.CreateBrowser();
        using var __ = client;

        string token = await WebsiteFixture.GetAntiforgeryTokenAsync(client, "/register");

        using var response = await client.PostAsync("/register", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["Username"] = "x!",
            ["Email"] = "not-an-email",
            ["Password"] = "short",
        }));

        string html = await response.Content.ReadAsStringAsync();
        Assert.Multiple(() =>
        {
            // Exactly the messages the in-client POST /users path produces (shared rules).
            Assert.That(html, Does.Contain("Username must be between 3 and 15 characters."));
            Assert.That(html, Does.Contain("Please enter a valid email address."));
            Assert.That(html, Does.Contain("Password must be at least 8 characters."));
        });
    }

    [Test]
    public async Task Logout_ClearsCookie_AndRevokesTheToken()
    {
        var (client, cookies) = WebsiteFixture.CreateBrowser();
        using var _ = client;

        // Sign in first.
        string loginToken = await WebsiteFixture.GetAntiforgeryTokenAsync(client, "/login");
        using (var login = await client.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = loginToken,
            ["Login"] = WebsiteFixture.SeededUsername,
            ["Password"] = WebsiteFixture.SeededPassword,
        })))
            Assert.That(login.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        string sessionToken = cookies.GetCookies(WebsiteFixture.BaseAddress)["typebeat_session"]!.Value;

        // The authed home page carries the logout form (and its antiforgery token).
        string logoutToken = await WebsiteFixture.GetAntiforgeryTokenAsync(client, "/");

        using var logout = await client.PostAsync("/logout", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = logoutToken,
        }));

        string html = await logout.Content.ReadAsStringAsync();
        Assert.Multiple(() =>
        {
            Assert.That(logout.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(html, Does.Contain("href=\"/login\""));
            Assert.That(html, Does.Not.Contain("user-chip"));
            Assert.That(cookies.GetCookies(WebsiteFixture.BaseAddress)["typebeat_session"], Is.Null);
        });

        // Logout revoked the token row, so the bearer path must now reject it too.
        using var meRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v2/me/");
        meRequest.Headers.Add("Authorization", $"Bearer {sessionToken}");

        using var meResponse = await WebsiteFixture.Client.SendAsync(meRequest);
        Assert.That(meResponse.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task PostLogin_WithoutAntiforgeryToken_Is400()
    {
        var (client, _) = WebsiteFixture.CreateBrowser();
        using var __ = client;

        using var response = await client.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Login"] = WebsiteFixture.SeededUsername,
            ["Password"] = WebsiteFixture.SeededPassword,
        }));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }
}
