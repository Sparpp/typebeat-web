using System.Net;
using Dapper;
using Newtonsoft.Json.Linq;
using Npgsql;

namespace Typebeat.Web.Tests.Website;

/// <summary>
/// End-to-end cookie auth through the M2 two-step flow: password → emailed code → session. Login
/// and register no longer sign the user in directly; they email a code and hand off to /verify,
/// where the session is minted. Also pins the identity-unification contract — the raw token in the
/// session cookie IS a bearer access token, so /api/v2/me/ resolves it to the same user.
/// </summary>
public class CookieAuthFlowTest
{
    [Test]
    public async Task LoginThenVerify_ShowsUserChip_AndCookieTokenResolvesViaBearerPath()
    {
        const string email = "chip.user@example.com";
        long userId = await WebsiteFixture.SeedUserAsync("chip user", email, "hunter2hunter2", verified: true);

        var (client, cookies) = WebsiteFixture.CreateBrowser();
        using var _ = client;

        // Step 1: password. Lands on /verify with NO session yet.
        string loginToken = await WebsiteFixture.GetAntiforgeryTokenAsync(client, "/login");
        using (var login = await client.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = loginToken,
            ["Login"] = "chip user",
            ["Password"] = "hunter2hunter2",
        })))
        {
            string verifyHtml = await login.Content.ReadAsStringAsync();
            Assert.Multiple(() =>
            {
                Assert.That(login.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(login.RequestMessage!.RequestUri!.AbsolutePath, Is.EqualTo("/verify"));
                Assert.That(verifyHtml, Does.Contain("code"));
                Assert.That(cookies.GetCookies(WebsiteFixture.BaseAddress)["typebeat_session"], Is.Null, "no session before the code step");
            });
        }

        // Step 2: the emailed code.
        string code = WebsiteFixture.Emails.LastCodeFor(email)!;
        Assert.That(code, Is.Not.Null.And.Not.Empty, "a login code should have been emailed");

        string verifyToken = await WebsiteFixture.GetAntiforgeryTokenAsync(client, "/verify");
        using var response = await client.PostAsync("/verify", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = verifyToken,
            ["Code"] = code,
        }));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        string html = await response.Content.ReadAsStringAsync();
        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("user-chip"));
            Assert.That(html, Does.Contain("chip user"));
            Assert.That(html, Does.Contain($"href=\"/users/{userId}\""));
            Assert.That(html, Does.Contain("sign out"));
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
            Assert.That(me["id"]!.Value<long>(), Is.EqualTo(userId));
            Assert.That(me["username"]!.Value<string>(), Is.EqualTo("chip user"));
        });
    }

    [Test]
    public async Task Login_WrongPassword_ShowsGenericError_SendsNoCode_AndSetsNoCookie()
    {
        const string email = "wrongpw.user@example.com";
        await WebsiteFixture.SeedUserAsync("wrongpw user", email, "hunter2hunter2", verified: true);

        var (client, cookies) = WebsiteFixture.CreateBrowser();
        using var _ = client;

        string token = await WebsiteFixture.GetAntiforgeryTokenAsync(client, "/login");

        using var response = await client.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["Login"] = "wrongpw user",
            ["Password"] = "not the password",
        }));

        string html = await response.Content.ReadAsStringAsync();
        Assert.Multiple(() =>
        {
            // Re-rendered login form (200), generic message, no session, and crucially NO code sent
            // (so the form cannot be used to probe which accounts exist).
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(response.RequestMessage!.RequestUri!.AbsolutePath, Is.EqualTo("/login"));
            Assert.That(html, Does.Contain("The username or password is incorrect."));
            Assert.That(cookies.GetCookies(WebsiteFixture.BaseAddress)["typebeat_session"], Is.Null);
            Assert.That(WebsiteFixture.Emails.CountFor(email), Is.EqualTo(0));
        });
    }

    [Test]
    public async Task RegisterThenVerify_SetsVerifiedAt_AndSignsIn()
    {
        const string email = "reg.verify@example.com";

        var (client, cookies) = WebsiteFixture.CreateBrowser();
        using var _ = client;

        string regToken = await WebsiteFixture.GetAntiforgeryTokenAsync(client, "/register");
        using (var register = await client.PostAsync("/register", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = regToken,
            ["Username"] = "reg verify",
            ["Email"] = email,
            ["Password"] = "registerpass1",
        })))
        {
            Assert.Multiple(() =>
            {
                // Lands on /verify, NOT signed in, and NOT yet verified.
                Assert.That(register.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(register.RequestMessage!.RequestUri!.AbsolutePath, Is.EqualTo("/verify"));
                Assert.That(cookies.GetCookies(WebsiteFixture.BaseAddress)["typebeat_session"], Is.Null);
            });
        }

        Assert.That(await VerifiedAtAsync(email), Is.Null, "account starts unverified");

        string code = WebsiteFixture.Emails.LastCodeFor(email)!;
        Assert.That(code, Is.Not.Null.And.Not.Empty);

        // A wrong code first: fails, no session.
        string wrongToken = await WebsiteFixture.GetAntiforgeryTokenAsync(client, "/verify");
        using (var wrong = await client.PostAsync("/verify", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = wrongToken,
            ["Code"] = NextWrongCode(code),
        })))
        {
            string wrongHtml = await wrong.Content.ReadAsStringAsync();
            Assert.Multiple(() =>
            {
                Assert.That(wrong.RequestMessage!.RequestUri!.AbsolutePath, Is.EqualTo("/verify"));
                Assert.That(wrongHtml, Does.Contain("alert-error"));
                Assert.That(cookies.GetCookies(WebsiteFixture.BaseAddress)["typebeat_session"], Is.Null);
            });
        }

        // The right code: verified + signed in.
        string rightToken = await WebsiteFixture.GetAntiforgeryTokenAsync(client, "/verify");
        using (var right = await client.PostAsync("/verify", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = rightToken,
            ["Code"] = code,
        })))
        {
            string html = await right.Content.ReadAsStringAsync();
            Assert.Multiple(() =>
            {
                Assert.That(html, Does.Contain("user-chip"));
                Assert.That(html, Does.Contain("reg verify"));
                Assert.That(cookies.GetCookies(WebsiteFixture.BaseAddress)["typebeat_session"], Is.Not.Null);
            });
        }

        Assert.That(await VerifiedAtAsync(email), Is.Not.Null, "verify sets verified_at");
    }

    [Test]
    public async Task Login_VerifiesAPreviouslyUnverifiedAccount()
    {
        // An account created without going through website signup (e.g. registered in-game, whose
        // OAuth password grant has no email step) starts unverified. Completing a website login's
        // emailed code is the self-service path to becoming verified — which is the gate for beatmap
        // submission — with no admin lever.
        const string email = "ingame.unverified@example.com";
        await WebsiteFixture.SeedUserAsync("ingame unverified", email, "hunter2hunter2", verified: false);

        Assert.That(await VerifiedAtAsync(email), Is.Null, "account starts unverified");

        var (client, _) = WebsiteFixture.CreateBrowser();
        using var __ = client;

        using (await WebsiteFixture.LoginAndVerifyAsync(client, "ingame unverified", "hunter2hunter2")) { }

        Assert.That(await VerifiedAtAsync(email), Is.Not.Null, "completing the login email code verifies the account");
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
        const string email = "logout.user@example.com";
        await WebsiteFixture.SeedUserAsync("logout user", email, "hunter2hunter2", verified: true);

        var (client, cookies) = WebsiteFixture.CreateBrowser();
        using var _ = client;

        using (await WebsiteFixture.LoginAndVerifyAsync(client, "logout user", "hunter2hunter2")) { }

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

    private static string NextWrongCode(string code)
        => code == "000000" ? "111111" : "000000";

    private static async Task<DateTime?> VerifiedAtAsync(string email)
    {
        await using var conn = new NpgsqlConnection(WebsiteFixture.ConnectionString);
        await conn.OpenAsync();
        return await conn.ExecuteScalarAsync<DateTime?>(
            "SELECT verified_at FROM users WHERE email = @email::citext", new { email });
    }
}
