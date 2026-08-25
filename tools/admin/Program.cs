using System.Security.Cryptography;
using System.Text;
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
        "issue-token"     => await IssueToken(conn, args),
        "revoke-token"    => await RevokeToken(conn, args),
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

// ---------------------------------------------------------------------------------------------
// Short-lived bearer tokens, the lever behind the "Origin-side ingest" runbook in deploy/README.md:
// when a user's upload dies between their client and the origin, a maintainer curls the /bss
// endpoints from inside the prod docker network holding a token that acts AS that user.
//
// src/Typebeat.Web/Auth/TokenService.cs is the SOURCE OF TRUTH for the shape below; what follows
// is a deliberate small replication of TokenService.IssueAsync + newToken(), so this stays a plain
// Dapper/Npgsql console app instead of dragging the ASP.NET hosting graph in via a project
// reference. TokenShapeDrifted() is what keeps the replication honest: it refuses to mint anything
// once the oauth_tokens shape stops matching what is replicated here.
// ---------------------------------------------------------------------------------------------

static async Task<int> IssueToken(NpgsqlConnection conn, string[] args)
{
    // Deliberately far below TokenService.ACCESS_LIFETIME_SECONDS (24h): this credential exists
    // for the length of one maintainer curl session, and the op still revokes it afterwards.
    const int default_minutes = 30;
    const int max_minutes = 240;

    if (args.Length < 2)
    {
        Console.Error.WriteLine($"usage: issue-token <username> [minutes] (default {default_minutes}, max {max_minutes})");
        return 1;
    }

    string username = args[1];
    int minutes = default_minutes;

    if (args.Length >= 3 && (!int.TryParse(args[2], out minutes) || minutes < 1 || minutes > max_minutes))
    {
        Console.Error.WriteLine($"error: minutes '{args[2]}' must be a whole number between 1 and {max_minutes}.");
        return 1;
    }

    // Checked before anything else: on a drifted (or wrong) database, mint nothing at all.
    if (await TokenShapeDrifted(conn))
        return 1;

    var user = await conn.QuerySingleOrDefaultAsync(
        "SELECT id AS id, restricted AS restricted, verified_at AS verifiedAt FROM users WHERE username = @username",
        new { username });

    if (user is null)
    {
        Console.Error.WriteLine($"error: no user with username '{username}'.");
        return 1;
    }

    long userId = (long)user.id;

    // Mirrors TokenService.IssueAsync: one row carrying both hashes. The refresh half has to be
    // minted (refresh_hash is NOT NULL UNIQUE) but is NEVER printed, and it expires WITH the
    // access half rather than after TokenService's 30 days, so a forgotten cleanup still cannot
    // leave a long-lived credential behind.
    (string access, byte[] accessHash) = NewToken();
    (_, byte[] refreshHash) = NewToken();

    var issued = await conn.QuerySingleAsync(
        """
        INSERT INTO oauth_tokens (user_id, access_hash, refresh_hash, access_expires_at, refresh_expires_at)
        VALUES (@userId, @accessHash, @refreshHash,
                now() + make_interval(mins => @minutes),
                now() + make_interval(mins => @minutes))
        RETURNING id AS id, access_expires_at AS expiresAt
        """,
        new { userId, accessHash, refreshHash, minutes });

    long tokenId = (long)issued.id;
    string expires = ((DateTime)issued.expiresAt).ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss");

    Console.WriteLine($"issued token {tokenId} for '{username}' (id {userId}), valid {minutes} minute(s), until {expires} UTC.");

    if ((bool)user.restricted)
        Console.WriteLine("warning: this user is RESTRICTED, so the bearer path treats the token as signed out (401 on every call).");

    if (user.verifiedAt is null)
        Console.WriteLine("warning: this user is not email-verified, so /bss submission will 422 until they verify.");

    Console.WriteLine();
    Console.WriteLine(access);
    Console.WriteLine();
    Console.WriteLine($"revoke it as soon as the op is done: admin revoke-token {tokenId}");
    return 0;
}

static async Task<int> RevokeToken(NpgsqlConnection conn, string[] args)
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("usage: revoke-token <token_id>");
        return 1;
    }

    if (!long.TryParse(args[1], out long tokenId))
    {
        Console.Error.WriteLine($"error: token_id '{args[1]}' is not a valid number.");
        return 1;
    }

    var token = await conn.QuerySingleOrDefaultAsync(
        """
        SELECT t.user_id AS userId, u.username AS username, t.revoked_at AS revokedAt
        FROM oauth_tokens t
        JOIN users u ON u.id = t.user_id
        WHERE t.id = @tokenId
        """,
        new { tokenId });

    if (token is null)
    {
        Console.Error.WriteLine($"error: no token with id {tokenId}.");
        return 1;
    }

    string owner = (string)token.username;

    if (token.revokedAt is not null)
    {
        Console.WriteLine($"token {tokenId} (user '{owner}') is already revoked; no change.");
        return 0;
    }

    // Same UPDATE TokenService.RevokeByAccessTokenAsync runs, addressed by row id because the
    // maintainer has the token id from issue-token, not the token text.
    await conn.ExecuteAsync("UPDATE oauth_tokens SET revoked_at = now() WHERE id = @tokenId", new { tokenId });

    Console.WriteLine($"revoked token {tokenId} (user '{owner}', id {(long)token.userId}).");
    return 0;
}

/// <summary>
/// True (having printed why) when oauth_tokens no longer matches the shape IssueToken replicates.
/// The INSERT already breaks loudly if a replicated column is renamed or dropped; this catches the
/// case that would otherwise pass silently, a migration adding another required column that the
/// replication does not populate. Either way the fix is the same: re-read
/// src/Typebeat.Web/Auth/TokenService.cs and bring IssueToken back in line with it.
/// </summary>
static async Task<bool> TokenShapeDrifted(NpgsqlConnection conn)
{
    string[] written = ["user_id", "access_hash", "refresh_hash", "access_expires_at", "refresh_expires_at"];

    var present = new HashSet<string>();
    var unpopulated = new List<string>();

    var columns = await conn.QueryAsync(
        """
        SELECT column_name AS name,
               (is_nullable = 'NO' AND column_default IS NULL) AS required
        FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'oauth_tokens'
        """);

    foreach (var column in columns)
    {
        string name = (string)column.name;
        present.Add(name);

        if ((bool)column.required && !written.Contains(name))
            unpopulated.Add(name);
    }

    if (present.Count == 0)
    {
        Console.Error.WriteLine("error: table oauth_tokens does not exist; is this the typebeat database?");
        return true;
    }

    var missing = written.Where(c => !present.Contains(c)).ToList();

    if (missing.Count == 0 && unpopulated.Count == 0)
        return false;

    Console.Error.WriteLine("error: oauth_tokens has drifted from this tool's replication of TokenService.");

    if (missing.Count > 0)
        Console.Error.WriteLine($"  written here but absent from the table: {string.Join(", ", missing)}");

    if (unpopulated.Count > 0)
        Console.Error.WriteLine($"  required by the table but not written here: {string.Join(", ", unpopulated)}");

    Console.Error.WriteLine("  fix: re-read src/Typebeat.Web/Auth/TokenService.cs and update issue-token to match.");
    return true;
}

/// <summary>
/// TokenService.newToken() replicated: 32 random bytes, base64url (unpadded) on the wire,
/// SHA-256 of the UTF-8 token text at rest.
/// </summary>
static (string Token, byte[] Hash) NewToken()
{
    byte[] raw = RandomNumberGenerator.GetBytes(32);
    string token = Convert.ToBase64String(raw).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    return (token, SHA256.HashData(Encoding.UTF8.GetBytes(token)));
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
          issue-token <username> [mins]   mint a short-lived bearer token acting as a user
          revoke-token <token_id>         revoke one issued token (the cleanup half)
          stats                           summary counts

        issue-token/revoke-token back the "Origin-side ingest" runbook in deploy/README.md.

        database: env TYPEBEAT_DB, else Host=localhost;Port=5432;Database=typebeat;...
        """);
}
