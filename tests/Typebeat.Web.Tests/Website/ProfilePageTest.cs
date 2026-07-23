using System.Net;
using Dapper;
using Npgsql;
using Typebeat.Web.Auth;

namespace Typebeat.Web.Tests.Website;

/// <summary>
/// User profile page: header + stats card (global rank / totals / grade counts), the five
/// sections (best/recent scores, most played, maps, favourites), name→id canonical redirect,
/// hidden-map visibility, empty states, and the throttled users.last_visit touch.
///
/// Seeds its own users/sets on top of <see cref="PublicSiteSeed"/>, all with past
/// submitted_at offsets and small counters so the landing/listing sort and paging assertions
/// (newest = Fresh Drop, firsts by plays/favs, &lt;100 public sets) stay untouched.
/// </summary>
public class ProfilePageTest
{
    private const string star_username = "profile star";
    private const string star_password = "starpass-123456";
    private const string visitor_username = "visit tester";
    private const string visitor_password = "visitpass-123456";

    private static long starId;
    private static long rivalId;
    private static long visitorId;
    private static long idlerId;

    private static long starRankedSetId;
    private static long starSecondSetId;
    private static long starHiddenSetId;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        await PublicSiteSeed.EnsureSeededAsync();

        await using var conn = new NpgsqlConnection(WebsiteFixture.ConnectionString);
        await conn.OpenAsync();

        var passwords = new PasswordService();

        starId = await InsertUserAsync(conn, star_username, passwords.Hash(star_password));
        rivalId = await InsertUserAsync(conn, "rank rival");
        visitorId = await InsertUserAsync(conn, visitor_username, passwords.Hash(visitor_password));
        idlerId = await InsertUserAsync(conn, "idle observer");

        // user_stats drives the displayed Total score / Play count / Play time only. Global rank is
        // now the cumulative ranked-score metric (GlobalRanking), so it comes from real scores on
        // ranked maps below, NOT from these totals.
        await conn.ExecuteAsync(
            """
            INSERT INTO user_stats (user_id, play_count, total_score, play_time_s)
            VALUES (@starId, 42, 5000000, 7200), (@rivalId, 10, 9000000, 600)
            """,
            new { starId, rivalId });

        // The B-side is 'pending' (browsable, awaiting review): every profile section, maps,
        // scores, most played, must treat it like the ranked set. Its seeded ranked=true
        // scores model a set that took scores while ranked and was later unranked.
        starRankedSetId = await InsertSetAsync(conn, starId, "Star Anthem", "The Profile Makers", "ranked", days: -3);
        starSecondSetId = await InsertSetAsync(conn, starId, "Star Bside", "The Profile Makers", "pending", days: -4);
        starHiddenSetId = await InsertSetAsync(conn, starId, "Star Secret Stash", "Should Stay Off", "hidden", days: -3);

        long beatmapA = await InsertBeatmapAsync(conn, starRankedSetId, wpm: 90, stars: 3.6);
        long beatmapB = await InsertBeatmapAsync(conn, starSecondSetId, wpm: 60, stars: 2.4);

        // Map A: best 800k S (95%), a weaker 500k A that per-map-best folding must hide.
        await InsertScoreAsync(conn, starId, beatmapA, 800_000, 0.95, "S");
        await InsertScoreAsync(conn, starId, beatmapA, 500_000, 0.90, "A");
        // Map B: best 300k B (90%), plus a fail that may only surface under Recent scores.
        await InsertScoreAsync(conn, starId, beatmapB, 300_000, 0.90, "B");
        await InsertScoreAsync(conn, starId, beatmapB, 100_000, 0.40, "F", passed: false);

        // rival's 900k on ranked map A tops star's cumulative ranked score (800k, map B is
        // pending, so it does not count toward ranked score), making star global rank #2.
        await InsertScoreAsync(conn, rivalId, beatmapA, 900_000, 0.97, "S");

        await conn.ExecuteAsync(
            "INSERT INTO favourites (user_id, set_id) VALUES (@starId, @setId)",
            new { starId, setId = PublicSiteSeed.CoveredSetId });
    }

    [Test]
    public async Task Profile_RendersHeaderStatsAndSections()
    {
        using var response = await WebsiteFixture.Client.GetAsync($"/users/{starId}");
        string html = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            // Header: username, joined date, cover band + initial-letter avatar fallback.
            Assert.That(html, Does.Contain(star_username));
            Assert.That(html, Does.Contain("Joined"));
            Assert.That(html, Does.Contain("profile-cover--"));
            Assert.That(html, Does.Contain("profile-avatar--fallback"));

            // Stats card: dense rank #2 (rival holds #1), totals grid, computed accuracy
            // (mean over per-map bests: (0.95 + 0.90) / 2), grade counts S=1 B=1.
            Assert.That(html, Does.Contain("profile-rank__value\">#2<"));
            Assert.That(html, Does.Contain("5,000,000"));
            Assert.That(html, Does.Contain(">42<"));
            Assert.That(html, Does.Contain("2h 0m"));
            Assert.That(html, Does.Contain("92.50%"));
            Assert.That(html, Does.Contain("grade-counts"));

            // Best scores: the 800k S row links its set; per-map-best folding hides the 500k
            // play HERE (it still legitimately shows under Recent scores, so scope the check).
            string bestSection = html[html.IndexOf(">Best scores<", StringComparison.Ordinal)
                                      ..html.IndexOf(">Recent scores<", StringComparison.Ordinal)];
            Assert.That(bestSection, Does.Contain("score-row"));
            Assert.That(bestSection, Does.Contain("Star Anthem"));
            Assert.That(bestSection, Does.Contain("[The Profile Makers]"));
            Assert.That(bestSection, Does.Contain("800,000"));
            Assert.That(bestSection, Does.Not.Contain("500,000"));

            // Recent scores include the fail; most played counts every play (map A twice).
            Assert.That(html, Does.Contain("100,000"));
            Assert.That(html, Does.Contain("2 plays"));

            // Maps: own published sets as cards, the pending B-side included, wearing its
            // Pending pill; the hidden set never leaks to anonymous viewers.
            Assert.That(html, Does.Contain($"data-set-id=\"{starRankedSetId}\""));
            Assert.That(html, Does.Contain($"data-set-id=\"{starSecondSetId}\""));
            Assert.That(html, Does.Contain(">Pending</span>"));
            Assert.That(html, Does.Not.Contain("Star Secret Stash"));

            // Favourites: the favourited (covered) seed set renders as a card.
            Assert.That(html, Does.Contain($"data-set-id=\"{PublicSiteSeed.CoveredSetId}\""));
        });
    }

    [Test]
    public async Task Profile_OwnerSeesTheirHiddenMaps()
    {
        var (client, _) = WebsiteFixture.CreateBrowser();
        using var __ = client;

        await LoginAsync(client, star_username, star_password);

        using var response = await client.GetAsync($"/users/{starId}");
        string html = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(html, Does.Contain("Star Secret Stash"));
            Assert.That(html, Does.Contain($"data-set-id=\"{starHiddenSetId}\""));
        });
    }

    [Test]
    public async Task ProfileByName_RedirectsCanonicallyToIdUrl()
    {
        using var client = WebsiteFixture.CreateNoRedirectClient();

        // citext username lookup is case-insensitive; both spellings 302 to the id URL.
        using var exact = await client.GetAsync($"/users/{Uri.EscapeDataString(star_username)}");
        using var shouty = await client.GetAsync($"/users/{Uri.EscapeDataString(star_username.ToUpperInvariant())}");
        using var unknown = await client.GetAsync("/users/nobody-with-this-name");

        Assert.Multiple(() =>
        {
            Assert.That(exact.StatusCode, Is.EqualTo(HttpStatusCode.Found));
            Assert.That(exact.Headers.Location!.OriginalString, Is.EqualTo($"/users/{starId}"));
            Assert.That(shouty.StatusCode, Is.EqualTo(HttpStatusCode.Found));
            Assert.That(shouty.Headers.Location!.OriginalString, Is.EqualTo($"/users/{starId}"));
            Assert.That(unknown.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        });
    }

    [Test]
    public async Task Profile_EmptyStates_ForFreshUser()
    {
        // The fixture-seeded user has a zeroed user_stats row and no scores/maps/favourites.
        using var response = await WebsiteFixture.Client.GetAsync($"/users/{WebsiteFixture.SeededUserId}");
        string html = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(html, Does.Contain(WebsiteFixture.SeededUsername));
            Assert.That(html, Does.Contain("Unranked"));
            Assert.That(html, Does.Contain("No scores yet"));
            Assert.That(html, Does.Contain("No maps uploaded yet"));
            Assert.That(html, Does.Contain("No favourites yet"));
        });
    }

    [Test]
    public async Task Profile_UnknownId_Is404()
    {
        using var response = await WebsiteFixture.Client.GetAsync("/users/987654321");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task LastVisit_TouchedByAuthedPageView_NeverByAnonymous()
    {
        Assert.That(await LastVisitAsync(visitorId), Is.Null, "precondition: fresh account");

        var (client, _) = WebsiteFixture.CreateBrowser();
        using var __ = client;

        await LoginAsync(client, visitor_username, visitor_password);
        using (var page = await client.GetAsync("/"))
            Assert.That(page.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        Assert.That(await LastVisitAsync(visitorId), Is.Not.Null, "authed page view must touch last_visit");

        // Anonymous traffic touches nobody: the idler stays untouched after anonymous loads.
        using (var anon = await WebsiteFixture.Client.GetAsync($"/users/{idlerId}"))
            Assert.That(anon.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        Assert.That(await LastVisitAsync(idlerId), Is.Null, "anonymous requests must not touch last_visit");
    }

    // ---- helpers ----

    private static async Task LoginAsync(HttpClient client, string username, string password)
    {
        // Login is now a two-step flow (password → emailed code → session).
        using var _ = await WebsiteFixture.LoginAndVerifyAsync(client, username, password);
    }

    private static async Task<DateTime?> LastVisitAsync(long userId)
    {
        await using var conn = new NpgsqlConnection(WebsiteFixture.ConnectionString);
        await conn.OpenAsync();
        return await conn.ExecuteScalarAsync<DateTime?>(
            "SELECT last_visit FROM users WHERE id = @userId", new { userId });
    }

    private static async Task<long> InsertUserAsync(NpgsqlConnection conn, string username, string? hash = null)
        => await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO users (username, email, password_hash, country_code)
            VALUES (@username, @email, @hash, 'US')
            RETURNING id
            """,
            new { username, email = username.Replace(' ', '.') + "@example.com", hash = hash ?? "not-a-real-hash" });

    private static async Task<long> InsertSetAsync(NpgsqlConnection conn, long ownerId,
        string title, string artist, string status, int days)
        => await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmapsets (owner_id, title, artist, status, submitted_at, updated_at)
            VALUES (@ownerId, @title, @artist, @status, now() + @offset, now() + @offset)
            RETURNING id
            """,
            new { ownerId, title, artist, status, offset = TimeSpan.FromDays(days) });

    private static async Task<long> InsertBeatmapAsync(NpgsqlConnection conn, long setId, double wpm, double stars)
        => await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmaps
                (set_id, version_name, checksum_md5, total_length_s, drain_length_s,
                 difficulty_rating, filename, word_count, char_count, wpm)
            VALUES
                (@setId, 'type!beat', @checksum, 90, 80, @stars, 'map.osu', 100, 500, @wpm)
            RETURNING id
            """,
            new { setId, checksum = Guid.NewGuid().ToString("N"), stars, wpm });

    private static async Task InsertScoreAsync(NpgsqlConnection conn, long userId, long beatmapId,
        long totalScore, double accuracy, string rank, bool passed = true)
        => await conn.ExecuteAsync(
            """
            INSERT INTO scores
                (user_id, beatmap_id, total_score, accuracy, max_combo, rank, passed, ranked,
                 mods, statistics, maximum_statistics)
            VALUES
                (@userId, @beatmapId, @totalScore, @accuracy, 50, @rank, @passed, true,
                 '[]'::jsonb, '{"great":90,"ok":5,"meh":2,"miss":3}'::jsonb, '{"great":100}'::jsonb)
            """,
            new { userId, beatmapId, totalScore, accuracy, rank, passed });
}
