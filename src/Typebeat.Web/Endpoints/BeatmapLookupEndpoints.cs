using Dapper;
using Microsoft.Extensions.Caching.Memory;
using Typebeat.Web.Auth;
using Typebeat.Web.Caching;
using Typebeat.Web.Data;
using Typebeat.Web.Packages;
using Typebeat.Web.Wire;

namespace Typebeat.Web.Endpoints;

/// <summary>
/// GET /api/v2/beatmaps/lookup: the client's import-time metadata lookup
/// (typebeat.Game.Beatmaps.APIBeatmapMetadataSource via GetBeatmapRequest, Target
/// "beatmaps/lookup"). The request carries up to three query params, added only when set:
///   checksum = the local .osu MD5 (primary identity), id = OnlineID, filename = the .osu path.
///
/// We resolve by checksum first (so the echoed checksum equals the client's local hash and
/// MatchesOnlineVersion holds), then by id. filename-only lookups have no column to resolve
/// against in our schema, so they 404, which the client handles gracefully: a Failed request
/// leaves onlineMetadata null and the map is simply treated as not-online (no logout, no crash).
///
/// Published sets ('pending', 'unranked', 'ranked' or 'loved') are visible, and the REAL status is reported:
/// "ranked" unlocks leaderboards client-side, "pending" keeps them locked until a map
/// reviewer approves the set (migration 005). Authed: the client always runs lookups through
/// the API, so RequireBearer.
/// </summary>
public static class BeatmapLookupEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        // A pack import fires one lookup per difficulty, so this gets a deep token bucket rather
        // than a window (backlog 366, Auth/RateLimits.cs).
        app.MapGet("/api/v2/beatmaps/lookup", HandleAsync).RequireBearer().RequireRateLimiting(RateLimits.Lookup);
    }

    private static async Task<IResult> HandleAsync(HttpContext ctx, Db db)
    {
        string? checksum = trimmed(ctx.Request.Query["checksum"]);
        // filename is accepted (the client sends it) but our schema stores no per-map path, so it
        // is not a resolvable key on its own; present only so an id/checksum-less call still 404s.
        _ = trimmed(ctx.Request.Query["filename"]);

        bool hasId = int.TryParse(ctx.Request.Query["id"], out int id) && id > 0;

        // Resolve by checksum first, then by online id. Neither present → nothing to look up.
        // A FOUND row is memoised for CacheEviction.LookupMemoTtl (backlog 366): a pack import
        // asks for the same handful of maps again on every metadata refresh, and every upload or
        // rank change drops the whole memo. A miss is never memoised, so a map published a second
        // ago is found at once.
        var eviction = ctx.RequestServices.GetRequiredService<CacheEviction>();
        string? memoKey = checksum is not null ? eviction.LookupKey("c", checksum)
            : hasId ? eviction.LookupKey("i", id.ToString(System.Globalization.CultureInfo.InvariantCulture))
            : null;

        LookupRow? row = null;
        if (memoKey is not null && !eviction.Memo.TryGetValue(memoKey, out row))
        {
            await using var conn = await db.OpenAsync(ctx.RequestAborted);

            row = checksum is not null
                ? await conn.QuerySingleOrDefaultAsync<LookupRow>(baseQuery + "AND b.checksum_md5 = @checksum", new { checksum })
                : await conn.QuerySingleOrDefaultAsync<LookupRow>(baseQuery + "AND b.id = @id", new { id });

            if (row is not null)
                eviction.Memo.Set(memoKey, row, CacheEviction.LookupMemoTtl);
        }

        if (row is null)
            return WireJson.Error(StatusCodes.Status404NotFound, "not found");

        // Real covers when the set has them (cover_key is the covers/{setId}/{ver} prefix);
        // the generated /img/default-cover.jpg otherwise. Always this host, never a ppy CDN.
        string urlBase = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
        string status = BeatmapsetEndpoints.StatusString(row.Status);

        // Whether the set's current version carries a vocals stem (backlog 396). Computed once per
        // lookup, on the set the resolved difficulty belongs to, never per difficulty row: a stem is
        // a set-level file and every difficulty shares the package. Deliberately outside the lookup
        // memo (which caches only the row): a stem attach cuts a new version, and this must read the
        // live version every time rather than a memoised one.
        bool hasVocalsStem = await StemBackfill.CurrentVersionHasStemAsync(db, row.BeatmapSetId, ctx.RequestAborted);

        return WireJson.Ok(new APIBeatmapResponse
        {
            Id = (int)row.BeatmapId,
            BeatmapsetId = (int)row.BeatmapSetId,
            ModeInt = row.RulesetId,
            Status = status,
            Checksum = row.Checksum,
            UserId = (int)row.OwnerId,
            DifficultyRating = row.DifficultyRating,
            TotalLength = row.TotalLengthSeconds,
            HitLength = row.DrainLengthSeconds,
            Version = row.Version,
            LastUpdated = row.UpdatedAt,
            Beatmapset = new APIBeatmapSetResponse
            {
                Id = (int)row.BeatmapSetId,
                Title = row.Title,
                Artist = row.Artist,
                Status = status,
                Creator = row.Creator,
                UserId = (int)row.OwnerId,
                Covers = BeatmapCovers.FromCoverKey(urlBase, row.CoverKey),
                SubmittedDate = row.SubmittedAt,
                // Schema has no dedicated ranked-date column; updated_at (when the set was last
                // touched) is the closest anchor. Pending sets have no ranked date.
                RankedDate = row.Status is "ranked" or "loved" ? row.UpdatedAt : null,
                LastUpdated = row.UpdatedAt,
                Explicit = row.Explicit,
                // Fills a local Unspecified language for maps whose .osu carries no Language: line.
                SongLanguage = row.Language,
                // The set-level stem flag (backlog 396); the client's UPDATE offer reads it against
                // its local copy's stem presence.
                HasVocalsStem = hasVocalsStem,
            },
        });
    }

    // Published sets only ('pending', 'unranked', 'ranked' or 'loved'). The predicate is completed by the caller
    // with the identity clause. The status column rides along so the response reports the
    // real state: "pending" is what keeps un-reviewed maps' leaderboards locked client-side.
    private const string baseQuery =
        """
        SELECT b.id               AS beatmapId,
               b.set_id           AS beatmapSetId,
               b.ruleset_id       AS rulesetId,
               b.checksum_md5     AS checksum,
               b.total_length_s   AS totalLengthSeconds,
               b.drain_length_s   AS drainLengthSeconds,
               b.difficulty_rating AS difficultyRating,
               b.version_name     AS version,
               bs.id              AS setId,
               bs.owner_id        AS ownerId,
               bs.title           AS title,
               bs.artist          AS artist,
               bs.cover_key       AS coverKey,
               bs.status          AS status,
               bs.explicit        AS explicit,
               u.username         AS creator,
               bs.submitted_at    AS submittedAt,
               bs.updated_at      AS updatedAt,
               bs.language        AS language
        FROM beatmaps b
        JOIN beatmapsets bs ON bs.id = b.set_id
        JOIN users u ON u.id = bs.owner_id
        WHERE bs.status IN ('pending', 'unranked', 'ranked', 'loved')

        """;

    private static string? trimmed(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // snake_case columns aliased to camelCase per the SELECT idiom (see TokenService).
    private sealed record LookupRow(
        long BeatmapId,
        long BeatmapSetId,
        short RulesetId,
        string Checksum,
        double TotalLengthSeconds,
        double DrainLengthSeconds,
        double DifficultyRating,
        string Version,
        long SetId,
        long OwnerId,
        string Title,
        string Artist,
        string? CoverKey,
        string Status,
        bool Explicit,
        string Creator,
        // timestamptz arrives from Npgsql as UTC DateTime; a DateTimeOffset ctor param makes
        // Dapper's constructor matching fail at runtime ("no matching signature").
        DateTime SubmittedAt,
        DateTime UpdatedAt,
        // Appended last: Dapper matches this positional record's constructor by column order.
        string Language);
}
