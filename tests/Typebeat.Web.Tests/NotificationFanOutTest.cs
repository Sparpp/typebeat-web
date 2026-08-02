using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Typebeat.Web.Data;
using Typebeat.Web.Packages;
using Typebeat.Web.Social;
using Typebeat.Web.Storage;

namespace Typebeat.Web.Tests;

/// <summary>
/// The watched-mapper notification fan-out (027_notifications.sql), driven through the REAL
/// ingest path rather than by calling <see cref="Notifications.FanOutMapperUploadAsync"/>
/// directly: the whole feature rests on WHERE the fan-out is hooked (the hidden -> published
/// latch inside PackageIngest), so a test that skipped the ingest would prove nothing about the
/// property that actually matters, which is "publishing notifies, re-uploading does not".
///
/// Own database, drop+recreated, matching every other DB-backed fixture in this repo.
/// </summary>
[TestFixture]
[NonParallelizable]
public class NotificationFanOutTest
{
    // Must stay distinct from typebeat_pkgtests / typebeat_webtests / typebeat_wirecompat.
    private const string database_name = "typebeat_notiftests";

    private const string connection_string =
        "Host=localhost;Port=5432;Database=" + database_name + ";Username=postgres;Password=postgres";

    private const string admin_connection_string =
        "Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=postgres";

    private NpgsqlDataSource dataSource = null!;
    private Db db = null!;
    private LocalFileStore fileStore = null!;
    private PackageIngest ingest = null!;
    private string fileRoot = null!;

    private long mapperId;
    private long watcherId;
    private long otherWatcherId;
    private long strangerId;
    private long restrictedWatcherId;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        await using (var conn = new NpgsqlConnection(admin_connection_string))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync($"DROP DATABASE IF EXISTS {database_name} WITH (FORCE)");
            await conn.ExecuteAsync($"CREATE DATABASE {database_name}");
        }

        await Db.EnsureExtensionsAsync(connection_string);

        dataSource = NpgsqlDataSource.Create(connection_string);
        db = new Db(dataSource);
        await db.MigrateAsync(NullLogger.Instance);

        fileRoot = Path.Combine(Path.GetTempPath(), "typebeat-notiftests-" + Guid.NewGuid().ToString("N"));
        fileStore = new LocalFileStore(fileRoot);
        ingest = new PackageIngest(db, fileStore, new CoverGenerator(), new PreviewGenerator(), NullLogger<PackageIngest>.Instance);

        await using var setup = await db.OpenAsync();

        mapperId = await insertUserAsync(setup, "notif mapper");
        watcherId = await insertUserAsync(setup, "notif watcher");
        otherWatcherId = await insertUserAsync(setup, "notif watcher two");
        strangerId = await insertUserAsync(setup, "notif stranger");
        restrictedWatcherId = await insertUserAsync(setup, "notif banned watcher", restricted: true);

        // Two live watchers, one restricted watcher (delisted site-wide, treated as signed out by
        // the cookie middleware, so notifying it is pure waste), and a stranger who watches
        // nobody. The mapper cannot appear on this list at all: user_follows carries a CHECK that
        // makes watching yourself unrepresentable.
        foreach (long follower in new[] { watcherId, otherWatcherId, restrictedWatcherId })
        {
            await setup.ExecuteAsync(
                "INSERT INTO user_follows (follower_id, followee_id, kind) VALUES (@follower, @mapperId, 'mapper')",
                new { follower, mapperId });
        }

        // The stranger follows the mapper as a PLAYER, not as a mapper: the two kinds share one
        // table, and only the bell arms notifications.
        await setup.ExecuteAsync(
            "INSERT INTO user_follows (follower_id, followee_id, kind) VALUES (@strangerId, @mapperId, 'user')",
            new { strangerId, mapperId });
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (dataSource != null)
            await dataSource.DisposeAsync();

        if (fileRoot != null && Directory.Exists(fileRoot))
            Directory.Delete(fileRoot, recursive: true);
    }

    [Test]
    public async Task FirstPublish_NotifiesEveryWatcher_AndNobodyElse()
    {
        long setId = await createHiddenSetAsync();

        await ingestAsync(setId, beatmapId: 2001);

        await using var conn = await db.OpenAsync();

        var recipients = (await conn.QueryAsync<long>(
            "SELECT user_id FROM user_notifications WHERE set_id = @setId ORDER BY user_id",
            new { setId })).ToList();

        var row = await conn.QuerySingleAsync<(string Kind, long ActorId, DateTime? ReadAt)>(
            """
            SELECT kind AS Kind, actor_id AS ActorId, read_at AS ReadAt
            FROM user_notifications WHERE set_id = @setId AND user_id = @watcherId
            """,
            new { setId, watcherId });

        Assert.Multiple(() =>
        {
            Assert.That(recipients, Is.EqualTo(new[] { watcherId, otherWatcherId }.OrderBy(id => id).ToList()),
                "both live watchers, and only them");
            Assert.That(recipients, Does.Not.Contain(mapperId), "the mapper does not notify themselves");
            Assert.That(recipients, Does.Not.Contain(strangerId), "a 'user' follow is not a mapper watch");
            Assert.That(recipients, Does.Not.Contain(restrictedWatcherId), "restricted accounts are skipped");

            Assert.That(row.Kind, Is.EqualTo(Notifications.MapperUploadKind));
            Assert.That(row.ActorId, Is.EqualTo(mapperId), "the actor is the mapper");
            Assert.That(row.ReadAt, Is.Null, "a fresh notification is unread");
        });

        // The set really did go public; the latch is the status flip, not a side effect.
        string status = await conn.ExecuteScalarAsync<string>(
            "SELECT status FROM beatmapsets WHERE id = @setId", new { setId }) ?? "";
        Assert.That(status, Is.EqualTo("pending"));
    }

    [Test]
    public async Task ReUpload_DoesNotNotifyAgain()
    {
        long setId = await createHiddenSetAsync();

        await ingestAsync(setId, beatmapId: 2101);

        // A CHANGED package (new version cut) and an IDENTICAL one (the no-op fast path, which
        // still runs the publish latch) are the two ways an ingest can revisit a published set.
        // Neither is a new upload, so neither may re-notify.
        await ingestAsync(setId, beatmapId: 2101, tags: "typebeat lyrics typing revised");
        await ingestAsync(setId, beatmapId: 2101, tags: "typebeat lyrics typing revised");

        await using var conn = await db.OpenAsync();

        int versions = await conn.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM set_versions WHERE set_id = @setId", new { setId });

        int notifications = await conn.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM user_notifications WHERE set_id = @setId", new { setId });

        Assert.Multiple(() =>
        {
            Assert.That(versions, Is.EqualTo(2), "one changed re-upload cut a version, the identical one did not");
            Assert.That(notifications, Is.EqualTo(2), "still exactly one notification per watcher");
        });
    }

    [Test]
    public async Task ReNotify_IsUnrepresentable_EvenByHand()
    {
        // Belt and braces on the latch: the partial unique index makes a duplicate impossible for
        // every OTHER write path too (a future republish tool, a manual INSERT, a retry).
        long setId = await createHiddenSetAsync();
        await ingestAsync(setId, beatmapId: 2201);

        await using var conn = await db.OpenAsync();

        Assert.That(
            async () => await conn.ExecuteAsync(
                """
                INSERT INTO user_notifications (user_id, kind, set_id, actor_id)
                VALUES (@watcherId, 'mapper_upload', @setId, @mapperId)
                """,
                new { watcherId, setId, mapperId }),
            Throws.InstanceOf<PostgresException>());
    }

    [Test]
    public async Task WatchingAfterThePublish_NotifiesNothingRetroactively()
    {
        long setId = await createHiddenSetAsync();
        await ingestAsync(setId, beatmapId: 2301);

        await using var conn = await db.OpenAsync();

        long latecomerId = await insertUserAsync(conn, "notif latecomer");

        await conn.ExecuteAsync(
            "INSERT INTO user_follows (follower_id, followee_id, kind) VALUES (@latecomerId, @mapperId, 'mapper')",
            new { latecomerId, mapperId });

        int notifications = await conn.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM user_notifications WHERE user_id = @latecomerId", new { latecomerId });

        // Fan-out on WRITE: the bell arms the FUTURE. What the latecomer missed is what the
        // /watching upload feed is for.
        Assert.That(notifications, Is.Zero);
    }

    [Test]
    public async Task DeletingTheSet_TakesItsNotificationsWithIt()
    {
        long setId = await createHiddenSetAsync();
        await ingestAsync(setId, beatmapId: 2401);

        await using var conn = await db.OpenAsync();

        // The FK cascade is what makes typed reference columns worth their migration cost over a
        // jsonb payload: no sweep, no dangling ids, no notification pointing at nothing.
        await conn.ExecuteAsync("DELETE FROM beatmaps WHERE set_id = @setId", new { setId });
        await conn.ExecuteAsync("DELETE FROM version_files WHERE version_id IN (SELECT id FROM set_versions WHERE set_id = @setId)", new { setId });
        await conn.ExecuteAsync("DELETE FROM set_versions WHERE set_id = @setId", new { setId });
        await conn.ExecuteAsync("DELETE FROM beatmapsets WHERE id = @setId", new { setId });

        int left = await conn.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM user_notifications WHERE set_id = @setId", new { setId });

        Assert.That(left, Is.Zero);
    }

    // ---- helpers ----

    /// <summary>A pre-publish shell exactly as PUT /bss/beatmapsets creates one.</summary>
    private async Task<long> createHiddenSetAsync()
    {
        await using var conn = await db.OpenAsync();

        return await conn.ExecuteScalarAsync<long>(
            "INSERT INTO beatmapsets (owner_id, status, intended_status) VALUES (@mapperId, 'hidden', 'pending') RETURNING id",
            new { mapperId });
    }

    private async Task ingestAsync(long setId, long beatmapId, string tags = "typebeat lyrics typing")
    {
        (string Name, byte[] Content)[] entries =
        [
            ("map.osu", SyntheticPackage.Utf8(SyntheticPackage.OsuText(
                creator: "notif mapper", tags: tags, beatmapId: beatmapId, beatmapSetId: setId))),
            ("audio.mp3", SyntheticPackage.Utf8("fake audio bytes")),
            ("bg.jpg", SyntheticPackage.TinyPng()),
        ];

        using var zip = SyntheticPackage.Zip(entries);
        var parsed = BeatmapPackageParser.Parse(zip);

        PackageValidator.Validate(parsed, setId, [beatmapId], "notif mapper");

        await using var scope = await ingest.BeginSetScopeAsync(setId);
        await ingest.IngestAsync(scope, zip, parsed, setId, mapperId);
    }

    private static async Task<long> insertUserAsync(NpgsqlConnection conn, string username, bool restricted = false)
        => await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO users (username, email, password_hash, country_code, restricted)
            VALUES (@username, @email, 'x', 'US', @restricted)
            RETURNING id
            """,
            new { username, email = username.Replace(' ', '.') + "@example.com", restricted });
}
