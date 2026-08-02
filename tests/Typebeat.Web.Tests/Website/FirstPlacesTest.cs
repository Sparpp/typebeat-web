using System.Net;
using System.Text.RegularExpressions;
using Dapper;
using Npgsql;

namespace Typebeat.Web.Tests.Website;

/// <summary>
/// First places (task 67): the profile section listing the ranked maps whose LEADERBOARD this user
/// currently tops, plus the count of them in the stats card.
///
/// The definition under test is deliberately not a formula of its own: a first place is "the map
/// board's rank-1 row is yours", so the tie-break case asserts against the BOARD ITSELF (the set
/// page's podium) rather than against a hardcoded winner, and the restricted-rival case asserts that
/// the profile and the board agree even where that is unflattering (boards do not delist restricted
/// accounts today, see <c>BeatmapLeaderboard</c>).
///
/// Every test seeds its own throwaway users, and its own beatmaps inside two shared sets, so no test
/// can move another's counts. Scores stay in a low band on purpose: a user's cumulative ranked score
/// feeds the GLOBAL rank shown on every profile, and <see cref="ProfilePageTest"/> asserts an exact
/// rank, so nothing seeded here may total 800,000 or more. NonParallelizable like the other
/// login-driven website fixtures (the pin-control case runs the two-step sign-in).
/// </summary>
[NonParallelizable]
public class FirstPlacesTest
{
    private const string password = "firstplace-4242";

    /// <summary>Shared 'ranked' set: every board under test is a difficulty of this one.</summary>
    private static long rankedSetId;

    /// <summary>Shared 'pending' set: its maps serve no ranked board, so they hold no first place.</summary>
    private static long pendingSetId;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        await PublicSiteSeed.EnsureSeededAsync();

        await using var conn = Db();
        await conn.OpenAsync();

        long mapperId = await InsertUserAsync(conn, "fp mapper " + Guid.NewGuid().ToString("N")[..8]);

        rankedSetId = await InsertSetAsync(conn, mapperId, "First Place Anthem", "ranked");
        pendingSetId = await InsertSetAsync(conn, mapperId, "First Place Waiting Room", "pending");
    }

    [Test]
    public async Task FirstPlaces_ListTheMapsYouTop_NewestFirst_AndCountThemInTheStats()
    {
        long holder = await SeedUserAsync("holder");
        long rival = await SeedUserAsync("rival");

        // Two boards this user tops (the older win first, so "newest first" has something to prove)
        // and one they do not.
        long heldOld = await NewMapAsync(rankedSetId);
        await SeedScoreAsync(holder, heldOld, 61_110, endedAt: DaysAgo(9));
        await SeedScoreAsync(rival, heldOld, 61_010);

        long heldNew = await NewMapAsync(rankedSetId);
        await SeedScoreAsync(holder, heldNew, 61_220, endedAt: DaysAgo(2));
        await SeedScoreAsync(rival, heldNew, 61_020);

        long lost = await NewMapAsync(rankedSetId);
        await SeedScoreAsync(rival, lost, 61_330);
        await SeedScoreAsync(holder, lost, 61_030);

        string html = await ProfileAsync(WebsiteFixture.Client, holder);
        string section = FirstPlacesSection(html);

        Assert.Multiple(() =>
        {
            Assert.That(section, Does.Contain("61,110"));
            Assert.That(section, Does.Contain("61,220"));
            Assert.That(section, Does.Not.Contain("61,030"), "a beaten score is not a first place");

            Assert.That(section.IndexOf("61,220", StringComparison.Ordinal),
                Is.LessThan(section.IndexOf("61,110", StringComparison.Ordinal)),
                "the newest first place must sit at the top of the section");

            Assert.That(RowCount(section), Is.EqualTo(2));
            Assert.That(StatCount(html), Is.EqualTo(2), "the stats card must agree with the section");
        });
    }

    [Test]
    public async Task FirstPlace_DisappearsAsSoonAsSomebodyBeatsIt()
    {
        long holder = await SeedUserAsync("loser");
        long usurper = await SeedUserAsync("usurper");

        long map = await NewMapAsync(rankedSetId);
        await SeedScoreAsync(holder, map, 62_110);

        string before = await ProfileAsync(WebsiteFixture.Client, holder);

        await SeedScoreAsync(usurper, map, 62_220);

        string after = await ProfileAsync(WebsiteFixture.Client, holder);
        string usurperHtml = await ProfileAsync(WebsiteFixture.Client, usurper);

        Assert.Multiple(() =>
        {
            Assert.That(FirstPlacesSection(before), Does.Contain("62,110"));
            Assert.That(StatCount(before), Is.EqualTo(1));

            // Computed on read: the loss lands on the very next view, with nothing to recompute.
            Assert.That(HasSection(after), Is.False, "the section goes away with the last first place");
            Assert.That(StatCount(after), Is.Zero);

            Assert.That(FirstPlacesSection(usurperHtml), Does.Contain("62,220"));
            Assert.That(StatCount(usurperHtml), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task TiedScores_FollowWhicheverSideTheLeaderboardItselfPuts_First()
    {
        long early = await SeedUserAsync("tie early");
        long late = await SeedUserAsync("tie late");

        // Identical totals on one board, seeded in a known order. Which of them is rank 1 is the
        // BOARD's business (its tie-break), so this test reads the answer off the set page's podium
        // instead of asserting one.
        long map = await NewMapAsync(rankedSetId);
        await SeedScoreAsync(early, map, 63_110);
        await SeedScoreAsync(late, map, 63_110);

        long boardTop = await BoardTopUserAsync(rankedSetId, map);

        long boardSecond = boardTop == early ? late : early;

        string topHtml = await ProfileAsync(WebsiteFixture.Client, boardTop);
        string secondHtml = await ProfileAsync(WebsiteFixture.Client, boardSecond);

        Assert.Multiple(() =>
        {
            Assert.That(boardTop, Is.AnyOf(early, late), "the podium must be one of the two tied users");

            Assert.That(FirstPlacesSection(topHtml), Does.Contain("63,110"));
            Assert.That(StatCount(topHtml), Is.EqualTo(1));

            Assert.That(HasSection(secondHtml), Is.False,
                "a tie has exactly one first place, and it belongs to whoever the board ranks first");
            Assert.That(StatCount(secondHtml), Is.Zero);
        });
    }

    [Test]
    public async Task FirstPlaces_IgnoreUnrankedSets_UnrankedScores_AndFails()
    {
        long holder = await SeedUserAsync("filtered");
        long rival = await SeedUserAsync("filter rival");

        // The one genuine first place, so the section renders and can be checked for what it omits.
        long held = await NewMapAsync(rankedSetId);
        await SeedScoreAsync(holder, held, 64_110);

        // A pending set serves no ranked board at all, however big the score on it.
        long pendingMap = await NewMapAsync(pendingSetId);
        await SeedScoreAsync(holder, pendingMap, 64_220);

        // An unranked-flagged score (unranked mod / non-default rate / admin unrank) is not on the
        // ranked board, so the rival's smaller ranked score tops that map.
        long unrankedScoreMap = await NewMapAsync(rankedSetId);
        await SeedScoreAsync(holder, unrankedScoreMap, 64_330, ranked: false);
        await SeedScoreAsync(rival, unrankedScoreMap, 64_030);

        // A failed play is not on any board either.
        long failedMap = await NewMapAsync(rankedSetId);
        await SeedScoreAsync(holder, failedMap, 64_440, passed: false);
        await SeedScoreAsync(rival, failedMap, 64_040);

        string html = await ProfileAsync(WebsiteFixture.Client, holder);
        string section = FirstPlacesSection(html);

        Assert.Multiple(() =>
        {
            Assert.That(section, Does.Contain("64,110"));
            Assert.That(section, Does.Not.Contain("64,220"), "a pending set has no ranked board");
            Assert.That(section, Does.Not.Contain("64,330"), "an unranked score is not on the board");
            Assert.That(section, Does.Not.Contain("64,440"), "a failed play is not on the board");

            Assert.That(RowCount(section), Is.EqualTo(1));
            Assert.That(StatCount(html), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task RestrictedRivalAtTheTop_KeepsTheFirstPlace_ExactlyAsTheBoardShows()
    {
        long holder = await SeedUserAsync("clean");
        long banned = await SeedUserAsync("banned", restricted: true);

        long map = await NewMapAsync(rankedSetId);
        await SeedScoreAsync(banned, map, 65_220);
        await SeedScoreAsync(holder, map, 65_110);

        long boardTop = await BoardTopUserAsync(rankedSetId, map);
        string html = await ProfileAsync(WebsiteFixture.Client, holder);

        using var bannedProfile = await WebsiteFixture.Client.GetAsync($"/users/{banned}");

        Assert.Multiple(() =>
        {
            // Boards do NOT delist restricted accounts (the global rankings do). First places are
            // defined as "the board's rank-1 row is yours", so they inherit that, unflattering as it
            // is: the honest player behind a restricted score holds no first place, which is exactly
            // what the map page shows. If boards start delisting, BeatmapLeaderboard is the one
            // place to change and this expectation flips with it.
            Assert.That(boardTop, Is.EqualTo(banned), "precondition: the board still ranks the restricted score first");

            Assert.That(HasSection(html), Is.False);
            Assert.That(StatCount(html), Is.Zero);

            // And the restricted holder's own profile is unreachable, so the first place is listed
            // nowhere at all.
            Assert.That(bannedProfile.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        });
    }

    [Test]
    public async Task FirstPlaces_AreCappedWithAnHonestCountOfTheRest()
    {
        const int held = 22;
        long hoarder = await SeedUserAsync("hoarder");

        for (int i = 0; i < held; i++)
            await SeedScoreAsync(hoarder, await NewMapAsync(rankedSetId), 30_000 + i);

        string html = await ProfileAsync(WebsiteFixture.Client, hoarder);
        string section = FirstPlacesSection(html);

        Assert.Multiple(() =>
        {
            Assert.That(RowCount(section), Is.EqualTo(20), "the section caps at 20 rows");
            Assert.That(section, Does.Contain($"Showing 20 of {held} first places."));
            Assert.That(StatCount(html), Is.EqualTo(held), "the stats card counts every first place, uncapped");
        });
    }

    [Test]
    public async Task NoFirstPlaces_RendersNoSectionAndAZeroStat()
    {
        long nobody = await SeedUserAsync("nobody");
        long rival = await SeedUserAsync("nobody rival");

        long map = await NewMapAsync(rankedSetId);
        await SeedScoreAsync(rival, map, 66_220);
        await SeedScoreAsync(nobody, map, 66_110);

        string html = await ProfileAsync(WebsiteFixture.Client, nobody);

        Assert.Multiple(() =>
        {
            Assert.That(HasSection(html), Is.False);
            Assert.That(html, Does.Not.Contain(">First places</h2>"));
            Assert.That(StatCount(html), Is.Zero, "the stat stays on the card, reading zero");

            // The score is still a real play: it shows under the other sections.
            Assert.That(html, Does.Contain("66,110"));
        });
    }

    [Test]
    public async Task FirstPlaceRows_CarryThePinControlForTheirOwnerOnly()
    {
        var (client, _) = WebsiteFixture.CreateBrowser();
        using var __ = client;

        string username = "fp pinner " + Guid.NewGuid().ToString("N")[..8];
        long owner = await WebsiteFixture.SeedUserAsync(username, username.Replace(' ', '.') + "@example.com", password, verified: true);
        await WebsiteFixture.LoginAndVerifyAsync(client, username, password);

        long map = await NewMapAsync(rankedSetId);
        long scoreId = await SeedScoreAsync(owner, map, 67_110);

        string ownHtml = await ProfileAsync(client, owner);
        string anonHtml = await ProfileAsync(WebsiteFixture.Client, owner);

        // Pinning the score (the handler has its own coverage in PinnedScoresTest) must flip the
        // control in THIS section too, which is what proves the rows are wired like Best/Recent.
        await using (var conn = Db())
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync(
                "INSERT INTO score_pins (score_id, user_id) VALUES (@scoreId, @owner)", new { scoreId, owner });
        }

        string pinnedHtml = await ProfileAsync(client, owner);

        Assert.Multiple(() =>
        {
            Assert.That(FirstPlacesSection(ownHtml), Does.Contain("handler=Pin"));
            Assert.That(FirstPlacesSection(anonHtml), Does.Not.Contain("handler=Pin"),
                "a visitor gets no pin control on somebody else's first place");

            Assert.That(FirstPlacesSection(pinnedHtml), Does.Contain("handler=Unpin"));
        });
    }

    // ---- helpers ----

    private static NpgsqlConnection Db() => new NpgsqlConnection(WebsiteFixture.ConnectionString);

    private static DateTime DaysAgo(int days) => DateTime.UtcNow.AddDays(-days);

    private static async Task<string> ProfileAsync(HttpClient client, long userId)
    {
        using var response = await client.GetAsync($"/users/{userId}");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        return await response.Content.ReadAsStringAsync();
    }

    private static bool HasSection(string html) => html.Contains(">First places</h2>", StringComparison.Ordinal);

    /// <summary>The markup of the First places section alone, so a row from Best/Recent scores can
    /// never satisfy an assertion about it.</summary>
    private static string FirstPlacesSection(string html)
    {
        int start = html.IndexOf(">First places</h2>", StringComparison.Ordinal);
        Assert.That(start, Is.GreaterThan(-1), "expected a First places section");

        int end = html.IndexOf(">Recent scores<", StringComparison.Ordinal);
        Assert.That(end, Is.GreaterThan(start), "the First places section must sit above Recent scores");

        return html[start..end];
    }

    private static int RowCount(string section)
        => Regex.Matches(section, "<div class=\"score-row\">").Count;

    /// <summary>The First places number on the stats card.</summary>
    private static int StatCount(string html)
    {
        var match = Regex.Match(html, "First places</span><span class=\"stat-row__value\">([\\d,]+)</span>");
        Assert.That(match.Success, Is.True, "expected a First places stat row on the profile");

        return int.Parse(match.Groups[1].Value.Replace(",", ""));
    }

    /// <summary>
    /// Who the LEADERBOARD itself puts at rank 1 on a map, read off the set page's podium. The point
    /// of going through the page is that it is the board's own rendering, not a second opinion about
    /// what the board would say.
    /// </summary>
    private static async Task<long> BoardTopUserAsync(long setId, long beatmapId)
    {
        using var response = await WebsiteFixture.Client.GetAsync($"/beatmapsets/{setId}?diff={beatmapId}");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        string html = await response.Content.ReadAsStringAsync();
        var match = Regex.Match(html, "podium__name\" href=\"/users/(\\d+)\"");
        Assert.That(match.Success, Is.True, "expected a podium on the map's leaderboard");

        return long.Parse(match.Groups[1].Value);
    }

    private static async Task<long> SeedUserAsync(string label, bool restricted = false)
    {
        await using var conn = Db();
        await conn.OpenAsync();
        return await InsertUserAsync(conn, $"fp {label} " + Guid.NewGuid().ToString("N")[..6], restricted);
    }

    private static async Task<long> InsertUserAsync(NpgsqlConnection conn, string username, bool restricted = false)
        => await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO users (username, email, password_hash, country_code, restricted)
            VALUES (@username, @email, 'not-a-real-hash', 'US', @restricted)
            RETURNING id
            """,
            new { username, email = Guid.NewGuid().ToString("N") + "@example.com", restricted });

    private static async Task<long> InsertSetAsync(NpgsqlConnection conn, long ownerId, string title, string status)
        => await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmapsets (owner_id, title, artist, status, submitted_at, updated_at)
            VALUES (@ownerId, @title, 'The Chart Toppers', @status, now() - interval '6 days', now() - interval '6 days')
            RETURNING id
            """,
            new { ownerId, title, status });

    /// <summary>A fresh difficulty, i.e. a fresh leaderboard, inside one of the shared sets.</summary>
    private static async Task<long> NewMapAsync(long setId)
    {
        await using var conn = Db();
        await conn.OpenAsync();

        return await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmaps
                (set_id, version_name, checksum_md5, total_length_s, drain_length_s,
                 difficulty_rating, filename, word_count, char_count, wpm)
            VALUES
                (@setId, 'type!beat', @checksum, 90, 80, 3.0, 'map.osu', 100, 500, 80)
            RETURNING id
            """,
            new { setId, checksum = Guid.NewGuid().ToString("N") });
    }

    private static async Task<long> SeedScoreAsync(long userId, long beatmapId, long totalScore,
        bool ranked = true, bool passed = true, DateTime? endedAt = null)
    {
        await using var conn = Db();
        await conn.OpenAsync();

        return await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO scores
                (user_id, beatmap_id, total_score, accuracy, completion, max_combo, rank, passed, ranked,
                 mods, statistics, maximum_statistics, ended_at)
            VALUES
                (@userId, @beatmapId, @totalScore, 0.95, 0.98, 50, 'S', @passed, @ranked,
                 '[]'::jsonb, '{"great":90}'::jsonb, '{"great":100}'::jsonb, @endedAt)
            RETURNING id
            """,
            new { userId, beatmapId, totalScore, ranked, passed, endedAt = endedAt ?? DateTime.UtcNow });
    }
}
