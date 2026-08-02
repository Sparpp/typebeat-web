using System.Net;
using Dapper;
using Npgsql;

namespace Typebeat.Web.Tests.Website;

/// <summary>
/// Pinned scores (task 63): the profile's "Pinned" section and the Pin/Unpin POST handlers behind
/// the owner-only control on each score row.
///
/// The point of most of these tests is that the RULES ARE SERVER-SIDE. The pin control only ever
/// renders on your own ranked rows, so every refusal here (someone else's score, an unranked
/// score, the 11th pin) is driven by POSTing the handler directly, which is exactly what a forged
/// request or a stale page does.
///
/// Each test seeds its own throwaway user (pins are per user, and the cap is a per-user count), so
/// no test can be perturbed by another's pins. They share one set seeded here, with a past
/// submitted_at and default counters, so the landing/listing sort assertions elsewhere in this
/// namespace stay untouched.
///
/// That set is 'pending', not 'ranked', on purpose. Pinning cares about the SCORE's ranked flag,
/// not the set's status (and pending sets are browsable, so their plays show on profiles like any
/// other), while a 'ranked' set would push every user seeded here onto the global rankings board
/// and disturb RankingsPageTest's assertions. The scores also use a distinctive 4,4xx,xxx band so
/// their rendered figures cannot collide with another test's "must not contain" checks.
///
/// NonParallelizable for the same reason as AccountSettingsTest: the two-step login runs through
/// the single-slot capturing email sender and these tests mutate the shared database.
/// </summary>
[NonParallelizable]
public class PinnedScoresTest
{
    private const string password = "pinpinpin-4242";

    private static long setId;
    private static long beatmapId;

    /// <summary>A second set, only ever used by the visibility test that hides it mid-test.</summary>
    private static long hideableSetId;
    private static long hideableBeatmapId;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        await PublicSiteSeed.EnsureSeededAsync();

        await using var conn = Db();
        await conn.OpenAsync();

        long ownerId = await WebsiteFixture.SeedUserAsync(
            "pin_mapper_" + Guid.NewGuid().ToString("N")[..8], Guid.NewGuid().ToString("N") + "@example.com", password);

        setId = await InsertSetAsync(conn, ownerId, "Pinned Anthem", "The Thumbtacks", "pending");
        beatmapId = await InsertBeatmapAsync(conn, setId);

        hideableSetId = await InsertSetAsync(conn, ownerId, "Pinned Vanishing Act", "The Thumbtacks", "pending");
        hideableBeatmapId = await InsertBeatmapAsync(conn, hideableSetId);
    }

    [Test]
    public async Task Pin_RendersSectionForEveryVisitor_NewestPinFirst()
    {
        var (client, _) = WebsiteFixture.CreateBrowser();
        using var __ = client;
        long userId = await SeedAndLoginAsync(client);

        long older = await SeedScoreAsync(userId, 4_400_111);
        long newer = await SeedScoreAsync(userId, 4_400_222);

        Assert.That(await PinAsync(client, userId, older), Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await PinAsync(client, userId, newer), Is.EqualTo(HttpStatusCode.OK));

        // Owner's own view: section present, newest pin on top, each row offering "unpin".
        string ownHtml = await GetProfileAsync(client, userId);
        string ownSection = PinnedSection(ownHtml);

        // A visitor sees the same section, without any control.
        string anonHtml = await GetProfileAsync(WebsiteFixture.Client, userId);
        string anonSection = PinnedSection(anonHtml);

        Assert.Multiple(() =>
        {
            Assert.That(ownSection, Does.Contain("Pinned Anthem"));
            Assert.That(ownSection.IndexOf("4,400,222", StringComparison.Ordinal),
                Is.LessThan(ownSection.IndexOf("4,400,111", StringComparison.Ordinal)),
                "the newest pin must sit at the top of the section");
            Assert.That(ownSection, Does.Contain("handler=Unpin"));

            Assert.That(anonSection, Does.Contain("4,400,222"));
            Assert.That(anonSection, Does.Contain("4,400,111"));
            Assert.That(anonSection, Does.Not.Contain("handler=Unpin"), "a visitor gets no pin controls");
            Assert.That(anonHtml, Does.Not.Contain("handler=Pin"));
        });
    }

    [Test]
    public async Task PinControl_ShowsOnOwnBestAndRecentRows_NeverOnSomebodyElsesProfile()
    {
        var (owner, _) = WebsiteFixture.CreateBrowser();
        using var __ = owner;
        long userId = await SeedAndLoginAsync(owner);
        await SeedScoreAsync(userId, 4_400_333);

        string ownHtml = await GetProfileAsync(owner, userId);

        var (visitor, _) = WebsiteFixture.CreateBrowser();
        using var ___ = visitor;
        await SeedAndLoginAsync(visitor);
        string visitorHtml = await GetProfileAsync(visitor, userId);

        Assert.Multiple(() =>
        {
            // Best scores and Recent scores both render the row, so both carry a control.
            Assert.That(CountOccurrences(ownHtml, "handler=Pin"), Is.EqualTo(2));
            Assert.That(visitorHtml, Does.Not.Contain("handler=Pin"),
                "a signed-in visitor must not get pin controls on somebody else's scores");
        });
    }

    [Test]
    public async Task PinnedSection_IsHiddenEntirelyWhenNothingIsPinned()
    {
        var (client, _) = WebsiteFixture.CreateBrowser();
        using var __ = client;
        long userId = await SeedAndLoginAsync(client);
        await SeedScoreAsync(userId, 4_400_444);

        string ownHtml = await GetProfileAsync(client, userId);
        string anonHtml = await GetProfileAsync(WebsiteFixture.Client, userId);

        Assert.Multiple(() =>
        {
            Assert.That(ownHtml, Does.Not.Contain(">Pinned</h2>"));
            Assert.That(anonHtml, Does.Not.Contain(">Pinned</h2>"));
        });
    }

    [Test]
    public async Task Pin_RefusesSomebodyElsesScore()
    {
        var (owner, _) = WebsiteFixture.CreateBrowser();
        using var __ = owner;
        long ownerId = await SeedAndLoginAsync(owner);
        long ownerScore = await SeedScoreAsync(ownerId, 4_400_555);

        var (thief, _) = WebsiteFixture.CreateBrowser();
        using var ___ = thief;
        long thiefId = await SeedAndLoginAsync(thief);

        var status = await PinAsync(thief, thiefId, ownerScore);
        int thiefPins = await PinCountAsync(thiefId);
        bool pinnedAtAll = await IsPinnedAsync(ownerScore);

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(thiefPins, Is.Zero);
            Assert.That(pinnedAtAll, Is.False, "nobody's pin row may appear for that score");
        });
    }

    [Test]
    public async Task Pin_RefusesUnrankedScore()
    {
        var (client, _) = WebsiteFixture.CreateBrowser();
        using var __ = client;
        long userId = await SeedAndLoginAsync(client);
        long unranked = await SeedScoreAsync(userId, 4_400_666, ranked: false);

        using var response = await PostPinAsync(client, userId, unranked, "Pin");
        string html = await response.Content.ReadAsStringAsync();
        int pins = await PinCountAsync(userId);

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), "the refusal redirects back to the profile");
            Assert.That(html, Does.Contain("not ranked, so it cannot be pinned"));
            Assert.That(pins, Is.Zero);
        });
    }

    [Test]
    public async Task Pin_IsIdempotent()
    {
        var (client, _) = WebsiteFixture.CreateBrowser();
        using var __ = client;
        long userId = await SeedAndLoginAsync(client);
        long scoreId = await SeedScoreAsync(userId, 4_400_777);

        Assert.That(await PinAsync(client, userId, scoreId), Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await PinAsync(client, userId, scoreId), Is.EqualTo(HttpStatusCode.OK),
            "re-pinning is a no-op, not an error");

        Assert.That(await PinCountAsync(userId), Is.EqualTo(1));

        // And the section shows the score once, not twice.
        string html = await GetProfileAsync(client, userId);
        Assert.That(CountOccurrences(PinnedSection(html), "4,400,777"), Is.EqualTo(1));
    }

    [Test]
    public async Task Pin_StopsAtTheCap()
    {
        var (client, _) = WebsiteFixture.CreateBrowser();
        using var __ = client;
        long userId = await SeedAndLoginAsync(client);

        // Nine pins seeded straight into the table, the tenth through the handler.
        for (int i = 0; i < 9; i++)
            await PinDirectlyAsync(userId, await SeedScoreAsync(userId, 4_402_000 + i));

        long tenth = await SeedScoreAsync(userId, 4_400_888);
        long eleventh = await SeedScoreAsync(userId, 4_400_999);

        Assert.That(await PinAsync(client, userId, tenth), Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await PinCountAsync(userId), Is.EqualTo(10));

        using var refused = await PostPinAsync(client, userId, eleventh, "Pin");
        string html = await refused.Content.ReadAsStringAsync();
        int pinsAfterRefusal = await PinCountAsync(userId);
        bool eleventhPinned = await IsPinnedAsync(eleventh);

        Assert.Multiple(() =>
        {
            Assert.That(refused.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(html, Does.Contain("You can pin up to 10 scores"));
            Assert.That(pinsAfterRefusal, Is.EqualTo(10), "the cap holds");
            Assert.That(eleventhPinned, Is.False);
        });

        // Unpinning makes room, and the 11th then lands.
        Assert.That(await UnpinAsync(client, userId, tenth), Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await PinAsync(client, userId, eleventh), Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await PinCountAsync(userId), Is.EqualTo(10));
    }

    [Test]
    public async Task Unpin_RemovesTheRowAndTheSection()
    {
        var (client, _) = WebsiteFixture.CreateBrowser();
        using var __ = client;
        long userId = await SeedAndLoginAsync(client);
        long scoreId = await SeedScoreAsync(userId, 4_401_212);

        await PinAsync(client, userId, scoreId);
        Assert.That(await PinCountAsync(userId), Is.EqualTo(1));

        Assert.That(await UnpinAsync(client, userId, scoreId), Is.EqualTo(HttpStatusCode.OK));

        string html = await GetProfileAsync(client, userId);
        int pins = await PinCountAsync(userId);

        Assert.Multiple(() =>
        {
            Assert.That(pins, Is.Zero);
            Assert.That(html, Does.Not.Contain(">Pinned</h2>"), "the section disappears once it is empty");
        });
    }

    [Test]
    public async Task Unpin_CannotTouchSomebodyElsesPin()
    {
        var (owner, _) = WebsiteFixture.CreateBrowser();
        using var __ = owner;
        long ownerId = await SeedAndLoginAsync(owner);
        long scoreId = await SeedScoreAsync(ownerId, 4_401_313);
        await PinAsync(owner, ownerId, scoreId);

        var (thief, _) = WebsiteFixture.CreateBrowser();
        using var ___ = thief;
        long thiefId = await SeedAndLoginAsync(thief);

        Assert.That(await UnpinAsync(thief, thiefId, scoreId), Is.EqualTo(HttpStatusCode.OK),
            "the no-op unpin still redirects rather than erroring");
        Assert.That(await IsPinnedAsync(scoreId), Is.True, "the owner's pin must survive");
    }

    [Test]
    public async Task Pin_RequiresSignIn()
    {
        var (owner, _) = WebsiteFixture.CreateBrowser();
        using var __ = owner;
        long ownerId = await SeedAndLoginAsync(owner);
        long scoreId = await SeedScoreAsync(ownerId, 4_401_414);

        // Anonymous browser: an antiforgery token from /login is a valid token, the handler still
        // refuses because there is no session.
        var (anon, _) = WebsiteFixture.CreateBrowser();
        using var ___ = anon;
        string token = await WebsiteFixture.GetAntiforgeryTokenAsync(anon, "/login");

        using var response = await anon.PostAsync($"/users/{ownerId}?handler=Pin",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["scoreId"] = scoreId.ToString(),
            }));

        bool pinned = await IsPinnedAsync(scoreId);

        Assert.Multiple(() =>
        {
            Assert.That(response.RequestMessage!.RequestUri!.AbsolutePath, Is.EqualTo("/login"));
            Assert.That(pinned, Is.False);
        });
    }

    [Test]
    public async Task Pin_WithoutAntiforgeryToken_IsRefused()
    {
        var (client, _) = WebsiteFixture.CreateBrowser();
        using var __ = client;
        long userId = await SeedAndLoginAsync(client);
        long scoreId = await SeedScoreAsync(userId, 4_401_818);

        // Razor Pages validates antiforgery on every POST handler; this proves the pin handler is
        // not somehow exempt (a bare cross-site form post must not be able to rearrange a profile).
        using var response = await client.PostAsync($"/users/{userId}?handler=Pin",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["scoreId"] = scoreId.ToString() }));

        bool pinned = await IsPinnedAsync(scoreId);

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(pinned, Is.False);
        });
    }

    [Test]
    public async Task PinnedSection_DropsScoresThatStopBeingVisible_ButKeepsThePin()
    {
        var (client, _) = WebsiteFixture.CreateBrowser();
        using var __ = client;
        long userId = await SeedAndLoginAsync(client);

        long unrankedLater = await SeedScoreAsync(userId, 4_401_515);
        long onHiddenSet = await SeedScoreAsync(userId, 4_401_616, beatmap: hideableBeatmapId);

        await PinAsync(client, userId, unrankedLater);
        await PinAsync(client, userId, onHiddenSet);
        Assert.That(PinnedSection(await GetProfileAsync(client, userId)), Does.Contain("4,401,515"));

        await using (var conn = Db())
        {
            await conn.OpenAsync();
            // An admin unranks the score; the set the other score lives on goes hidden.
            await conn.ExecuteAsync("UPDATE scores SET ranked = false WHERE id = @id", new { id = unrankedLater });
            await conn.ExecuteAsync("UPDATE beatmapsets SET status = 'hidden' WHERE id = @id", new { id = hideableSetId });
        }

        string html = await GetProfileAsync(client, userId);
        int survivingPins = await PinCountAsync(userId);

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Not.Contain(">Pinned</h2>"), "both pinned scores are now invisible");
            Assert.That(html, Does.Not.Contain("4,401,515"));
            Assert.That(html, Does.Not.Contain("4,401,616"));

            // The pins themselves survive (they still count against the cap, and unpin still works).
            Assert.That(survivingPins, Is.EqualTo(2));
        });

        // Re-pinning the now-unranked score reads as "already pinned", never as a fresh refusal,
        // and unpinning it works even though it is no longer rankable.
        Assert.That(await PinAsync(client, userId, unrankedLater), Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await UnpinAsync(client, userId, unrankedLater), Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await IsPinnedAsync(unrankedLater), Is.False);

        // Leave the shared set as this test found it.
        await using (var conn = Db())
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("UPDATE beatmapsets SET status = 'pending' WHERE id = @id", new { id = hideableSetId });
        }
    }

    [Test]
    public async Task DeletingAScore_CascadesItsPinAway()
    {
        var (client, _) = WebsiteFixture.CreateBrowser();
        using var __ = client;
        long userId = await SeedAndLoginAsync(client);
        long scoreId = await SeedScoreAsync(userId, 4_401_717);

        await PinAsync(client, userId, scoreId);
        Assert.That(await IsPinnedAsync(scoreId), Is.True);

        await using var conn = Db();
        await conn.OpenAsync();
        await conn.ExecuteAsync("DELETE FROM scores WHERE id = @id", new { id = scoreId });

        Assert.That(await IsPinnedAsync(scoreId), Is.False, "the pin row must cascade with its score");
    }

    // ---- helpers ----

    private static NpgsqlConnection Db() => new NpgsqlConnection(WebsiteFixture.ConnectionString);

    private static async Task<long> SeedAndLoginAsync(HttpClient client)
    {
        string username = "pinner_" + Guid.NewGuid().ToString("N")[..10];
        long id = await WebsiteFixture.SeedUserAsync(username, username + "@example.com", password, verified: true);
        await WebsiteFixture.LoginAndVerifyAsync(client, username, password);
        return id;
    }

    /// <summary>POSTs the Pin/Unpin handler with a freshly minted antiforgery token.</summary>
    private static async Task<HttpResponseMessage> PostPinAsync(HttpClient client, long profileId, long scoreId, string handler)
    {
        // /settings always carries forms (and therefore a token) for a signed-in user, whatever
        // state the profile page happens to be in.
        string token = await WebsiteFixture.GetAntiforgeryTokenAsync(client, "/settings");

        return await client.PostAsync($"/users/{profileId}?handler={handler}",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["scoreId"] = scoreId.ToString(),
            }));
    }

    private static async Task<HttpStatusCode> PinAsync(HttpClient client, long profileId, long scoreId)
    {
        using var response = await PostPinAsync(client, profileId, scoreId, "Pin");
        return response.StatusCode;
    }

    private static async Task<HttpStatusCode> UnpinAsync(HttpClient client, long profileId, long scoreId)
    {
        using var response = await PostPinAsync(client, profileId, scoreId, "Unpin");
        return response.StatusCode;
    }

    private static async Task<string> GetProfileAsync(HttpClient client, long userId)
    {
        using var response = await client.GetAsync($"/users/{userId}");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>The markup between the Pinned heading and the next section, so ordering and
    /// control assertions cannot be satisfied by a row in Best/Recent scores.</summary>
    private static string PinnedSection(string html)
    {
        int start = html.IndexOf(">Pinned</h2>", StringComparison.Ordinal);
        Assert.That(start, Is.GreaterThan(-1), "expected a Pinned section");

        int end = html.IndexOf(">Best scores<", StringComparison.Ordinal);
        Assert.That(end, Is.GreaterThan(start), "the Pinned section must sit above Best scores");

        return html[start..end];
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0;
        for (int i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static async Task<int> PinCountAsync(long userId)
    {
        await using var conn = Db();
        await conn.OpenAsync();
        return await conn.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM score_pins WHERE user_id = @userId", new { userId });
    }

    private static async Task<bool> IsPinnedAsync(long scoreId)
    {
        await using var conn = Db();
        await conn.OpenAsync();
        return await conn.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM score_pins WHERE score_id = @scoreId)", new { scoreId });
    }

    private static async Task PinDirectlyAsync(long userId, long scoreId)
    {
        await using var conn = Db();
        await conn.OpenAsync();
        await conn.ExecuteAsync(
            "INSERT INTO score_pins (score_id, user_id) VALUES (@scoreId, @userId)", new { scoreId, userId });
    }

    private static async Task<long> SeedScoreAsync(long userId, long totalScore, bool ranked = true, long? beatmap = null)
    {
        await using var conn = Db();
        await conn.OpenAsync();

        return await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO scores
                (user_id, beatmap_id, total_score, accuracy, completion, max_combo, rank, passed, ranked,
                 mods, statistics, maximum_statistics)
            VALUES
                (@userId, @beatmapId, @totalScore, 0.95, 0.98, 50, 'S', true, @ranked,
                 '[]'::jsonb, '{"great":90}'::jsonb, '{"great":100}'::jsonb)
            RETURNING id
            """,
            new { userId, beatmapId = beatmap ?? beatmapId, totalScore, ranked });
    }

    private static async Task<long> InsertSetAsync(NpgsqlConnection conn, long ownerId, string title, string artist, string status)
        => await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmapsets (owner_id, title, artist, status, submitted_at, updated_at)
            VALUES (@ownerId, @title, @artist, @status, now() - interval '6 days', now() - interval '6 days')
            RETURNING id
            """,
            new { ownerId, title, artist, status });

    private static async Task<long> InsertBeatmapAsync(NpgsqlConnection conn, long set)
        => await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmaps
                (set_id, version_name, checksum_md5, total_length_s, drain_length_s,
                 difficulty_rating, filename, word_count, char_count, wpm)
            VALUES
                (@set, 'type!beat', @checksum, 90, 80, 3.2, 'map.osu', 100, 500, 80)
            RETURNING id
            """,
            new { set, checksum = Guid.NewGuid().ToString("N") });
}
