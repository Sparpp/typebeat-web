using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Typebeat.Web.Data;

namespace Typebeat.Web.Tests;

/// <summary>
/// <see cref="Db.MigrateAsync"/> run by two processes at once (a rollback overlapping a deploy):
/// the advisory lock must serialise them, so both succeed and every migration lands exactly once.
/// Same local Postgres pattern as <see cref="PackageIngestDbTest"/>: a DEDICATED drop+recreated
/// database, superuser postgres/postgres.
/// </summary>
[TestFixture]
[NonParallelizable]
public class MigrateLockTest
{
    // NOTE: distinct from every other fixture's database; each fixture force-drops its own.
    private static readonly string database_name = TestDatabase.NameFor("typebeat_migratelocktests");

    private static readonly string connection_string = TestDatabase.ConnectionStringFor(database_name);

    [SetUp]
    public async Task SetUp()
    {
        await TestDatabase.RecreateAsync(database_name);

        await Db.EnsureExtensionsAsync(connection_string);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        await TestDatabase.DropAsync(database_name);
    }

    [Test]
    public async Task ConcurrentMigrate_AppliesEveryMigrationOnce_AndReleasesTheLock()
    {
        // Two data sources, so the two passes share no pooled connection, as two processes would not.
        await using var first = NpgsqlDataSource.Create(connection_string);
        await using var second = NpgsqlDataSource.Create(connection_string);

        await Task.WhenAll(
            new Db(first).MigrateAsync(NullLogger.Instance),
            new Db(second).MigrateAsync(NullLogger.Instance));

        int embedded = typeof(Db).Assembly.GetManifestResourceNames()
                                 .Count(n => n.Contains(".Data.Migrations.", StringComparison.Ordinal)
                                             && n.EndsWith(".sql", StringComparison.OrdinalIgnoreCase));

        await using var conn = new NpgsqlConnection(connection_string);
        await conn.OpenAsync();

        long recorded = await conn.ExecuteScalarAsync<long>("SELECT count(*) FROM schema_migrations");
        long held = await conn.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND database = (SELECT oid FROM pg_database WHERE datname = current_database())");

        Assert.Multiple(() =>
        {
            Assert.That(embedded, Is.GreaterThan(0));
            Assert.That(recorded, Is.EqualTo(embedded));
            Assert.That(held, Is.Zero, "the session lock is released after the pass");
        });
    }
}
