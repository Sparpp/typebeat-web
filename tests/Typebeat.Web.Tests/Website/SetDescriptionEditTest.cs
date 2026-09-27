using System.Net;
using Dapper;
using Npgsql;

namespace Typebeat.Web.Tests.Website;

/// <summary>
/// The set page's description editing (backlog 295): owner or reviewer only, enforced as the
/// UPDATE's WHERE clause (0 rows = 404, the page convention); over-budget text rejected rather
/// than truncated (the Settings bio rule); rendered through the page's existing plain-text
/// discipline; and updated_at deliberately NOT bumped, so an edit is not a free listing-sort
/// boost.
/// </summary>
public class SetDescriptionEditTest
{
    private const string owner_name = "desc owner";
    private const string reviewer_name = "desc reviewer";
    private const string password = "hunter2hunter2";

    private long ownerId;
    private long setId;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        await PublicSiteSeed.EnsureSeededAsync();

        ownerId = await WebsiteFixture.SeedUserAsync(owner_name, "desc.owner@example.com", password);

        long reviewerId = await WebsiteFixture.SeedUserAsync(reviewer_name, "desc.reviewer@example.com", password);

        await using var conn = new NpgsqlConnection(WebsiteFixture.ConnectionString);
        await conn.OpenAsync();

        await conn.ExecuteAsync("UPDATE users SET map_reviewer = true WHERE id = @reviewerId", new { reviewerId });

        // Backdated updated_at, so a handler that wrongly bumped it moves the value by days,
        // not by the milliseconds a same-statement now() would.
        setId = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmapsets (owner_id, title, artist, status, description, submitted_at, updated_at)
            VALUES (@ownerId, 'Describable Tune', 'The Editors', 'ranked', 'Original words',
                    now() - interval '5 days', now() - interval '5 days')
            RETURNING id
            """,
            new { ownerId });
    }

    [Test]
    public async Task Owner_SeesTheBox_AndSavesEncodedText_WithoutBumpingUpdatedAt()
    {
        var (client, _) = WebsiteFixture.CreateBrowser();
        using var __ = client;
        using (await WebsiteFixture.LoginAndVerifyAsync(client, owner_name, password)) { }

        // The box renders for the owner, prefilled.
        using (var page = await client.GetAsync($"/beatmapsets/{setId}"))
        {
            string beforeHtml = await page.Content.ReadAsStringAsync();
            Assert.That(beforeHtml, Does.Contain("description-box"));
            Assert.That(beforeHtml, Does.Contain("Edit description"));
        }

        DateTime updatedBefore = await UpdatedAtAsync(setId);

        // Whitespace shell + markup probe: stored trimmed, rendered encoded (the seed's idiom).
        using var response = await PostDescriptionAsync(client, setId,
            "  New words for the set\n<script>alert(2)</script>  ");
        string html = await response.Content.ReadAsStringAsync();

        string stored = await DescriptionAsync(setId);
        DateTime updatedAfter = await UpdatedAtAsync(setId);

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(response.RequestMessage!.RequestUri!.Query, Does.Contain("saved=description"));
            Assert.That(html, Does.Contain("Description saved."));
            Assert.That(html, Does.Contain("New words for the set"));
            Assert.That(html, Does.Contain("&lt;script&gt;alert(2)&lt;/script&gt;"));
            Assert.That(html, Does.Not.Contain("<script>alert(2)</script>"));

            Assert.That(stored,
                Is.EqualTo("New words for the set\n<script>alert(2)</script>"), "stored trimmed, not encoded");
            Assert.That(updatedAfter, Is.EqualTo(updatedBefore),
                "a description edit must not gift a listing-sort bump");
        });
    }

    [Test]
    public async Task Reviewer_MayEditSomebodyElsesSet()
    {
        var (client, _) = WebsiteFixture.CreateBrowser();
        using var __ = client;
        using (await WebsiteFixture.LoginAndVerifyAsync(client, reviewer_name, password)) { }

        long otherSetId = await InsertSetAsync("Reviewer Touched Tune", "Fixed description");

        using var response = await PostDescriptionAsync(client, otherSetId, "Cleaned up by review");

        string stored = await DescriptionAsync(otherSetId);

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(response.RequestMessage!.RequestUri!.Query, Does.Contain("saved=description"));
            Assert.That(stored, Is.EqualTo("Cleaned up by review"));
        });
    }

    [Test]
    public async Task NonOwner_Post_IsNotFound_AndWritesNothing()
    {
        long targetId = await InsertSetAsync("Untouchable Tune", "Keep these words");

        // A dedicated user, not the shared "web player" (its hourly login-code budget belongs to
        // the older tests: EmailCodeService.MaxPerHour).
        await WebsiteFixture.SeedUserAsync("desc bystander", "desc.bystander@example.com", password);

        var (client, _) = WebsiteFixture.CreateBrowser();
        using var __ = client;
        using (await WebsiteFixture.LoginAndVerifyAsync(client, "desc bystander", password)) { }

        using var response = await PostDescriptionAsync(client, targetId, "vandalism");

        string stored = await DescriptionAsync(targetId);

        Assert.Multiple(() =>
        {
            // The page convention: never Forbid. The forged POST matches no row and 404s.
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(stored, Is.EqualTo("Keep these words"));
        });
    }

    [Test]
    public async Task Anonymous_Post_IsSentToLogin()
    {
        var (client, _) = WebsiteFixture.CreateBrowser();
        using var __ = client;

        using var response = await PostDescriptionAsync(client, setId, "drive-by");

        Assert.That(response.RequestMessage!.RequestUri!.AbsolutePath, Is.EqualTo("/login"));
    }

    [Test]
    public async Task OverLong_IsRejected_NotTruncated()
    {
        long targetId = await InsertSetAsync("Verbose Tune", "Short and stored");

        var (client, _) = WebsiteFixture.CreateBrowser();
        using var __ = client;
        using (await WebsiteFixture.LoginAndVerifyAsync(client, owner_name, password)) { }

        // 2001 characters after trimming: one past the budget the DB CHECK also enforces.
        using var response = await PostDescriptionAsync(client, targetId, new string('x', 2001));
        string html = await response.Content.ReadAsStringAsync();
        string stored = await DescriptionAsync(targetId);

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(response.RequestMessage!.RequestUri!.Query, Does.Contain("description=long"));
            Assert.That(html, Does.Contain("nothing was saved"));
            Assert.That(stored, Is.EqualTo("Short and stored"),
                "rejected means untouched, never a silent truncation");
        });
    }

    // ---- helpers ----

    private async Task<long> InsertSetAsync(string title, string description)
    {
        await using var conn = new NpgsqlConnection(WebsiteFixture.ConnectionString);
        await conn.OpenAsync();

        return await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmapsets (owner_id, title, artist, status, description, submitted_at, updated_at)
            VALUES (@ownerId, @title, 'The Editors', 'ranked', @description,
                    now() - interval '5 days', now() - interval '5 days')
            RETURNING id
            """,
            new { ownerId, title, description });
    }

    private static async Task<HttpResponseMessage> PostDescriptionAsync(HttpClient client, long setId, string description)
    {
        string token = await WebsiteFixture.GetAntiforgeryTokenAsync(client, $"/beatmapsets/{setId}");

        return await client.PostAsync($"/beatmapsets/{setId}?handler=Description",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["description"] = description,
            }));
    }

    private static async Task<string> DescriptionAsync(long setId)
    {
        await using var conn = new NpgsqlConnection(WebsiteFixture.ConnectionString);
        await conn.OpenAsync();
        return await conn.ExecuteScalarAsync<string>(
            "SELECT description FROM beatmapsets WHERE id = @setId", new { setId }) ?? "";
    }

    private static async Task<DateTime> UpdatedAtAsync(long setId)
    {
        await using var conn = new NpgsqlConnection(WebsiteFixture.ConnectionString);
        await conn.OpenAsync();
        return await conn.ExecuteScalarAsync<DateTime>(
            "SELECT updated_at FROM beatmapsets WHERE id = @setId", new { setId });
    }
}
