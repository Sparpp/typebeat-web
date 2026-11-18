using System.Net;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Typebeat.Web.Auth;

namespace Typebeat.Web.Tests.Website;

/// <summary>
/// <see cref="SignInHistory"/>: signed-in requests record (account, address, instance), anonymous
/// ones record nothing, and an account first seen where a restricted account was is queued for
/// review (and only queued: nobody is restricted by it).
/// </summary>
public class SignInHistoryTest
{
    private const string password = "signins-pass-123456";

    [Test]
    public async Task ABearerRequest_RecordsAddressInstanceAndAgent_AnonymousRecordsNothing()
    {
        long userId = await WebsiteFixture.SeedUserAsync("signin record", "signin-record@example.com", password, verified: true);
        string ip = WebsiteFixture.NextClientIp();
        string instance = Guid.NewGuid().ToString("N");

        using (var response = await meAsync(await bearerAsync(userId), ip, instance, "type!beat"))
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        using (var anonymous = await meAsync(bearer: null, WebsiteFixture.NextClientIp(), Guid.NewGuid().ToString("N"), "type!beat"))
            Assert.That(anonymous.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));

        var rows = (await rowsAsync(userId)).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(rows, Has.Count.EqualTo(1));
            Assert.That(rows[0].Ip, Is.EqualTo(ip));
            Assert.That(rows[0].Instance, Is.EqualTo(instance));
            Assert.That(rows[0].UserAgent, Is.EqualTo("type!beat"));
        });
    }

    [Test]
    public async Task AMalformedInstance_IsRecordedAsNone()
    {
        long userId = await WebsiteFixture.SeedUserAsync("signin junk", "signin-junk@example.com", password, verified: true);

        using (await meAsync(await bearerAsync(userId), WebsiteFixture.NextClientIp(), "not a valid id!", "type!beat"))
        {
        }

        Assert.That((await rowsAsync(userId)).Single().Instance, Is.Empty);
    }

    [Test]
    public async Task AnAccountSeenWhereARestrictedAccountWas_IsQueuedForReview_NotRestricted()
    {
        long banned = await WebsiteFixture.SeedUserAsync("signin banned", "signin-banned@example.com", password, verified: true);
        long sameInstance = await WebsiteFixture.SeedUserAsync("signin alt one", "signin-alt1@example.com", password, verified: true);
        long sameIp = await WebsiteFixture.SeedUserAsync("signin alt two", "signin-alt2@example.com", password, verified: true);
        long stranger = await WebsiteFixture.SeedUserAsync("signin stranger", "signin-stranger@example.com", password, verified: true);

        string bannedIp = WebsiteFixture.NextClientIp();
        string bannedInstance = Guid.NewGuid().ToString("N");

        using (await meAsync(await bearerAsync(banned), bannedIp, bannedInstance, "type!beat"))
        {
        }

        await using (var conn = await openAsync())
            await conn.ExecuteAsync("UPDATE users SET restricted = true WHERE id = @banned", new { banned });

        using (await meAsync(await bearerAsync(sameInstance), WebsiteFixture.NextClientIp(), bannedInstance, "type!beat"))
        {
        }

        using (await meAsync(await bearerAsync(sameIp), bannedIp, Guid.NewGuid().ToString("N"), "type!beat"))
        {
        }

        using (await meAsync(await bearerAsync(stranger), WebsiteFixture.NextClientIp(), Guid.NewGuid().ToString("N"), "type!beat"))
        {
        }

        await using var db = await openAsync();
        var reviews = (await db.QueryAsync<(long UserId, string Via)>(
            "SELECT user_id, via FROM sign_in_reviews WHERE other_user_id = @banned ORDER BY user_id", new { banned })).ToList();
        int restricted = await db.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM users WHERE id = ANY(@ids) AND restricted", new { ids = new[] { sameInstance, sameIp, stranger } });

        Assert.Multiple(() =>
        {
            Assert.That(reviews, Is.EquivalentTo(new[] { (sameInstance, "instance"), (sameIp, "ip") }));
            Assert.That(restricted, Is.Zero, "a review is only a queue entry");
        });
    }

    [Test]
    public async Task AWebsiteSession_IsGivenAnInstanceCookie_AndRecordedUnderIt()
    {
        long userId = await WebsiteFixture.SeedUserAsync("signin browser", "signin-browser@example.com", password, verified: true);
        var (client, cookies) = WebsiteFixture.CreateBrowser();
        using var _ = client;

        using (await WebsiteFixture.LoginAndVerifyAsync(client, "signin browser", password))
        {
        }

        using (var page = await client.GetAsync("/"))
            Assert.That(page.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        string? cookie = cookies.GetCookies(WebsiteFixture.BaseAddress)[SignInHistory.InstanceCookie]?.Value;

        Assert.Multiple(async () =>
        {
            Assert.That(SignInHistory.IsValidInstance(cookie), Is.True);
            Assert.That((await rowsAsync(userId)).Select(r => r.Instance), Does.Contain(cookie));
        });
    }

    private static async Task<string> bearerAsync(long userId)
        => (await WebsiteFixture.Services.GetRequiredService<TokenService>().IssueAsync(userId)).AccessToken;

    private static async Task<HttpResponseMessage> meAsync(string? bearer, string ip, string instance, string userAgent)
    {
        using var client = WebsiteFixture.CreateNoRedirectClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v2/me/");

        if (bearer != null)
            request.Headers.Add("Authorization", $"Bearer {bearer}");

        request.Headers.Add("X-Forwarded-For", ip);
        request.Headers.Add(SignInHistory.InstanceHeader, instance);
        request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        return await client.SendAsync(request);
    }

    private static async Task<IEnumerable<(string Ip, string Instance, string UserAgent)>> rowsAsync(long userId)
    {
        await using var conn = await openAsync();
        return await conn.QueryAsync<(string, string, string)>(
            "SELECT ip, instance, user_agent FROM sign_ins WHERE user_id = @userId", new { userId });
    }

    private static async Task<NpgsqlConnection> openAsync()
    {
        var conn = new NpgsqlConnection(WebsiteFixture.ConnectionString);
        await conn.OpenAsync();
        return conn;
    }
}
