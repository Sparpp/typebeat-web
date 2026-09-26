using System.Net;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using Npgsql;
using Typebeat.Web.Auth;
using Typebeat.Web.Data;

namespace Typebeat.Web.Tests.Website;

/// <summary>
/// "Continue with Google" without Google. The HTTP legs that talk to Google (the redirect, the
/// code exchange, the JWKS fetch) cannot run here, and the ID-token validator is covered on its own
/// (GoogleIdTokenValidatorTest). What this pins is everything the flow DECIDES once it holds a
/// validated identity, driven straight against the website test database through
/// <see cref="ExternalLogins"/> and <see cref="AccountCreation.CreateExternalAsync"/>: the callback's
/// three sign-in branches and its refusals, the link and unlink rules. Through the EXISTING website
/// host (no new one) it pins the rest: with TYPEBEAT_GOOGLE_* unset every button is hidden and
/// every route 404s, and a password-less account created through Google cannot authenticate by
/// password anywhere until it sets one in Settings, after which the game's password grant works.
/// </summary>
[NonParallelizable]
public class GoogleSignInTest
{
    private NpgsqlDataSource dataSource = null!;
    private Db db = null!;

    [OneTimeSetUp]
    public void OpenDb()
    {
        dataSource = NpgsqlDataSource.Create(WebsiteFixture.ConnectionString);
        db = new Db(dataSource);
    }

    [OneTimeTearDown]
    public void CloseDb() => dataSource.Dispose();

    // ---- the callback's sign-in branches ----

    [Test]
    public async Task SignIn_NewGoogleAccount_NeedsAUsername_AndCreatesAPasswordlessVerifiedAccount()
    {
        var identity = newIdentity();

        var outcome = await ExternalLogins.ResolveSignInAsync(db, identity);
        Assert.That(outcome.Kind, Is.EqualTo(ExternalLogins.SignInKind.NeedsAccount));

        var created = await AccountCreation.CreateExternalAsync(db, uniqueUsername(), identity);
        Assert.That(created.Succeeded, Is.True, string.Join("; ", created.UsernameErrors.Concat(created.EmailErrors)));

        var row = await userRowAsync(created.UserId!.Value);
        Assert.Multiple(() =>
        {
            Assert.That(row.PasswordHash, Is.Null, "a Google-created account has no password");
            Assert.That(row.Verified, Is.True, "Google asserted email_verified");
            Assert.That(row.HasStats, Is.True);
        });

        // Coming back with the same subject is now the linked branch.
        var again = await ExternalLogins.ResolveSignInAsync(db, identity);
        Assert.Multiple(() =>
        {
            Assert.That(again.Kind, Is.EqualTo(ExternalLogins.SignInKind.SignedIn));
            Assert.That(again.UserId, Is.EqualTo(created.UserId));
        });
    }

    [Test]
    public async Task SignIn_Linked_FindsTheUserBySubject_EvenAfterTheGoogleEmailChanged()
    {
        var identity = newIdentity();
        long id = (await AccountCreation.CreateExternalAsync(db, uniqueUsername(), identity)).UserId!.Value;

        var moved = identity with { Email = "renamed." + identity.Email };
        var outcome = await ExternalLogins.ResolveSignInAsync(db, moved);

        await using var conn = await db.OpenAsync();
        var link = await ExternalLogins.GetGoogleLinkAsync(conn, id);

        Assert.Multiple(() =>
        {
            Assert.That(outcome.Kind, Is.EqualTo(ExternalLogins.SignInKind.SignedIn));
            Assert.That(outcome.UserId, Is.EqualTo(id));
            Assert.That(link.Email, Is.EqualTo(moved.Email), "the displayed Google email follows the account");
        });
    }

    [Test]
    public async Task SignIn_EmailMatchOnAConfirmedAccount_LinksAndSignsIn()
    {
        string email = uniqueEmail();
        long id = await WebsiteFixture.SeedUserAsync(uniqueUsername(), email, "hunter2hunter2", verified: true);

        // Google reports the address in a different case; citext matching must still find it.
        var identity = newIdentity(email.ToUpperInvariant());
        var outcome = await ExternalLogins.ResolveSignInAsync(db, identity);

        await using var conn = await db.OpenAsync();
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(outcome.Kind, Is.EqualTo(ExternalLogins.SignInKind.SignedIn));
            Assert.That(outcome.UserId, Is.EqualTo(id));
            Assert.That((await ExternalLogins.GetGoogleLinkAsync(conn, id)).Linked, Is.True);
            Assert.That((await userRowAsync(id)).PasswordHash, Is.Not.Null, "linking keeps the existing password");
        });
    }

    [Test]
    public async Task SignIn_EmailMatchOnAnUnconfirmedAccount_IsRefused_AndNotLinked()
    {
        string email = uniqueEmail();
        long id = await WebsiteFixture.SeedUserAsync(uniqueUsername(), email, "hunter2hunter2", verified: false);

        var outcome = await ExternalLogins.ResolveSignInAsync(db, newIdentity(email));

        await using var conn = await db.OpenAsync();
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(outcome.Kind, Is.EqualTo(ExternalLogins.SignInKind.Refused));
            Assert.That(outcome.Error, Is.EqualTo(ExternalLogins.UnconfirmedRefusal));
            Assert.That((await ExternalLogins.GetGoogleLinkAsync(conn, id)).Linked, Is.False);
        });
    }

    [Test]
    public async Task SignIn_EmailMatchOnAnAccountLinkedToAnotherGoogleAccount_IsRefused()
    {
        string email = uniqueEmail();
        long id = await WebsiteFixture.SeedUserAsync(uniqueUsername(), email, "hunter2hunter2", verified: true);
        Assert.That(await ExternalLogins.LinkAsync(db, id, newIdentity("elsewhere." + email)), Is.EqualTo(ExternalLogins.LinkResult.Linked));

        var outcome = await ExternalLogins.ResolveSignInAsync(db, newIdentity(email));

        Assert.Multiple(() =>
        {
            Assert.That(outcome.Kind, Is.EqualTo(ExternalLogins.SignInKind.Refused));
            Assert.That(outcome.Error, Is.EqualTo(ExternalLogins.OtherGoogleRefusal));
        });
    }

    [Test]
    public async Task SignIn_RestrictedAccount_IsRefused_OnBothBranches()
    {
        var linked = newIdentity();
        long linkedId = (await AccountCreation.CreateExternalAsync(db, uniqueUsername(), linked)).UserId!.Value;

        string email = uniqueEmail();
        long matchedId = await WebsiteFixture.SeedUserAsync(uniqueUsername(), email, "hunter2hunter2", verified: true);

        await using (var conn = await db.OpenAsync())
            await conn.ExecuteAsync("UPDATE users SET restricted = true WHERE id = ANY(@ids)", new { ids = new[] { linkedId, matchedId } });

        await Assert.MultipleAsync(async () =>
        {
            Assert.That((await ExternalLogins.ResolveSignInAsync(db, linked)).Kind, Is.EqualTo(ExternalLogins.SignInKind.Refused));
            Assert.That((await ExternalLogins.ResolveSignInAsync(db, newIdentity(email))).Kind, Is.EqualTo(ExternalLogins.SignInKind.Refused));
        });
    }

    [Test]
    public async Task CreateExternal_TakenUsername_IsAFieldError_AndCreatesNothing()
    {
        string taken = uniqueUsername();
        await WebsiteFixture.SeedUserAsync(taken, uniqueEmail(), "hunter2hunter2");

        var identity = newIdentity();
        var result = await AccountCreation.CreateExternalAsync(db, taken, identity);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.UsernameErrors, Does.Contain("Username is already taken."));
            Assert.That((await ExternalLogins.ResolveSignInAsync(db, identity)).Kind, Is.EqualTo(ExternalLogins.SignInKind.NeedsAccount),
                "no half-made account or link was left behind");
        });
    }

    // ---- link / unlink (Settings) ----

    [Test]
    public async Task Link_RefusesASubjectLinkedToSomeoneElse_AndASecondGoogleAccount()
    {
        var owned = newIdentity();
        await AccountCreation.CreateExternalAsync(db, uniqueUsername(), owned);

        long me = await WebsiteFixture.SeedUserAsync(uniqueUsername(), uniqueEmail(), "hunter2hunter2", verified: true);
        var mine = newIdentity();

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(await ExternalLogins.LinkAsync(db, me, owned), Is.EqualTo(ExternalLogins.LinkResult.SubjectTaken));
            Assert.That(await ExternalLogins.LinkAsync(db, me, mine), Is.EqualTo(ExternalLogins.LinkResult.Linked));
            Assert.That(await ExternalLogins.LinkAsync(db, me, mine), Is.EqualTo(ExternalLogins.LinkResult.Linked), "re-linking the same subject is harmless");
            Assert.That(await ExternalLogins.LinkAsync(db, me, newIdentity()), Is.EqualTo(ExternalLogins.LinkResult.UserAlreadyLinked));
        });
    }

    [Test]
    public async Task Unlink_IsRefusedWithoutAPassword_AndAllowedWithOne()
    {
        long passwordless = (await AccountCreation.CreateExternalAsync(db, uniqueUsername(), newIdentity())).UserId!.Value;

        long withPassword = await WebsiteFixture.SeedUserAsync(uniqueUsername(), uniqueEmail(), "hunter2hunter2", verified: true);
        await ExternalLogins.LinkAsync(db, withPassword, newIdentity());

        await using var conn = await db.OpenAsync();
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(await ExternalLogins.UnlinkAsync(db, passwordless), Is.EqualTo(ExternalLogins.UnlinkResult.NoPassword));
            Assert.That((await ExternalLogins.GetGoogleLinkAsync(conn, passwordless)).Linked, Is.True, "still linked: it is the only way in");

            Assert.That(await ExternalLogins.UnlinkAsync(db, withPassword), Is.EqualTo(ExternalLogins.UnlinkResult.Unlinked));
            Assert.That((await ExternalLogins.GetGoogleLinkAsync(conn, withPassword)).Linked, Is.False);
            Assert.That(await ExternalLogins.UnlinkAsync(db, withPassword), Is.EqualTo(ExternalLogins.UnlinkResult.NotLinked));
        });
    }

    // ---- the host, with Google unconfigured ----

    [TestCase("/auth/google")]
    [TestCase("/auth/google/callback?code=x&state=y")]
    [TestCase("/auth/google/username")]
    public async Task Unconfigured_GoogleRoutes_404(string path)
    {
        using var client = WebsiteFixture.CreateNoRedirectClient();
        using var response = await client.GetAsync(path);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [TestCase("/login")]
    [TestCase("/register")]
    public async Task Unconfigured_AuthPages_ShowNoGoogleButton(string path)
    {
        using var response = await WebsiteFixture.Client.GetAsync(path);
        string html = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(html, Does.Not.Contain("Continue with Google"));
            Assert.That(html, Does.Not.Contain("/auth/google"));
        });
    }

    [Test]
    public async Task Unconfigured_Settings_HasNoGoogleSection_AndLinkHandler404s()
    {
        var (client, cookies) = WebsiteFixture.CreateBrowser();
        using var _ = client;
        await signInDirectlyAsync(cookies, await WebsiteFixture.SeedUserAsync(uniqueUsername(), uniqueEmail(), "hunter2hunter2", verified: true));

        string token = await WebsiteFixture.GetAntiforgeryTokenAsync(client, "/settings");
        string html = await (await client.GetAsync("/settings")).Content.ReadAsStringAsync();

        using var linkSignedIn = await client.PostAsync("/settings?handler=LinkGoogle", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
        }));

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Not.Contain("Google account"));
            Assert.That(html, Does.Not.Contain("Set password"), "an account with a password is not offered a first one");
            Assert.That(linkSignedIn.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        });
    }

    // ---- a null password hash never authenticates ----

    [Test]
    public async Task PasswordlessAccount_CannotUseTheWebsitePasswordLogin()
    {
        var identity = newIdentity();
        string username = uniqueUsername();
        await AccountCreation.CreateExternalAsync(db, username, identity);

        var (client, cookies) = WebsiteFixture.CreateBrowser();
        using var _ = client;

        int sent = WebsiteFixture.Emails.CountFor(identity.Email);
        foreach (string login in new[] { username, identity.Email })
        {
            string token = await WebsiteFixture.GetAntiforgeryTokenAsync(client, "/login");
            using var response = await client.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["Login"] = login,
                ["Password"] = "anything-at-all",
            }));
            string html = await response.Content.ReadAsStringAsync();

            Assert.Multiple(() =>
            {
                Assert.That(response.RequestMessage!.RequestUri!.AbsolutePath, Is.EqualTo("/login"));
                Assert.That(html, Does.Contain("The username or password is incorrect."));
            });
        }

        Assert.Multiple(() =>
        {
            Assert.That(WebsiteFixture.Emails.CountFor(identity.Email), Is.EqualTo(sent), "no login code is sent");
            Assert.That(cookies.GetCookies(WebsiteFixture.BaseAddress)["typebeat_challenge"], Is.Null);
        });
    }

    [Test]
    public async Task PasswordlessAccount_CannotTakeThePasswordGrant()
    {
        var identity = newIdentity();
        string username = uniqueUsername();
        await AccountCreation.CreateExternalAsync(db, username, identity);

        foreach (string login in new[] { username, identity.Email })
        {
            foreach (string password in new[] { "anything-at-all", "" })
            {
                using var response = await passwordGrantAsync(login, password);
                string body = await response.Content.ReadAsStringAsync();

                Assert.Multiple(() =>
                {
                    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), $"{login} / '{password}'");
                    Assert.That(JObject.Parse(body)["error"]?.ToString(), Is.EqualTo("invalid_grant"));
                });
            }
        }
    }

    [Test]
    public void NullOrEmptyHash_NeverVerifies()
    {
        var passwords = new PasswordService();

        Assert.Multiple(() =>
        {
            Assert.That(passwords.Verify(null, "whatever1"), Is.False);
            Assert.That(passwords.Verify(string.Empty, "whatever1"), Is.False);
            Assert.That(passwords.Verify(passwords.Hash("whatever1"), string.Empty), Is.False);
            Assert.That(passwords.Verify(passwords.Hash("whatever1"), "whatever1"), Is.True);
        });
    }

    // ---- Settings: a password-less account sets its first password ----

    [Test]
    public async Task PasswordlessAccount_SetsAPasswordInSettings_ByEmailedCode_ThenTheGameGrantWorks()
    {
        var identity = newIdentity();
        string username = uniqueUsername();
        long id = (await AccountCreation.CreateExternalAsync(db, username, identity)).UserId!.Value;

        var (client, cookies) = WebsiteFixture.CreateBrowser();
        using var _ = client;
        await signInDirectlyAsync(cookies, id);

        string html = await (await client.GetAsync("/settings")).Content.ReadAsStringAsync();
        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("Set password"));
            Assert.That(html, Does.Contain("The game client only"), "the note explains why a password is needed");
        });

        // A wrong code sets nothing.
        using (await postSettingsAsync(client, "SendPasswordCode", new())) { }
        string code = WebsiteFixture.Emails.LastCodeFor(identity.Email)!;
        Assert.That(code, Is.Not.Null.And.Not.Empty, "a 'password' code was emailed");

        string wrong = code == "000000" ? "111111" : "000000";
        using (var bad = await postSettingsAsync(client, "SetPassword", new()
        {
            ["PasswordCode"] = wrong,
            ["NewPassword"] = "firstpassword9",
            ["ConfirmPassword"] = "firstpassword9",
        }))
        {
            Assert.That(await bad.Content.ReadAsStringAsync(), Does.Contain("incorrect or has expired"));
        }
        Assert.That((await userRowAsync(id)).PasswordHash, Is.Null);

        // The right code sets it.
        using (var ok = await postSettingsAsync(client, "SetPassword", new()
        {
            ["PasswordCode"] = code,
            ["NewPassword"] = "firstpassword9",
            ["ConfirmPassword"] = "firstpassword9",
        }))
        {
            string after = await ok.Content.ReadAsStringAsync();
            Assert.Multiple(() =>
            {
                Assert.That(ok.RequestMessage!.RequestUri!.Query, Does.Contain("saved=password"));
                Assert.That(after, Does.Not.Contain("Set password"), "the section goes once a password exists");
            });
        }

        using var grant = await passwordGrantAsync(username, "firstpassword9");
        string body = await grant.Content.ReadAsStringAsync();
        Assert.Multiple(() =>
        {
            Assert.That(grant.StatusCode, Is.EqualTo(HttpStatusCode.OK), body);
            Assert.That(JObject.Parse(body)["access_token"]?.ToString(), Is.Not.Null.And.Not.Empty);
        });
    }

    [Test]
    public async Task DeletingAGoogleAccount_DropsItsLink()
    {
        var identity = newIdentity();
        string username = uniqueUsername();
        long id = (await AccountCreation.CreateExternalAsync(db, username, identity)).UserId!.Value;

        var (client, cookies) = WebsiteFixture.CreateBrowser();
        using var _ = client;
        await signInDirectlyAsync(cookies, id);

        using (await postSettingsAsync(client, "Delete", new() { ["ConfirmUsername"] = username })) { }

        await using var conn = await db.OpenAsync();
        await Assert.MultipleAsync(async () =>
        {
            Assert.That((await ExternalLogins.GetGoogleLinkAsync(conn, id)).Linked, Is.False);
            Assert.That((await ExternalLogins.ResolveSignInAsync(db, identity)).Kind, Is.EqualTo(ExternalLogins.SignInKind.NeedsAccount),
                "the Google account is free to sign up again");
        });
    }

    // ---- helpers ----

    private static int counter;

    private static string uniqueUsername() => "g" + Guid.NewGuid().ToString("N")[..10];

    private static string uniqueEmail() => $"google.{Guid.NewGuid():N}@example.com";

    private static GoogleIdentity newIdentity(string? email = null)
        => new($"sub-{Interlocked.Increment(ref counter)}-{Guid.NewGuid():N}", email ?? uniqueEmail(), "Test Player");

    private async Task<(string? PasswordHash, bool Verified, bool HasStats)> userRowAsync(long id)
    {
        await using var conn = await db.OpenAsync();
        return await conn.QuerySingleAsync<(string? PasswordHash, bool Verified, bool HasStats)>(
            """
            SELECT password_hash AS PasswordHash, verified_at IS NOT NULL AS Verified,
                   EXISTS (SELECT 1 FROM user_stats s WHERE s.user_id = u.id) AS HasStats
            FROM users u WHERE id = @id
            """,
            new { id });
    }

    /// <summary>
    /// Puts a session for <paramref name="userId"/> in the jar the way every sign-in path ends
    /// (TokenService.IssueAsync into the session cookie): the only way to be signed in as an
    /// account whose real way in, Google, cannot run here.
    /// </summary>
    private static async Task signInDirectlyAsync(CookieContainer cookies, long userId)
    {
        var pair = await WebsiteFixture.Services.GetRequiredService<TokenService>().IssueAsync(userId);
        cookies.Add(WebsiteFixture.BaseAddress, new Cookie(SessionCookieAuth.CookieName, pair.AccessToken) { Secure = true, HttpOnly = true });
    }

    private static async Task<HttpResponseMessage> postSettingsAsync(HttpClient client, string handler, Dictionary<string, string> fields)
    {
        fields["__RequestVerificationToken"] = await WebsiteFixture.GetAntiforgeryTokenAsync(client, "/settings");
        return await client.PostAsync($"/settings?handler={handler}", new FormUrlEncodedContent(fields));
    }

    private static Task<HttpResponseMessage> passwordGrantAsync(string login, string password)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/oauth/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["username"] = login,
                ["password"] = password,
                ["client_id"] = "1",
                ["client_secret"] = "typebeat-official-client",
                ["scope"] = "*",
            }),
        };
        // A fresh CF-Connecting-IP per call keeps these clear of the grant's 10-per-5-minutes limiter.
        request.Headers.Add("CF-Connecting-IP", WebsiteFixture.NextClientIp());
        return WebsiteFixture.Client.SendAsync(request);
    }
}
