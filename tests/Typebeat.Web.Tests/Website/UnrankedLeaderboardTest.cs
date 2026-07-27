using System.Net;
using System.Net.Http.Headers;
using Dapper;
using Newtonsoft.Json.Linq;
using Npgsql;
using Typebeat.Web.Auth;
using Typebeat.Web.Data;

namespace Typebeat.Web.Tests.Website;

/// <summary>
/// The game-facing leaderboard endpoint's board selection (backlog 53): a map whose set is
/// published but NOT ranked ('pending', or the creator's 'unranked') serves the website's
/// UNRANKED board through <c>GET /api/v2/beatmaps/{id}/scores</c>, instead of the empty
/// collection it used to return.
///
/// <para>
/// "The website's unranked board" is the /beatmapsets/{id}?board=unranked tab: passed plays with
/// <c>scores.ranked = false</c>, best per user (DISTINCT ON), ordered total_score DESC then id ASC,
/// capped at 50. The same predicate, applied to a non-ranked map, is everything anybody has typed
/// there, since every play on a non-ranked set is stored unranked by construction.
/// </para>
///
/// <para>
/// The two directions that must NOT leak are pinned here as well: a ranked map's board is still
/// ranked rows only (an unranked row on a ranked map, a deploy-window clamp or an unranked mod,
/// stays off it), and a non-ranked map's board is still unranked rows only (a set's ranked-era
/// scores stay buried while it is un-ranked).
/// </para>
/// </summary>
[TestFixture]
[NonParallelizable]
public class UnrankedLeaderboardTest
{
    private static NpgsqlDataSource dataSource = null!;

    private static string bearer = null!;
    private static long callerId;
    private static long rivalId;
    private static long ownerId;

    private static long pendingBeatmapId;
    private static long unrankedBeatmapId;
    private static long rankedSetId;
    private static long rankedBeatmapId;
    private static long hiddenBeatmapId;

    private static long callerBestUnrankedScoreId;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        await PublicSiteSeed.EnsureSeededAsync();

        dataSource = NpgsqlDataSource.Create(WebsiteFixture.ConnectionString);

        await using var conn = await dataSource.OpenConnectionAsync();

        ownerId = PublicSiteSeed.MapperId;
        callerId = await InsertUserAsync(conn, "board caller");
        rivalId = await InsertUserAsync(conn, "board rival");

        bearer = (await new TokenService(new Db(dataSource)).IssueAsync(callerId)).AccessToken;

        // ---- a pending set: awaiting review, takes plays, ranks nothing ----
        long pendingSetId = await InsertSetAsync(conn, "Board Pending", "pending");
        pendingBeatmapId = await InsertBeatmapAsync(conn, pendingSetId);

        // The caller has two unranked plays; only the better may surface (best per user).
        callerBestUnrankedScoreId = await InsertScoreAsync(conn, callerId, pendingBeatmapId, 800_000, ranked: false, passed: true);
        await InsertScoreAsync(conn, callerId, pendingBeatmapId, 400_000, ranked: false, passed: true);

        // A rival tops the board.
        await InsertScoreAsync(conn, rivalId, pendingBeatmapId, 900_000, ranked: false, passed: true);

        // A failed play is not a leaderboard row on any board.
        await InsertScoreAsync(conn, ownerId, pendingBeatmapId, 950_000, ranked: false, passed: false);

        // A ranked-era row left over from before this set was un-ranked: it keeps its flag, and
        // the unranked board must not resurrect it (the website buries it the same way).
        await InsertScoreAsync(conn, ownerId, pendingBeatmapId, 999_000, ranked: true, passed: true);

        // ---- a creator-marked 'unranked' set: published, playable, never rankable ----
        long unrankedSetId = await InsertSetAsync(conn, "Board Never Ranking", "unranked");
        unrankedBeatmapId = await InsertBeatmapAsync(conn, unrankedSetId);
        await InsertScoreAsync(conn, rivalId, unrankedBeatmapId, 123_456, ranked: false, passed: true);

        // ---- a ranked set carrying BOTH kinds of row ----
        rankedSetId = await InsertSetAsync(conn, "Board Ranked", "ranked");
        rankedBeatmapId = await InsertBeatmapAsync(conn, rankedSetId);
        await InsertScoreAsync(conn, callerId, rankedBeatmapId, 700_000, ranked: true, passed: true);
        await InsertScoreAsync(conn, rivalId, rankedBeatmapId, 990_000, ranked: false, passed: true);

        // ---- a hidden set: not published at all, so no board of any kind ----
        long hiddenSetId = await InsertSetAsync(conn, "Board Hidden", "hidden");
        hiddenBeatmapId = await InsertBeatmapAsync(conn, hiddenSetId);
        await InsertScoreAsync(conn, rivalId, hiddenBeatmapId, 111_111, ranked: false, passed: true);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (dataSource != null)
            await dataSource.DisposeAsync();
    }

    /// <summary>
    /// The headline behaviour: a pending map answers with its unranked board, best per user, in
    /// total-score order, with failed plays and ranked-era rows excluded, and every row flagged
    /// <c>ranked: false</c> so the client can cue it as an unranked board.
    /// </summary>
    [Test]
    public async Task PendingMap_ServesTheUnrankedBoard()
    {
        var board = await GetLeaderboardAsync(pendingBeatmapId);
        var scores = (JArray)board["scores"]!;

        Assert.Multiple(() =>
        {
            Assert.That((int)board["score_count"]!, Is.EqualTo(2), "two distinct users with a passed unranked play");
            Assert.That(scores, Has.Count.EqualTo(2));

            Assert.That((long)scores[0]["user_id"]!, Is.EqualTo(rivalId));
            Assert.That((long)scores[0]["total_score"]!, Is.EqualTo(900_000));

            // Best per user: the caller's 400,000 is dropped in favour of their 800,000.
            Assert.That((long)scores[1]["user_id"]!, Is.EqualTo(callerId));
            Assert.That((long)scores[1]["total_score"]!, Is.EqualTo(800_000));

            Assert.That(scores.Select(s => (bool)s["ranked"]!), Is.All.False, "unranked-board rows are flagged on the wire");

            // The 950,000 failed play and the 999,000 ranked-era play are both absent.
            Assert.That(scores.Select(s => (long)s["total_score"]!), Does.Not.Contain(950_000L));
            Assert.That(scores.Select(s => (long)s["total_score"]!), Does.Not.Contain(999_000L));
        });
    }

    /// <summary>
    /// The caller's own row and position come from the SAME board, so an unranked play is never
    /// positioned against ranked ones (or vice versa).
    /// </summary>
    [Test]
    public async Task PendingMap_UserScoreIsTheCallersBestUnrankedPlay()
    {
        var board = await GetLeaderboardAsync(pendingBeatmapId);
        var userScore = board["user_score"]!;

        Assert.Multiple(() =>
        {
            Assert.That(userScore.Type, Is.Not.EqualTo(JTokenType.Null));
            Assert.That((long)userScore["score"]!["id"]!, Is.EqualTo(callerBestUnrankedScoreId));
            Assert.That((long)userScore["score"]!["total_score"]!, Is.EqualTo(800_000));
            Assert.That((bool)userScore["score"]!["ranked"]!, Is.False);
            Assert.That((int)userScore["position"]!, Is.EqualTo(2), "second on the unranked board, behind the rival");
        });
    }

    /// <summary>A creator's not-for-ranking set is served exactly like a pending one.</summary>
    [Test]
    public async Task CreatorUnrankedMap_ServesTheUnrankedBoard()
    {
        var board = await GetLeaderboardAsync(unrankedBeatmapId);
        var scores = (JArray)board["scores"]!;

        Assert.Multiple(() =>
        {
            Assert.That((int)board["score_count"]!, Is.EqualTo(1));
            Assert.That(scores, Has.Count.EqualTo(1));
            Assert.That((long)scores[0]["total_score"]!, Is.EqualTo(123_456));
            Assert.That((bool)scores[0]["ranked"]!, Is.False);
            Assert.That(board["user_score"]!.Type, Is.EqualTo(JTokenType.Null), "the caller has not played it");
        });
    }

    /// <summary>
    /// The no-leak guard in the other direction: a RANKED map's board is what it always was, ranked
    /// rows only. The rival's 990,000 unranked row on it (a clamped or unranked-mod play) beats
    /// every ranked score there and still must not appear.
    /// </summary>
    [Test]
    public async Task RankedMap_BoardIsUnchangedAndTakesNoUnrankedRows()
    {
        var board = await GetLeaderboardAsync(rankedBeatmapId);
        var scores = (JArray)board["scores"]!;

        Assert.Multiple(() =>
        {
            Assert.That((int)board["score_count"]!, Is.EqualTo(1));
            Assert.That(scores, Has.Count.EqualTo(1));
            Assert.That((long)scores[0]["user_id"]!, Is.EqualTo(callerId));
            Assert.That((long)scores[0]["total_score"]!, Is.EqualTo(700_000));
            Assert.That((bool)scores[0]["ranked"]!, Is.True, "ranked boards still report ranked: true");

            Assert.That((long)board["user_score"]!["score"]!["total_score"]!, Is.EqualTo(700_000));
            Assert.That((int)board["user_score"]!["position"]!, Is.EqualTo(1));
        });
    }

    /// <summary>
    /// The seeded ranked leaderboard map is the regression anchor for "byte-identical to today":
    /// the cheat suspect's unranked 999,999,999 stays off it, and the board is still the three
    /// ranked typists, best per user, in score order.
    /// </summary>
    [Test]
    public async Task SeededRankedBoard_IsUntouched()
    {
        var board = await GetLeaderboardAsync(PublicSiteSeed.LeaderboardBeatmapId);
        var scores = (JArray)board["scores"]!;

        Assert.Multiple(() =>
        {
            Assert.That((int)board["score_count"]!, Is.EqualTo(3));
            Assert.That(scores.Select(s => (long)s["total_score"]!), Is.EqualTo(new[] { 900_000L, 700_000L, 500_000L }));
            Assert.That(scores.Select(s => (long)s["user_id"]!), Does.Not.Contain(PublicSiteSeed.CheatSuspectId));
        });
    }

    /// <summary>
    /// Hidden (and removed) sets are not published: they take no scores through submission and get
    /// no board here either, so a private map's plays cannot be enumerated through the API.
    /// </summary>
    [Test]
    public async Task HiddenMap_HasNoBoardAtAll()
    {
        var board = await GetLeaderboardAsync(hiddenBeatmapId);

        Assert.Multiple(() =>
        {
            Assert.That((int)board["score_count"]!, Is.EqualTo(0));
            Assert.That((JArray)board["scores"]!, Is.Empty);
            Assert.That(board["user_score"]!.Type, Is.EqualTo(JTokenType.Null));
        });
    }

    /// <summary>A beatmap id that resolves to nothing is an empty board, not an error.</summary>
    [Test]
    public async Task UnknownMap_IsAnEmptyBoard()
    {
        var board = await GetLeaderboardAsync(999_999_999);

        Assert.Multiple(() =>
        {
            Assert.That((int)board["score_count"]!, Is.EqualTo(0));
            Assert.That((JArray)board["scores"]!, Is.Empty);
        });
    }

    /// <summary>
    /// Parity anchor for the semantics being mirrored: the website's Unranked tab on the ranked set
    /// lists exactly the ranked=false passed rows (the rival's 990,000) and none of the ranked ones,
    /// which is the predicate the endpoint now applies to non-ranked maps.
    /// </summary>
    [Test]
    public async Task SiteUnrankedTab_ListsTheSameRowsThePredicateSelects()
    {
        using var rankedTab = await WebsiteFixture.Client.GetAsync($"/beatmapsets/{rankedSetId}?diff={rankedBeatmapId}&board=ranked");
        using var unrankedTab = await WebsiteFixture.Client.GetAsync($"/beatmapsets/{rankedSetId}?diff={rankedBeatmapId}&board=unranked");

        string ranked = await rankedTab.Content.ReadAsStringAsync();
        string unranked = await unrankedTab.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(ranked, Does.Contain("board caller"));
            Assert.That(ranked, Does.Not.Contain("board rival"));

            Assert.That(unranked, Does.Contain("board rival"));
            Assert.That(unranked, Does.Not.Contain("board caller"));
        });
    }

    // ---- helpers ----

    private static async Task<JObject> GetLeaderboardAsync(long beatmapId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v2/beatmaps/{beatmapId}/scores");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

        using var response = await WebsiteFixture.Client.SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), "leaderboard");

        return JObject.Parse(await response.Content.ReadAsStringAsync());
    }

    private static async Task<long> InsertUserAsync(NpgsqlConnection conn, string username)
        => await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO users (username, email, password_hash, country_code)
            VALUES (@username, @username || '@boards.example', 'not-a-real-hash', 'US')
            RETURNING id
            """,
            new { username });

    private static async Task<long> InsertSetAsync(NpgsqlConnection conn, string title, string status)
        => await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmapsets (owner_id, title, artist, status, submitted_at, updated_at)
            VALUES (@ownerId, @title, 'The Boarders', @status, now() - interval '9 days', now() - interval '9 days')
            RETURNING id
            """,
            new { ownerId, title, status });

    private static async Task<long> InsertBeatmapAsync(NpgsqlConnection conn, long setId)
        => await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmaps (set_id, version_name, checksum_md5, total_length_s, drain_length_s, difficulty_rating, filename)
            VALUES (@setId, 'type!beat', @checksum, 60, 0, 2.0, 'map.osu')
            RETURNING id
            """,
            new { setId, checksum = Guid.NewGuid().ToString("N") });

    private static async Task<long> InsertScoreAsync(NpgsqlConnection conn, long userId, long beatmapId, long totalScore, bool ranked, bool passed)
        => await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO scores
                (user_id, beatmap_id, total_score, accuracy, completion, max_combo, rank, passed, ranked,
                 mods, statistics, maximum_statistics)
            VALUES
                (@userId, @beatmapId, @totalScore, 0.95, 0.97, 50, @rank, @passed, @ranked,
                 '[]'::jsonb, '{"great":100,"ok":5,"meh":2,"miss":3}'::jsonb, '{"great":110}'::jsonb)
            RETURNING id
            """,
            new { userId, beatmapId, totalScore, ranked, passed, rank = passed ? "S" : "F" });
}
