using Dapper;
using Npgsql;

namespace Typebeat.Web.Scoring;

/// <summary>
/// Outcome of a pin attempt. Everything except <see cref="Pinned"/> and <see cref="AlreadyPinned"/>
/// is a refusal the UI never offers (the pin control only renders on your own ranked rows), so a
/// refusal means a hand-made POST or a stale page, and the caller answers accordingly.
/// </summary>
public enum PinResult
{
    /// <summary>The pin row was created.</summary>
    Pinned,

    /// <summary>Already pinned; the request is a no-op (pinning is idempotent, not an error).</summary>
    AlreadyPinned,

    /// <summary>No such score, or it belongs to somebody else. Indistinguishable on purpose.</summary>
    NotYours,

    /// <summary>The score exists and is yours, but it is not ranked, so it cannot be pinned.</summary>
    NotRanked,

    /// <summary>The user already holds <see cref="ScorePins.MaxPins"/> pins.</summary>
    LimitReached,
}

/// <summary>
/// Pinned scores: the write half of the profile's "Pinned" section (see migration
/// 022_score_pins.sql). Ownership, rankedness and the per-user cap are ALL enforced here, on the
/// server, because the UI gating (controls only on your own ranked rows) is cosmetic, anyone can
/// POST the handler directly.
///
/// The read half stays in the profile page: the section is one more score-row query alongside
/// Best/Recent and must share their column list and visibility filters, so splitting it out here
/// would only separate it from the thing it has to stay identical to.
/// </summary>
public static class ScorePins
{
    /// <summary>Pins a user may hold at once (osu-web's cap too). The 11th is refused.</summary>
    public const int MaxPins = 10;

    /// <summary>
    /// Pins <paramref name="scoreId"/> for <paramref name="userId"/>, enforcing ownership,
    /// rankedness and the cap.
    ///
    /// The whole check-then-insert runs under a per-user advisory lock in one transaction (the
    /// pattern PackageIngest uses per set), so two concurrent pin requests from the same account
    /// cannot both read "9 pins" and land an 11th. The lock is xact-scoped, so it is released by
    /// the commit or by the rollback that disposal performs on every refusal path.
    /// </summary>
    public static async Task<PinResult> PinAsync(NpgsqlConnection conn, long userId, long scoreId, CancellationToken ct = default)
    {
        await using var tx = await conn.BeginTransactionAsync(ct);

        await conn.ExecuteAsync(
            "SELECT pg_advisory_xact_lock(hashtextextended('score-pin:' || @userId::text, 0))",
            new { userId }, tx);

        // Ownership is a WHERE clause, not a comparison the caller could get wrong: a score that
        // is not this user's simply does not come back.
        var score = await conn.QuerySingleOrDefaultAsync<(bool Ranked, bool Pinned)?>(
            """
            SELECT sc.ranked AS Ranked,
                   EXISTS (SELECT 1 FROM score_pins p WHERE p.score_id = sc.id) AS Pinned
            FROM scores sc
            WHERE sc.id = @scoreId AND sc.user_id = @userId
            """,
            new { scoreId, userId }, tx);

        if (score is not { } row)
            return PinResult.NotYours;

        // Checked before rankedness: a score that was pinned while ranked and later unranked by
        // an admin keeps its (now hidden) pin row, and re-POSTing must not read as a new refusal.
        if (row.Pinned)
            return PinResult.AlreadyPinned;

        if (!row.Ranked)
            return PinResult.NotRanked;

        int pins = await conn.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM score_pins WHERE user_id = @userId",
            new { userId }, tx);

        if (pins >= MaxPins)
            return PinResult.LimitReached;

        await conn.ExecuteAsync(
            "INSERT INTO score_pins (score_id, user_id) VALUES (@scoreId, @userId)",
            new { scoreId, userId }, tx);

        await tx.CommitAsync(ct);
        return PinResult.Pinned;
    }

    /// <summary>
    /// Unpins <paramref name="scoreId"/>. The user_id predicate is the ownership check, so a
    /// forged score id belonging to somebody else deletes nothing. Returns whether a row went.
    /// Unpinning is never blocked by rankedness: a score that stopped being ranked while pinned
    /// must still be removable (its row is hidden from the section, but the pin still counts
    /// against the cap).
    /// </summary>
    public static async Task<bool> UnpinAsync(NpgsqlConnection conn, long userId, long scoreId)
        => await conn.ExecuteAsync(
            "DELETE FROM score_pins WHERE score_id = @scoreId AND user_id = @userId",
            new { scoreId, userId }) > 0;

    /// <summary>
    /// Score ids this user has pinned, for marking the pin control's on/off state on rows the
    /// profile renders elsewhere (Best/Recent). Includes pins whose score is currently hidden
    /// from the section, so its control still reads "unpin" wherever the row does show.
    /// </summary>
    public static async Task<HashSet<long>> PinnedScoreIdsAsync(NpgsqlConnection conn, long userId)
        => (await conn.QueryAsync<long>(
            "SELECT score_id FROM score_pins WHERE user_id = @userId",
            new { userId })).ToHashSet();
}
