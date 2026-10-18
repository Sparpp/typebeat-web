using Dapper;
using Npgsql;

namespace Typebeat.WireCompat;

/// <summary>
/// Scratch databases for the DB-backed fixtures. Each fixture names its own by a fixed prefix
/// (distinct across fixtures) plus a per-run suffix, so two runs sharing one Postgres (two
/// checkouts, or a run started while another is still going) never force-drop each other's
/// database mid-run. Fixtures create theirs in setup and drop it again in teardown.
///
/// The admin connection (a role allowed to CREATE DATABASE, on any database it can reach,
/// normally "postgres") comes from <see cref="AdminConnectionVariable"/> when set, else the local
/// superuser that CI's service container also provides. Every test database connection string is
/// derived from it, so one variable moves the whole suite to another server.
///
/// Kept identical to the copy in tests/Typebeat.Web.Tests (separate assemblies, no shared source).
/// </summary>
public static class TestDatabase
{
    /// <summary>Environment variable holding the admin connection string; unset or empty means the local default.</summary>
    public const string AdminConnectionVariable = "TYPEBEAT_TEST_PG_ADMIN";

    private const string default_admin_connection_string =
        "Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=postgres";

    /// <summary>The connection every fixture creates and drops its database through.</summary>
    public static string AdminConnectionString { get; } =
        Environment.GetEnvironmentVariable(AdminConnectionVariable) is { Length: > 0 } configured
            ? configured
            : default_admin_connection_string;

    /// <summary>
    /// Eight lowercase hex characters, fixed for the life of the test process. Postgres truncates
    /// identifiers past 63 bytes, so keep prefixes short and lowercase. A run that is killed before
    /// its teardown leaves its databases behind under names no later run reuses; list them with
    /// <c>SELECT format('DROP DATABASE %I WITH (FORCE);', datname) FROM pg_database WHERE datname ~ '^typebeat_.*_[0-9a-f]{8}$'</c>
    /// and run the output. Setup never sweeps them, since that would kill a concurrent run's databases.
    /// </summary>
    public static string RunId { get; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>This run's database name for a fixture's <paramref name="prefix"/>.</summary>
    public static string NameFor(string prefix) => prefix + "_" + RunId;

    /// <summary>The admin connection string pointed at <paramref name="databaseName"/> instead.</summary>
    public static string ConnectionStringFor(string databaseName)
        => new NpgsqlConnectionStringBuilder(AdminConnectionString) { Database = databaseName }.ConnectionString;

    /// <summary>
    /// Drops and creates <paramref name="databaseName"/>. FORCE terminates any lingering
    /// connections (Postgres 13+); neither statement may run inside a transaction, so they are
    /// executed as standalone commands.
    /// </summary>
    public static async Task RecreateAsync(string databaseName)
    {
        await using var conn = new NpgsqlConnection(AdminConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync($"DROP DATABASE IF EXISTS {databaseName} WITH (FORCE)");
        await conn.ExecuteAsync($"CREATE DATABASE {databaseName}");
    }

    /// <summary>
    /// Best-effort teardown drop. Tolerates the database already being gone, and a server that
    /// cannot be reached (NUnit runs teardown even when setup failed for exactly that reason).
    /// </summary>
    public static async Task DropAsync(string databaseName)
    {
        try
        {
            await using var conn = new NpgsqlConnection(AdminConnectionString);
            await conn.OpenAsync();
            await conn.ExecuteAsync($"DROP DATABASE IF EXISTS {databaseName} WITH (FORCE)");
        }
        catch (NpgsqlException)
        {
            // Nothing left to drop, or nothing reachable to drop it on; neither fails the run.
        }
    }
}
