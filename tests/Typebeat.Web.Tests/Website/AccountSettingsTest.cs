using System.Net;
using System.Net.Http.Headers;
using Dapper;
using Newtonsoft.Json.Linq;
using Npgsql;
using Typebeat.Web.Auth;
using Typebeat.Web.Data;

namespace Typebeat.Web.Tests.Website;

/// <summary>
/// The /settings page: login gating, description save, avatar upload + serving, and the two
/// account-deletion paths (wrong confirmation keeps the account; correct confirmation anonymizes
/// it while keeping the user's uploaded maps). Signed-in browsers come from the two-step fixture
/// helper; each test seeds its own throwaway user so deletions don't disturb the shared seed.
///
/// NonParallelizable like the other DB+login website fixtures (RankedApprovalTest): these tests
/// drive the two-step login through the shared capturing email sender (WebsiteFixture.Emails,
/// whose LastCode is a single slot) and mutate the shared database, so they must not run
/// concurrently with the rest of the Website namespace.
/// </summary>
[NonParallelizable]
public class AccountSettingsTest
{
    private const string password = "correcthorse42";

    // A tiny valid 64x64 PNG (the same asset StubEndpoints serves as the default avatar), enough
    // for ImageSharp to decode and re-encode into the stored avatar/banner JPEG.
    private const string png_base64 =
        "iVBORw0KGgoAAAANSUhEUgAAAEAAAABACAIAAAAlC+aJAAAAe0lEQVR4nO3PUQkAIBTAQJu9XsY2gSH8OITBAtzWnvN1" +
        "iwsa0IIGtKABLWhACxrQgga0oAEtaEALGtCCBrSgAS1oQAsa0IIGtKABLWhACxrQgga0oAEtaEALGtCCBrSgAS1oQAsa" +
        "0IIGtKABLWhACxrQgga0oIHxiJeBC2uMsYdARYnQAAAAAElFTkSuQmCC";

    private static async Task<(long id, string username)> SeedAndLoginAsync(HttpClient client)
    {
        string username = "settings_" + Guid.NewGuid().ToString("N")[..10];
        long id = await WebsiteFixture.SeedUserAsync(username, username + "@example.com", password, verified: true);
        await WebsiteFixture.LoginAndVerifyAsync(client, username, password);
        return (id, username);
    }

    private static NpgsqlConnection Db() => new NpgsqlConnection(WebsiteFixture.ConnectionString);

    [Test]
    public async Task Settings_Anonymous_RedirectsToLogin()
    {
        using var client = WebsiteFixture.CreateNoRedirectClient();
        using var response = await client.GetAsync("/settings");

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
            Assert.That(response.Headers.Location?.ToString(), Does.Contain("/login"));
        });
    }

    [Test]
    public async Task Settings_SignedIn_RendersForms()
    {
        var (client, _) = WebsiteFixture.CreateBrowser();
        using var __ = client;
        await SeedAndLoginAsync(client);

        using var response = await client.GetAsync("/settings");
        string html = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(html, Does.Contain("Account settings"));
            Assert.That(html, Does.Contain("Delete account"));
            Assert.That(html, Does.Contain("handler=Avatar"));
        });
    }

    [Test]
    public async Task Description_Save_PersistsAndShowsOnProfile()
    {
        var (client, _) = WebsiteFixture.CreateBrowser();
        using var __ = client;
        var (id, _) = await SeedAndLoginAsync(client);

        const string bio = "I type fast and I break combos.";
        string token = await WebsiteFixture.GetAntiforgeryTokenAsync(client, "/settings");

        using (var save = await client.PostAsync("/settings?handler=Profile", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["Description"] = bio,
        })))
            Assert.That(save.StatusCode, Is.EqualTo(HttpStatusCode.OK), "profile save should PRG back to /settings");

        await using var conn = Db();
        await conn.OpenAsync();
        string? stored = await conn.ExecuteScalarAsync<string>("SELECT description FROM users WHERE id = @id", new { id });
        Assert.That(stored, Is.EqualTo(bio));

        // And it renders on the public profile.
        using var profile = await WebsiteFixture.Client.GetAsync($"/users/{id}");
        string profileHtml = await profile.Content.ReadAsStringAsync();
        Assert.That(profileHtml, Does.Contain(bio));
    }

    [Test]
    public async Task Description_TooLong_IsRejected()
    {
        var (client, _) = WebsiteFixture.CreateBrowser();
        using var __ = client;
        var (id, _) = await SeedAndLoginAsync(client);

        string token = await WebsiteFixture.GetAntiforgeryTokenAsync(client, "/settings");
        using var save = await client.PostAsync("/settings?handler=Profile", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["Description"] = new string('x', 2001),
        }));

        string html = await save.Content.ReadAsStringAsync();
        Assert.That(html, Does.Contain("too long"));

        await using var conn = Db();
        await conn.OpenAsync();
        string? stored = await conn.ExecuteScalarAsync<string>("SELECT description FROM users WHERE id = @id", new { id });
        Assert.That(stored, Is.Empty, "the over-long description must not be saved");
    }

    [Test]
    public async Task Avatar_Upload_StoresKeyAndServesImage()
    {
        var (client, _) = WebsiteFixture.CreateBrowser();
        using var __ = client;
        var (id, _) = await SeedAndLoginAsync(client);

        string token = await WebsiteFixture.GetAntiforgeryTokenAsync(client, "/settings");
        using (var form = new MultipartFormDataContent { { new StringContent(token), "__RequestVerificationToken" } })
        {
            var file = new ByteArrayContent(Convert.FromBase64String(png_base64));
            file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(file, "avatar", "avatar.png");

            using var upload = await client.PostAsync("/settings?handler=Avatar", form);
            Assert.That(upload.StatusCode, Is.EqualTo(HttpStatusCode.OK), "avatar upload should PRG back to /settings");
        }

        await using var conn = Db();
        await conn.OpenAsync();
        string? avatarKey = await conn.ExecuteScalarAsync<string?>("SELECT avatar_key FROM users WHERE id = @id", new { id });

        Assert.That(avatarKey, Is.Not.Null.And.StartWith($"avatars/{id}/"));

        // The stored key is served as a JPEG (world-readable, no auth).
        using var image = await WebsiteFixture.Client.GetAsync("/" + avatarKey);
        Assert.Multiple(() =>
        {
            Assert.That(image.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(image.Content.Headers.ContentType?.MediaType, Is.EqualTo("image/jpeg"));
        });
    }

    [Test]
    public async Task Me_EmitsUploadedAvatarUrl_ToTheGameClient()
    {
        string username = "meavatar_" + Guid.NewGuid().ToString("N")[..10];
        long id = await WebsiteFixture.SeedUserAsync(username, username + "@example.com", password, verified: true);

        string avatarKey = $"avatars/{id}/9.jpg";
        await using (var conn = Db())
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("UPDATE users SET avatar_key = @k WHERE id = @id", new { id, k = avatarKey });
        }

        // Mint a real bearer for this user (the game-client path), same as BssFixture does.
        await using var ds = NpgsqlDataSource.Create(WebsiteFixture.ConnectionString);
        var pair = await new TokenService(new Db(ds)).IssueAsync(id);

        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/v2/me/");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pair.AccessToken);
        using var resp = await WebsiteFixture.Client.SendAsync(req);

        var me = JObject.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Multiple(() =>
        {
            Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            // The client renders this avatar_url in-game; it must be the uploaded one, absolute.
            Assert.That((string?)me["avatar_url"], Is.EqualTo($"https://localhost/{avatarKey}"));
        });
    }

    [Test]
    public async Task Delete_WrongUsername_KeepsAccount()
    {
        var (client, _) = WebsiteFixture.CreateBrowser();
        using var __ = client;
        var (id, username) = await SeedAndLoginAsync(client);

        string token = await WebsiteFixture.GetAntiforgeryTokenAsync(client, "/settings");
        using var attempt = await client.PostAsync("/settings?handler=Delete", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["ConfirmUsername"] = "not-my-name",
        }));

        string html = await attempt.Content.ReadAsStringAsync();
        Assert.That(html, Does.Contain("Type your username exactly"));

        await using var conn = Db();
        await conn.OpenAsync();
        string? stillThere = await conn.ExecuteScalarAsync<string>("SELECT username::text FROM users WHERE id = @id", new { id });
        Assert.That(stillThere, Is.EqualTo(username), "the account must be untouched after a failed confirmation");
    }

    [Test]
    public async Task Delete_CorrectUsername_AnonymizesButKeepsMaps()
    {
        var (client, _) = WebsiteFixture.CreateBrowser();
        using var __ = client;
        var (id, username) = await SeedAndLoginAsync(client);

        // Give the user an uploaded map and an avatar so we can prove the map survives and the
        // personal data is scrubbed.
        long setId;
        await using (var setup = Db())
        {
            await setup.OpenAsync();
            setId = await setup.ExecuteScalarAsync<long>(
                "INSERT INTO beatmapsets (owner_id, title, artist, status) VALUES (@id, 'Keeper', 'Artist', 'ranked') RETURNING id",
                new { id });
            await setup.ExecuteAsync("UPDATE users SET avatar_key = @k, description = 'bye' WHERE id = @id",
                new { id, k = $"avatars/{id}/1.jpg" });
        }

        string token = await WebsiteFixture.GetAntiforgeryTokenAsync(client, "/settings");
        using (var delete = await client.PostAsync("/settings?handler=Delete", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["ConfirmUsername"] = username,
        })))
        {
            string landing = await delete.Content.ReadAsStringAsync();
            Assert.That(delete.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(landing, Does.Contain("account has been deleted"));
        }

        await using var conn = Db();
        await conn.OpenAsync();

        var user = await conn.QuerySingleAsync<(string Username, string Email, string PasswordHash, string Country, string? AvatarKey, string Description, bool IsAdmin, DateTime? DeletedAt)>(
            """
            SELECT username::text AS Username, email::text AS Email, password_hash AS PasswordHash,
                   country_code AS Country, avatar_key AS AvatarKey, description AS Description,
                   is_admin AS IsAdmin, deleted_at AS DeletedAt
            FROM users WHERE id = @id
            """, new { id });

        Assert.Multiple(() =>
        {
            // Personal data scrubbed, identity tombstoned.
            Assert.That(user.Username, Is.EqualTo($"deleted_{id}"));
            Assert.That(user.Email, Is.EqualTo($"deleted_{id}@deleted.invalid"));
            Assert.That(user.PasswordHash, Is.Empty);
            Assert.That(user.Country, Is.EqualTo("XX"));
            Assert.That(user.AvatarKey, Is.Null);
            Assert.That(user.Description, Is.Empty);
            Assert.That(user.DeletedAt, Is.Not.Null);
        });

        // The uploaded map survives, still owned by the (now anonymous) row.
        long ownerOfSet = await conn.ExecuteScalarAsync<long>("SELECT owner_id FROM beatmapsets WHERE id = @setId", new { setId });
        Assert.That(ownerOfSet, Is.EqualTo(id));

        // Every session/bearer token is gone — the account can't be accessed.
        int tokens = await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM oauth_tokens WHERE user_id = @id", new { id });
        Assert.That(tokens, Is.Zero);
    }
}
