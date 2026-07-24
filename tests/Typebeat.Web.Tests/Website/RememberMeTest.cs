using Dapper;
using Npgsql;

namespace Typebeat.Web.Tests.Website;

/// <summary>
/// The login "remember me for 30 days" option: when checked, the session minted after the email
/// code clears lives 30 days (token row + cookie) instead of the usual 24h; when unchecked, nothing
/// changes. Logout still revokes the row, and an expired credential is rejected either way. The
/// choice is made at the password step and carried through the challenge cookie, so it never lets
/// anyone skip the code step.
/// </summary>
public class RememberMeTest
{
    // The two lifetimes the login flow can mint, kept in sync with TokenService.ACCESS_LIFETIME_SECONDS
    // (24h) and SessionCookieAuth.RememberMeLifetimeSeconds (30 days).
    private static readonly TimeSpan default_lifetime = TimeSpan.FromHours(24);
    private static readonly TimeSpan remember_lifetime = TimeSpan.FromDays(30);

    [Test]
    public async Task Login_WithRememberMe_MintsA30DaySession()
    {
        const string email = "remember.me@example.com";
        long userId = await WebsiteFixture.SeedUserAsync("remember me", email, "hunter2hunter2", verified: true);

        var (client, cookies) = WebsiteFixture.CreateBrowser();
        using var _ = client;

        using (await WebsiteFixture.LoginAndVerifyAsync(client, "remember me", "hunter2hunter2", rememberMe: true)) { }

        var session = cookies.GetCookies(WebsiteFixture.BaseAddress)["typebeat_session"];
        Assert.That(session, Is.Not.Null, "a session cookie should be set");

        // The cookie persists (a far-future expiry) rather than being a session cookie. Asserted as
        // "clearly beyond the 24h default" to stay robust against local-time/DST wall-clock quirks in
        // System.Net.Cookie.Expires.
        Assert.That(session!.Expires, Is.GreaterThan(DateTime.Now.AddDays(25)),
            "remember-me cookie should persist well past the 24h default");

        // The credential itself (the token row the cookie resolves against) also lives ~30 days.
        var expiry = await accessExpiryAsync(userId);
        Assert.That(expiry, Is.EqualTo(DateTime.UtcNow.Add(remember_lifetime)).Within(TimeSpan.FromHours(1)),
            "remember-me token should expire ~30 days out");
    }

    [Test]
    public async Task Login_WithoutRememberMe_KeepsThe24hSession()
    {
        const string email = "no.remember@example.com";
        long userId = await WebsiteFixture.SeedUserAsync("no remember", email, "hunter2hunter2", verified: true);

        var (client, cookies) = WebsiteFixture.CreateBrowser();
        using var _ = client;

        using (await WebsiteFixture.LoginAndVerifyAsync(client, "no remember", "hunter2hunter2")) { }

        Assert.That(cookies.GetCookies(WebsiteFixture.BaseAddress)["typebeat_session"], Is.Not.Null);

        // Unchanged behaviour: the token still expires at the default 24h, nowhere near 30 days.
        var expiry = await accessExpiryAsync(userId);
        Assert.That(expiry, Is.EqualTo(DateTime.UtcNow.Add(default_lifetime)).Within(TimeSpan.FromHours(1)),
            "a plain login should keep the 24h lifetime");
        Assert.That(expiry, Is.LessThan(DateTime.UtcNow.Add(default_lifetime).AddHours(2)),
            "a plain login must not be long-lived");
    }

    [Test]
    public async Task Logout_RevokesTheRememberedSession()
    {
        const string email = "remember.logout@example.com";
        long userId = await WebsiteFixture.SeedUserAsync("remember logout", email, "hunter2hunter2", verified: true);

        var (client, cookies) = WebsiteFixture.CreateBrowser();
        using var _ = client;

        using (await WebsiteFixture.LoginAndVerifyAsync(client, "remember logout", "hunter2hunter2", rememberMe: true)) { }
        Assert.That(cookies.GetCookies(WebsiteFixture.BaseAddress)["typebeat_session"], Is.Not.Null);

        // POST /logout (antiforgery-validated) revokes the token row and clears the cookie.
        string logoutToken = await WebsiteFixture.GetAntiforgeryTokenAsync(client, "/");
        using (await client.PostAsync("/logout", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = logoutToken,
        }))) { }

        Assert.That(await revokedCountAsync(userId), Is.EqualTo(1), "logout revokes the remembered token even though it is long-lived");

        // A protected view now treats us as anonymous.
        using var home = await client.GetAsync("/");
        string html = await home.Content.ReadAsStringAsync();
        Assert.That(html, Does.Not.Contain("user-chip"), "the session is gone after logout");
    }

    [Test]
    public async Task ExpiredRememberedToken_IsRejected()
    {
        const string email = "remember.expired@example.com";
        long userId = await WebsiteFixture.SeedUserAsync("remember expired", email, "hunter2hunter2", verified: true);

        var (client, _) = WebsiteFixture.CreateBrowser();
        using var __ = client;

        using (await WebsiteFixture.LoginAndVerifyAsync(client, "remember expired", "hunter2hunter2", rememberMe: true)) { }

        // Force the token past its expiry: the cookie value is unchanged, but ResolveAsync gates on
        // access_expires_at, so the browser is signed out on the very next request.
        await using (var conn = new NpgsqlConnection(WebsiteFixture.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync(
                "UPDATE oauth_tokens SET access_expires_at = now() - interval '1 minute' WHERE user_id = @userId",
                new { userId });
        }

        using var home = await client.GetAsync("/");
        string html = await home.Content.ReadAsStringAsync();
        Assert.That(html, Does.Not.Contain("user-chip"), "an expired remember-me token no longer authenticates");
    }

    private static async Task<DateTime> accessExpiryAsync(long userId)
    {
        await using var conn = new NpgsqlConnection(WebsiteFixture.ConnectionString);
        await conn.OpenAsync();
        return await conn.ExecuteScalarAsync<DateTime>(
            "SELECT access_expires_at FROM oauth_tokens WHERE user_id = @userId ORDER BY id DESC LIMIT 1",
            new { userId });
    }

    private static async Task<int> revokedCountAsync(long userId)
    {
        await using var conn = new NpgsqlConnection(WebsiteFixture.ConnectionString);
        await conn.OpenAsync();
        return await conn.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM oauth_tokens WHERE user_id = @userId AND revoked_at IS NOT NULL",
            new { userId });
    }
}
