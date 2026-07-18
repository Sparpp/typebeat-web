using System.Net;
using Dapper;
using Npgsql;

namespace Typebeat.Web.Tests.Website;

/// <summary>
/// Global rankings page: cumulative score = sum of best-per-map scores on RANKED maps only.
/// Seeds its own users/sets (unique names) on top of <see cref="PublicSiteSeed"/> and asserts
/// only on those rows, so it never depends on (or disturbs) the shared fixtures' counts.
/// </summary>
[NonParallelizable]
public class RankingsPageTest
{
    private long aliceId;
    private long bobId;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        await PublicSiteSeed.EnsureSeededAsync();

        await using var conn = new NpgsqlConnection(WebsiteFixture.ConnectionString);
        await conn.OpenAsync();

        aliceId = await insertUserAsync(conn, "rk alice");
        bobId = await insertUserAsync(conn, "rk bob");
        long restrictedId = await insertUserAsync(conn, "rk restricted", restricted: true);
        long deletedId = await insertUserAsync(conn, "rk deleted", deleted: true);

        long rankedSet = await insertSetAsync(conn, "Rankings Ranked Set", "ranked");
        long mapA = await insertBeatmapAsync(conn, rankedSet);
        long mapB = await insertBeatmapAsync(conn, rankedSet);

        long pendingSet = await insertSetAsync(conn, "Rankings Pending Set", "pending");
        long mapC = await insertBeatmapAsync(conn, pendingSet);

        // alice: best-per-map folds 300k (not 300k+100k) on A, plus 200k on B = 500k over 2 maps.
        await insertScoreAsync(conn, aliceId, mapA, 300_000);
        await insertScoreAsync(conn, aliceId, mapA, 100_000);
        await insertScoreAsync(conn, aliceId, mapB, 200_000);

        // bob: 400k on A. His unranked-flag and failed scores must not count.
        await insertScoreAsync(conn, bobId, mapA, 400_000);
        await insertScoreAsync(conn, bobId, mapA, 888_888, ranked: false);
        await insertScoreAsync(conn, bobId, mapB, 777_777, passed: false);

        // Pending-map scores contribute nothing (alice stays at 500k).
        await insertScoreAsync(conn, aliceId, mapC, 555_555);

        // Delisted accounts never surface, however large their totals.
        await insertScoreAsync(conn, restrictedId, mapA, 911_111);
        await insertScoreAsync(conn, deletedId, mapA, 922_222);
    }

    [Test]
    public async Task Rankings_SumsBestPerRankedMap_AndDelistsExcludedUsers()
    {
        using var response = await WebsiteFixture.Client.GetAsync("/rankings");
        string html = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            // Both players list, linked to their profiles, with folded cumulative totals.
            Assert.That(html, Does.Contain($"href=\"/users/{aliceId}\""));
            Assert.That(html, Does.Contain($"href=\"/users/{bobId}\""));
            Assert.That(html, Does.Contain("500,000"));
            Assert.That(html, Does.Contain("400,000"));

            // alice (500k) ranks above bob (400k).
            Assert.That(html.IndexOf("rk alice", StringComparison.Ordinal),
                Is.LessThan(html.IndexOf("rk bob", StringComparison.Ordinal)));

            // Nothing from the unranked flag, the fail, or the pending map leaked into totals.
            Assert.That(html, Does.Not.Contain("888,888"));
            Assert.That(html, Does.Not.Contain("777,777"));
            Assert.That(html, Does.Not.Contain("555,555"));

            // Restricted + deleted accounts are delisted entirely.
            Assert.That(html, Does.Not.Contain("rk restricted"));
            Assert.That(html, Does.Not.Contain("rk deleted"));
            Assert.That(html, Does.Not.Contain("911,111"));
            Assert.That(html, Does.Not.Contain("922,222"));
        });
    }

    [Test]
    public async Task Rankings_LinkedFromSiteNav()
    {
        using var response = await WebsiteFixture.Client.GetAsync("/");
        string html = await response.Content.ReadAsStringAsync();

        Assert.That(html, Does.Contain("href=\"/rankings\""));
    }

    private static async Task<long> insertUserAsync(NpgsqlConnection conn, string username, bool restricted = false, bool deleted = false)
        => await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO users (username, email, password_hash, country_code, restricted, deleted_at)
            VALUES (@username, @email, 'not-a-real-hash', 'US', @restricted, @deletedAt)
            RETURNING id
            """,
            new
            {
                username,
                email = username.Replace(' ', '.') + "@example.com",
                restricted,
                deletedAt = deleted ? DateTimeOffset.UtcNow : (DateTimeOffset?)null,
            });

    private static async Task<long> insertSetAsync(NpgsqlConnection conn, string title, string status)
        => await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmapsets (owner_id, title, artist, status, submitted_at, updated_at)
            VALUES (@ownerId, @title, 'Rankings Artist', @status, now(), now())
            RETURNING id
            """,
            new { ownerId = PublicSiteSeed.MapperId, title, status });

    private static async Task<long> insertBeatmapAsync(NpgsqlConnection conn, long setId)
        => await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmaps
                (set_id, version_name, checksum_md5, total_length_s, drain_length_s,
                 difficulty_rating, filename, word_count, char_count, wpm)
            VALUES (@setId, 'type!beat', @checksum, 90, 80, 3.0, 'map.osu', 100, 500, 75)
            RETURNING id
            """,
            new { setId, checksum = Guid.NewGuid().ToString("N") });

    private static async Task insertScoreAsync(NpgsqlConnection conn, long userId, long beatmapId,
        long totalScore, bool ranked = true, bool passed = true)
        => await conn.ExecuteAsync(
            """
            INSERT INTO scores
                (user_id, beatmap_id, total_score, accuracy, completion, max_combo, rank, passed, ranked,
                 mods, statistics, maximum_statistics)
            VALUES
                (@userId, @beatmapId, @totalScore, 0.95, 0.97, 50, 'A', @passed, @ranked,
                 '[]'::jsonb, '{"great":100}'::jsonb, '{"great":103}'::jsonb)
            """,
            new { userId, beatmapId, totalScore, passed, ranked });
}
