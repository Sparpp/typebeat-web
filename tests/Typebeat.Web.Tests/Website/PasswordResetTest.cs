using System.Net;

namespace Typebeat.Web.Tests.Website;

/// <summary>
/// The password-reset flow (/forgot-password → emailed 'reset' code → /reset-password): it swaps the
/// password, signs the user in, and revokes prior sessions — while staying enumeration-safe (an
/// unknown email is indistinguishable from a real one, right down to the Set-Cookie header) and never
/// burning a valid code on a fumbled new password.
/// </summary>
public class PasswordResetTest
{
    [Test]
    public async Task RequestThenReset_SignsIn_ThenNewPasswordAuthenticates_OldDoesNot()
    {
        const string email = "reset.happy@example.com";
        const string oldPassword = "oldpassword1";
        const string newPassword = "brandnewpass2";
        await WebsiteFixture.SeedUserAsync("reset happy", email, oldPassword, verified: true);

        var (client, cookies) = WebsiteFixture.CreateBrowser();
        using var _ = client;

        // Step 1: request a code. Lands on /reset-password with a challenge cookie but no session.
        using (var forgot = await postForgotAsync(client, email))
        {
            Assert.Multiple(() =>
            {
                Assert.That(forgot.RequestMessage!.RequestUri!.AbsolutePath, Is.EqualTo("/reset-password"));
                Assert.That(cookies.GetCookies(WebsiteFixture.BaseAddress)["typebeat_challenge"], Is.Not.Null);
                Assert.That(cookies.GetCookies(WebsiteFixture.BaseAddress)["typebeat_session"], Is.Null);
            });
        }

        string code = WebsiteFixture.Emails.LastCodeFor(email)!;
        Assert.That(code, Is.Not.Null.And.Not.Empty, "a reset code should have been emailed");

        // Step 2: the code + a new password signs us in.
        using (var reset = await postResetAsync(client, code, newPassword, newPassword))
        {
            string html = await reset.Content.ReadAsStringAsync();
            Assert.Multiple(() =>
            {
                Assert.That(html, Does.Contain("user-chip"), "reset signs the user in");
                Assert.That(cookies.GetCookies(WebsiteFixture.BaseAddress)["typebeat_session"], Is.Not.Null);
            });
        }

        // The new password now authenticates (lands on /verify), and the old one does not.
        var (fresh, _) = WebsiteFixture.CreateBrowser();
        using var __ = fresh;

        int before = WebsiteFixture.Emails.CountFor(email);
        using (var oldLogin = await postLoginAsync(fresh, "reset happy", oldPassword))
        {
            string html = await oldLogin.Content.ReadAsStringAsync();
            Assert.Multiple(() =>
            {
                Assert.That(oldLogin.RequestMessage!.RequestUri!.AbsolutePath, Is.EqualTo("/login"), "old password is rejected");
                Assert.That(html, Does.Contain("The username or password is incorrect."));
                Assert.That(WebsiteFixture.Emails.CountFor(email), Is.EqualTo(before), "a rejected login sends no code");
            });
        }

        using (var newLogin = await postLoginAsync(fresh, "reset happy", newPassword))
        {
            Assert.Multiple(() =>
            {
                Assert.That(newLogin.RequestMessage!.RequestUri!.AbsolutePath, Is.EqualTo("/verify"), "new password authenticates");
                Assert.That(WebsiteFixture.Emails.CountFor(email), Is.EqualTo(before + 1), "a good login emails a fresh login code");
            });
        }
    }

    [Test]
    public async Task UnknownEmail_IsIndistinguishable_SetsChallengeCookie_SendsNoCode()
    {
        const string unknown = "nobody.here@example.com";

        var (client, cookies) = WebsiteFixture.CreateBrowser();
        using var _ = client;

        using var forgot = await postForgotAsync(client, unknown);

        Assert.Multiple(() =>
        {
            // Same redirect and the same Set-Cookie as a real account — the decoy challenge — so the
            // response can't be used to probe which emails are registered...
            Assert.That(forgot.RequestMessage!.RequestUri!.AbsolutePath, Is.EqualTo("/reset-password"));
            Assert.That(cookies.GetCookies(WebsiteFixture.BaseAddress)["typebeat_challenge"], Is.Not.Null);
            // ...but no email is ever sent to an address with no account.
            Assert.That(WebsiteFixture.Emails.CountFor(unknown), Is.EqualTo(0));
        });

        // And a code entered against the decoy challenge yields the generic failure, no session.
        using var reset = await postResetAsync(client, "123456", "somenewpassword", "somenewpassword");
        string html = await reset.Content.ReadAsStringAsync();
        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("incorrect or has expired"));
            Assert.That(cookies.GetCookies(WebsiteFixture.BaseAddress)["typebeat_session"], Is.Null);
        });
    }

    [Test]
    public async Task Reset_WrongCode_ShowsGenericError_NoSession()
    {
        const string email = "reset.wrongcode@example.com";
        await WebsiteFixture.SeedUserAsync("reset wrongcode", email, "oldpassword1", verified: true);

        var (client, cookies) = WebsiteFixture.CreateBrowser();
        using var _ = client;

        using (await postForgotAsync(client, email)) { }
        string code = WebsiteFixture.Emails.LastCodeFor(email)!;
        string wrong = code == "000000" ? "111111" : "000000";

        using var reset = await postResetAsync(client, wrong, "brandnewpass2", "brandnewpass2");
        string html = await reset.Content.ReadAsStringAsync();
        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("incorrect or has expired"));
            Assert.That(cookies.GetCookies(WebsiteFixture.BaseAddress)["typebeat_session"], Is.Null);
        });
    }

    [Test]
    public async Task Reset_PasswordsMismatch_DoesNotBurnTheCode()
    {
        const string email = "reset.mismatch@example.com";
        await WebsiteFixture.SeedUserAsync("reset mismatch", email, "oldpassword1", verified: true);

        var (client, cookies) = WebsiteFixture.CreateBrowser();
        using var _ = client;

        using (await postForgotAsync(client, email)) { }
        string code = WebsiteFixture.Emails.LastCodeFor(email)!;

        // A mismatch is rejected on the field, BEFORE the code is checked...
        using (var mismatch = await postResetAsync(client, code, "brandnewpass2", "different3different"))
        {
            string html = await mismatch.Content.ReadAsStringAsync();
            Assert.Multiple(() =>
            {
                Assert.That(html, Does.Contain("passwords do not match").IgnoreCase);
                Assert.That(cookies.GetCookies(WebsiteFixture.BaseAddress)["typebeat_session"], Is.Null);
            });
        }

        // ...so the SAME code still works on a correct resubmit.
        using (var ok = await postResetAsync(client, code, "brandnewpass2", "brandnewpass2"))
        {
            string html = await ok.Content.ReadAsStringAsync();
            Assert.That(html, Does.Contain("user-chip"), "the code survived the fumbled attempt");
        }
    }

    [Test]
    public async Task Reset_TooShortPassword_DoesNotBurnTheCode()
    {
        const string email = "reset.short@example.com";
        await WebsiteFixture.SeedUserAsync("reset short", email, "oldpassword1", verified: true);

        var (client, cookies) = WebsiteFixture.CreateBrowser();
        using var _ = client;

        using (await postForgotAsync(client, email)) { }
        string code = WebsiteFixture.Emails.LastCodeFor(email)!;

        // Too short: the shared password rule rejects it before the code is consumed.
        using (var tooShort = await postResetAsync(client, code, "short", "short"))
        {
            string html = await tooShort.Content.ReadAsStringAsync();
            Assert.Multiple(() =>
            {
                Assert.That(html, Does.Contain("Password must be at least 8 characters."));
                Assert.That(cookies.GetCookies(WebsiteFixture.BaseAddress)["typebeat_session"], Is.Null);
            });
        }

        using (var ok = await postResetAsync(client, code, "brandnewpass2", "brandnewpass2"))
        {
            string html = await ok.Content.ReadAsStringAsync();
            Assert.That(html, Does.Contain("user-chip"), "the code survived the too-short attempt");
        }
    }

    [Test]
    public async Task Reset_RevokesEverySession()
    {
        const string email = "reset.revoke@example.com";
        const string password = "oldpassword1";
        await WebsiteFixture.SeedUserAsync("reset revoke", email, password, verified: true);

        // Session A: a normal signed-in browser, holding a bearer-usable token.
        var (browserA, cookiesA) = WebsiteFixture.CreateBrowser();
        using var _ = browserA;
        using (await WebsiteFixture.LoginAndVerifyAsync(browserA, "reset revoke", password)) { }
        string tokenA = cookiesA.GetCookies(WebsiteFixture.BaseAddress)["typebeat_session"]!.Value;

        // The token resolves on the bearer path before the reset.
        Assert.That(await meStatusAsync(tokenA), Is.EqualTo(HttpStatusCode.OK));

        // Session B resets the password.
        var (browserB, _) = WebsiteFixture.CreateBrowser();
        using var __ = browserB;
        using (await postForgotAsync(browserB, email)) { }
        string code = WebsiteFixture.Emails.LastCodeFor(email)!;
        using (await postResetAsync(browserB, code, "brandnewpass2", "brandnewpass2")) { }

        // Session A's token is now revoked everywhere (web + game).
        Assert.That(await meStatusAsync(tokenA), Is.EqualTo(HttpStatusCode.Unauthorized), "reset revokes prior sessions");
    }

    [Test]
    public async Task PostForgotPassword_WithoutAntiforgeryToken_Is400()
    {
        var (client, _) = WebsiteFixture.CreateBrowser();
        using var __ = client;

        using var response = await client.PostAsync("/forgot-password", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = "whoever@example.com",
        }));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task PostResetPassword_WithoutAntiforgeryToken_Is400()
    {
        var (client, _) = WebsiteFixture.CreateBrowser();
        using var __ = client;

        using var response = await client.PostAsync("/reset-password", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Code"] = "123456",
            ["NewPassword"] = "brandnewpass2",
            ["ConfirmPassword"] = "brandnewpass2",
        }));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    // ---- helpers ----

    private static async Task<HttpResponseMessage> postForgotAsync(HttpClient client, string email)
    {
        string token = await WebsiteFixture.GetAntiforgeryTokenAsync(client, "/forgot-password");
        return await client.PostAsync("/forgot-password", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["Email"] = email,
        }));
    }

    private static async Task<HttpResponseMessage> postResetAsync(HttpClient client, string code, string newPassword, string confirmPassword)
    {
        string token = await WebsiteFixture.GetAntiforgeryTokenAsync(client, "/reset-password");
        return await client.PostAsync("/reset-password", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["Code"] = code,
            ["NewPassword"] = newPassword,
            ["ConfirmPassword"] = confirmPassword,
        }));
    }

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

    private static async Task<HttpStatusCode> meStatusAsync(string sessionToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v2/me/");
        request.Headers.Add("Authorization", $"Bearer {sessionToken}");
        using var response = await WebsiteFixture.Client.SendAsync(request);
        return response.StatusCode;
    }
}
