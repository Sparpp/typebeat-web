using System.Net;
using System.Text.RegularExpressions;
using Dapper;
using Npgsql;
using Typebeat.Web.Social;

namespace Typebeat.Web.Tests.Website;

/// <summary>
/// The set page's comments section (backlog 295, 036_beatmapset_comments.sql): posting (signed-in
/// only, rate limited, owner notified in the same transaction), rendering (plain text encoded,
/// restricted authors delisted, soft-deleted rows absent), the three delete permissions (author /
/// set owner / reviewer, with only the reviewer's removal audited), and the keyset pagination
/// cursor. The fan-out behaviour model is NotificationFanOutTest; this drives the page handlers.
/// </summary>
public class SetCommentsTest
{
    private const string mapper_name = "comment mapper";
    private const string author_name = "comment author";
    private const string reviewer_name = "comment reviewer";
    private const string password = "hunter2hunter2";

    private long mapperId;
    private long authorId;
    private long reviewerId;
    private long restrictedId;
    private long setId;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        await PublicSiteSeed.EnsureSeededAsync();

        mapperId = await WebsiteFixture.SeedUserAsync(mapper_name, "comment.mapper@example.com", password);
        authorId = await WebsiteFixture.SeedUserAsync(author_name, "comment.author@example.com", password);
        reviewerId = await WebsiteFixture.SeedUserAsync(reviewer_name, "comment.reviewer@example.com", password);

        await using var conn = await OpenAsync();

        await conn.ExecuteAsync("UPDATE users SET map_reviewer = true WHERE id = @reviewerId", new { reviewerId });

        restrictedId = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO users (username, email, password_hash, country_code, restricted)
            VALUES ('restricted rambler', 'restricted.rambler@example.com', 'x', 'US', true)
            RETURNING id
            """);

        setId = await InsertSetAsync(conn, "Commentable Tune");
    }

    // ---- pure statics (no host) ----

    [Test]
    public void BodyPredicates_TrimAndBudget()
    {
        Assert.Multiple(() =>
        {
            Assert.That(BeatmapsetComments.Normalize("  hi there \n"), Is.EqualTo("hi there"));
            Assert.That(BeatmapsetComments.IsPostable(""), Is.False, "empty never stores");
            Assert.That(BeatmapsetComments.IsPostable(new string('x', 2000)), Is.True, "the budget itself is fine");
            Assert.That(BeatmapsetComments.IsPostable(new string('x', 2001)), Is.False, "one past it is not");
        });
    }

    [Test]
    public void CommentNotification_TargetsTheCommentsAnchor()
    {
        Assert.Multiple(() =>
        {
            Assert.That(new NotificationTarget(Notifications.MapCommentKind, 42).Url, Is.EqualTo("/beatmapsets/42#comments"));
            Assert.That(new NotificationTarget(Notifications.MapperUploadKind, 42).Url, Is.EqualTo("/beatmapsets/42"));
        });
    }

    // ---- posting ----

    [Test]
    public async Task Post_RendersEncoded_AndNotifiesTheMapper_InsideTheInsert()
    {
        using var client = await SignInAsync(author_name);

        using var response = await PostCommentAsync(client, setId, "  Nice map! <b>bold?</b>  ");
        string html = await response.Content.ReadAsStringAsync();

        await using var conn = await OpenAsync();

        var comment = await conn.QuerySingleAsync<(long Id, string Body)>(
            """
            SELECT id AS Id, body AS Body FROM beatmapset_comments
            WHERE set_id = @setId AND user_id = @authorId
            ORDER BY id DESC LIMIT 1
            """,
            new { setId, authorId });

        var note = await conn.QuerySingleAsync<(long UserId, long ActorId, long CommentId, DateTime? ReadAt)>(
            """
            SELECT user_id AS UserId, actor_id AS ActorId, comment_id AS CommentId, read_at AS ReadAt
            FROM user_notifications
            WHERE kind = 'map_comment' AND comment_id = @commentId
            """,
            new { commentId = comment.Id });

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(html, Does.Contain("comment author"));
            Assert.That(html, Does.Contain("Nice map! &lt;b&gt;bold?&lt;/b&gt;"));
            Assert.That(html, Does.Not.Contain("<b>bold?</b>"));

            Assert.That(comment.Body, Is.EqualTo("Nice map! <b>bold?</b>"), "stored trimmed, not encoded");

            // The 'map_comment' fan-out: one row, to the mapper, from the commenter, unread.
            Assert.That(note.UserId, Is.EqualTo(mapperId));
            Assert.That(note.ActorId, Is.EqualTo(authorId));
            Assert.That(note.ReadAt, Is.Null);
        });
    }

    [Test]
    public async Task SelfComment_NotifiesNobody()
    {
        using var client = await SignInAsync(mapper_name);

        using var response = await PostCommentAsync(client, setId, "mapper's own note");

        await using var conn = await OpenAsync();

        long commentId = await conn.ExecuteScalarAsync<long>(
            "SELECT max(id) FROM beatmapset_comments WHERE set_id = @setId AND user_id = @mapperId",
            new { setId, mapperId });

        int notes = await conn.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM user_notifications WHERE kind = 'map_comment' AND comment_id = @commentId",
            new { commentId });

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(notes, Is.Zero, "the owner commenting on their own set notifies nobody");
        });
    }

    [Test]
    public async Task Anonymous_Post_IsSentToLogin()
    {
        var (client, _) = WebsiteFixture.CreateBrowser();
        using var __ = client;

        using var response = await PostCommentAsync(client, setId, "drive-by");

        Assert.That(response.RequestMessage!.RequestUri!.AbsolutePath, Is.EqualTo("/login"));
    }

    [Test]
    public async Task OverLong_IsRejected_NotTruncated()
    {
        using var client = await SignInAsync(author_name);

        await using var conn = await OpenAsync();
        int before = await CountRowsAsync(conn, setId);

        using var response = await PostCommentAsync(client, setId, new string('y', 2001));

        int after = await CountRowsAsync(conn, setId);

        Assert.Multiple(() =>
        {
            Assert.That(response.RequestMessage!.RequestUri!.Query, Does.Contain("comment=long"));
            Assert.That(after, Is.EqualTo(before), "rejected means nothing stored");
        });
    }

    [Test]
    public async Task RateLimit_TenPerWindow_ThenTheSlowNotice()
    {
        // A freshly seeded user, so this test's budget cannot collide with any other's (the
        // limiter is keyed on user id and static across the suite).
        await WebsiteFixture.SeedUserAsync("comment flooder", "comment.flooder@example.com", password);
        using var client = await SignInAsync("comment flooder");

        await using var conn = await OpenAsync();
        long flooderId = await conn.ExecuteScalarAsync<long>(
            "SELECT id FROM users WHERE username = 'comment flooder'");

        for (int i = 1; i <= 10; i++)
        {
            using var ok = await PostCommentAsync(client, setId, $"flood {i}");
            Assert.That(ok.RequestMessage!.RequestUri!.Query, Does.Not.Contain("comment=slow"), $"post {i} is within budget");
        }

        using var refused = await PostCommentAsync(client, setId, "flood 11");
        string html = await refused.Content.ReadAsStringAsync();

        int stored = await conn.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM beatmapset_comments WHERE set_id = @setId AND user_id = @flooderId",
            new { setId, flooderId });

        Assert.Multiple(() =>
        {
            Assert.That(refused.RequestMessage!.RequestUri!.Query, Does.Contain("comment=slow"));
            Assert.That(html, Does.Contain("faster than the site accepts"));
            Assert.That(stored, Is.EqualTo(10), "the eleventh post stored nothing");
        });
    }

    // ---- rendering ----

    [Test]
    public async Task RestrictedAuthor_CommentsAreDelisted()
    {
        await using var conn = await OpenAsync();
        long commentSet = await InsertSetAsync(conn, "Restricted Commented Tune");
        await PublicSiteSeed.SeedCommentAsync(conn, commentSet, restrictedId, "banned words nobody sees");
        await PublicSiteSeed.SeedCommentAsync(conn, commentSet, authorId, "a visible remark");

        using var response = await WebsiteFixture.Client.GetAsync($"/beatmapsets/{commentSet}");
        string html = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("a visible remark"));
            Assert.That(html, Does.Not.Contain("banned words nobody sees"));
            Assert.That(html, Does.Not.Contain("restricted rambler"));
            Assert.That(html, Does.Contain("Comments (1)"), "the heading's count skips the delisted row too");
        });
    }

    // ---- deleting ----

    [Test]
    public async Task Author_DeletesTheirOwn_SoftAndUnaudited()
    {
        await using var conn = await OpenAsync();
        long commentSet = await InsertSetAsync(conn, "Self Delete Tune");
        long commentId = await PublicSiteSeed.SeedCommentAsync(conn, commentSet, authorId, "regretted words");

        using var client = await SignInAsync(author_name);
        using var response = await DeleteCommentAsync(client, commentSet, commentId);
        string html = await response.Content.ReadAsStringAsync();

        var row = await conn.QuerySingleAsync<(DateTime? DeletedAt, long? DeletedBy)>(
            "SELECT deleted_at AS DeletedAt, deleted_by AS DeletedBy FROM beatmapset_comments WHERE id = @commentId",
            new { commentId });

        int audits = await AuditCountAsync(conn, commentId);

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(html, Does.Not.Contain("regretted words"), "a soft-deleted row never renders");
            Assert.That(row.DeletedAt, Is.Not.Null, "soft delete: the row stays");
            Assert.That(row.DeletedBy, Is.EqualTo(authorId));
            Assert.That(audits, Is.Zero, "deleting your own comment is not moderation");
        });
    }

    [Test]
    public async Task SetOwner_DeletesOnTheirOwnSet_Unaudited()
    {
        await using var conn = await OpenAsync();
        long commentSet = await InsertSetAsync(conn, "Owner Tidied Tune");
        long commentId = await PublicSiteSeed.SeedCommentAsync(conn, commentSet, authorId, "spam on the thread");

        using var client = await SignInAsync(mapper_name);
        using var response = await DeleteCommentAsync(client, commentSet, commentId);

        var row = await conn.QuerySingleAsync<(DateTime? DeletedAt, long? DeletedBy)>(
            "SELECT deleted_at AS DeletedAt, deleted_by AS DeletedBy FROM beatmapset_comments WHERE id = @commentId",
            new { commentId });

        int audits = await AuditCountAsync(conn, commentId);

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(row.DeletedAt, Is.Not.Null);
            Assert.That(row.DeletedBy, Is.EqualTo(mapperId));
            Assert.That(audits, Is.Zero, "tidying your own set's thread is not moderation");
        });
    }

    [Test]
    public async Task Reviewer_DeletesAnybodys_AndTheAuditRowLands()
    {
        await using var conn = await OpenAsync();
        long commentSet = await InsertSetAsync(conn, "Moderated Tune");
        long commentId = await PublicSiteSeed.SeedCommentAsync(conn, commentSet, authorId, "rule-breaking words");

        using var client = await SignInAsync(reviewer_name);
        using var response = await DeleteCommentAsync(client, commentSet, commentId);

        var row = await conn.QuerySingleAsync<(DateTime? DeletedAt, long? DeletedBy)>(
            "SELECT deleted_at AS DeletedAt, deleted_by AS DeletedBy FROM beatmapset_comments WHERE id = @commentId",
            new { commentId });

        var audit = await conn.QuerySingleAsync<(long ActorId, long? SetId, string Note)>(
            """
            SELECT actor_id AS ActorId, set_id AS SetId, note AS Note
            FROM moderation_actions
            WHERE action = 'comment_delete' AND note = @note
            """,
            new { note = $"comment {commentId}" });

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(row.DeletedAt, Is.Not.Null);
            Assert.That(row.DeletedBy, Is.EqualTo(reviewerId));
            Assert.That(audit.ActorId, Is.EqualTo(reviewerId));
            Assert.That(audit.SetId, Is.EqualTo(commentSet));
        });
    }

    [Test]
    public async Task Stranger_Delete_IsNotFound_AndTheRowStaysLive()
    {
        await using var conn = await OpenAsync();
        long commentSet = await InsertSetAsync(conn, "Untidied Tune");
        long commentId = await PublicSiteSeed.SeedCommentAsync(conn, commentSet, authorId, "stays right here");

        // Signed in, but neither the author, the set's owner, nor a reviewer. A dedicated user,
        // not the shared "web player" (its hourly login-code budget belongs to the older tests:
        // EmailCodeService.MaxPerHour).
        await WebsiteFixture.SeedUserAsync("comment bystander", "comment.bystander@example.com", password);
        using var client = await SignInAsync("comment bystander");
        using var response = await DeleteCommentAsync(client, commentSet, commentId);

        bool live = await conn.ExecuteScalarAsync<bool>(
            "SELECT deleted_at IS NULL FROM beatmapset_comments WHERE id = @commentId", new { commentId });

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound), "no row matched: 404, never Forbid");
            Assert.That(live, Is.True);
        });
    }

    // ---- pagination ----

    [Test]
    public async Task Pagination_Fifty_ThenACursorLink_PreservingDiffAndBoard()
    {
        await using var conn = await OpenAsync();
        long pagedSet = await InsertSetAsync(conn, "Paged Thread Tune");

        long diffId = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmaps (set_id, version_name, checksum_md5, total_length_s, drain_length_s, difficulty_rating, filename)
            VALUES (@pagedSet, 'paged diff', @checksum, 60, 55, 2.5, 'paged.osu')
            RETURNING id
            """,
            new { pagedSet, checksum = Guid.NewGuid().ToString("N") });

        for (int i = 1; i <= 51; i++)
            await PublicSiteSeed.SeedCommentAsync(conn, pagedSet, authorId, $"pag remark {i:000}");

        using var page1 = await WebsiteFixture.Client.GetAsync($"/beatmapsets/{pagedSet}?diff={diffId}&board=unranked");
        string html1 = await page1.Content.ReadAsStringAsync();

        var cursor = Regex.Match(html1, "comments_after=(\\d+)");

        Assert.Multiple(() =>
        {
            Assert.That(html1, Does.Contain("Comments (51)"));
            Assert.That(html1, Does.Contain("pag remark 001"), "oldest first");
            Assert.That(html1, Does.Contain("pag remark 050"));
            Assert.That(html1, Does.Not.Contain("pag remark 051"), "the 51st waits behind the cursor");

            // The show-more link carries the cursor AND the visitor's diff/board choices.
            Assert.That(cursor.Success, Is.True, "a show-more cursor link renders");
            Assert.That(html1, Does.Contain($"diff={diffId}&amp;board=unranked&amp;comments_after="));
        });

        using var page2 = await WebsiteFixture.Client.GetAsync(
            $"/beatmapsets/{pagedSet}?diff={diffId}&board=unranked&comments_after={cursor.Groups[1].Value}");
        string html2 = await page2.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(html2, Does.Contain("pag remark 051"));
            Assert.That(html2, Does.Not.Contain("pag remark 001"), "the cursor page starts past the first fifty");
            Assert.That(html2, Does.Not.Contain("Show more comments"), "no further page, no further link");
        });
    }

    // ---- helpers ----

    private static async Task<NpgsqlConnection> OpenAsync()
    {
        var conn = new NpgsqlConnection(WebsiteFixture.ConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    private async Task<long> InsertSetAsync(NpgsqlConnection conn, string title)
        => await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmapsets (owner_id, title, artist, status)
            VALUES (@mapperId, @title, 'The Threaded', 'ranked')
            RETURNING id
            """,
            new { mapperId, title });

    private static async Task<HttpClient> SignInAsync(string username)
    {
        var (client, _) = WebsiteFixture.CreateBrowser();
        using (await WebsiteFixture.LoginAndVerifyAsync(client, username, password)) { }
        return client;
    }

    private static async Task<HttpResponseMessage> PostCommentAsync(HttpClient client, long setId, string body)
    {
        string token = await WebsiteFixture.GetAntiforgeryTokenAsync(client, $"/beatmapsets/{setId}");

        return await client.PostAsync($"/beatmapsets/{setId}?handler=Comment",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["body"] = body,
            }));
    }

    private static async Task<HttpResponseMessage> DeleteCommentAsync(HttpClient client, long setId, long commentId)
    {
        string token = await WebsiteFixture.GetAntiforgeryTokenAsync(client, $"/beatmapsets/{setId}");

        return await client.PostAsync($"/beatmapsets/{setId}?handler=DeleteComment",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["commentId"] = commentId.ToString(),
            }));
    }

    private static async Task<int> CountRowsAsync(NpgsqlConnection conn, long setId)
        => await conn.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM beatmapset_comments WHERE set_id = @setId", new { setId });

    private static async Task<int> AuditCountAsync(NpgsqlConnection conn, long commentId)
        => await conn.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM moderation_actions WHERE action = 'comment_delete' AND note = @note",
            new { note = $"comment {commentId}" });
}
