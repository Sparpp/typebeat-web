using System.Reflection;
using System.Text;
using Dapper;
using Npgsql;

namespace Typebeat.Web.Data;

/// <summary>
/// Thin wrapper around a pooled <see cref="NpgsqlDataSource"/> plus the SQL-first migrator.
/// All data access goes through Dapper against hand-written SQL — the schema in
/// Data/Migrations is the single source of truth, no ORM-generated DDL.
/// </summary>
public sealed class Db(NpgsqlDataSource dataSource)
{
    public NpgsqlDataSource DataSource { get; } = dataSource;

    public async Task<NpgsqlConnection> OpenAsync(CancellationToken ct = default)
        => await DataSource.OpenConnectionAsync(ct);

    public static string ResolveConnectionString(IConfiguration config)
        => Environment.GetEnvironmentVariable("TYPEBEAT_DB")
           ?? config.GetConnectionString("typebeat")
           ?? "Host=localhost;Port=5432;Database=typebeat;Username=typebeat;Password=typebeat";

    /// <summary>
    /// Creates required Postgres extensions on a throwaway connection, before the pooled
    /// <see cref="NpgsqlDataSource"/> is built. See the call site in Program.cs: the pool caches
    /// the type catalog on first connect, so citext must exist first or its OID is never learned.
    /// Idempotent (also declared in migration 001); citext is trusted so the DB owner can create it.
    /// </summary>
    public static async Task EnsureExtensionsAsync(string connectionString, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await conn.ExecuteAsync("CREATE EXTENSION IF NOT EXISTS citext");
    }

    /// <summary>
    /// Applies embedded Data/Migrations/*.sql in filename order, recording each in
    /// schema_migrations. Each migration runs in its own transaction.
    /// </summary>
    public async Task MigrateAsync(ILogger logger, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);

        await conn.ExecuteAsync(
            """
            CREATE TABLE IF NOT EXISTS schema_migrations
            (
                name       text PRIMARY KEY,
                applied_at timestamptz NOT NULL DEFAULT now()
            )
            """);

        var applied = (await conn.QueryAsync<string>("SELECT name FROM schema_migrations")).ToHashSet();

        var assembly = Assembly.GetExecutingAssembly();

        var migrations = assembly.GetManifestResourceNames()
                                 .Where(n => n.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
                                 .OrderBy(n => n, StringComparer.Ordinal);

        foreach (string resource in migrations)
        {
            // "Typebeat.Web.Data.Migrations.001_init.sql" -> "001_init.sql"
            string name = resource.Split(".Migrations.").Last();

            if (applied.Contains(name))
                continue;

            logger.LogInformation("Applying migration {Name}...", name);

            await using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            string sql = await reader.ReadToEndAsync(ct);

            await using var tx = await conn.BeginTransactionAsync(ct);
            await conn.ExecuteAsync(sql, transaction: tx);
            await conn.ExecuteAsync("INSERT INTO schema_migrations (name) VALUES (@name)", new { name }, tx);
            await tx.CommitAsync(ct);

            logger.LogInformation("Migration {Name} applied.", name);
        }
    }
}
