using System.Net;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Typebeat.Web.Auth;
using Typebeat.Web.Email;

namespace Typebeat.Web.Tests.Website;

/// <summary>
/// Fixture for the website (Razor Pages + cookie auth) tests, mirroring the WireCompat
/// ServerFixture pattern: drop+recreate a DEDICATED "typebeat_webtests" database (superuser
/// postgres/postgres, matching CI's service container), point the server at it via TYPEBEAT_DB,
/// boot the real app in-process via <see cref="WebApplicationFactory{Program}"/>, seed one user.
///
/// Scoped to the Typebeat.Web.Tests.Website namespace so the pure wire-shape tests in the
/// parent namespace keep running without Postgres.
///
/// Clients are created with an HTTPS base address: the session cookie is Secure, and
/// <see cref="CookieContainer"/> won't attach Secure cookies to plain-http requests.
/// </summary>
[SetUpFixture]
public class WebsiteFixture
{
    public const string DatabaseName = "typebeat_webtests";

    public const string ConnectionString =
        "Host=localhost;Port=5432;Database=" + DatabaseName + ";Username=postgres;Password=postgres";

    private const string admin_connection_string =
        "Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=postgres";

    public static readonly Uri BaseAddress = new Uri("https://localhost");

    public const string SeededUsername = "web player";
    public const string SeededPassword = "hunter2hunter2";
    public static long SeededUserId { get; private set; }

    private static WebApplicationFactory<Program>? factory;

    /// <summary>Shared anonymous client (no cookie jar) for stateless page/API checks.</summary>
    public static HttpClient Client { get; private set; } = null!;

    /// <summary>
    /// The capturing email sender the host uses in place of the real one — tests read the emitted
    /// verification code from it.
    /// </summary>
    public static CapturingEmailSender Emails { get; } = new();

    // Each browser client is stamped with a unique CF-Connecting-IP so per-IP rate limiters
    // (registration 3/hour, login 10/5min) never collide across tests.
    private static int ipCounter;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        await recreateDatabaseAsync();

        // Must be set BEFORE the host is built (Db.ResolveConnectionString reads it at startup).
        Environment.SetEnvironmentVariable("TYPEBEAT_DB", ConnectionString);

        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureTestServices(services =>
            {
                // Swap the real/log sender for the capturing double so tests can read codes.
                services.RemoveAll<IEmailSender>();
                services.AddSingleton<IEmailSender>(Emails);
            }));

        Client = factory.CreateDefaultClient(BaseAddress, new RedirectHandler());

        // A no-op request forces host startup to complete (and surfaces migration errors early).
        using (var probe = await Client.GetAsync("/health"))
            probe.EnsureSuccessStatusCode();

        await seedAsync();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        Client?.Dispose();
        factory?.Dispose();
    }

    /// <summary>
    /// A browser-like client: follows redirects and keeps cookies in the returned jar, which
    /// tests can inspect (e.g. to read the raw typebeat_session token). Handler order matches
    /// WebApplicationFactoryClientOptions: redirects outermost, cookies innermost, so Set-Cookie
    /// on a 302 is stored before the redirected request goes out.
    /// </summary>
    public static (HttpClient client, CookieContainer cookies) CreateBrowser()
    {
        var cookies = new CookieContainer();
        var client = factory!.CreateDefaultClient(
            BaseAddress, new RedirectHandler(), new CookieContainerHandler(cookies), new ClientIpHandler(NextClientIp()));
        return (client, cookies);
    }

    /// <summary>
    /// A client that does NOT follow redirects (no <see cref="RedirectHandler"/>), for asserting
    /// raw status codes and Location headers (e.g. the /beatmaps/{id} 301).
    /// </summary>
    public static HttpClient CreateNoRedirectClient()
        => factory!.CreateDefaultClient(BaseAddress);

    /// <summary>A fresh, unique CF-Connecting-IP so a caller can claim its own rate-limit bucket.</summary>
    public static string NextClientIp()
    {
        int n = Interlocked.Increment(ref ipCounter);
        return $"10.{(n >> 16) & 255}.{(n >> 8) & 255}.{n & 255}";
    }

    /// <summary>Stamps a fixed CF-Connecting-IP on every request (GetClientIp reads it first).</summary>
    private sealed class ClientIpHandler(string ip) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Remove("CF-Connecting-IP");
            request.Headers.Add("CF-Connecting-IP", ip);
            return base.SendAsync(request, cancellationToken);
        }
    }

    /// <summary>
    /// Seeds a user directly (bypassing the flow) with a real PBKDF2 hash, returning its id.
    /// Optionally pre-verified. Used by tests that need a known credential without spending the
    /// registration rate budget.
    /// </summary>
    public static async Task<long> SeedUserAsync(string username, string email, string password, bool verified = false)
    {
        string hash = new PasswordService().Hash(password);

        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();

        long id = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO users (username, email, password_hash, country_code, verified_at)
            VALUES (@username, @email, @hash, 'US', @verifiedAt)
            RETURNING id
            """,
            new { username, email, hash, verifiedAt = verified ? DateTime.UtcNow : (DateTime?)null });

        await conn.ExecuteAsync("INSERT INTO user_stats (user_id) VALUES (@id)", new { id });
        return id;
    }

    /// <summary>
    /// Drives the full two-step website sign-in on <paramref name="client"/>: POST /login (which
    /// now emails a code and redirects to /verify) then POST /verify with the captured code,
    /// leaving an authenticated session in the client's cookie jar. Returns the final /verify
    /// response (already redirected to the landing page).
    /// </summary>
    public static async Task<HttpResponseMessage> LoginAndVerifyAsync(HttpClient client, string login, string password)
    {
        string loginToken = await GetAntiforgeryTokenAsync(client, "/login");
        using (var loginResponse = await client.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = loginToken,
            ["Login"] = login,
            ["Password"] = password,
        })))
            Assert.That(loginResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK), $"login as {login} should land on /verify");

        string code = Emails.LastCode ?? throw new InvalidOperationException($"no login code captured for {login}");

        string verifyToken = await GetAntiforgeryTokenAsync(client, "/verify");
        var verifyResponse = await client.PostAsync("/verify", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = verifyToken,
            ["Code"] = code,
        }));

        Assert.That(verifyResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK), $"verify for {login}");
        return verifyResponse;
    }

    /// <summary>GETs a page and extracts the antiforgery request token from its form markup.</summary>
    public static async Task<string> GetAntiforgeryTokenAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();

        string html = await response.Content.ReadAsStringAsync();
        var match = Regex.Match(html, "__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.That(match.Success, Is.True, $"no antiforgery token found in {url}");

        return match.Groups[1].Value;
    }

    private static async Task recreateDatabaseAsync()
    {
        await using (var conn = new NpgsqlConnection(admin_connection_string))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync($"DROP DATABASE IF EXISTS {DatabaseName} WITH (FORCE)");
            await conn.ExecuteAsync($"CREATE DATABASE {DatabaseName}");
        }

        // citext must exist before the server's pooled data source first connects — same type
        // catalog snapshot pitfall documented in the WireCompat ServerFixture.
        await using (var conn = new NpgsqlConnection(ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("CREATE EXTENSION IF NOT EXISTS citext");
        }
    }

    private static async Task seedAsync()
    {
        // Real PBKDF2 hash so the login form's PasswordService.Verify path is exercised for real.
        string hash = new PasswordService().Hash(SeededPassword);

        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();

        SeededUserId = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO users (username, email, password_hash, country_code)
            VALUES (@username, 'web_player@example.com', @hash, 'US')
            RETURNING id
            """,
            new { username = SeededUsername, hash });

        await conn.ExecuteAsync("INSERT INTO user_stats (user_id) VALUES (@id)", new { id = SeededUserId });
    }
}
