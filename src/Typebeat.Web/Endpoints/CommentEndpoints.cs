using System.Globalization;
using Dapper;
using Newtonsoft.Json;
using Typebeat.Web.Auth;
using Typebeat.Web.Data;
using Typebeat.Web.Social;
using Typebeat.Web.Wire;

namespace Typebeat.Web.Endpoints;

/// <summary>
/// The beatmap set overlay's comments section, over the set page's own comments (<see cref="BeatmapsetComments"/>),
/// in osu-web's CommentBundle shape:
///
///  - <c>GET /api/v2/comments?commentable_type=beatmapset&amp;commentable_id={id}</c> (GetCommentsRequest);
///  - <c>POST /api/v2/comments</c> (CommentPostRequest), answering a bundle of the new comment;
///  - <c>DELETE /api/v2/comments/{id}</c> (CommentDeleteRequest), answering a bundle of the deleted comment.
///
/// <para>
/// Comments here are flat and unvoted: every comment is top-level with no replies and no votes, a <c>parent_id</c>
/// read (replies) answers an empty page, a reply post is refused, and the <c>top</c> sort is the <c>new</c> one.
/// Posting and deleting follow the website's rules exactly: the same body budget, the same shared rate limit, the
/// same delete permission (author, set owner or reviewer) and the same reviewer moderation audit.
/// </para>
///
/// <para>
/// Only beatmapsets have comments; any other commentable type 404s. Reads need a published set (the set GET's
/// rule); posting needs a set the poster can see (published, or their own hidden one).
/// </para>
/// </summary>
public static class CommentEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v2/comments", GetCommentsAsync);
        app.MapPost("/api/v2/comments", PostCommentAsync).RequireBearer();
        app.MapDelete("/api/v2/comments/{commentId:long}", DeleteCommentAsync).RequireBearer();
    }

    private static async Task<IResult> PostCommentAsync(HttpContext ctx, Db db)
    {
        var user = ctx.AuthedUser();
        var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);

        if (!form["comment[commentable_type]"].ToString().Equals("beatmapset", StringComparison.OrdinalIgnoreCase)
            || !long.TryParse(form["comment[commentable_id]"], NumberStyles.None, CultureInfo.InvariantCulture, out long setId))
            return WireJson.Error(StatusCodes.Status404NotFound, "not found");

        // The client sends an empty parent_id for a top-level comment.
        if (!string.IsNullOrEmpty(form["comment[parent_id]"]))
            return WireJson.Error(StatusCodes.Status422UnprocessableEntity, "replies are not supported");

        string body = BeatmapsetComments.Normalize(form["comment[message]"]);

        if (!BeatmapsetComments.IsPostable(body))
            return WireJson.Error(StatusCodes.Status422UnprocessableEntity, $"a comment must be 1 to {BeatmapsetComments.MaxBodyLength} characters");

        await using var conn = await db.OpenAsync(ctx.RequestAborted);

        var set = await findSetAsync(conn, setId, ctx.RequestAborted);

        if (set is null || (!BeatmapsetEndpoints.IsPublished(set.Status) && set.OwnerId != user.Id))
            return WireJson.Error(StatusCodes.Status404NotFound, "not found");

        // After the checks, so a 404 or a refused body never burns comment budget (the website's order).
        if (!BeatmapsetComments.PostLimiter.Allow(user.Id.ToString(CultureInfo.InvariantCulture)))
            return WireJson.Error(StatusCodes.Status429TooManyRequests, "you are commenting too fast");

        long commentId = await BeatmapsetComments.PostAsync(conn, setId, user.Id, body, ctx.RequestAborted);
        var row = await BeatmapsetComments.FindAsync(conn, commentId, ctx.RequestAborted);

        var posted = row is null ? new List<CommentRowModel>() : new List<CommentRowModel> { row.Comment };
        return WireJson.Ok(bundle(ctx, set, posted, total: posted.Count, hasMore: false));
    }

    private static async Task<IResult> DeleteCommentAsync(long commentId, HttpContext ctx, Db db, ILoggerFactory loggers)
    {
        var user = ctx.AuthedUser();

        await using var conn = await db.OpenAsync(ctx.RequestAborted);

        var found = await BeatmapsetComments.FindAsync(conn, commentId, ctx.RequestAborted);

        if (found is null)
            return WireJson.Error(StatusCodes.Status404NotFound, "not found");

        // Permission is the soft delete's WHERE clause: no row means not found, whatever the reason.
        var deleted = await BeatmapsetComments.SoftDeleteAsync(conn, found.SetId, commentId, user.Id, user.CanReviewMaps, ctx.RequestAborted);

        if (deleted is null)
            return WireJson.Error(StatusCodes.Status404NotFound, "not found");

        if (CommentModeration.IsModeration(deleted, user.Id, user.CanReviewMaps))
        {
            try
            {
                await CommentModeration.RecordAsync(conn, user.Id, found.SetId, commentId);
            }
            catch (Exception e)
            {
                loggers.CreateLogger(nameof(CommentEndpoints))
                       .LogWarning(e, "Comment {CommentId} on set {SetId} was removed but the audit row failed.", commentId, found.SetId);
            }
        }

        var set = await findSetAsync(conn, found.SetId, ctx.RequestAborted);
        var wire = bundle(ctx, set!, [found.Comment], total: 0, hasMore: false);
        wire.Comments[0].DeletedAt = DateTimeOffset.UtcNow;

        return WireJson.Ok(wire);
    }

    private static Task<SetRow?> findSetAsync(Npgsql.NpgsqlConnection conn, long setId, CancellationToken ct)
        => conn.QuerySingleOrDefaultAsync<SetRow?>(new CommandDefinition(
            """
            SELECT s.id AS Id, s.owner_id AS OwnerId, s.artist AS Artist, s.title AS Title, s.status AS Status
            FROM beatmapsets s
            WHERE s.id = @setId
            """,
            new { setId }, cancellationToken: ct));

    /// <summary>The CommentBundle around <paramref name="rows"/>, with their authors and the set's commentable meta.</summary>
    private static CommentBundleWire bundle(HttpContext ctx, SetRow set, IReadOnlyList<CommentRowModel> rows, int total, bool hasMore)
    {
        string scheme = ctx.Request.Scheme;
        string host = ctx.Request.Host.Value ?? string.Empty;

        return new CommentBundleWire
        {
            CommentableMeta =
            [
                new CommentableMetaWire
                {
                    Id = set.Id,
                    OwnerId = set.OwnerId,
                    Title = $"{set.Artist} - {set.Title}",
                    Url = $"{scheme}://{host}/beatmapsets/{set.Id}",
                    // Null: anyone signed in may comment (the client's editor handles the signed-out case itself).
                    CurrentUserAttributes = new CommentableUserAttributesWire(),
                },
            ],
            Comments = rows.Select(r => new CommentWire
            {
                Id = r.Id,
                UserId = r.UserId,
                Message = r.Body,
                CommentableId = set.Id,
                CreatedAt = r.CreatedAt,
                UpdatedAt = r.CreatedAt,
            }).ToList(),
            HasMore = hasMore,
            Users = rows.DistinctBy(r => r.UserId).Select(r => new ScoreUserWire
            {
                Id = r.UserId,
                Username = r.Username,
                CountryCode = r.CountryCode,
                AvatarUrl = UserWire.AvatarUrl(scheme, host, r.AvatarKey),
            }).ToList(),
            Total = total,
            TopLevelCount = total,
        };
    }

    private static async Task<IResult> GetCommentsAsync(HttpContext ctx, Db db)
    {
        var q = ctx.Request.Query;

        if (!q["commentable_type"].ToString().Equals("beatmapset", StringComparison.OrdinalIgnoreCase)
            || !long.TryParse(q["commentable_id"], NumberStyles.None, CultureInfo.InvariantCulture, out long setId))
            return WireJson.Error(StatusCodes.Status404NotFound, "not found");

        await using var conn = await db.OpenAsync(ctx.RequestAborted);

        var set = await findSetAsync(conn, setId, ctx.RequestAborted);

        if (set is null || !BeatmapsetEndpoints.IsPublished(set.Status))
            return WireJson.Error(StatusCodes.Status404NotFound, "not found");

        int page = int.TryParse(q["page"], NumberStyles.None, CultureInfo.InvariantCulture, out int p) && p > 0 ? p : 1;
        bool newestFirst = !q["sort"].ToString().Equals("old", StringComparison.OrdinalIgnoreCase);

        int total = await BeatmapsetComments.CountAsync(conn, setId, ctx.RequestAborted);

        // A reply page: nothing here has replies.
        var rows = q.ContainsKey("parent_id")
            ? new List<CommentRowModel>()
            : await BeatmapsetComments.PageAsync(conn, setId, (page - 1) * BeatmapsetComments.PageSize,
                BeatmapsetComments.PageSize + 1, newestFirst, ctx.RequestAborted);

        bool hasMore = rows.Count > BeatmapsetComments.PageSize;
        if (hasMore)
            rows.RemoveAt(rows.Count - 1);

        return WireJson.Ok(bundle(ctx, set, rows, total, hasMore));
    }

    private sealed record SetRow(long Id, long OwnerId, string Artist, string Title, string Status);
}

/// <summary>
/// The client's CommentBundle. DECLARATION ORDER IS LOAD-BEARING: the client's <c>user_votes</c> and
/// <c>users</c> setters walk <c>comments</c>, <c>included_comments</c> and <c>pinned_comments</c> the
/// moment they are deserialized, so all three must already be on the object (non-null) by then.
/// </summary>
public sealed class CommentBundleWire
{
    [JsonProperty("commentable_meta")]
    public List<CommentableMetaWire> CommentableMeta { get; init; } = [];

    [JsonProperty("comments")]
    public List<CommentWire> Comments { get; init; } = [];

    [JsonProperty("included_comments")]
    public List<CommentWire> IncludedComments { get; init; } = [];

    [JsonProperty("pinned_comments")]
    public List<CommentWire> PinnedComments { get; init; } = [];

    [JsonProperty("has_more")]
    public bool HasMore { get; init; }

    [JsonProperty("has_more_id")]
    public long? HasMoreId { get; init; }

    [JsonProperty("user_follow")]
    public bool UserFollow { get; init; }

    [JsonProperty("user_votes")]
    public List<long> UserVotes { get; init; } = [];

    [JsonProperty("users")]
    public List<ScoreUserWire> Users { get; init; } = [];

    [JsonProperty("total")]
    public int Total { get; init; }

    [JsonProperty("top_level_count")]
    public int TopLevelCount { get; init; }
}

public sealed class CommentWire
{
    [JsonProperty("id")]
    public long Id { get; init; }

    [JsonProperty("parent_id")]
    public long? ParentId { get; init; }

    [JsonProperty("user_id")]
    public long UserId { get; init; }

    /// <summary>The stored plain-text body; the client renders it as markdown.</summary>
    [JsonProperty("message")]
    public string Message { get; init; } = string.Empty;

    [JsonProperty("replies_count")]
    public int RepliesCount { get; init; }

    [JsonProperty("votes_count")]
    public int VotesCount { get; init; }

    [JsonProperty("commentable_type")]
    public string CommentableType { get; init; } = "beatmapset";

    [JsonProperty("commentable_id")]
    public long CommentableId { get; init; }

    [JsonProperty("created_at")]
    public DateTimeOffset CreatedAt { get; init; }

    [JsonProperty("updated_at")]
    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>Set only on the delete response's own comment; deleted comments are never listed.</summary>
    [JsonProperty("deleted_at")]
    public DateTimeOffset? DeletedAt { get; set; }

    [JsonProperty("pinned")]
    public bool Pinned { get; init; }
}

public sealed class CommentableMetaWire
{
    [JsonProperty("id")]
    public long Id { get; init; }

    [JsonProperty("owner_id")]
    public long? OwnerId { get; init; }

    /// <summary>The badge the client puts beside the set owner's own comments.</summary>
    [JsonProperty("owner_title")]
    public string OwnerTitle { get; init; } = "MAPPER";

    [JsonProperty("title")]
    public string Title { get; init; } = string.Empty;

    [JsonProperty("type")]
    public string Type { get; init; } = "beatmapset";

    [JsonProperty("url")]
    public string Url { get; init; } = string.Empty;

    [JsonProperty("current_user_attributes")]
    public CommentableUserAttributesWire? CurrentUserAttributes { get; init; }
}

public sealed class CommentableUserAttributesWire
{
    [JsonProperty("can_new_comment_reason")]
    public string? CanNewCommentReason { get; init; }
}
