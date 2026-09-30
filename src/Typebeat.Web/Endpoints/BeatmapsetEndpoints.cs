using Dapper;
using Typebeat.Web.Auth;
using Typebeat.Web.Data;
using Typebeat.Web.Wire;

namespace Typebeat.Web.Endpoints;

/// <summary>
/// Beatmapset-level APIv2 endpoints:
///
///  - GET /api/v2/beatmapsets/{id}: GetBeatmapSetRequest (Target "beatmapsets/{id}"), the
///    submission wizard's status preselect on update and the post-submit result card. Shape is
///    APIBeatmapSet with nested beatmaps[] (Wire/BeatmapWire.cs). Optional auth: public sets
///    are visible to everyone; 'hidden'/'removed' sets 404 for everyone but the owner.
///  - GET /api/v2/me/beatmapset-favourites: GetMyFavouriteBeatmapSetsResponse
///    { beatmapset_ids: [...] }, read from the favourites table (was an empty stub in M1).
///
/// Status strings bind to the client's BeatmapOnlineStatus by member NAME (see BeatmapWire):
/// 'ranked' → "ranked" (reviewer-approved: leaderboards live, MatchesOnlineVersion satisfied),
/// 'pending' → "pending" (published upload awaiting review; browsable, no leaderboards,
/// the client's LeaderboardManager blocks non-ranked-family statuses natively),
/// 'unranked' → "unranked" (published, browsable and downloadable; the creator opted out of
/// ranking in the wizard, so it never earns leaderboard rows),
/// 'hidden' → "wip" (owner-only pre-publish state, preselects WIP in the wizard),
/// 'removed' → "graveyard" (owner-only; submission to it is blocked with a 422 in BSS).
/// </summary>
public static class BeatmapsetEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v2/beatmapsets/{setId:long}", GetBeatmapSetAsync);

        // Fetched at login by the client. The literal "me/beatmapset-favourites" route
        // out-specifies MeEndpoints' "me/{ruleset}" template, so both can coexist.
        app.MapGet("/api/v2/me/beatmapset-favourites", GetFavouritesAsync).RequireBearer();
    }

    private static async Task<IResult> GetBeatmapSetAsync(long setId, HttpContext ctx, Db db)
    {
        // Optional auth (bearer for the game, session cookie for the website): only needed to
        // see one's own hidden/removed sets and the has_favourited flag.
        var requester = await ctx.ResolveBearerAsync() ?? ctx.SessionUser();

        await using var conn = await db.OpenAsync(ctx.RequestAborted);

        var set = await conn.QuerySingleOrDefaultAsync<SetRow>(
            """
            SELECT s.id              AS id,
                   s.owner_id        AS ownerId,
                   s.title           AS title,
                   s.title_unicode   AS titleUnicode,
                   s.artist          AS artist,
                   s.artist_unicode  AS artistUnicode,
                   s.source          AS source,
                   s.tags            AS tags,
                   s.status          AS status,
                   s.explicit        AS explicit,
                   s.has_video       AS hasVideo,
                   s.cover_key       AS coverKey,
                   s.preview_key     AS previewKey,
                   s.bpm             AS bpm,
                   s.play_count      AS playCount,
                   s.favourite_count AS favouriteCount,
                   s.submitted_at    AS submittedAt,
                   s.updated_at      AS updatedAt,
                   u.username        AS creator,
                   s.language        AS language
            FROM beatmapsets s
            JOIN users u ON u.id = s.owner_id
            WHERE s.id = @setId
            """,
            new { setId });

        if (set is null || (!IsPublished(set.Status) && requester?.Id != set.OwnerId))
            return WireJson.Error(StatusCodes.Status404NotFound, "not found");

        // Live difficulties only: filename IS NOT NULL ⇔ part of the current version (the
        // BSS lifecycle convention, dropped diffs keep their rows for the scores FK).
        var beatmaps = (await conn.QueryAsync<BeatmapRow>(
            """
            SELECT id                AS id,
                   ruleset_id        AS rulesetId,
                   checksum_md5      AS checksum,
                   total_length_s    AS totalLengthSeconds,
                   drain_length_s    AS drainLengthSeconds,
                   difficulty_rating AS difficultyRating,
                   version_name      AS version,
                   play_count        AS playCount
            FROM beatmaps
            WHERE set_id = @setId AND filename IS NOT NULL
            ORDER BY difficulty_rating, id
            """,
            new { setId })).ToList();

        bool hasFavourited = requester != null && await conn.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM favourites WHERE user_id = @userId AND set_id = @setId)",
            new { userId = requester!.Id, setId });

        string urlBase = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
        string status = StatusString(set.Status);

        return WireJson.Ok(new APIBeatmapSetResponse
        {
            Id = (int)set.Id,
            Title = set.Title,
            Artist = set.Artist,
            Status = status,
            Creator = set.Creator,
            UserId = (int)set.OwnerId,
            Covers = BeatmapCovers.FromCoverKey(urlBase, set.CoverKey),
            SubmittedDate = set.SubmittedAt,
            // No dedicated ranked-date column; updated_at is the same anchor the lookup uses.
            RankedDate = set.Status == "ranked" ? set.UpdatedAt : null,
            LastUpdated = set.UpdatedAt,
            TitleUnicode = set.TitleUnicode,
            ArtistUnicode = set.ArtistUnicode,
            Source = set.Source,
            Tags = set.Tags,
            PreviewUrl = set.PreviewKey is { } previewKey ? $"{urlBase}/{previewKey}" : string.Empty,
            HasFavourited = hasFavourited,
            PlayCount = set.PlayCount,
            FavouriteCount = set.FavouriteCount,
            Bpm = set.Bpm is { } bpm ? (double)bpm : 0,
            HasVideo = set.HasVideo,
            // Echoed so the wizard can preselect the explicit toggle when updating a set.
            Explicit = set.Explicit,
            SongLanguage = set.Language,
            Beatmaps =beatmaps.Select(b => new APIBeatmapResponse
            {
                Id = (int)b.Id,
                BeatmapsetId = (int)set.Id,
                ModeInt = b.RulesetId,
                Status = status,
                Checksum = b.Checksum,
                UserId = (int)set.OwnerId,
                DifficultyRating = b.DifficultyRating,
                TotalLength = b.TotalLengthSeconds,
                HitLength = b.DrainLengthSeconds,
                Version = b.Version,
                LastUpdated = set.UpdatedAt,
                PlayCount = b.PlayCount,
                Beatmapset = null, // the outer object is the set; no back-reference.
            }).ToList(),
        });
    }

    private static async Task<IResult> GetFavouritesAsync(HttpContext ctx, Db db)
    {
        var user = ctx.AuthedUser();

        await using var conn = await db.OpenAsync(ctx.RequestAborted);

        var ids = (await conn.QueryAsync<long>(
            "SELECT set_id FROM favourites WHERE user_id = @userId ORDER BY created_at DESC, set_id DESC",
            new { userId = user.Id })).ToList();

        return WireJson.Ok(new { beatmapset_ids = ids });
    }

    /// <summary>
    /// True for the three PUBLISHED beatmapset statuses: 'pending' (awaiting review), 'unranked'
    /// (creator opted out of ranking) and 'ranked'. Those are world-readable: browsable, and their
    /// package, covers and preview stream to anyone, signed in or not.
    ///
    /// This is a SECURITY BOUNDARY, so it is an explicit allow-list of three and never a "not
    /// hidden" test. 'hidden' is an unpublished shell (nobody's business but the owner's) and
    /// 'removed' is a takedown, whose bytes must keep 404ing for everyone but the owner: the
    /// runbook in deploy/README.md and the bounded one-day cover TTL both depend on it. A new
    /// status must be added here deliberately, after deciding it is publishable.
    ///
    /// It exists because the same list was hand-copied into four gates and migration 012's
    /// 'unranked' reached only two of them, which owner-locked every unranked set's download,
    /// cover and preview.
    /// </summary>
    public static bool IsPublished(string dbStatus) => dbStatus is "pending" or "unranked" or "ranked";

    /// <summary>DB status → the wire string the client's BeatmapOnlineStatus binds by name.</summary>
    public static string StatusString(string dbStatus) => dbStatus switch
    {
        "ranked" => "ranked",
        "pending" => "pending",
        "unranked" => "unranked",
        "hidden" => "wip",
        _ => "graveyard",
    };

    // timestamptz arrives as UTC DateTime (Dapper ctor matching fails on DateTimeOffset params).
    private sealed record SetRow(
        long Id,
        long OwnerId,
        string Title,
        string TitleUnicode,
        string Artist,
        string ArtistUnicode,
        string Source,
        string Tags,
        string Status,
        bool Explicit,
        bool HasVideo,
        string? CoverKey,
        string? PreviewKey,
        decimal? Bpm,
        int PlayCount,
        int FavouriteCount,
        DateTime SubmittedAt,
        DateTime UpdatedAt,
        string Creator,
        // Appended last: Dapper matches this positional record's constructor by column order.
        string Language);

    private sealed record BeatmapRow(
        long Id,
        short RulesetId,
        string Checksum,
        double TotalLengthSeconds,
        double DrainLengthSeconds,
        double DifficultyRating,
        string Version,
        int PlayCount);
}
