using System.Net;
using System.Text.RegularExpressions;
using Dapper;
using Npgsql;

namespace Typebeat.Web.Tests.Website;

/// <summary>
/// The M2 email-verification behaviors that the cookie-flow test doesn't drill into: the login
/// two-step (fresh code every login, no session until verified), brute-force burn, the challenge
/// requirement, resend cooldown, and antiforgery on /verify.
/// </summary>
public class EmailVerificationTest
{
    [Test]
    public async Task Login_EmailsFreshCode_AndWithholdsSessionUntilVerified()
    {
        const string email = "login.flow@example.com";
        await WebsiteFixture.SeedUserAsync("login flow", email, "hunter2hunter2", verified: true);

        var (client, cookies) = WebsiteFixture.CreateBrowser();
        using var _ = client;

        // Password step: a 'login' code is emailed, and NO session exists yet.
        string loginToken = await WebsiteFixture.GetAntiforgeryTokenAsync(client, "/login");
        using (var login = await client.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = loginToken,
            ["Login"] = "login flow",
            ["Password"] = "hunter2hunter2",
        })))
        {
            string verifyPage = await login.Content.ReadAsStringAsync();
            Assert.Multiple(() =>
            {
                Assert.That(login.RequestMessage!.RequestUri!.AbsolutePath, Is.EqualTo("/verify"));
                // Login copy, not signup copy.
                Assert.That(verifyPage, Does.Contain("Finish signing in"));
                Assert.That(cookies.GetCookies(WebsiteFixture.BaseAddress)["typebeat_session"], Is.Null);
            });
        }

        // A protected view still treats us as anonymous before the code step.
        using (var home = await client.GetAsync("/"))
        {
            string html = await home.Content.ReadAsStringAsync();
            Assert.That(html, Does.Not.Contain("user-chip"), "no session until the code is entered");
        }

        string code = WebsiteFixture.Emails.LastCodeFor(email)!;
        Assert.That(code, Is.Not.Null.And.Not.Empty);

        // Code step: session established.
        string verifyToken = await WebsiteFixture.GetAntiforgeryTokenAsync(client, "/verify");
        using (var verify = await client.PostAsync("/verify", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = verifyToken,
            ["Code"] = code,
        })))
        {
            string html = await verify.Content.ReadAsStringAsync();
            Assert.Multiple(() =>
            {
                Assert.That(html, Does.Contain("user-chip"));
                Assert.That(cookies.GetCookies(WebsiteFixture.BaseAddress)["typebeat_session"], Is.Not.Null);
            });
        }
    }

    [Test]
    public async Task SixthWrongAttempt_BurnsTheCode_RequiringResend()
    {
        const string email = "burn.me@example.com";
        await WebsiteFixture.SeedUserAsync("burn me", email, "hunter2hunter2", verified: true);

        var (client, _) = WebsiteFixture.CreateBrowser();
        using var __ = client;

        // Get a live code (via login).
        using (await postLoginAsync(client, "burn me", "hunter2hunter2")) { }
        string code = WebsiteFixture.Emails.LastCodeFor(email)!;
        string wrong = code == "000000" ? "111111" : "000000";

        // Attempts 1..5 are ordinary failures; the 6th burns the code.
        for (int attempt = 1; attempt <= 6; attempt++)
        {
            using var response = await postVerifyAsync(client, wrong);
            string html = await response.Content.ReadAsStringAsync();
            Assert.That(response.RequestMessage!.RequestUri!.AbsolutePath, Is.EqualTo("/verify"));

            if (attempt < 6)
                Assert.That(html, Does.Contain("incorrect or has expired"), $"attempt {attempt} should be a plain failure");
            else
                Assert.That(html, Does.Contain("Too many incorrect attempts"), "6th wrong attempt burns the code");
        }

        // Even the CORRECT code no longer works; it was burned.
        using (var afterBurn = await postVerifyAsync(client, code))
        {
            string html = await afterBurn.Content.ReadAsStringAsync();
            Assert.That(html, Does.Not.Contain("user-chip"), "burned code cannot establish a session");
        }

        // A resend issues a brand-new code that DOES work.
        using (await postResendAsync(client)) { }
        string fresh = WebsiteFixture.Emails.LastCodeFor(email)!;
        Assert.That(fresh, Is.Not.EqualTo(code), "resend rotates the code");

        using (var ok = await postVerifyAsync(client, fresh))
        {
            string html = await ok.Content.ReadAsStringAsync();
            Assert.That(html, Does.Contain("user-chip"), "the resent code verifies");
        }
    }

    [Test]
    public async Task PostVerify_WithoutChallengeCookie_RedirectsToLogin()
    {
        var (client, cookies) = WebsiteFixture.CreateBrowser();
        using var __ = client;

        // No prior /login, so no challenge cookie. A valid antiforgery token still comes back from
        // the /login page the GET /verify redirects to.
        string token = await WebsiteFixture.GetAntiforgeryTokenAsync(client, "/verify");

        using var response = await client.PostAsync("/verify", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["Code"] = "123456",
        }));

        Assert.Multiple(() =>
        {
            Assert.That(response.RequestMessage!.RequestUri!.AbsolutePath, Is.EqualTo("/login"));
            Assert.That(cookies.GetCookies(WebsiteFixture.BaseAddress)["typebeat_session"], Is.Null);
        });
    }

    [Test]
    public async Task Resend_WithinCooldown_DoesNotSendAgain()
    {
        const string email = "cooldown@example.com";
        await WebsiteFixture.SeedUserAsync("cool down", email, "hunter2hunter2", verified: true);

        var (client, _) = WebsiteFixture.CreateBrowser();
        using var __ = client;

        // Login sends code #1 (still live).
        using (await postLoginAsync(client, "cool down", "hunter2hunter2")) { }
        Assert.That(WebsiteFixture.Emails.CountFor(email), Is.EqualTo(1));

        // An immediate resend is inside the 60s cooldown while that code is still live: no new send.
        using var resend = await postResendAsync(client);
        string html = await resend.Content.ReadAsStringAsync();
        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("Please wait"));
            Assert.That(WebsiteFixture.Emails.CountFor(email), Is.EqualTo(1), "cooldown suppresses the second send");
        });
    }

    /// <summary>
    /// The resend regression: "send a new code" used to render the page from its own handler, so
    /// the browser sat on /verify?handler=Resend and the action-less code form posted there too.
    /// Pressing Sign in then sent yet another email instead of checking the code. Resend now
    /// redirects (PRG) and the code form names the default handler explicitly.
    /// </summary>
    [Test]
    public async Task Resend_RedirectsBackToVerify_AndTheCodeFormStillChecksTheCode()
    {
        const string email = "resend.prg@example.com";
        await WebsiteFixture.SeedUserAsync("resend prg", email, "hunter2hunter2", verified: true);

        var (client, cookies) = WebsiteFixture.CreateBrowser();
        using var __ = client;

        using (await postLoginAsync(client, "resend prg", "hunter2hunter2")) { }
        string code = WebsiteFixture.Emails.LastCodeFor(email)!;
        int sentBefore = WebsiteFixture.Emails.CountFor(email);

        string page;
        using (var resend = await postResendAsync(client))
        {
            page = await resend.Content.ReadAsStringAsync();
            Assert.Multiple(() =>
            {
                // The POST went to ?handler=Resend; the page we landed on is the redirect target.
                Assert.That(resend.RequestMessage!.Method, Is.EqualTo(HttpMethod.Get), "resend answers with a redirect, not a rendered page");
                Assert.That(resend.RequestMessage!.RequestUri!.PathAndQuery, Is.EqualTo("/verify"));
                // The status line survives the hop (inside the cooldown this is the "wait" line).
                Assert.That(page, Does.Contain("Please wait"));
            });
        }

        // The code form's action is the default handler, never the resend one.
        var codeForm = Regex.Match(page, "<form method=\"post\" action=\"([^\"]*)\"");
        Assert.That(codeForm.Success, Is.True, "the code form carries an explicit action");
        string action = System.Net.WebUtility.HtmlDecode(codeForm.Groups[1].Value);
        Assert.That(action, Does.Not.Contain("handler"), "the code form must post to the default handler");

        // Posting the code where the form says to signs in, and sends no further email.
        string token = Regex.Match(page, "__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        using (var verify = await client.PostAsync(action, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["Code"] = code,
        })))
        {
            Assert.Multiple(() =>
            {
                Assert.That(cookies.GetCookies(WebsiteFixture.BaseAddress)["typebeat_session"], Is.Not.Null, "the code was checked");
                Assert.That(WebsiteFixture.Emails.CountFor(email), Is.EqualTo(sentBefore), "no extra code was emailed");
            });
        }
    }

    [Test]
    public async Task PostVerify_WithoutAntiforgeryToken_Is400()
    {
        const string email = "csrf.verify@example.com";
        await WebsiteFixture.SeedUserAsync("csrf verify", email, "hunter2hunter2", verified: true);

        var (client, _) = WebsiteFixture.CreateBrowser();
        using var __ = client;

        // Establish the challenge cookie, then POST /verify WITHOUT the antiforgery token.
        using (await postLoginAsync(client, "csrf verify", "hunter2hunter2")) { }

        using var response = await client.PostAsync("/verify", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Code"] = WebsiteFixture.Emails.LastCodeFor(email)!,
        }));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task DeletedUserBehindChallenge_FailsClosed()
    {
        const string email = "vanishing@example.com";
        long userId = await WebsiteFixture.SeedUserAsync("vanishing", email, "hunter2hunter2", verified: true);

        var (client, cookies) = WebsiteFixture.CreateBrowser();
        using var __ = client;

        using (await postLoginAsync(client, "vanishing", "hunter2hunter2")) { }
        string code = WebsiteFixture.Emails.LastCodeFor(email)!;

        // Restrict the user mid-flow: the challenge must now fail closed (no session minted).
        await using (var conn = new NpgsqlConnection(WebsiteFixture.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("UPDATE users SET restricted = true WHERE id = @userId", new { userId });
        }

        using var response = await postVerifyAsync(client, code);
        Assert.Multiple(() =>
        {
            Assert.That(response.RequestMessage!.RequestUri!.AbsolutePath, Is.EqualTo("/login"));
            Assert.That(cookies.GetCookies(WebsiteFixture.BaseAddress)["typebeat_session"], Is.Null);
        });
    }

    // ---- helpers ----

    private static async Task<HttpResponseMessage> postLoginAsync(HttpClient client, string login, string password)
    {
        string token = await WebsiteFixture.GetAntiforgeryTokenAsync(client, "/login");
        return await client.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["Login"] = login,
            ["Password"] = password,
        }));
    }

    private static async Task<HttpResponseMessage> postVerifyAsync(HttpClient client, string code)
    {
        string token = await WebsiteFixture.GetAntiforgeryTokenAsync(client, "/verify");
        return await client.PostAsync("/verify", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["Code"] = code,
        }));
    }

    private static async Task<HttpResponseMessage> postResendAsync(HttpClient client)
    {
        string token = await WebsiteFixture.GetAntiforgeryTokenAsync(client, "/verify");
        return await client.PostAsync("/verify?handler=Resend", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
        }));
    }
}
