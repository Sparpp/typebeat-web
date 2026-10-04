using System.Text;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Typebeat.Web.Data;

namespace Typebeat.Web.Tests;

/// <summary>
/// Migration 024's backfill against a database in the pre-deploy state: 001–023 applied, with a
/// history of score rows spread over months and across two UTC month boundaries. 024 must create
/// <c>user_month_playcounts</c> and derive every month from <c>scores.ended_at</c>, bucketed in
/// UTC, one row per play. Same harness shape as <see cref="Migration004BackfillTest"/>.
/// </summary>
[TestFixture]
[NonParallelizable]
public class Migration024PlayHistoryTest
{
    private const string database_name = "typebeat_migr024tests";

    private const string connection_string =
        "Host=localhost;Port=5432;Database=" + database_name + ";Username=postgres;Password=postgres";

    private const string admin_connection_string =
        "Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=postgres";

    private static readonly string[] migrations_before_024 =
    [
        "001_init.sql", "002_website_uploads.sql", "003_anonymous_downloads.sql",
        "004_prem3_filename_backfill.sql", "005_ranked_approval.sql", "006_email_verification.sql",
        "007_account_settings.sql", "008_completion_rank.sql", "009_boundary_pace.sql",
        "010_backfill_play_counts.sql", "011_user_preferences.sql", "012_unranked_status.sql",
        "013_explicit_flag.sql", "014_replay_storage.sql", "015_ht_nerf_rescore.sql",
        "016_refund_skip_gate.sql", "017_rate_gate_refund.sql", "018_lyrics_search.sql",
        "019_language.sql", "020_performance_points.sql", "022_score_pins.sql", "023_follows.sql",
    ];

    private NpgsqlDataSource dataSource = null!;

    private long veteranId;   // plays across three months, including both boundary cases
    private long newcomerId;  // one play, one month
    private long lurkerId;    // an account with no scores at all

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

        await using (var conn = new NpgsqlConnection(connection_string))
        {
            await conn.OpenAsync();

            await conn.ExecuteAsync(
                "CREATE TABLE schema_migrations (name text PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now())");

            foreach (string migration in migrations_before_024)
            {
                await conn.ExecuteAsync(readEmbeddedMigration(migration));
                await conn.ExecuteAsync("INSERT INTO schema_migrations (name) VALUES (@migration)", new { migration });
            }

            // Mark every LATER migration applied without running it, so MigrateAsync applies exactly
            // its own target. 045 re-grades PASSED rows on accuracy, which would otherwise move ranks
            // other fixtures assert untouched.
            await conn.ExecuteAsync("INSERT INTO schema_migrations (name) VALUES (@migration)",
                new { migration = "045_accuracy_rank_regrade.sql" });

            veteranId = await insertUserAsync(conn, "history veteran");
            newcomerId = await insertUserAsync(conn, "history newcomer");
            lurkerId = await insertUserAsync(conn, "history lurker");

            long setId = await conn.ExecuteScalarAsync<long>(
                "INSERT INTO beatmapsets (owner_id, title, artist, status) VALUES (@veteranId, 'Backfill Ballad', 'The Historians', 'ranked') RETURNING id",
                new { veteranId });

            long beatmapId = await conn.ExecuteScalarAsync<long>(
                """
                INSERT INTO beatmaps (set_id, version_name, checksum_md5, total_length_s, drain_length_s, filename)
                VALUES (@setId, 'type!beat', @checksum, 60, 54, 'map.osu')
                RETURNING id
                """,
                new { setId, checksum = Guid.NewGuid().ToString("N") });

            // January: three plays, one of them a FAIL. Fails count toward user_stats.play_count,
            // so they must count here too or the chart's total would undershoot the stat.
            await insertScoreAsync(conn, veteranId, beatmapId, "2026-01-05 12:00:00+00");
            await insertScoreAsync(conn, veteranId, beatmapId, "2026-01-19 09:30:00+00");
            await insertScoreAsync(conn, veteranId, beatmapId, "2026-01-31 23:59:59+00", passed: false);

            // February: skipped entirely. The backfill must NOT invent a zero row for it (the
            // chart fills gaps on read); a stored row would claim a month that never happened.

            // March: the two boundary cases, an hour either side of midnight UTC.
            await insertScoreAsync(conn, veteranId, beatmapId, "2026-02-28 23:30:00+00"); // still February
            await insertScoreAsync(conn, veteranId, beatmapId, "2026-03-01 00:30:00+00"); // March

            // Written with a non-UTC offset: 23:00 on 31 March in UTC-5 is 04:00 on 1 April in
            // UTC, so this play belongs to APRIL. The bucket follows the instant, not the text.
            await insertScoreAsync(conn, veteranId, beatmapId, "2026-03-31 23:00:00-05");

            await insertScoreAsync(conn, newcomerId, beatmapId, "2026-01-14 18:00:00+00");
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
    public async Task Backfill_CountsEveryScoreRow_IntoItsUtcMonth()
    {
        await using var conn = await dataSource.OpenConnectionAsync();

        var months = await monthsOf(conn, veteranId);

        Assert.That(months, Is.EqualTo(new[]
        {
            (new DateOnly(2026, 1, 1), 3), // two passes and a fail
            (new DateOnly(2026, 2, 1), 1), // 28 Feb 23:30 UTC
            (new DateOnly(2026, 3, 1), 1), // 1 Mar 00:30 UTC
            (new DateOnly(2026, 4, 1), 1), // 31 Mar 23:00 UTC-5 == 1 Apr 04:00 UTC
        }));
    }

    [Test]
    public async Task Backfill_LeavesEmptyMonthsUnrecorded()
    {
        await using var conn = await dataSource.OpenConnectionAsync();

        // The veteran played in January and February but the gap-filling belongs to the read path,
        // so no row exists for a month with no plays at either end of the range.
        bool anyZero = await conn.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM user_month_playcounts WHERE plays = 0)");

        Assert.That(anyZero, Is.False, "the rollup stores plays, not a dense calendar");
    }

    [Test]
    public async Task Backfill_IsPerUser_AndSkipsAccountsWithNoPlays()
    {
        await using var conn = await dataSource.OpenConnectionAsync();

        var newcomer = await monthsOf(conn, newcomerId);
        var lurker = await monthsOf(conn, lurkerId);

        Assert.Multiple(() =>
        {
            Assert.That(newcomer, Is.EqualTo(new[] { (new DateOnly(2026, 1, 1), 1) }));
            Assert.That(lurker, Is.Empty, "an account that never played has no history at all");
        });
    }

    [Test]
    public async Task Rollup_KeyedByUserAndMonth_AndCascadesFromUsers()
    {
        await using var conn = await dataSource.OpenConnectionAsync();

        // The primary key is what makes the write path's upsert atomic; a second row for the same
        // month must be impossible rather than merely unwritten.
        Assert.That(
            async () => await conn.ExecuteAsync(
                "INSERT INTO user_month_playcounts (user_id, month, plays) VALUES (@veteranId, '2026-01-01', 1)",
                new { veteranId }),
            Throws.Exception.With.Message.Contains("user_month_playcounts_pkey"));

        // Users are never hard-deleted today, but the FK says what happens when one is.
        long doomed = await insertUserAsync(conn, "history doomed");
        await conn.ExecuteAsync(
            "INSERT INTO user_month_playcounts (user_id, month, plays) VALUES (@doomed, '2026-01-01', 4)",
            new { doomed });
        await conn.ExecuteAsync("DELETE FROM users WHERE id = @doomed", new { doomed });

        Assert.That(await monthsOf(conn, doomed), Is.Empty);
    }

    // ---- helpers ----

    private static async Task<List<(DateOnly Month, int Plays)>> monthsOf(NpgsqlConnection conn, long userId)
        => (await conn.QueryAsync<(DateOnly, int)>(
            "SELECT month, plays FROM user_month_playcounts WHERE user_id = @userId ORDER BY month",
            new { userId })).ToList();

    private static Task<long> insertUserAsync(NpgsqlConnection conn, string username)
        => conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO users (username, email, password_hash, country_code)
            VALUES (@username, @username || '@example.com', 'x', 'US')
            RETURNING id
            """,
            new { username });

    private static Task insertScoreAsync(NpgsqlConnection conn, long userId, long beatmapId, string endedAt, bool passed = true)
        => conn.ExecuteAsync(
            """
            INSERT INTO scores
                (user_id, beatmap_id, total_score, accuracy, completion, max_combo, rank, passed, ranked,
                 mods, statistics, maximum_statistics, ended_at)
            VALUES
                (@userId, @beatmapId, 100000, 0.9, 0.9, 10, @rank, @passed, @passed,
                 '[]'::jsonb, '{"great":10}'::jsonb, '{"great":10}'::jsonb, @endedAt::timestamptz)
            """,
            new { userId, beatmapId, passed, rank = passed ? "S" : "F", endedAt });

    private static string readEmbeddedMigration(string name)
    {
        var assembly = typeof(Db).Assembly;
        string resource = assembly.GetManifestResourceNames().Single(n => n.EndsWith(name, StringComparison.Ordinal));

        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
