using System.Globalization;
using Dapper;
using Typebeat.Web.Data;

namespace Typebeat.Web.Endpoints;

/// <summary>
/// The score feed consumed by the Discord bot ("Buddy", discord-buddybot).
///
/// This is deliberately NOT part of the osu-compatible /api/v2 wire surface that the game client
/// speaks: it is a private integration feed, shaped for a poller. It is guarded by a single static
/// key (<c>TYPEBEAT_BUDDY_KEY</c>) rather than OAuth, because the consumer is a service, not a
/// user, and issuing it a user token would give it far more authority than reading recent plays.
/// When the key is unset the endpoint 404s, so a deploy that has not opted in exposes nothing.
///
/// The contract is cursor-based: the caller passes the highest score id it has already handled and
/// gets the next batch in ascending id order. That makes the poller idempotent and restart-safe
/// without any server-side per-consumer state, and it can never silently skip a score the way a
/// timestamp window can when clocks or long transactions misbehave.
///
/// Rows are enriched with the two facts a notification needs but a client cannot cheaply derive:
/// the play's CURRENT position on its map's leaderboard, and whether it is the player's own best.
/// Deriving those here keeps the leaderboard rules (best-per-user, and which board a set's status
/// selects) in one place instead of reimplemented in the bot.
/// </summary>
public static class BuddyEndpoints
{
    /// <summary>Header carrying the shared key. Chosen over Authorization so it cannot be confused with a bearer token.</summary>
    public const string KEY_HEADER = "X-Buddy-Key";

    private const int default_limit = 50;
    private const int max_limit = 200;

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v2/scores/recent", RecentAsync);
    }

    private static async Task<IResult> RecentAsync(HttpContext ctx, Db db, IConfiguration config)
    {
        string? expected = config["TYPEBEAT_BUDDY_KEY"];

        // Not configured = feature off. 404 (not 401) so an un-opted-in deployment does not even
        // advertise that the endpoint exists.
        if (string.IsNullOrWhiteSpace(expected))
            return Results.NotFound();

        string? provided = ctx.Request.Headers[KEY_HEADER];

        if (string.IsNullOrEmpty(provided) || !CryptographicEquals(provided, expected))
            return Results.Unauthorized();

        long afterId = 0;
        if (long.TryParse(ctx.Request.Query["after_id"], NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsedAfter))
            afterId = Math.Max(0, parsedAfter);

        int limit = default_limit;
        if (int.TryParse(ctx.Request.Query["limit"], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedLimit))
            limit = Math.Clamp(parsedLimit, 1, max_limit);

        await using var conn = await db.OpenAsync(ctx.RequestAborted);

        // Only PASSED plays on a published set, and only rows that sit on their set's applicable
        // board: a ranked set's board is its ranked rows, a pending/unranked set's board is its
        // unranked rows (every play on a non-ranked set is stored unranked by construction). This
        // mirrors ScoreEndpoints.Leaderboard so a position reported here matches the site exactly.
        // Hidden sets are excluded outright.
        var rows = await conn.QueryAsync<RecentScoreRow>(
            """
            SELECT s.id                                             AS Id,
                   s.user_id                                        AS UserId,
                   u.username                                       AS Username,
                   u.avatar_key                                     AS AvatarKey,
                   s.beatmap_id                                     AS BeatmapId,
                   bs.id                                            AS SetId,
                   bs.artist                                        AS Artist,
                   bs.title                                         AS Title,
                   b.version_name                                   AS VersionName,
                   bs.status                                        AS SetStatus,
                   b.difficulty_rating                              AS StarRating,
                   s.total_score                                    AS TotalScore,
                   -- Performance points for this play (task 61). 0 means either a genuinely
                   -- worthless play or one not yet recomputed at the current formula version, so
                   -- the bot treats 0 as "nothing to show" rather than "zero pp earned".
                   s.pp                                             AS Pp,
                   s.rank                                           AS Rank,
                   s.accuracy                                       AS Accuracy,
                   s.max_combo                                      AS MaxCombo,
                   COALESCE((s.maximum_statistics->>'great')::int, 0) AS Notes,
                   COALESCE((s.statistics->>'miss')::int, 0)          AS MissCount,
                   s.mods::text                                     AS ModsJson,
                   s.ranked                                         AS Ranked,
                   s.ended_at                                       AS EndedAt,
                   (s.replay_key IS NOT NULL)                       AS HasReplay,
                   -- Current position on the map's board: one plus the number of OTHER players
                   -- whose best on that board beats this score.
                   (SELECT count(*) + 1
                      FROM (SELECT o.user_id, max(o.total_score) AS best
                              FROM scores o
                              JOIN beatmaps ob  ON ob.id = o.beatmap_id
                              JOIN beatmapsets obs ON obs.id = ob.set_id
                             WHERE o.beatmap_id = s.beatmap_id
                               AND o.passed
                               AND o.ranked = (obs.status = 'ranked')
                               AND o.user_id <> s.user_id
                             GROUP BY o.user_id) peers
                     WHERE peers.best > s.total_score)              AS Position,
                   -- Is this the player's own best on this board? Distinguishes a genuine
                   -- improvement from a lesser replay that still happens to sit high.
                   (s.total_score = (SELECT max(m.total_score)
                                       FROM scores m
                                      WHERE m.beatmap_id = s.beatmap_id
                                        AND m.user_id = s.user_id
                                        AND m.passed
                                        AND m.ranked = s.ranked))    AS IsPersonalBest
            FROM scores s
            JOIN users u        ON u.id = s.user_id
            JOIN beatmaps b     ON b.id = s.beatmap_id
            JOIN beatmapsets bs ON bs.id = b.set_id
            WHERE s.id > @afterId
              AND s.passed
              AND bs.status IN ('ranked', 'pending', 'unranked')
              AND s.ranked = (bs.status = 'ranked')
              -- Never announce a restricted or deleted account's play.
              AND NOT u.restricted
              AND u.deleted_at IS NULL
            ORDER BY s.id
            LIMIT @limit
            """,
            new { afterId, limit });

        var list = rows.ToList();

        return Results.Json(new
        {
            // Echoed so the poller can advance its cursor without re-scanning the payload, and so
            // an empty batch still tells it where it stands.
            cursor = list.Count > 0 ? list[^1].Id : afterId,
            count = list.Count,
            scores = list,
        });
    }

    /// <summary>
    /// Length-independent, content-constant-time comparison, so the key cannot be recovered by
    /// timing the response.
    /// </summary>
    private static bool CryptographicEquals(string a, string b)
    {
        byte[] left = System.Text.Encoding.UTF8.GetBytes(a);
        byte[] right = System.Text.Encoding.UTF8.GetBytes(b);

        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Security.Cryptography.SHA256.HashData(left),
            System.Security.Cryptography.SHA256.HashData(right));
    }

    /// <summary>
    /// Mapped by NAME (not a positional record) on purpose: the SELECT above is long and will be
    /// edited, and positional mapping breaks silently when a column moves.
    /// </summary>
    public sealed class RecentScoreRow
    {
        public long Id { get; set; }
        public long UserId { get; set; }
        public string Username { get; set; } = string.Empty;
        /// <summary>Storage key (avatars/{id}/{v}.jpg); the bot composes the absolute URL from its site base.</summary>
        public string? AvatarKey { get; set; }
        public long BeatmapId { get; set; }
        public long SetId { get; set; }
        public string? Artist { get; set; }
        public string? Title { get; set; }
        public string? VersionName { get; set; }
        public string SetStatus { get; set; } = string.Empty;
        public double StarRating { get; set; }
        public long TotalScore { get; set; }
        public double Pp { get; set; }
        public string? Rank { get; set; }
        public double Accuracy { get; set; }
        public int MaxCombo { get; set; }
        public int Notes { get; set; }
        public int MissCount { get; set; }
        public string? ModsJson { get; set; }
        public bool Ranked { get; set; }
        public DateTimeOffset EndedAt { get; set; }
        public bool HasReplay { get; set; }
        public int Position { get; set; }
        public bool IsPersonalBest { get; set; }
    }
}
