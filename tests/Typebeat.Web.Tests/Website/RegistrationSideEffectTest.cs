using System.Net;
using Dapper;
using Newtonsoft.Json.Linq;
using Npgsql;

namespace Typebeat.Web.Tests.Website;

/// <summary>
/// The game-client registration path (POST /users) gains a verify-code email as a pure side
/// effect. This pins that the WIRE RESPONSE is unchanged — same 200 and same {id,username} body —
/// and, critically, that a failing email sender never changes the status or body (the account is
/// still created and returned).
/// </summary>
public class RegistrationSideEffectTest
{
    [Test]
    public async Task PostUsers_Succeeds_AndEmailsVerifyCode()
    {
        const string email = "ingame.newbie@example.com";

        using var response = await postUsersAsync("ingame newbie", email, "gamepass12345");
        string body = await response.Content.ReadAsStringAsync();

        var json = JObject.Parse(body);
        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/json"));
            Assert.That(json["username"]!.Value<string>(), Is.EqualTo("ingame newbie"));
            Assert.That(json["id"]!.Value<long>(), Is.GreaterThan(0));
            // The side effect fired: a verify code was emailed for the new account.
            Assert.That(WebsiteFixture.Emails.LastCodeFor(email), Is.Not.Null.And.Not.Empty);
        });
    }

    [Test]
    public async Task PostUsers_StillSucceeds_WhenEmailSendThrows()
    {
        const string email = "ingame.mailfail@example.com";

        WebsiteFixture.Emails.ThrowOnSend = true;
        try
        {
            using var response = await postUsersAsync("mail fail", email, "gamepass12345");
            string body = await response.Content.ReadAsStringAsync();

            var json = JObject.Parse(body);
            Assert.Multiple(() =>
            {
                // A thrown send must NOT change the wire contract.
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(json["username"]!.Value<string>(), Is.EqualTo("mail fail"));
                Assert.That(json["id"]!.Value<long>(), Is.GreaterThan(0));
            });
        }
        finally
        {
            WebsiteFixture.Emails.ThrowOnSend = false;
        }

        // The account still exists despite the failed email.
        await using var conn = new NpgsqlConnection(WebsiteFixture.ConnectionString);
        await conn.OpenAsync();
        bool exists = await conn.ExecuteScalarAsync<bool>(
            "SELECT EXISTS(SELECT 1 FROM users WHERE email = @email::citext)", new { email });
        Assert.That(exists, Is.True, "account is created even when the verify email fails to send");
    }

    private static async Task<HttpResponseMessage> postUsersAsync(string username, string email, string password)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/users")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["user[username]"] = username,
                ["user[user_email]"] = email,
                ["user[password]"] = password,
            }),
        };
        // The client's fixed UA is the gate; a unique IP dodges the 3/hour registration limiter.
        request.Headers.Add("User-Agent", "type!beat");
        request.Headers.Add("CF-Connecting-IP", WebsiteFixture.NextClientIp());

        return await WebsiteFixture.Client.SendAsync(request);
    }
}
