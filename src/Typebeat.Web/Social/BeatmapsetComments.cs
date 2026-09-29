using Dapper;
using Npgsql;

namespace Typebeat.Web.Social;

/// <summary>
/// User comments on a beatmapset (036_beatmapset_comments.sql): the set page's comments section.
/// One place owns the SQL and the permission predicates, the same shape as <see cref="Follows"/>
/// and <see cref="Notifications"/>, so the page handlers stay thin.
///
/// <para>
/// Deletion is SOFT (deleted_at/deleted_by): the list pages by keyset cursor over id, so rows must
/// not vanish out from under a reader's cursor, and a reviewer removal stays auditable off the row
/// itself. Every read filters <c>deleted_at IS NULL</c> and, per the site-wide delisting
/// convention, drops comments whose author has since been restricted.
/// </para>
/// </summary>
public static class BeatmapsetComments
{
    /// <summary>Matches the CHECK in 036 and the description/bio budget (Settings.IndexModel).</summary>
    public const int MaxBodyLength = 2000;

    /// <summary>Comments shown per page of the set page's section (keyset-paged, oldest first).</summary>
    public const int PageSize = 50;

    /// <summary>What every write path stores: the body with the whitespace shell trimmed off.</summary>
    public static string Normalize(string? body) => (body ?? string.Empty).Trim();

    /// <summary>
    /// Whether a normalized body may be stored: non-empty and within budget. The REJECT side of
    /// over-long input (the Settings bio rule, not the report-truncate rule) lives in the caller;
    /// this is the single predicate both the handler and its tests read.
    /// </summary>
    public static bool IsPostable(string normalizedBody)
        => normalizedBody.Length is > 0 and <= MaxBodyLength;

    /// <summary>Live rows by non-delisted authors, the comment analogue of the notification
    /// visibility rule. Aliases: <c>c</c> = beatmapset_comments, <c>u</c> = the author.</summary>
    private const string visible_predicate = "c.deleted_at IS NULL AND NOT u.restricted";

    // ---- read ----

    /// <summary>How many comments the section's heading counts: live, visible authors only.</summary>
    public static async Task<int> CountAsync(NpgsqlConnection conn, long setId, CancellationToken ct = default)
        => await conn.ExecuteScalarAsync<int>(
            new CommandDefinition(
                $"""
                 SELECT count(*)
                 FROM beatmapset_comments c
                 JOIN users u ON u.id = c.user_id
                 WHERE c.set_id = @setId AND {visible_predicate}
                 """,
                new { setId }, cancellationToken: ct));

    /// <summary>
    /// One page of a set's comments, oldest first (a conversation reads downward), keyset-paged:
    /// rows with id above <paramref name="afterId"/>, <paramref name="limit"/> of them. Callers
    /// pass PageSize + 1 and pop the sentinel to learn whether a next page exists, the listing's
    /// idiom.
    /// </summary>
    public static async Task<List<CommentRowModel>> ListAsync(
        NpgsqlConnection conn, long setId, long afterId, int limit, CancellationToken ct = default)
        => (await conn.QueryAsync<CommentRowModel>(
            new CommandDefinition(
                $"""
                 SELECT c.id         AS Id,
                        c.user_id    AS UserId,
                        u.username::text AS Username,
                        u.avatar_key AS AvatarKey,
                        c.body       AS Body,
                        c.created_at AS CreatedAt,
                        u.country_code::text AS CountryCode
                 FROM beatmapset_comments c
                 JOIN users u ON u.id = c.user_id
                 WHERE c.set_id = @setId AND c.id > @afterId AND {visible_predicate}
                 ORDER BY c.id ASC
                 LIMIT @limit
                 """,
                new { setId, afterId, limit }, cancellationToken: ct))).ToList();

    // ---- write ----

    /// <summary>
    /// Stores one comment and fans out its notification in the SAME transaction, so a rolled-back
    /// insert cannot leave a notification pointing at a comment that never landed (the
    /// <see cref="Notifications.FanOutMapperUploadAsync"/> rule). The body must already be
    /// normalized and postable; the caller owns the refusal UX.
    /// </summary>
    /// <returns>The new comment's id.</returns>
    public static async Task<long> PostAsync(
        NpgsqlConnection conn, long setId, long authorId, string body, CancellationToken ct = default)
    {
        await using var tx = await conn.BeginTransactionAsync(ct);

        long commentId = await conn.ExecuteScalarAsync<long>(
            new CommandDefinition(
                """
                INSERT INTO beatmapset_comments (set_id, user_id, body)
                VALUES (@setId, @authorId, @body)
                RETURNING id
                """,
                new { setId, authorId, body }, transaction: tx, cancellationToken: ct));

        await FanOutCommentAsync(conn, tx, setId, commentId, authorId, ct);

        await tx.CommitAsync(ct);
        return commentId;
    }

    /// <summary>
    /// One 'map_comment' notification to the set's owner, inside the insert's transaction.
    /// The owner commenting on their own set notifies nobody (the WHERE, not a caller branch),
    /// and a restricted owner is skipped for the fan-out's usual reason: the cookie middleware
    /// treats a restricted account as signed out, so it can never see the badge.
    /// </summary>
    /// <returns>How many notifications were written (0 or 1).</returns>
    public static async Task<int> FanOutCommentAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, long setId, long commentId, long authorId,
        CancellationToken ct = default)
        => await conn.ExecuteAsync(
            new CommandDefinition(
                $"""
                 INSERT INTO user_notifications (user_id, kind, set_id, actor_id, comment_id)
                 SELECT s.owner_id, '{Notifications.MapCommentKind}', @setId, @authorId, @commentId
                 FROM beatmapsets s
                 JOIN users o ON o.id = s.owner_id
                 WHERE s.id = @setId
                   AND s.owner_id <> @authorId
                   AND NOT o.restricted
                 """,
                new { setId, commentId, authorId }, transaction: tx, cancellationToken: ct));

    /// <summary>
    /// Soft-deletes one live comment, permission as the WHERE clause (the page convention: an
    /// unpermitted or nonexistent target matches no row and the caller answers NotFound, never
    /// Forbid). Permitted deleters: the comment's author, the set's owner on their own set, or a
    /// map reviewer (<paramref name="canReview"/>).
    /// </summary>
    /// <returns>Who wrote it and who owns the set (the caller's audit decision), or null when
    /// nothing was deleted.</returns>
    public static async Task<DeletedComment?> SoftDeleteAsync(
        NpgsqlConnection conn, long setId, long commentId, long viewerId, bool canReview,
        CancellationToken ct = default)
        => await conn.QuerySingleOrDefaultAsync<DeletedComment?>(
            new CommandDefinition(
                """
                UPDATE beatmapset_comments c
                SET deleted_at = now(), deleted_by = @viewerId
                FROM beatmapsets s
                WHERE c.id = @commentId AND c.set_id = @setId AND s.id = c.set_id
                  AND c.deleted_at IS NULL
                  AND (c.user_id = @viewerId OR s.owner_id = @viewerId OR @canReview)
                RETURNING c.user_id AS AuthorId, s.owner_id AS OwnerId
                """,
                new { setId, commentId, viewerId, canReview }, cancellationToken: ct));
}

/// <summary>The soft-delete's report: enough for the caller to decide whether to audit.</summary>
/// <param name="AuthorId">Who wrote the deleted comment.</param>
/// <param name="OwnerId">Who owns the set it sat on.</param>
public sealed record DeletedComment(long AuthorId, long OwnerId);

/// <summary>What _CommentRow.cshtml renders: the comment plus the bits only the page knows
/// (which set the delete form posts to, and whether this viewer may delete this row).</summary>
public sealed record CommentRowView(CommentRowModel Comment, long SetId, bool CanDelete);

/// <summary>One rendered comment (Pages/Shared/_CommentRow.cshtml).</summary>
public sealed record CommentRowModel(
    long Id, long UserId, string Username, string? AvatarKey, string Body, DateTime CreatedAt, string CountryCode)
{
    /// <summary>Uploaded avatar, or null for the initial-letter fallback.</summary>
    public string? AvatarUrl => AvatarKey is null ? null : $"/{AvatarKey}";

    /// <summary>Compact relative age (see <see cref="RelativeAge"/>).</summary>
    public string AgeLabel() => RelativeAge.Label(CreatedAt);
}
