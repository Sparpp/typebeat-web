namespace Typebeat.Web.Pages;

/// <summary>
/// View model for the shared user-row partial (Pages/Shared/_UserRow.cshtml): one full-width
/// list item for a PERSON, the counterpart of <see cref="ScoreRowModel"/> and
/// <see cref="BeatmapsetCardModel"/>. Used by the follower / following lists and by the watched
/// mappers strip on /watching. Hydrate it with <see cref="UserRowSql.Select"/>.
/// </summary>
/// <param name="AvatarKey">Uploaded avatar store key, or null → the initial-letter fallback disc
/// (same treatment the leaderboard podium and the nav chip use).</param>
/// <param name="LastVisit">Null for an account that has never signed in ("never").</param>
/// <param name="MapCount">Publicly visible beatmapsets this user owns. Renders as a "n maps" tag,
/// which is what makes a follower list also readable as "which of these people map".</param>
/// <param name="CountryCode">users.country_code, rendered through _Flag (nothing for XX).</param>
public sealed record UserRowModel(
    long Id,
    string Username,
    string? AvatarKey,
    DateTime CreatedAt,
    DateTime? LastVisit,
    long MapCount,
    string CountryCode)
{
    public string? AvatarUrl => AvatarKey is null ? null : $"/{AvatarKey}";
}

/// <summary>
/// Canonical SQL for hydrating <see cref="UserRowModel"/> rows with Dapper.
/// </summary>
public static class UserRowSql
{
    /// <summary>
    /// SELECT … FROM fragment producing exactly the row record's columns, aliased <c>u</c>.
    /// Callers append JOIN / WHERE / ORDER BY / LIMIT. As with
    /// <see cref="BeatmapsetCardSql.Select"/>, the column ORDER is load-bearing: Dapper binds a
    /// positional record's constructor parameters to the reader's columns POSITIONALLY, so a new
    /// column has to be appended at the same index in both this SELECT and the record.
    /// </summary>
    public const string Select =
        """
        SELECT u.id             AS Id,
               u.username::text AS Username,
               u.avatar_key     AS AvatarKey,
               u.created_at     AS CreatedAt,
               u.last_visit     AS LastVisit,
               (SELECT count(*)
                FROM beatmapsets bs
                WHERE bs.owner_id = u.id
                  AND bs.status IN ('pending', 'unranked', 'ranked')) AS MapCount,
               u.country_code::text AS CountryCode
        FROM users u
        """;
}
