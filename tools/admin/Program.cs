using Dapper;
using Npgsql;

// typebeat-web admin CLI — moderation levers over the M1 schema (see Data/Migrations/001_init.sql).
// Plain console app: args[0] is the command, remaining args its operands. Every command prints
// human-readable output; bad usage or an unknown entity exits non-zero with a helpful message.

if (args.Length == 0)
{
    Usage();
    return 1;
}

// Connection string mirrors Db.ResolveConnectionString: env override, then the localhost default.
string connString = Environment.GetEnvironmentVariable("TYPEBEAT_DB")
                    ?? "Host=localhost;Port=5432;Database=typebeat;Username=typebeat;Password=typebeat";

await using var conn = new NpgsqlConnection(connString);

try
{
    await conn.OpenAsync();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"error: could not connect to database: {ex.Message}");
    return 1;
}

string command = args[0];

try
{
    return command switch
    {
        "list-builds"     => await ListBuilds(conn),
        "block-build"     => await SetBuildBlocked(conn, args, blocked: true),
        "unblock-build"   => await SetBuildBlocked(conn, args, blocked: false),
        "unrank-build"    => await UnrankBuild(conn, args),
        "unrank-score"    => await SetScoreRanked(conn, args, ranked: false),
        "rerank-score"    => await SetScoreRanked(conn, args, ranked: true),
        "restrict-user"   => await SetUserRestricted(conn, args, restricted: true),
        "unrestrict-user" => await SetUserRestricted(conn, args, restricted: false),
        "stats"           => await Stats(conn),
        _                 => Unknown(command),
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}

// ---------------------------------------------------------------------------------------------

static async Task<int> ListBuilds(NpgsqlConnection conn)
{
    var rows = (await conn.QueryAsync(
        """
        SELECT b.id            AS id,
               b.version_hash  AS versionHash,
               b.first_seen_at AS firstSeenAt,
               b.blocked       AS blocked,
               count(s.id)     AS scoreCount
        FROM builds b
        LEFT JOIN scores s ON s.build_id = b.id
        GROUP BY b.id
        ORDER BY b.first_seen_at
        """)).ToList();

    if (rows.Count == 0)
    {
        Console.WriteLine("no builds registered.");
        return 0;
    }

    Console.WriteLine($"{"ID",-6} {"VERSION_HASH",-40} {"FIRST_SEEN",-22} {"BLOCKED",-8} {"SCORES",-7}");
    foreach (var r in rows)
    {
        string firstSeen = ((DateTime)r.firstSeenAt).ToString("yyyy-MM-dd HH:mm:ss");
        Console.WriteLine($"{(long)r.id,-6} {(string)r.versionHash,-40} {firstSeen,-22} {((bool)r.blocked ? "yes" : "no"),-8} {(long)r.scoreCount,-7}");
    }

    return 0;
}

static async Task<int> SetBuildBlocked(NpgsqlConnection conn, string[] args, bool blocked)
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine($"usage: {args[0]} <version_hash>");
        return 1;
    }

    string versionHash = args[1];

    var build = await conn.QuerySingleOrDefaultAsync(
        "SELECT id AS id, blocked AS blocked FROM builds WHERE version_hash = @versionHash",
        new { versionHash });

    if (build is null)
    {
        Console.Error.WriteLine($"error: no build with version_hash '{versionHash}'.");
        return 1;
    }

    await conn.ExecuteAsync(
        "UPDATE builds SET blocked = @blocked WHERE id = @id",
        new { blocked, id = (long)build.id });

    if (blocked)
    {
        long rankedScores = await conn.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM scores WHERE build_id = @id AND ranked",
            new { id = (long)build.id });

        Console.WriteLine($"blocked build '{versionHash}' (id {(long)build.id}); it has {rankedScores} ranked score(s).");
        Console.WriteLine("run 'unrank-build' to retroactively invalidate them.");
    }
    else
    {
        Console.WriteLine($"unblocked build '{versionHash}' (id {(long)build.id}).");
    }

    return 0;
}

static async Task<int> UnrankBuild(NpgsqlConnection conn, string[] args)
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("usage: unrank-build <version_hash>");
        return 1;
    }

    string versionHash = args[1];

    var build = await conn.QuerySingleOrDefaultAsync(
        "SELECT id AS id FROM builds WHERE version_hash = @versionHash",
        new { versionHash });

    if (build is null)
    {
        Console.Error.WriteLine($"error: no build with version_hash '{versionHash}'.");
        return 1;
    }

    int affected = await conn.ExecuteAsync(
        "UPDATE scores SET ranked = false WHERE build_id = @id AND ranked",
        new { id = (long)build.id });

    Console.WriteLine($"unranked {affected} score(s) from build '{versionHash}' (id {(long)build.id}).");
    return 0;
}

static async Task<int> SetScoreRanked(NpgsqlConnection conn, string[] args, bool ranked)
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine($"usage: {args[0]} <score_id>");
        return 1;
    }

    if (!long.TryParse(args[1], out long scoreId))
    {
        Console.Error.WriteLine($"error: score_id '{args[1]}' is not a valid number.");
        return 1;
    }

    var score = await conn.QuerySingleOrDefaultAsync(
        "SELECT ranked AS ranked FROM scores WHERE id = @scoreId",
        new { scoreId });

    if (score is null)
    {
        Console.Error.WriteLine($"error: no score with id {scoreId}.");
        return 1;
    }

    bool before = (bool)score.ranked;

    if (before == ranked)
    {
        Console.WriteLine($"score {scoreId} already ranked={ranked.ToString().ToLowerInvariant()}; no change.");
        return 0;
    }

    await conn.ExecuteAsync(
        "UPDATE scores SET ranked = @ranked WHERE id = @scoreId",
        new { ranked, scoreId });

    Console.WriteLine($"score {scoreId}: ranked {before.ToString().ToLowerInvariant()} -> {ranked.ToString().ToLowerInvariant()}.");
    return 0;
}

static async Task<int> SetUserRestricted(NpgsqlConnection conn, string[] args, bool restricted)
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine($"usage: {args[0]} <username>");
        return 1;
    }

    string username = args[1];

    var user = await conn.QuerySingleOrDefaultAsync(
        "SELECT id AS id, restricted AS restricted FROM users WHERE username = @username",
        new { username });

    if (user is null)
    {
        Console.Error.WriteLine($"error: no user with username '{username}'.");
        return 1;
    }

    long userId = (long)user.id;

    await conn.ExecuteAsync(
        "UPDATE users SET restricted = @restricted WHERE id = @userId",
        new { restricted, userId });

    if (restricted)
    {
        int unranked = await conn.ExecuteAsync(
            "UPDATE scores SET ranked = false WHERE user_id = @userId AND ranked",
            new { userId });

        int revoked = await conn.ExecuteAsync(
            "UPDATE oauth_tokens SET revoked_at = now() WHERE user_id = @userId AND revoked_at IS NULL",
            new { userId });

        Console.WriteLine($"restricted user '{username}' (id {userId}); unranked {unranked} score(s), revoked {revoked} token(s).");
    }
    else
    {
        Console.WriteLine($"unrestricted user '{username}' (id {userId}).");
        Console.WriteLine("note: previously-unranked scores are NOT automatically re-ranked.");
    }

    return 0;
}

static async Task<int> Stats(NpgsqlConnection conn)
{
    var row = await conn.QuerySingleAsync(
        """
        SELECT (SELECT count(*) FROM users)                    AS users,
               (SELECT count(*) FROM scores)                   AS totalScores,
               (SELECT count(*) FROM scores WHERE ranked)      AS rankedScores,
               (SELECT count(*) FROM beatmapsets)              AS beatmapsets,
               (SELECT count(*) FROM beatmaps)                 AS beatmaps
        """);

    Console.WriteLine($"users:       {(long)row.users}");
    Console.WriteLine($"scores:      {(long)row.rankedScores} ranked / {(long)row.totalScores} total");
    Console.WriteLine($"beatmapsets: {(long)row.beatmapsets}");
    Console.WriteLine($"beatmaps:    {(long)row.beatmaps}");
    return 0;
}

// ---------------------------------------------------------------------------------------------

static int Unknown(string command)
{
    Console.Error.WriteLine($"error: unknown command '{command}'.");
    Usage();
    return 1;
}

static void Usage()
{
    Console.Error.WriteLine(
        """
        typebeat admin CLI

        usage: admin <command> [args]

        commands:
          list-builds                     list builds with score counts and blocked state
          block-build <version_hash>      block a build (reports its ranked score count)
          unblock-build <version_hash>    unblock a build
          unrank-build <version_hash>     unrank all ranked scores from a build
          unrank-score <score_id>         mark a score unranked
          rerank-score <score_id>         mark a score ranked
          restrict-user <username>        restrict a user (unranks scores, revokes tokens)
          unrestrict-user <username>      lift a user's restriction
          stats                           summary counts

        database: env TYPEBEAT_DB, else Host=localhost;Port=5432;Database=typebeat;...
        """);
}
