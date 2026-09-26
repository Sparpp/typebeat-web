using System.Reflection;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Typebeat.Web.Data;

namespace Typebeat.Web.Tests;

/// <summary>
/// Migration 004 backfill semantics against a real database that simulates the production
/// state it exists for: migrations 001–003 already applied, plus (a) a pre-M3 set (the M1
/// seed tool's shape: beatmapsets + beatmaps only, filename NULL, zero set_versions; prod's
/// "Wolf" set) and (b) a BSS-era set whose NULL-filename diff means "dropped from the current
/// version" and must NOT be resurrected. MigrateAsync then applies exactly 004 on top.
/// </summary>
[TestFixture]
[NonParallelizable]
public class Migration004BackfillTest
{
    // Dedicated database; every DB-backed fixture in this repo force-drops its own.
    private const string database_name = "typebeat_migr004tests";

    private const string connection_string =
        "Host=localhost;Port=5432;Database=" + database_name + ";Username=postgres;Password=postgres";

    private const string admin_connection_string =
        "Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=postgres";

    private NpgsqlDataSource dataSource = null!;

    private long preM3BeatmapId;
    private long weirdNameBeatmapId;
    private long droppedDiffBeatmapId;
    private long liveDiffBeatmapId;

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

        // Simulate the pre-deploy production database: 001–003 applied and recorded, so
        // MigrateAsync below applies exactly 004, against data that already exists.
        await using (var conn = new NpgsqlConnection(connection_string))
        {
            await conn.OpenAsync();

            await conn.ExecuteAsync(
                "CREATE TABLE schema_migrations (name text PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now())");

            foreach (string migration in new[] { "001_init.sql", "002_website_uploads.sql", "003_anonymous_downloads.sql" })
            {
                await conn.ExecuteAsync(readEmbeddedMigration(migration));
                await conn.ExecuteAsync("INSERT INTO schema_migrations (name) VALUES (@migration)", new { migration });
            }

            long ownerId = await conn.ExecuteScalarAsync<long>(
                """
                INSERT INTO users (username, email, password_hash, country_code)
                VALUES ('legacy mapper', 'legacy@example.com', 'x', 'US')
                RETURNING id
                """);

            // (a) Pre-M3 sets: no set_versions, filename NULL, exactly what tools/seed wrote.
            long preM3SetId = await conn.ExecuteScalarAsync<long>(
                "INSERT INTO beatmapsets (owner_id, title, artist) VALUES (@ownerId, 'The Wolf', 'Siames') RETURNING id",
                new { ownerId });

            preM3BeatmapId = await insertBeatmapAsync(conn, preM3SetId, "type!beat", filename: null);
            weirdNameBeatmapId = await insertBeatmapAsync(conn, preM3SetId, "  ha/rd:mo*de  ", filename: null);

            // (b) BSS-era set: has a version; its NULL-filename row is a DROPPED diff.
            long bssSetId = await conn.ExecuteScalarAsync<long>(
                "INSERT INTO beatmapsets (owner_id, title, artist) VALUES (@ownerId, 'Uploaded Song', 'The Pipeline') RETURNING id",
                new { ownerId });

            await conn.ExecuteAsync(
                "INSERT INTO set_versions (set_id, version_no, package_key) VALUES (@bssSetId, 1, 'packages/' || @bssSetId || '/1.typb')",
                new { bssSetId });

            droppedDiffBeatmapId = await insertBeatmapAsync(conn, bssSetId, "old cut", filename: null);
            liveDiffBeatmapId = await insertBeatmapAsync(conn, bssSetId, "current", filename: "current.osu");
        }

        dataSource = NpgsqlDataSource.Create(connection_string);
        await new Db(dataSource).MigrateAsync(NullLogger.Instance);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (dataSource != null)
            await dataSource.DisposeAsync();
    }

    [Test]
    public async Task PreM3Diffs_GetASyntheticFilename_FromVersionName()
    {
        await using var conn = await dataSource.OpenConnectionAsync();

        string? plain = await filenameOf(conn, preM3BeatmapId);
        string? weird = await filenameOf(conn, weirdNameBeatmapId);

        Assert.Multiple(() =>
        {
            Assert.That(plain, Is.EqualTo("type!beat.osu"));
            // Trimmed; path separators / reserved filename characters sanitized to '_'.
            Assert.That(weird, Is.EqualTo("ha_rd_mo_de.osu"));
        });
    }

    [Test]
    public async Task BssEraDroppedDiffs_StayNull_LiveDiffsUntouched()
    {
        await using var conn = await dataSource.OpenConnectionAsync();

        string? dropped = await filenameOf(conn, droppedDiffBeatmapId);
        string? live = await filenameOf(conn, liveDiffBeatmapId);

        Assert.Multiple(() =>
        {
            Assert.That(dropped, Is.Null, "NULL on a versioned set means 'dropped diff' and must survive 004");
            Assert.That(live, Is.EqualTo("current.osu"));
        });
    }

    [Test]
    public async Task Migrations_AppliedInOrder_001Through035()
    {
        await using var conn = await dataSource.OpenConnectionAsync();

        var applied = (await conn.QueryAsync<string>("SELECT name FROM schema_migrations ORDER BY name")).ToList();
        Assert.That(applied, Is.EqualTo(new[]
        {
            "001_init.sql",
            "002_website_uploads.sql",
            "003_anonymous_downloads.sql",
            "004_prem3_filename_backfill.sql",
            "005_ranked_approval.sql",
            "006_email_verification.sql",
            "007_account_settings.sql",
            "008_completion_rank.sql",
            "009_boundary_pace.sql",
            "010_backfill_play_counts.sql",
            "011_user_preferences.sql",
            "012_unranked_status.sql",
            "013_explicit_flag.sql",
            "014_replay_storage.sql",
            "015_ht_nerf_rescore.sql",
            "016_refund_skip_gate.sql",
            "017_rate_gate_refund.sql",
            "018_lyrics_search.sql",
            "019_language.sql",
            "020_performance_points.sql",
            "022_score_pins.sql",
            "023_follows.sql",
            "024_play_history.sql",
            "025_replay_views.sql",
            "026_profile_order.sql",
            "027_notifications.sql",
            "028_wpm_curve.sql",
            "029_literate_stars.sql",
            "030_gameplay_fingerprint.sql",
            "031_freestyle_cell_count.sql",
            "032_reviewer_grants.sql",
            "033_target_wpm.sql",
            "034_ratings_matrix.sql",
            "035_google_sign_in.sql",
        }));
    }

    [Test]
    public async Task Migration005_GrandfathersPublicRowsToRanked_AndDefaultsToHidden()
    {
        await using var conn = await dataSource.OpenConnectionAsync();

        // Both seeded sets predate 005 with 001's default status 'public'; they were live
        // with leaderboards, so they must come out 'ranked'.
        var statuses = (await conn.QueryAsync<string>("SELECT status FROM beatmapsets WHERE title IN ('The Wolf', 'Uploaded Song')")).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(statuses, Has.Count.EqualTo(2));
            Assert.That(statuses, Is.All.EqualTo("ranked"));
        });

        // The new-row default is 'hidden' (a BSS shell before its first upload)…
        long shellId = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmapsets (owner_id, title, artist)
            SELECT id, 'Post 005 Shell', 'The Defaults' FROM users LIMIT 1
            RETURNING id
            """);

        string? shellStatus = await conn.ExecuteScalarAsync<string>(
            "SELECT status FROM beatmapsets WHERE id = @shellId", new { shellId });
        Assert.That(shellStatus, Is.EqualTo("hidden"));

        // …and 'public' is no longer a legal status at all.
        Assert.That(
            async () => await conn.ExecuteAsync("UPDATE beatmapsets SET status = 'public' WHERE id = @shellId", new { shellId }),
            Throws.Exception.With.Message.Contains("beatmapsets_status_check"));
    }

    [Test]
    public async Task Migration014_AddsReplayColumns_DefaultingToNoReplay()
    {
        await using var conn = await dataSource.OpenConnectionAsync();

        // A score written without any replay awareness (every pre-feature row, and every row the
        // submit endpoint writes) must read back as "no replay stored".
        long scoreId = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO scores
                (user_id, beatmap_id, total_score, accuracy, completion, max_combo, rank, passed, ranked)
            SELECT u.id, @beatmapId, 100, 1.0, 1.0, 10, 'S', true, true FROM users u LIMIT 1
            RETURNING id
            """,
            new { beatmapId = liveDiffBeatmapId });

        var row = await conn.QuerySingleAsync<(string? Key, int? Bytes, DateTime? UploadedAt)>(
            "SELECT replay_key AS Key, replay_bytes AS Bytes, replay_uploaded_at AS UploadedAt FROM scores WHERE id = @scoreId",
            new { scoreId });

        Assert.Multiple(() =>
        {
            Assert.That(row.Key, Is.Null, "replay_key NULL is the has_replay=false marker");
            Assert.That(row.Bytes, Is.Null);
            Assert.That(row.UploadedAt, Is.Null);
        });
    }

    private static async Task<string?> filenameOf(NpgsqlConnection conn, long beatmapId)
        => await conn.ExecuteScalarAsync<string?>(
            "SELECT filename FROM beatmaps WHERE id = @beatmapId", new { beatmapId });

    private static async Task<long> insertBeatmapAsync(NpgsqlConnection conn, long setId, string versionName, string? filename)
        => await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmaps (set_id, version_name, checksum_md5, total_length_s, drain_length_s, difficulty_rating, filename)
            VALUES (@setId, @versionName, @checksum, 100, 90, 2.5, @filename)
            RETURNING id
            """,
            new { setId, versionName, checksum = Guid.NewGuid().ToString("N"), filename });

    private static string readEmbeddedMigration(string name)
    {
        var assembly = typeof(Db).GetTypeInfo().Assembly;
        string resource = assembly.GetManifestResourceNames().Single(n => n.EndsWith(name, StringComparison.Ordinal));

        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
