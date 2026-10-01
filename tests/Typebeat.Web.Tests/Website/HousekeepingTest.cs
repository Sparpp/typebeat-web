using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Typebeat.Web.Auth;
using Typebeat.Web.Data;
using Typebeat.Web.Ops;
using Typebeat.Web.Storage;

namespace Typebeat.Web.Tests.Website;

/// <summary>
/// The housekeeping pass (backlog 365), driven through <see cref="Housekeeping.RunOnceAsync"/>
/// against the website fixture's shared database and its real <see cref="IFileStore"/>. The hosted
/// service itself never runs in a test host (TYPEBEAT_HOUSEKEEPING is unset there), and no new host
/// is built.
///
/// Every row is seeded for users and maps of this fixture's own, at the REAL now, with the rows
/// that should go backdated past their cutoffs. Nothing else in this database is old enough to
/// qualify (the other fixtures write at now()), so a pass here only ever touches what it seeded.
/// </summary>
[TestFixture]
[NonParallelizable]
public class HousekeepingTest
{
    private static NpgsqlDataSource dataSource = null!;
    private static IFileStore store = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        await PublicSiteSeed.EnsureSeededAsync();
        dataSource = NpgsqlDataSource.Create(WebsiteFixture.ConnectionString);
        store = WebsiteFixture.Services.GetRequiredService<IFileStore>();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (dataSource != null)
            await dataSource.DisposeAsync();
    }

    private static readonly HousekeepingOptions off = new() { ReplaySweep = ReplaySweepMode.Off };

    // ---------------------------------------------------------------------------------------------
    // The table sweeps.
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task OAuthTokens_ExpiredOrRevokedPastTheMargin_Go_LiveAndJustConsumedRowsStay()
    {
        await using var conn = await dataSource.OpenConnectionAsync();
        long userId = await insertUserAsync(conn, "hk oauth");

        long revokedOld = await insertOAuthAsync(conn, userId, "now() + interval '1 hour'", "now() + interval '30 days'", revoked: "now() - interval '8 days'");
        long bothExpiredOld = await insertOAuthAsync(conn, userId, "now() - interval '9 days'", "now() - interval '8 days'");
        long revokedRecently = await insertOAuthAsync(conn, userId, "now() + interval '1 hour'", "now() + interval '30 days'", revoked: "now() - interval '1 day'");
        long liveRememberMe = await insertOAuthAsync(conn, userId, "now() + interval '1 hour'", "now() + interval '30 days'");
        long justConsumed = await insertOAuthAsync(conn, userId, "now() + interval '1 hour'", "now() + interval '30 days'", consumed: "now()");
        long accessExpiredRefreshLive = await insertOAuthAsync(conn, userId, "now() - interval '20 days'", "now() + interval '10 days'");

        // A real bearer still works after the pass (the row TokenService reads is never touched).
        var tokens = new TokenService(new Db(dataSource));
        string bearer = (await tokens.IssueAsync(userId)).AccessToken;

        var report = await Housekeeping.RunOnceAsync(conn, store, off, DateTimeOffset.UtcNow);

        var left = (await conn.QueryAsync<long>("SELECT id FROM oauth_tokens WHERE user_id = @userId", new { userId })).ToHashSet();

        using var me = new HttpRequestMessage(HttpMethod.Get, "/api/v2/me/");
        me.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        using var meResponse = await WebsiteFixture.Client.SendAsync(me);

        Assert.Multiple(() =>
        {
            Assert.That(report.SkippedLocked, Is.False);
            Assert.That(report.OAuthTokens, Is.GreaterThanOrEqualTo(2));
            Assert.That(left, Does.Not.Contain(revokedOld));
            Assert.That(left, Does.Not.Contain(bothExpiredOld));
            Assert.That(left, Does.Contain(revokedRecently), "inside the seven-day margin");
            Assert.That(left, Does.Contain(liveRememberMe));
            Assert.That(left, Does.Contain(justConsumed));
            Assert.That(left, Does.Contain(accessExpiredRefreshLive), "a refresh half that still works keeps the row");
            Assert.That(meResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        });
    }

    [Test]
    public async Task EmailTokens_PastExpiryPlusSevenDays_Go()
    {
        await using var conn = await dataSource.OpenConnectionAsync();
        long userId = await insertUserAsync(conn, "hk email");

        long old = await insertEmailTokenAsync(conn, userId, "now() - interval '8 days'");
        long recent = await insertEmailTokenAsync(conn, userId, "now() - interval '1 day'");
        long live = await insertEmailTokenAsync(conn, userId, "now() + interval '10 minutes'");

        await Housekeeping.RunOnceAsync(conn, store, off, DateTimeOffset.UtcNow);

        var left = (await conn.QueryAsync<long>("SELECT id FROM email_tokens WHERE user_id = @userId", new { userId })).ToHashSet();

        Assert.Multiple(() =>
        {
            Assert.That(left, Does.Not.Contain(old));
            Assert.That(left, Does.Contain(recent));
            Assert.That(left, Does.Contain(live));
        });
    }

    [Test]
    public async Task ScoreTokens_ConsumedOnesAreNeverDeleted_UnconsumedOlderThanSevenDaysGo()
    {
        await using var conn = await dataSource.OpenConnectionAsync();
        long userId = await insertUserAsync(conn, "hk score tokens");
        var (_, beatmapId) = await insertMapAsync(conn, "Token Graveyard");
        long buildId = await conn.ExecuteScalarAsync<long>(
            "INSERT INTO builds (version_hash) VALUES (@h) ON CONFLICT (version_hash) DO UPDATE SET version_hash = excluded.version_hash RETURNING id",
            new { h = "hk-build" });
        long scoreId = await insertScoreAsync(conn, userId, beatmapId, 100_000);

        long consumedAncient = await insertScoreTokenAsync(conn, userId, beatmapId, buildId, "now() - interval '400 days'", scoreId);
        long unconsumedOld = await insertScoreTokenAsync(conn, userId, beatmapId, buildId, "now() - interval '8 days'", null);
        long unconsumedRecent = await insertScoreTokenAsync(conn, userId, beatmapId, buildId, "now() - interval '1 day'", null);

        await Housekeeping.RunOnceAsync(conn, store, off, DateTimeOffset.UtcNow);

        var left = (await conn.QueryAsync<long>("SELECT id FROM score_tokens WHERE user_id = @userId", new { userId })).ToHashSet();

        Assert.Multiple(() =>
        {
            Assert.That(left, Does.Contain(consumedAncient), "GateRefund and PlayedVersionRule read a consumed token's beatmap_hash");
            Assert.That(left, Does.Not.Contain(unconsumedOld));
            Assert.That(left, Does.Contain(unconsumedRecent));
        });
    }

    [Test]
    public async Task DownloadLog_OlderThan365Days_Goes_AndTheDownloadCountDoesNotMove()
    {
        await using var conn = await dataSource.OpenConnectionAsync();
        long userId = await insertUserAsync(conn, "hk downloader");
        var (setId, _) = await insertMapAsync(conn, "Download Ledger");
        await conn.ExecuteAsync("UPDATE beatmapsets SET download_count = 3 WHERE id = @setId", new { setId });

        await conn.ExecuteAsync(
            """
            INSERT INTO beatmapset_downloads (user_id, set_id, at) VALUES
                (@userId, @setId, now() - interval '400 days'),
                (@userId, @setId, now() - interval '366 days'),
                (@userId, @setId, now() - interval '300 days')
            """,
            new { userId, setId });

        var report = await Housekeeping.RunOnceAsync(conn, store, off, DateTimeOffset.UtcNow);

        long left = await conn.ExecuteScalarAsync<long>("SELECT count(*) FROM beatmapset_downloads WHERE user_id = @userId", new { userId });
        int count = await conn.ExecuteScalarAsync<int>("SELECT download_count FROM beatmapsets WHERE id = @setId", new { setId });

        Assert.Multiple(() =>
        {
            Assert.That(report.DownloadLogRows, Is.GreaterThanOrEqualTo(2));
            Assert.That(left, Is.EqualTo(1), "only the row inside the year survives");
            Assert.That(count, Is.EqualTo(3), "download_count is denormalised and never derived from the log");
        });
    }

    [Test]
    public async Task APassHeldByAnotherInstance_DoesNothing()
    {
        await using var holder = await dataSource.OpenConnectionAsync();
        await holder.ExecuteAsync("SELECT pg_advisory_lock(@key)", new { key = Housekeeping.AdvisoryLockKey });

        try
        {
            await using var conn = await dataSource.OpenConnectionAsync();
            long userId = await insertUserAsync(conn, "hk locked out");
            long old = await insertEmailTokenAsync(conn, userId, "now() - interval '30 days'");

            var report = await Housekeeping.RunOnceAsync(conn, store, off, DateTimeOffset.UtcNow);
            bool stillThere = await conn.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM email_tokens WHERE id = @old)", new { old });

            Assert.Multiple(() =>
            {
                Assert.That(report.SkippedLocked, Is.True);
                Assert.That(stillThere, Is.True);
            });
        }
        finally
        {
            await holder.ExecuteAsync("SELECT pg_advisory_unlock(@key)", new { key = Housekeeping.AdvisoryLockKey });
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Replay retention.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// One player, one map, a spread of stored replays that each exercises one keep rule, plus two
    /// that match none: the dry run must name exactly those two and delete nothing, the real run
    /// must prune exactly the same two (row nulled and stamped, object gone, GET 404), and a
    /// re-upload must clear the stamp again.
    /// </summary>
    [Test]
    public async Task ReplayRetention_KeepsTopNPinnedRecentAndViewed_PrunesTheRest_DryRunFirst()
    {
        await using var conn = await dataSource.OpenConnectionAsync();

        long ownerId = await insertUserAsync(conn, "hk replay owner");
        long viewerId = await insertUserAsync(conn, "hk replay viewer");
        var (_, beatmapId) = await insertMapAsync(conn, "Retention Rhapsody");

        long top1 = await storedReplayAsync(conn, ownerId, beatmapId, 900_000, uploadedDaysAgo: 60);
        long top2 = await storedReplayAsync(conn, ownerId, beatmapId, 800_000, uploadedDaysAgo: 60);
        long top3 = await storedReplayAsync(conn, ownerId, beatmapId, 700_000, uploadedDaysAgo: 60);
        long prunedA = await storedReplayAsync(conn, ownerId, beatmapId, 600_000, uploadedDaysAgo: 60);
        long pinned = await storedReplayAsync(conn, ownerId, beatmapId, 500_000, uploadedDaysAgo: 60);
        long recent = await storedReplayAsync(conn, ownerId, beatmapId, 400_000, uploadedDaysAgo: 5);
        long viewedRecently = await storedReplayAsync(conn, ownerId, beatmapId, 300_000, uploadedDaysAgo: 60);
        long prunedB = await storedReplayAsync(conn, ownerId, beatmapId, 200_000, uploadedDaysAgo: 60);

        // The unranked board is its own board: this play is ITS best, so it is kept even though five
        // ranked plays outscore it.
        long unrankedBest = await storedReplayAsync(conn, ownerId, beatmapId, 100_000, uploadedDaysAgo: 60, ranked: false);

        // A failed run sorts after every passed play on its board, whatever its score.
        long failedRun = await storedReplayAsync(conn, ownerId, beatmapId, 950_000, uploadedDaysAgo: 60, passed: false);

        await conn.ExecuteAsync("INSERT INTO score_pins (score_id, user_id) VALUES (@pinned, @ownerId)", new { pinned, ownerId });
        await conn.ExecuteAsync(
            """
            INSERT INTO replay_views (score_id, viewer_id, viewed_on) VALUES
                (@viewedRecently, @viewerId, (now() AT TIME ZONE 'UTC')::date - 10),
                (@prunedB, @viewerId, (now() AT TIME ZONE 'UTC')::date - 100)
            """,
            new { viewedRecently, viewerId, prunedB });

        var everyKeeper = new[] { top1, top2, top3, pinned, recent, viewedRecently, unrankedBest };
        var expectedPruned = new[] { prunedA, prunedB, failedRun };

        // ---- dry run ----
        var dry = await Housekeeping.RunOnceAsync(conn, store, new HousekeepingOptions { ReplaySweep = ReplaySweepMode.DryRun }, DateTimeOffset.UtcNow);

        var mine = everyKeeper.Concat(expectedPruned).ToArray();
        var dryMine = dry.ReplayScoreIds.Where(mine.Contains).ToList();
        long storedAfterDry = await conn.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM scores WHERE id = ANY(@mine) AND replay_key IS NOT NULL", new { mine });

        Assert.Multiple(() =>
        {
            Assert.That(dry.ReplayMode, Is.EqualTo(ReplaySweepMode.DryRun));
            Assert.That(dryMine, Is.EquivalentTo(expectedPruned));
            Assert.That(dry.ReplayBytes, Is.GreaterThanOrEqualTo(expectedPruned.Length * 512L));
            Assert.That(storedAfterDry, Is.EqualTo(mine.Length), "a dry run deletes nothing");
        });

        foreach (long id in expectedPruned)
            Assert.That(await store.ObjectExistsAsync(StoreKeys.Replay(id)), Is.True, $"dry run kept the object for {id}");

        // ---- the real run ----
        var on = await Housekeeping.RunOnceAsync(conn, store, new HousekeepingOptions { ReplaySweep = ReplaySweepMode.On }, DateTimeOffset.UtcNow);

        Assert.That(on.ReplayScoreIds, Is.EquivalentTo(dry.ReplayScoreIds), "the dry run reported exactly what the real run prunes");

        foreach (long id in expectedPruned)
        {
            var row = await conn.QuerySingleAsync<(string? Key, DateTime? PrunedAt)>(
                "SELECT replay_key AS Key, replay_pruned_at AS PrunedAt FROM scores WHERE id = @id", new { id });

            bool objectLeft = await store.ObjectExistsAsync(StoreKeys.Replay(id));

            Assert.Multiple(() =>
            {
                Assert.That(row.Key, Is.Null, $"score {id} replay_key");
                Assert.That(row.PrunedAt, Is.Not.Null, $"score {id} replay_pruned_at");
                Assert.That(objectLeft, Is.False, $"score {id} object");
            });
        }

        foreach (long id in everyKeeper)
        {
            var row = await conn.QuerySingleAsync<(string? Key, DateTime? PrunedAt)>(
                "SELECT replay_key AS Key, replay_pruned_at AS PrunedAt FROM scores WHERE id = @id", new { id });

            Assert.Multiple(() =>
            {
                Assert.That(row.Key, Is.Not.Null, $"keeper {id}");
                Assert.That(row.PrunedAt, Is.Null, $"keeper {id}");
            });
            Assert.That(await store.ObjectExistsAsync(StoreKeys.Replay(id)), Is.True, $"keeper {id} object");
        }

        using (var gone = await WebsiteFixture.Client.GetAsync($"/api/v2/scores/{prunedA}/replay"))
            Assert.That(gone.StatusCode, Is.EqualTo(HttpStatusCode.NotFound), "a pruned replay reads as never uploaded");

        // A second real run finds nothing more of ours to do.
        var again = await Housekeeping.RunOnceAsync(conn, store, new HousekeepingOptions { ReplaySweep = ReplaySweepMode.On }, DateTimeOffset.UtcNow);
        Assert.That(again.ReplayScoreIds.Where(mine.Contains), Is.Empty);

        // ---- a re-upload heals the row and clears the stamp ----
        string bearer = (await new TokenService(new Db(dataSource)).IssueAsync(ownerId)).AccessToken;

        using (var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v2/scores/{prunedA}/replay"))
        {
            var content = new ByteArrayContent(DiskGuardSiteTest.fakeReplay(3, 600));
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            request.Content = content;
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

            using var upload = await WebsiteFixture.Client.SendAsync(request);
            Assert.That(upload.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        }

        var healed = await conn.QuerySingleAsync<(string? Key, DateTime? PrunedAt)>(
            "SELECT replay_key AS Key, replay_pruned_at AS PrunedAt FROM scores WHERE id = @prunedA", new { prunedA });

        Assert.Multiple(() =>
        {
            Assert.That(healed.Key, Is.EqualTo(StoreKeys.Replay(prunedA)));
            Assert.That(healed.PrunedAt, Is.Null, "a replay that is stored again is not 'pruned'");
        });
    }

    [Test]
    public async Task ReplaySweepOff_LooksAtNothing()
    {
        await using var conn = await dataSource.OpenConnectionAsync();
        long ownerId = await insertUserAsync(conn, "hk replay off");
        var (_, beatmapId) = await insertMapAsync(conn, "Sweep Off Sonata");

        for (int i = 0; i < 4; i++)
            await storedReplayAsync(conn, ownerId, beatmapId, 900_000 - i * 1000, uploadedDaysAgo: 60);

        long fourth = await conn.ExecuteScalarAsync<long>(
            "SELECT id FROM scores WHERE user_id = @ownerId ORDER BY total_score ASC LIMIT 1", new { ownerId });

        var report = await Housekeeping.RunOnceAsync(conn, store, off, DateTimeOffset.UtcNow);
        string? key = await conn.ExecuteScalarAsync<string?>("SELECT replay_key FROM scores WHERE id = @fourth", new { fourth });

        Assert.Multiple(() =>
        {
            Assert.That(report.Replays, Is.Zero);
            Assert.That(key, Is.Not.Null);
        });

        // Tidy: a later dry run in this database should not be told about this fixture's rows.
        await conn.ExecuteAsync("UPDATE scores SET replay_uploaded_at = now() WHERE user_id = @ownerId", new { ownerId });
    }

    // ---- helpers ----

    private static async Task<long> insertUserAsync(NpgsqlConnection conn, string name)
    {
        string username = name + " " + Guid.NewGuid().ToString("N")[..6];
        return await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO users (username, email, password_hash, country_code)
            VALUES (@username, @email, 'x', 'US')
            RETURNING id
            """,
            new { username, email = username.Replace(' ', '.') + "@example.com" });
    }

    private static async Task<(long SetId, long BeatmapId)> insertMapAsync(NpgsqlConnection conn, string title)
    {
        long setId = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmapsets (owner_id, title, artist, status, submitted_at, updated_at)
            VALUES (@ownerId, @title, 'The Janitors', 'ranked', now() - interval '3 days', now() - interval '3 days')
            RETURNING id
            """,
            new { ownerId = PublicSiteSeed.MapperId, title });

        long beatmapId = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmaps (set_id, version_name, checksum_md5, total_length_s, drain_length_s, difficulty_rating, filename)
            VALUES (@setId, 'type!beat', @checksum, 60, 55, 2.0, 'map.osu')
            RETURNING id
            """,
            new { setId, checksum = Guid.NewGuid().ToString("N") });

        return (setId, beatmapId);
    }

    private static async Task<long> insertScoreAsync(NpgsqlConnection conn, long userId, long beatmapId, long totalScore, bool ranked = true, bool passed = true)
        => await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO scores
                (user_id, beatmap_id, total_score, accuracy, completion, max_combo, rank, passed, ranked,
                 mods, statistics, maximum_statistics)
            VALUES
                (@userId, @beatmapId, @totalScore, 0.97, 1.0, 100, 'S', @passed, @ranked,
                 '[]'::jsonb, '{"great":100}'::jsonb, '{"great":100}'::jsonb)
            RETURNING id
            """,
            new { userId, beatmapId, totalScore, ranked, passed });

    /// <summary>A score with a real stored replay object, uploaded the given number of days ago.</summary>
    private static async Task<long> storedReplayAsync(
        NpgsqlConnection conn, long userId, long beatmapId, long totalScore, int uploadedDaysAgo, bool ranked = true, bool passed = true)
    {
        long scoreId = await insertScoreAsync(conn, userId, beatmapId, totalScore, ranked, passed);
        string key = StoreKeys.Replay(scoreId);

        byte[] bytes = RandomNumberGenerator.GetBytes(512);
        using (var buffer = new MemoryStream(bytes))
            await store.WriteObjectAsync(key, buffer);

        await conn.ExecuteAsync(
            """
            UPDATE scores
            SET replay_key = @key, replay_bytes = @length, replay_uploaded_at = now() - make_interval(days => @uploadedDaysAgo)
            WHERE id = @scoreId
            """,
            new { key, length = bytes.Length, uploadedDaysAgo, scoreId });

        return scoreId;
    }

    private static async Task<long> insertOAuthAsync(NpgsqlConnection conn, long userId, string accessExpires, string refreshExpires, string? revoked = null, string? consumed = null)
        => await conn.ExecuteScalarAsync<long>(
            $"""
             INSERT INTO oauth_tokens (user_id, access_hash, refresh_hash, access_expires_at, refresh_expires_at, revoked_at, consumed_at)
             VALUES (@userId, @access, @refresh, {accessExpires}, {refreshExpires}, {revoked ?? "NULL"}, {consumed ?? "NULL"})
             RETURNING id
             """,
            new { userId, access = RandomNumberGenerator.GetBytes(32), refresh = RandomNumberGenerator.GetBytes(32) });

    private static async Task<long> insertEmailTokenAsync(NpgsqlConnection conn, long userId, string expires)
        => await conn.ExecuteScalarAsync<long>(
            $"""
             INSERT INTO email_tokens (user_id, token_hash, purpose, expires_at)
             VALUES (@userId, @hash, 'verify', {expires})
             RETURNING id
             """,
            new { userId, hash = RandomNumberGenerator.GetBytes(32) });

    private static async Task<long> insertScoreTokenAsync(NpgsqlConnection conn, long userId, long beatmapId, long buildId, string createdAt, long? scoreId)
        => await conn.ExecuteScalarAsync<long>(
            $"""
             INSERT INTO score_tokens (user_id, beatmap_id, beatmap_hash, build_id, score_id, created_at)
             VALUES (@userId, @beatmapId, @hash, @buildId, @scoreId, {createdAt})
             RETURNING id
             """,
            new { userId, beatmapId, hash = Guid.NewGuid().ToString("N"), buildId, scoreId });
}
