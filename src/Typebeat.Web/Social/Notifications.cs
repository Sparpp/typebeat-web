using Dapper;
using Npgsql;
using Typebeat.Web.Data;

namespace Typebeat.Web.Social;

/// <summary>
/// Notifications (027_notifications.sql): the header bell's badge count, the rows behind its
/// dropdown, the fan-out that creates them and the two read-marking writes. One place owns the
/// kind strings and the visibility rules so the badge and the list can never disagree about what
/// counts, which is the property that makes the badge clearable at all.
///
/// <para>
/// The site has exactly one kind today, <see cref="MapperUploadKind"/>, written by
/// <see cref="FanOutMapperUploadAsync"/> from inside the ingest transaction that publishes a set.
/// Everything else here is kind-agnostic on purpose: the reads project a nullable set/actor pair
/// and the rendering decides what to say about it, so a second kind is a CHECK edit, a row
/// partial branch, and nothing else.
/// </para>
/// </summary>
public static class Notifications
{
    /// <summary>A mapper this user watches published a new set. actor = mapper, set = the set.</summary>
    public const string MapperUploadKind = "mapper_upload";

    /// <summary>
    /// How many unread rows the badge query is willing to look at. The badge cannot render a
    /// number bigger than "99+" (see <see cref="BadgeLabel"/>), so counting past that is work
    /// nobody sees, and this query runs on EVERY page load of the whole site for every signed-in
    /// user. The cap turns an unbounded count into a bounded index scan for the pathological case
    /// (someone who watches prolific mappers and never opens the panel).
    /// </summary>
    public const int BadgeScanCap = 100;

    /// <summary>Rows the dropdown panel shows. Deliberately short: the panel is a glance, /watching is the list.</summary>
    public const int PanelSize = 8;

    /// <summary>Rows the /watching page's notification section shows.</summary>
    public const int PageSize = 50;

    /// <summary>
    /// What the badge prints. Anything past 99 collapses, so the badge never grows wide enough to
    /// shove the nav around.
    /// </summary>
    public static string BadgeLabel(int unread) => unread > 99 ? "99+" : unread.ToString();

    /// <summary>
    /// The rule for "this notification is still worth showing", shared BY CONSTRUCTION between
    /// the badge count and every list read (both interpolate this exact fragment). If they used
    /// different rules, a notification the list hid but the count counted would make the badge
    /// permanently unclearable, which is the single worst failure mode this feature has.
    ///
    /// <para>
    /// A notification pointing at a set is hidden once that set stops being publicly visible: a
    /// takedown ('removed') or a set that somehow went back to 'hidden' would render a card that
    /// 404s for the recipient. Same for an actor who has since been restricted, who is delisted
    /// everywhere else on the site. Rows whose reference columns are NULL (a future kind that
    /// points at nothing) pass, which is why each clause is guarded by an IS NULL test rather
    /// than written as a plain join predicate.
    /// </para>
    ///
    /// <para>Aliases in play: <c>n</c> = user_notifications, <c>s</c> = beatmapsets, <c>a</c> = actor.</para>
    /// </summary>
    private const string visible_predicate =
        """
        (n.set_id IS NULL OR s.status IN ('pending', 'unranked', 'ranked'))
        AND (n.actor_id IS NULL OR NOT a.restricted)
        """;

    private const string joins =
        """
        FROM user_notifications n
        LEFT JOIN beatmapsets s ON s.id = n.set_id
        LEFT JOIN users a ON a.id = n.actor_id
        """;

    // ---- write ----

    /// <summary>
    /// One notification per watcher of <paramref name="ownerId"/>, for the set that just became
    /// publicly visible. Called from inside <c>PackageIngest</c>'s per-set transaction, so the
    /// rows land with the publish or not at all: a rolled-back upload cannot leave notifications
    /// pointing at a set that never went live.
    ///
    /// <para>
    /// One statement, no round trip per watcher: the watchers ARE the rows, so the INSERT reads
    /// them straight out of user_follows through the primary key's followee index.
    /// </para>
    ///
    /// <para>
    /// The mapper is never in the result set. Not because of a filter here, but because
    /// user_follows carries a CHECK (follower_id &lt;&gt; followee_id) (023_follows.sql), so
    /// "watching yourself" is unrepresentable and the self-notification it would produce cannot
    /// be constructed by any write path, including this one.
    /// </para>
    ///
    /// <para>
    /// Restricted watchers are skipped: a restricted account is treated as signed out by the
    /// cookie middleware, so it can never see the badge these rows would feed.
    /// </para>
    ///
    /// <para>
    /// ON CONFLICT rides the partial unique index, so re-running this for a set that already
    /// notified a given watcher is a no-op rather than a duplicate or a crash.
    /// </para>
    /// </summary>
    /// <returns>How many watchers were notified.</returns>
    public static async Task<int> FanOutMapperUploadAsync(
        NpgsqlConnection conn, long setId, long ownerId, CancellationToken ct = default)
        => await conn.ExecuteAsync(
            new CommandDefinition(
                $"""
                 INSERT INTO user_notifications (user_id, kind, set_id, actor_id)
                 SELECT f.follower_id, '{MapperUploadKind}', @setId, @ownerId
                 FROM user_follows f
                 JOIN users w ON w.id = f.follower_id
                 WHERE f.followee_id = @ownerId
                   AND f.kind = '{Follows.MapperKind}'
                   AND NOT w.restricted
                 ON CONFLICT (user_id, set_id) WHERE kind = '{MapperUploadKind}' DO NOTHING
                 """,
                new { setId, ownerId }, cancellationToken: ct));

    /// <summary>
    /// Marks ONE notification read and reports where clicking it should go.
    ///
    /// <para>
    /// <paramref name="userId"/> is a WHERE clause, not a comparison: a forged id belonging to
    /// somebody else simply matches no row and comes back null, the same shape the pin handlers
    /// use. There is no "belongs to another user" branch to get wrong.
    /// </para>
    ///
    /// <para>
    /// read_at uses coalesce so re-clicking an already-read notification still navigates (and
    /// keeps its ORIGINAL read time) rather than 404ing or back-dating itself.
    /// </para>
    /// </summary>
    /// <returns>The row's target, or null when no notification of that id belongs to this user.</returns>
    public static async Task<NotificationTarget?> MarkReadAsync(
        NpgsqlConnection conn, long userId, long id, CancellationToken ct = default)
        => await conn.QuerySingleOrDefaultAsync<NotificationTarget?>(
            new CommandDefinition(
                """
                UPDATE user_notifications
                SET read_at = coalesce(read_at, now())
                WHERE id = @id AND user_id = @userId
                RETURNING kind AS Kind, set_id AS SetId
                """,
                new { id, userId }, cancellationToken: ct));

    /// <summary>
    /// Marks every unread notification of this user read (the panel's "mark all read").
    /// Deliberately unfiltered by <see cref="visible_predicate"/>: the point of the control is to
    /// zero the badge, and a row the list is hiding still has to stop being counted.
    /// </summary>
    /// <returns>How many rows were still unread.</returns>
    public static async Task<int> MarkAllReadAsync(
        NpgsqlConnection conn, long userId, CancellationToken ct = default)
        => await conn.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE user_notifications
                SET read_at = now()
                WHERE user_id = @userId AND read_at IS NULL
                """,
                new { userId }, cancellationToken: ct));

    // ---- read ----

    /// <summary>
    /// The badge number: unread, visible, capped at <see cref="BadgeScanCap"/> scanned rows.
    /// One index scan over ix_user_notifications_unread plus a primary-key probe per unread row
    /// for the visibility joins.
    /// </summary>
    public static async Task<int> UnreadCountAsync(
        NpgsqlConnection conn, long userId, CancellationToken ct = default)
        => await conn.ExecuteScalarAsync<int>(
            new CommandDefinition(
                $"""
                 SELECT count(*) FROM (
                     SELECT 1
                     {joins}
                     WHERE n.user_id = @userId
                       AND n.read_at IS NULL
                       AND {visible_predicate}
                     LIMIT {BadgeScanCap}
                 ) capped
                 """,
                new { userId }, cancellationToken: ct));

    /// <summary>
    /// The badge number for a request, resolving <see cref="Db"/> off the request services. The
    /// layout's bell partial calls this: it already has the session user from
    /// <c>Context.SessionUser()</c>, so this adds one cheap count and NOT a second identity
    /// lookup.
    /// </summary>
    public static async Task<int> UnreadCountAsync(HttpContext ctx, long userId)
    {
        var db = ctx.RequestServices.GetRequiredService<Db>();

        await using var conn = await db.OpenAsync(ctx.RequestAborted);
        return await UnreadCountAsync(conn, userId, ctx.RequestAborted);
    }

    /// <summary>
    /// This user's most recent visible notifications, newest first. id DESC breaks the created_at
    /// ties a fan-out produces in bulk (every row of one publish shares the statement's now()).
    /// </summary>
    public static async Task<IReadOnlyList<NotificationRowModel>> RecentAsync(
        NpgsqlConnection conn, long userId, int limit, CancellationToken ct = default)
        => (await conn.QueryAsync<NotificationRowModel>(
            new CommandDefinition(
                $"""
                 SELECT n.id            AS Id,
                        n.kind          AS Kind,
                        n.set_id        AS SetId,
                        n.created_at    AS CreatedAt,
                        n.read_at       AS ReadAt,
                        n.actor_id      AS ActorId,
                        a.username::text AS ActorName,
                        s.title         AS SetTitle,
                        s.title_unicode AS SetTitleUnicode,
                        s.artist        AS SetArtist,
                        s.artist_unicode AS SetArtistUnicode,
                        CASE WHEN s.cover_key IS NOT NULL THEN '/' || s.cover_key || '/list.jpg' END AS SetCoverUrl
                 {joins}
                 WHERE n.user_id = @userId
                   AND {visible_predicate}
                 ORDER BY n.created_at DESC, n.id DESC
                 LIMIT @limit
                 """,
                new { userId, limit }, cancellationToken: ct))).ToList();
}

/// <summary>Where a clicked notification sends the reader, plus the kind that decided it.</summary>
/// <param name="Kind">The notification's kind, for callers that branch on it.</param>
/// <param name="SetId">The beatmapset it points at, when it points at one.</param>
public sealed record NotificationTarget(string Kind, long? SetId)
{
    /// <summary>
    /// The page a click lands on. A kind with no set falls back to the notifications home rather
    /// than dead-ending, so adding a kind can never produce a link to nowhere.
    /// </summary>
    public string Url => SetId is long setId ? $"/beatmapsets/{setId}" : "/watching";
}

/// <summary>
/// One rendered notification (Pages/Shared/_NotificationRow.cshtml). Hydrated positionally by
/// Dapper from <see cref="Notifications.RecentAsync"/>: the SELECT's column ORDER and this
/// record's parameter order are one unit, so a new field goes at the END of both.
/// </summary>
/// <param name="SetId">The set it points at, null for a kind that points at none.</param>
/// <param name="ReadAt">Null = unread (rendered with the unread marker).</param>
/// <param name="ActorName">The user who caused it, null if the kind has no actor.</param>
public sealed record NotificationRowModel(
    long Id,
    string Kind,
    long? SetId,
    DateTime CreatedAt,
    DateTime? ReadAt,
    long? ActorId,
    string? ActorName,
    string? SetTitle,
    string? SetTitleUnicode,
    string? SetArtist,
    string? SetArtistUnicode,
    string? SetCoverUrl)
{
    public bool IsUnread => ReadAt is null;

    /// <summary>Set title, or its original non-romanized text when the viewer prefers that.</summary>
    public string DisplayTitle(bool preferOriginal)
        => MetadataDisplay.Pick(SetTitle ?? "a beatmap", SetTitleUnicode, preferOriginal);

    /// <summary>Set artist, or its original non-romanized text when the viewer prefers that.</summary>
    public string DisplayArtist(bool preferOriginal)
        => MetadataDisplay.Pick(SetArtist ?? "", SetArtistUnicode, preferOriginal);

    /// <summary>
    /// Compact relative age ("3m", "5h", "2d"), the same glanceable shape osu-web's notification
    /// list uses. Deliberately not the profile's prose "n days ago": a notification row is narrow
    /// and the timestamp is a suffix, not a sentence.
    /// </summary>
    public string AgeLabel()
    {
        var elapsed = DateTime.UtcNow - CreatedAt;

        if (elapsed < TimeSpan.Zero)
            elapsed = TimeSpan.Zero;

        if (elapsed < TimeSpan.FromMinutes(1))
            return "now";
        if (elapsed < TimeSpan.FromHours(1))
            return $"{(int)elapsed.TotalMinutes}m";
        if (elapsed < TimeSpan.FromDays(1))
            return $"{(int)elapsed.TotalHours}h";
        if (elapsed < TimeSpan.FromDays(30))
            return $"{(int)elapsed.TotalDays}d";
        if (elapsed < TimeSpan.FromDays(365))
            return $"{(int)(elapsed.TotalDays / 30)}mo";

        return $"{(int)(elapsed.TotalDays / 365)}y";
    }
}
