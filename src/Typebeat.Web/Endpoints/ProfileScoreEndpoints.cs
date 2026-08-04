using System.Globalization;
using Dapper;
using Npgsql;
using Typebeat.Web.Auth;
using Typebeat.Web.Data;
using Typebeat.Web.Scoring;
using Typebeat.Web.Wire;

namespace Typebeat.Web.Endpoints;

/// <summary>
/// The game profile overlay's SECTION fetches, the routes task 81 found missing:
/// <c>GET /api/v2/users/{id}/scores/{best|firsts|recent|pinned}</c> (the Ranks section's three
/// subsections plus Historical's recent plays) and
/// <c>GET /api/v2/users/{id}/beatmapsets/most_played</c> (Historical's most-played list). Each
/// answers a JSON ARRAY, which is what <c>PaginatedProfileSubsection</c> deserializes into a
/// <c>List&lt;T&gt;</c>.
///
/// <para>
/// WHICH ROWS each section is comes from <see cref="ProfileScores"/> and
/// <see cref="BeatmapLeaderboard"/>, never from SQL written here, so these lists and the website
/// profile's identically-named sections cannot disagree about a player's best play or their first
/// places. The matching COUNTS ride on the user payload (<see cref="UserWire.User"/>) and are
/// computed from those same fragments, because a section heading that claims a different number
/// from the list under it is exactly the failure this pair of endpoints exists to avoid.
/// </para>
///
/// <para>
/// PAGINATION is osu's: <c>offset</c> and <c>limit</c> query parameters, defaulted and clamped
/// here. The client asks for one MORE row than it means to show and treats the overflow as "there
/// is another page", so the clamp has to leave room for that (it asks for 51 on a 50-row page).
/// </para>
///
/// <para>
/// A profile that does not exist, or belongs to a restricted or deleted account, 404s rather than
/// answering an empty array: an empty list is a real answer about a real player, which the client
/// renders as "this section has nothing in it". Restricted users are invisible everywhere else on
/// this server and their sections are no exception.
/// </para>
/// </summary>
public static class ProfileScoreEndpoints
{
    /// <summary>Default page size when the client sends no <c>limit</c>.</summary>
    private const int default_limit = 10;

    /// <summary>
    /// Ceiling on <c>limit</c>. The client's largest legitimate ask is 51 (a 50-row page plus the
    /// probe row); 100 leaves headroom without letting a hand-made request page a whole history in
    /// one request.
    /// </summary>
    private const int max_limit = 100;

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v2/users/{userId:long}/scores/{type}", Scores).RequireBearer();
        app.MapGet("/api/v2/users/{userId:long}/beatmapsets/most_played", MostPlayed).RequireBearer();
    }

    // ---------------------------------------------------------------------------------------------
    // scores/{type}
    // ---------------------------------------------------------------------------------------------

    private static async Task<IResult> Scores(long userId, string type, HttpContext ctx, Db db)
    {
        await using var conn = await db.OpenAsync(ctx.RequestAborted);

        var owner = await visibleUserAsync(conn, userId, ctx.RequestAborted);

        if (owner is null)
            return WireJson.Error(StatusCodes.Status404NotFound, "user not found");

        var (offset, limit) = pagination(ctx);

        // The row set and the order it reads in, per section. Only the LIMIT/OFFSET and the display
        // join are written here; everything deciding WHICH plays these are lives in the fragments.
        (string rows, string order, bool filterVisibleSets) = type.ToLowerInvariant() switch
        {
            "pinned" => (ProfileScores.PinnedOfUser(score_columns), ProfileScores.PinnedOrder, true),
            "best" => (ProfileScores.BestOfUser(score_columns), ProfileScores.BestOrder, true),
            // First places are already confined to 'ranked' sets by the board definition itself,
            // which is a strict subset of visible, so no second status predicate is applied. A
            // duplicate one here would only be a second place for the two to drift apart.
            "firsts" => (BeatmapLeaderboard.FirstPlacesOfUserSql, ProfileScores.FirstPlacesOrder, false),
            "recent" => (ProfileScores.RecentOfUser(score_columns), ProfileScores.RecentOrder, true),
            _ => (string.Empty, string.Empty, false),
        };

        if (rows.Length == 0)
            return WireJson.Error(StatusCodes.Status404NotFound, "unknown score type");

        var scoreRows = await conn.QueryAsync<ProfileScoreRow>(new CommandDefinition(
            $"""
             SELECT {score_select},
                    {beatmap_select}
             FROM ({rows}) best
             JOIN beatmaps b ON b.id = best.beatmap_id
             JOIN beatmapsets s ON s.id = b.set_id
             JOIN users u ON u.id = s.owner_id
             {(filterVisibleSets ? $"WHERE {ProfileScores.OnVisibleSet("s")}" : string.Empty)}
             ORDER BY {order}
             LIMIT @limit OFFSET @offset
             """,
            new { id = userId, limit, offset }, cancellationToken: ctx.RequestAborted));

        string scheme = ctx.Request.Scheme;
        string host = ctx.Request.Host.Value ?? string.Empty;
        string urlBase = $"{scheme}://{host}";

        // One user object, reused by every row: every score on a profile belongs to that profile.
        var user = new ScoreUserWire
        {
            Id = owner.Id,
            Username = owner.Username,
            CountryCode = owner.CountryCode,
            AvatarUrl = UserWire.AvatarUrl(scheme, host, owner.AvatarKey),
        };

        return WireJson.Ok(scoreRows.Select(r => toWire(r, user, urlBase)).ToList());
    }

    // ---------------------------------------------------------------------------------------------
    // beatmapsets/most_played
    // ---------------------------------------------------------------------------------------------

    private static async Task<IResult> MostPlayed(long userId, HttpContext ctx, Db db)
    {
        await using var conn = await db.OpenAsync(ctx.RequestAborted);

        if (await visibleUserAsync(conn, userId, ctx.RequestAborted) is null)
            return WireJson.Error(StatusCodes.Status404NotFound, "user not found");

        var (offset, limit) = pagination(ctx);

        var rows = await conn.QueryAsync<MostPlayedRow>(new CommandDefinition(
            $"""
             SELECT mp.plays AS Plays,
                    {beatmap_select}
             FROM ({ProfileScores.MostPlayedOfUserSql}) mp
             JOIN beatmaps b ON b.id = mp.beatmap_id
             JOIN beatmapsets s ON s.id = b.set_id
             JOIN users u ON u.id = s.owner_id
             ORDER BY {ProfileScores.MostPlayedOrder}
             LIMIT @limit OFFSET @offset
             """,
            new { id = userId, limit, offset }, cancellationToken: ctx.RequestAborted));

        string urlBase = $"{ctx.Request.Scheme}://{ctx.Request.Host}";

        return WireJson.Ok(rows.Select(r =>
        {
            var set = buildSet(r, urlBase);

            return new MostPlayedBeatmapWire
            {
                BeatmapId = r.BeatmapId,
                PlayCount = r.Plays,
                // Sent nested AND as a sibling: the client's BeatmapInfo getter reassigns
                // beatmap.BeatmapSet from the sibling every time it is read, so a payload carrying
                // only the nested form would have it nulled out on first access.
                Beatmap = buildBeatmap(r, set),
                BeatmapSet = set,
            };
        }).ToList());
    }

    // ---- helpers ----

    /// <summary>
    /// The profile owner, or null when this profile may not be looked at at all. Restricted and
    /// deleted accounts 404 here for the same reason <see cref="UserEndpoints"/> 404s them: they
    /// are invisible site-wide, and a section endpoint answering <c>[]</c> would confirm the
    /// account exists.
    /// </summary>
    private static async Task<ScoreOwnerRow?> visibleUserAsync(NpgsqlConnection conn, long userId, CancellationToken ct)
        => await conn.QuerySingleOrDefaultAsync<ScoreOwnerRow>(new CommandDefinition(
            """
            SELECT id AS Id, username::text AS Username, country_code AS CountryCode, avatar_key AS AvatarKey
            FROM users
            WHERE id = @userId AND NOT restricted AND deleted_at IS NULL
            """,
            new { userId }, cancellationToken: ct));

    /// <summary>Reads and clamps osu's <c>offset</c>/<c>limit</c> pair. Garbage falls back to the defaults.</summary>
    private static (int Offset, int Limit) pagination(HttpContext ctx)
    {
        int offset = int.TryParse(ctx.Request.Query["offset"], NumberStyles.Integer, CultureInfo.InvariantCulture, out int o)
            ? Math.Max(0, o)
            : 0;

        int limit = int.TryParse(ctx.Request.Query["limit"], NumberStyles.Integer, CultureInfo.InvariantCulture, out int l)
            ? Math.Clamp(l, 1, max_limit)
            : default_limit;

        return (offset, limit);
    }

    /// <summary>
    /// The score columns the three parameterised section fragments project, qualified with the
    /// <c>sc</c> alias those fragments use. Aliased to the names <see cref="score_select"/> reads
    /// back off the <c>best</c> subquery, which is also the column list
    /// <see cref="BeatmapLeaderboard.FirstPlacesOfUserSql"/> yields (that one writes its own
    /// projection, so the two lists have to stay in step; that is why its doc comment names them).
    /// </summary>
    private const string score_columns =
        """
        sc.id, sc.beatmap_id, sc.rank, sc.accuracy, sc.total_score, sc.max_combo, sc.passed,
        sc.ranked, sc.pp, sc.ended_at, sc.mods::text AS mods,
        sc.statistics::text AS statistics, sc.maximum_statistics::text AS maximum_statistics,
        sc.replay_key IS NOT NULL AS has_replay
        """;

    private const string score_select =
        """
        best.id AS ScoreId, best.rank AS Rank, best.accuracy AS Accuracy, best.total_score AS TotalScore,
        best.max_combo AS MaxCombo, best.passed AS Passed, best.ranked AS Ranked, best.pp AS Pp,
        best.ended_at AS EndedAt, best.mods AS ModsJson, best.statistics AS StatisticsJson,
        best.maximum_statistics AS MaximumStatisticsJson, best.has_replay AS HasReplay
        """;

    /// <summary>
    /// Everything <see cref="buildBeatmap"/> and <see cref="buildSet"/> need, from the
    /// <c>b</c>/<c>s</c>/<c>u</c> joins both handlers make.
    /// </summary>
    private const string beatmap_select =
        """
        b.id AS BeatmapId, b.set_id AS SetId, b.ruleset_id AS RulesetId, b.checksum_md5 AS Checksum,
        b.total_length_s AS TotalLengthS, b.drain_length_s AS DrainLengthS,
        b.difficulty_rating AS DifficultyRating, b.version_name AS Version,
        s.owner_id AS OwnerId, s.title AS Title, s.artist AS Artist,
        s.title_unicode AS TitleUnicode, s.artist_unicode AS ArtistUnicode,
        s.cover_key AS CoverKey, s.status AS Status, s.explicit AS Explicit,
        s.submitted_at AS SubmittedAt, s.updated_at AS UpdatedAt, u.username::text AS Creator
        """;

    private static ProfileScoreWire toWire(ProfileScoreRow r, ScoreUserWire user, string urlBase)
    {
        var set = buildSet(r, urlBase);

        return new ProfileScoreWire
        {
            Id = r.ScoreId,
            BeatmapId = r.BeatmapId,
            RulesetId = r.RulesetId,
            Passed = r.Passed,
            TotalScore = r.TotalScore,
            Accuracy = r.Accuracy,
            UserId = user.Id,
            MaxCombo = r.MaxCombo,
            Rank = r.Rank,
            EndedAt = r.EndedAt,
            Mods = ScoreEndpoints.ParseMods(r.ModsJson),
            Statistics = ScoreEndpoints.ParseCounts(r.StatisticsJson),
            MaximumStatistics = ScoreEndpoints.ParseCounts(r.MaximumStatisticsJson),
            User = user,
            Beatmap = buildBeatmap(r, set),
            // PpRanking's eligibility rule, not a second opinion: a stored 0 means custom-rate or
            // not-yet-priced, both of which that board excludes, so it goes on the wire as "no pp"
            // rather than as the number 0. Paired with Processed = true the client renders a dash.
            Pp = r.Pp > 0 ? r.Pp : null,
            HasReplay = r.HasReplay,
            Ranked = r.Ranked,
            Preserve = true,
            Processed = true,
        };
    }

    private static APIBeatmapSetResponse buildSet(BeatmapDisplayRow b, string urlBase) => new()
    {
        Id = (int)b.SetId,
        Title = b.Title,
        Artist = b.Artist,
        Status = BeatmapsetEndpoints.StatusString(b.Status),
        Creator = b.Creator,
        UserId = (int)b.OwnerId,
        Covers = BeatmapCovers.FromCoverKey(urlBase, b.CoverKey),
        SubmittedDate = b.SubmittedAt,
        // No dedicated ranked-date column; updated_at is the anchor the lookup endpoint uses too.
        RankedDate = b.Status == "ranked" ? b.UpdatedAt : null,
        LastUpdated = b.UpdatedAt,
        TitleUnicode = b.TitleUnicode,
        ArtistUnicode = b.ArtistUnicode,
        Explicit = b.Explicit,
    };

    private static APIBeatmapResponse buildBeatmap(BeatmapDisplayRow b, APIBeatmapSetResponse set) => new()
    {
        Id = (int)b.BeatmapId,
        BeatmapsetId = (int)b.SetId,
        ModeInt = b.RulesetId,
        Status = set.Status,
        Checksum = b.Checksum,
        UserId = (int)b.OwnerId,
        DifficultyRating = b.DifficultyRating,
        TotalLength = b.TotalLengthS,
        HitLength = b.DrainLengthS,
        Version = b.Version,
        LastUpdated = b.UpdatedAt,
        Beatmapset = set,
    };

    // ---- Dapper row shapes ----
    //
    // Property-mapped classes rather than positional records: these carry ~20 columns each and a
    // positional record makes Dapper demand a constructor matching the WHOLE column list, so one
    // added column silently fails every row (the landmine Pages/Users/Profile.cshtml.cs records).
    // timestamptz arrives from Npgsql as a UTC DateTime; DateTimeOffset members would break the
    // mapping the same way, so the conversion to DateTimeOffset happens on the way to the wire.

    /// <summary>The map + set columns every row of both endpoints carries, for display.</summary>
    private class BeatmapDisplayRow
    {
        public long BeatmapId { get; set; }
        public long SetId { get; set; }
        public short RulesetId { get; set; }
        public string Checksum { get; set; } = string.Empty;
        public double TotalLengthS { get; set; }
        public double DrainLengthS { get; set; }
        public double DifficultyRating { get; set; }
        public string Version { get; set; } = string.Empty;
        public long OwnerId { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;
        public string TitleUnicode { get; set; } = string.Empty;
        public string ArtistUnicode { get; set; } = string.Empty;
        public string? CoverKey { get; set; }
        public string Status { get; set; } = string.Empty;
        public bool Explicit { get; set; }
        public DateTime SubmittedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public string Creator { get; set; } = string.Empty;
    }

    private sealed class ProfileScoreRow : BeatmapDisplayRow
    {
        public long ScoreId { get; set; }
        public string Rank { get; set; } = "D";
        public double Accuracy { get; set; }
        public long TotalScore { get; set; }
        public int MaxCombo { get; set; }
        public bool Passed { get; set; }
        public bool Ranked { get; set; }
        public double Pp { get; set; }
        public DateTime EndedAt { get; set; }
        public string? ModsJson { get; set; }
        public string? StatisticsJson { get; set; }
        public string? MaximumStatisticsJson { get; set; }
        public bool HasReplay { get; set; }
    }

    private sealed class MostPlayedRow : BeatmapDisplayRow
    {
        public long Plays { get; set; }
    }

    private sealed record ScoreOwnerRow(long Id, string Username, string CountryCode, string? AvatarKey);
}
