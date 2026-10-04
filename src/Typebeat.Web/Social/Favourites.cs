using Dapper;
using Npgsql;

namespace Typebeat.Web.Social;

/// <summary>
/// A player's favourite sets (<c>favourites</c>, with the denormalised <c>beatmapsets.favourite_count</c> kept in
/// step). One place owns the write so the website's toggle (Pages/Beatmapsets/Set) and the game's explicit
/// favourite / unfavourite (BeatmapsetEndpoints) cannot count differently.
/// </summary>
public static class Favourites
{
    /// <summary>Flips the favourite. Returns whether the set is now favourited.</summary>
    public static async Task<bool> ToggleAsync(NpgsqlConnection conn, long userId, long setId, CancellationToken ct = default)
    {
        await using var tx = await conn.BeginTransactionAsync(ct);

        bool removed = await removeAsync(conn, tx, userId, setId);

        if (!removed)
            await addAsync(conn, tx, userId, setId);

        await tx.CommitAsync(ct);
        return !removed;
    }

    /// <summary>
    /// Puts the favourite into the stated state. Idempotent: favouriting a favourite (or the reverse) changes nothing,
    /// and the count moves only when a row actually does.
    /// </summary>
    public static async Task SetAsync(NpgsqlConnection conn, long userId, long setId, bool favourited, CancellationToken ct = default)
    {
        await using var tx = await conn.BeginTransactionAsync(ct);

        if (favourited)
            await addAsync(conn, tx, userId, setId);
        else
            await removeAsync(conn, tx, userId, setId);

        await tx.CommitAsync(ct);
    }

    private static async Task<bool> addAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long userId, long setId)
    {
        int inserted = await conn.ExecuteAsync(
            "INSERT INTO favourites (user_id, set_id) VALUES (@userId, @setId) ON CONFLICT DO NOTHING",
            new { userId, setId }, tx);

        if (inserted > 0)
            await conn.ExecuteAsync("UPDATE beatmapsets SET favourite_count = favourite_count + 1 WHERE id = @setId", new { setId }, tx);

        return inserted > 0;
    }

    private static async Task<bool> removeAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long userId, long setId)
    {
        int deleted = await conn.ExecuteAsync(
            "DELETE FROM favourites WHERE user_id = @userId AND set_id = @setId",
            new { userId, setId }, tx);

        if (deleted > 0)
            await conn.ExecuteAsync("UPDATE beatmapsets SET favourite_count = greatest(favourite_count - 1, 0) WHERE id = @setId", new { setId }, tx);

        return deleted > 0;
    }
}
