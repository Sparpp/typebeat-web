using System.Collections.Concurrent;
using Dapper;
using Typebeat.Web.Data;

namespace Typebeat.Web.Auth;

/// <summary>
/// Throttled users.last_visit touch (migration 002). Called by <see cref="SessionCookieAuth"/>
/// for cookie-authed website requests; the game-side GET /api/v2/me handler should call it too
/// (Endpoints/ change, deliberately not made here, module boundary). Never called for
/// anonymous requests: the callers only see resolved users.
/// </summary>
public static class LastVisit
{
    /// <summary>How stale last_visit must be before another write is worth a round trip.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(15);

    // Per-process memo so the common case (active user clicking around) costs zero extra DB
    // round trips. The SQL below re-checks staleness, so multiple processes stay correct.
    private static readonly ConcurrentDictionary<long, DateTime> recently_touched = new();

    public static async Task TouchAsync(Db db, long userId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        if (recently_touched.TryGetValue(userId, out var last) && now - last < RefreshInterval)
            return;

        recently_touched[userId] = now;

        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(
            """
            UPDATE users
            SET last_visit = now()
            WHERE id = @userId
              AND (last_visit IS NULL OR last_visit < now() - @interval)
            """,
            new { userId, interval = RefreshInterval });
    }
}
