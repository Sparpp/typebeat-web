using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Dapper;
using Newtonsoft.Json.Linq;
using Npgsql;
using Typebeat.Web.Auth;
using Typebeat.Web.Data;
using Typebeat.Web.Scoring;

namespace Typebeat.Web.Tests.Website;

/// <summary>
/// Backlog 510: a RESTRICTED player's plays are on no beatmap board (<see cref="BeatmapLeaderboard.PlayerListed"/>).
/// Every surface built from the board fragments drops them together (the game client's board, its
/// participant count and the caller's position, the set page's board, the standing a submit
/// reports), and the per-score surfaces (the /scores/{id} permalink, the replay download) treat
/// their scores as not found. The rule is read per request from <c>users.restricted</c>, never
/// stored, so every test also unrestricts the player and asserts the rows come straight back.
///
/// <para>First places are pinned in <see cref="FirstPlacesTest"/>, next to the rest of their
/// definition. Each test seeds its own map and users, and every score stays in a low band (under
/// 800,000) because <see cref="ProfilePageTest"/> asserts an exact cumulative-score rank.</para>
/// </summary>
[TestFixture]
[NonParallelizable]
public class RestrictedBoardsTest
{
    private static NpgsqlDataSource dataSource = null!;
    private static TokenService tokens = null!;

    private static long rankedSetId;
    private static long pendingSetId;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        await PublicSiteSeed.EnsureSeededAsync();

        dataSource = NpgsqlDataSource.Create(WebsiteFixture.ConnectionString);
        tokens = new TokenService(new Db(dataSource));

        await using var conn = await dataSource.OpenConnectionAsync();

        rankedSetId = await insertSetAsync(conn, "Delisted Anthem", "ranked");
        pendingSetId = await insertSetAsync(conn, "Delisted Waiting Room", "pending");
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (dataSource != null)
            await dataSource.DisposeAsync();
    }

    [Test]
    public async Task GameBoard_DropsARestrictedPlayer_FromTheRowsTheCountAndThePosition()
    {
        var board = await seedBoardAsync(rankedSetId, ranked: true);
        string bearer = (await tokens.IssueAsync(board.Caller)).AccessToken;

        await setRestrictedAsync(board.Banned, true);
        var restricted = await getBoardAsync(board.BeatmapId, bearer);

        await setRestrictedAsync(board.Banned, false);
        var restored = await getBoardAsync(board.BeatmapId, bearer);

        Assert.Multiple(() =>
        {
            Assert.That(userIds(restricted), Is.EqualTo(new[] { board.Caller, board.Third }),
                "the restricted player's 71,900 is not on the board");
            Assert.That((int)restricted["score_count"]!, Is.EqualTo(2), "nor counted as a participant");
            Assert.That((int)restricted["user_score"]!["position"]!, Is.EqualTo(1),
                "nor counted ahead of the caller");

            Assert.That(userIds(restored), Is.EqualTo(new[] { board.Banned, board.Caller, board.Third }),
                "unrestricting puts the row back where it was");
            Assert.That((int)restored["score_count"]!, Is.EqualTo(3));
            Assert.That((int)restored["user_score"]!["position"]!, Is.EqualTo(2));
        });
    }

    [Test]
    public async Task UnrankedBoard_DropsARestrictedPlayer_Too()
    {
        var board = await seedBoardAsync(pendingSetId, ranked: false);
        string bearer = (await tokens.IssueAsync(board.Caller)).AccessToken;

        await setRestrictedAsync(board.Banned, true);
        var restricted = await getBoardAsync(board.BeatmapId, bearer);

        await setRestrictedAsync(board.Banned, false);
        var restored = await getBoardAsync(board.BeatmapId, bearer);

        Assert.Multiple(() =>
        {
            Assert.That(userIds(restricted), Is.EqualTo(new[] { board.Caller, board.Third }));
            Assert.That((int)restricted["score_count"]!, Is.EqualTo(2));
            Assert.That((int)restricted["user_score"]!["position"]!, Is.EqualTo(1));

            Assert.That(userIds(restored), Is.EqualTo(new[] { board.Banned, board.Caller, board.Third }));
        });
    }

    [Test]
    public async Task SetPageBoard_DropsARestrictedPlayer()
    {
        var board = await seedBoardAsync(rankedSetId, ranked: true);
        string url = $"/beatmapsets/{rankedSetId}?diff={board.BeatmapId}";

        await setRestrictedAsync(board.Banned, true);
        string restricted = await getHtmlAsync(url);

        await setRestrictedAsync(board.Banned, false);
        string restored = await getHtmlAsync(url);

        Assert.Multiple(() =>
        {
            Assert.That(restricted, Does.Not.Contain(board.BannedName), "the restricted player has no row on the page");
            Assert.That(podiumTop(restricted), Is.EqualTo(board.Caller), "the best listed player tops the podium");
            Assert.That(restricted, Does.Contain(board.ThirdName));

            Assert.That(restored, Does.Contain(board.BannedName));
            Assert.That(podiumTop(restored), Is.EqualTo(board.Banned));
        });
    }

    [Test]
    public async Task SubmittersStanding_DoesNotCountARestrictedRowAboveThem()
    {
        var board = await seedBoardAsync(rankedSetId, ranked: true);

        await setRestrictedAsync(board.Banned, true);
        var restricted = await standingAsync(board.BeatmapId, board.Caller);

        await setRestrictedAsync(board.Banned, false);
        var restored = await standingAsync(board.BeatmapId, board.Caller);

        Assert.Multiple(() =>
        {
            Assert.That(restricted?.Position, Is.EqualTo(1), "the reported position skips the restricted 71,900");
            Assert.That(restored?.Position, Is.EqualTo(2));
            Assert.That(restricted?.BestScoreId, Is.EqualTo(board.CallerScoreId));
        });
    }

    [Test]
    public async Task ScorePermalink_IsNotFoundForARestrictedPlayersScore()
    {
        var board = await seedBoardAsync(rankedSetId, ranked: true);
        using var client = WebsiteFixture.CreateNoRedirectClient();

        await setRestrictedAsync(board.Banned, true);
        using var restricted = await client.GetAsync($"/scores/{board.BannedScoreId}");
        using var listed = await client.GetAsync($"/scores/{board.CallerScoreId}");

        await setRestrictedAsync(board.Banned, false);
        using var restored = await client.GetAsync($"/scores/{board.BannedScoreId}");

        Assert.Multiple(() =>
        {
            Assert.That(restricted.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(listed.StatusCode, Is.EqualTo(HttpStatusCode.MovedPermanently),
                "a listed player's score on the same map still lands");
            Assert.That(restored.StatusCode, Is.EqualTo(HttpStatusCode.MovedPermanently));
            Assert.That(restored.Headers.Location!.OriginalString, Is.EqualTo($"/beatmapsets/{rankedSetId}"));
        });
    }

    [Test]
    public async Task ReplayDownload_IsNotFoundForARestrictedPlayersScore()
    {
        var board = await seedBoardAsync(rankedSetId, ranked: true);

        // Uploaded while the player was still in good standing, as a real replay would be.
        await uploadReplayAsync(board.BannedScoreId, (await tokens.IssueAsync(board.Banned)).AccessToken);

        await setRestrictedAsync(board.Banned, true);
        using var restricted = await WebsiteFixture.Client.GetAsync($"/api/v2/scores/{board.BannedScoreId}/replay");

        await setRestrictedAsync(board.Banned, false);
        using var restored = await WebsiteFixture.Client.GetAsync($"/api/v2/scores/{board.BannedScoreId}/replay");

        Assert.Multiple(() =>
        {
            Assert.That(restricted.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(restored.StatusCode, Is.EqualTo(HttpStatusCode.OK), "unrestricting serves the replay again");
        });
    }

    // ---- helpers ----

    /// <summary>One fresh board: a soon-to-be-restricted player on top, the caller second, a third behind.</summary>
    private sealed record SeededBoard(
        long BeatmapId, long Banned, string BannedName, long BannedScoreId,
        long Caller, long CallerScoreId, long Third, string ThirdName);

    private static async Task<SeededBoard> seedBoardAsync(long setId, bool ranked)
    {
        await using var conn = await dataSource.OpenConnectionAsync();

        string suffix = Guid.NewGuid().ToString("N")[..6];
        string bannedName = "delist banned " + suffix;
        string thirdName = "delist third " + suffix;

        long beatmapId = await insertBeatmapAsync(conn, setId);
        long banned = await insertUserAsync(conn, bannedName);
        long caller = await insertUserAsync(conn, "delist caller " + suffix);
        long third = await insertUserAsync(conn, thirdName);

        long bannedScore = await insertScoreAsync(conn, banned, beatmapId, 71_900, ranked);
        long callerScore = await insertScoreAsync(conn, caller, beatmapId, 71_800, ranked);
        await insertScoreAsync(conn, third, beatmapId, 71_700, ranked);

        await WebsiteFixture.EvictAllAsync();

        return new SeededBoard(beatmapId, banned, bannedName, bannedScore, caller, callerScore, third, thirdName);
    }

    /// <summary>
    /// What the admin tool's restrict/unrestrict does to the account row (its score un-ranking is
    /// left out on purpose: the board rule under test must not depend on it), then the eviction a
    /// SQL write owes the caches.
    /// </summary>
    private static async Task setRestrictedAsync(long userId, bool restricted)
    {
        await using (var conn = await dataSource.OpenConnectionAsync())
        {
            await conn.ExecuteAsync("UPDATE users SET restricted = @restricted WHERE id = @userId", new { restricted, userId });
        }

        await WebsiteFixture.EvictAllAsync();
    }

    private static async Task<(long BestScoreId, int Position)?> standingAsync(long beatmapId, long userId)
    {
        await using var conn = await dataSource.OpenConnectionAsync();
        return await ScoreSubmission.UserStandingAsync(conn, null, beatmapId, userId, wantRanked: true);
    }

    private static async Task<JObject> getBoardAsync(long beatmapId, string bearer)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v2/beatmaps/{beatmapId}/scores");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

        using var response = await WebsiteFixture.Client.SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), "leaderboard");

        return JObject.Parse(await response.Content.ReadAsStringAsync());
    }

    private static long[] userIds(JObject board)
        => ((JArray)board["scores"]!).Select(s => (long)s["user_id"]!).ToArray();

    private static async Task<string> getHtmlAsync(string url)
    {
        using var response = await WebsiteFixture.Client.GetAsync(url);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), url);
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>Who the set page's podium puts at rank 1.</summary>
    private static long podiumTop(string html)
    {
        var match = Regex.Match(html, "podium__name\" href=\"/users/(\\d+)\"");
        Assert.That(match.Success, Is.True, "expected a podium on the map's leaderboard");
        return long.Parse(match.Groups[1].Value);
    }

    private static async Task uploadReplayAsync(long scoreId, string bearer)
    {
        // Minimal legacy-replay shape, as ReplayViewsTest uploads.
        byte[] replay = new byte[64];
        replay[5] = 0x0b;
        replay[6] = 32;

        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v2/scores/{scoreId}/replay");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Content = new ByteArrayContent(replay);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        using var response = await WebsiteFixture.Client.SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent), $"replay upload for score {scoreId}");
    }

    private static async Task<long> insertUserAsync(NpgsqlConnection conn, string username)
        => await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO users (username, email, password_hash, country_code)
            VALUES (@username, @email, 'not-a-real-hash', 'US')
            RETURNING id
            """,
            new { username, email = Guid.NewGuid().ToString("N") + "@delisted.example" });

    private static async Task<long> insertSetAsync(NpgsqlConnection conn, string title, string status)
        => await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmapsets (owner_id, title, artist, status, submitted_at, updated_at)
            VALUES (@ownerId, @title, 'The Delisted', @status, now() - interval '5 days', now() - interval '5 days')
            RETURNING id
            """,
            new { ownerId = PublicSiteSeed.MapperId, title, status });

    private static async Task<long> insertBeatmapAsync(NpgsqlConnection conn, long setId)
        => await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmaps
                (set_id, version_name, checksum_md5, total_length_s, drain_length_s,
                 difficulty_rating, filename, word_count, char_count, wpm)
            VALUES
                (@setId, 'type!beat', @checksum, 90, 80, 3.0, 'map.osu', 100, 500, 80)
            RETURNING id
            """,
            new { setId, checksum = Guid.NewGuid().ToString("N") });

    private static async Task<long> insertScoreAsync(NpgsqlConnection conn, long userId, long beatmapId, long totalScore, bool ranked)
        => await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO scores
                (user_id, beatmap_id, total_score, accuracy, completion, max_combo, rank, passed, ranked,
                 mods, statistics, maximum_statistics)
            VALUES
                (@userId, @beatmapId, @totalScore, 0.95, 0.98, 50, 'S', true, @ranked,
                 '[]'::jsonb, '{"great":90}'::jsonb, '{"great":100}'::jsonb)
            RETURNING id
            """,
            new { userId, beatmapId, totalScore, ranked });
}
