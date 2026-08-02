using Dapper;
using Microsoft.AspNetCore.Antiforgery;
using Newtonsoft.Json;
using Npgsql;
using Typebeat.Web.Auth;
using Typebeat.Web.Data;
using Typebeat.Web.Scoring;
using Typebeat.Web.Storage;
using Typebeat.Web.Wire;

namespace Typebeat.Web.Endpoints;

/// <summary>
/// The in-browser web player's backend (additive; the bearer game-client score flow in
/// <see cref="ScoreEndpoints"/> is untouched). Four routes:
///
///  - GET  /play/map/{setId}/osu    → the set's primary .osu text (anonymous, same media gate as downloads)
///  - GET  /play/map/{setId}/audio  → the map's audio blob, range-capable (anonymous, same gate)
///  - POST /play/token              → issue a score token (cookie session + antiforgery)
///  - POST /play/submit             → complete a score token (cookie session + antiforgery)
///
/// The two mutating routes are the cookie-session mirror of <see cref="ScoreEndpoints"/>'s
/// CreateToken/SubmitScore: identical recompute + tamper-bounds via <see cref="ScoringContract"/>,
/// identical scores/user_stats side effects, but authenticated by the website session cookie
/// (<see cref="SessionCookieAuth.SessionUser"/>) and CSRF-protected via <see cref="IAntiforgery"/>
/// rather than a bearer token. Unlike the bearer path there is no client-sent beatmap_hash to
/// cross-check: we serve the exact map, so score_tokens.beatmap_hash is populated from the
/// server's stored checksum for the beatmap (the column is NOT NULL). Tamper-shaped input on the
/// mutating routes always resolves to a 4xx, never a 500.
/// </summary>
public static class PlayEndpoints
{
    private const int status_unprocessable = StatusCodes.Status422UnprocessableEntity;
    private const string web_build_hash = "web-player";

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/play/map/{setId:long}/osu", GetOsuAsync);
        app.MapGet("/play/map/{setId:long}/audio", GetAudioAsync);
        app.MapPost("/play/token", CreateTokenAsync);
        app.MapPost("/play/submit", SubmitScoreAsync);
    }

    // ---------------------------------------------------------------------------------------------
    // GET /play/map/{setId}/osu: the primary difficulty's .osu, served as text/plain. Anonymous,
    // same media-access gate as the download route (published set, or the owner).
    // ---------------------------------------------------------------------------------------------
    private static async Task<IResult> GetOsuAsync(long setId, HttpContext ctx, Db db, IFileStore store)
    {
        await using var conn = await db.OpenAsync(ctx.RequestAborted);

        if (!await CanSeeSetMediaAsync(ctx, conn, setId))
            return Results.NotFound();

        string? osuName = await ResolveOsuFilenameAsync(conn, setId);
        if (osuName is null)
            return Results.NotFound();

        var stream = await OpenManifestBlobAsync(conn, store, setId, osuName, ctx.RequestAborted);
        if (stream is null)
            return Results.NotFound();

        return Results.Stream(stream, "text/plain; charset=utf-8");
    }

    // ---------------------------------------------------------------------------------------------
    // GET /play/map/{setId}/audio: the AudioFilename referenced by the primary .osu, streamed with
    // range support (so <audio> can seek). Anonymous, same gate as /osu.
    // ---------------------------------------------------------------------------------------------
    private static async Task<IResult> GetAudioAsync(long setId, HttpContext ctx, Db db, IFileStore store)
    {
        await using var conn = await db.OpenAsync(ctx.RequestAborted);

        if (!await CanSeeSetMediaAsync(ctx, conn, setId))
            return Results.NotFound();

        string? osuName = await ResolveOsuFilenameAsync(conn, setId);
        if (osuName is null)
            return Results.NotFound();

        // Read the .osu text to discover its AudioFilename, then resolve THAT to a blob.
        byte[]? osuSha = await ResolveManifestShaAsync(conn, setId, osuName);
        if (osuSha is null)
            return Results.NotFound();

        string osuText;
        try
        {
            await using var osuStream = await store.OpenBlobReadAsync(osuSha, ctx.RequestAborted);
            using var reader = new StreamReader(osuStream);
            osuText = await reader.ReadToEndAsync(ctx.RequestAborted);
        }
        catch (FileNotFoundException)
        {
            return Results.NotFound();
        }

        string? audioName = ParseAudioFilename(osuText);
        if (string.IsNullOrEmpty(audioName))
            return Results.NotFound();

        var stream = await OpenManifestBlobAsync(conn, store, setId, audioName, ctx.RequestAborted);
        if (stream is null)
            return Results.NotFound();

        return Results.Stream(stream, AudioContentType(audioName), enableRangeProcessing: true);
    }

    // ---------------------------------------------------------------------------------------------
    // POST /play/token: issue a score token for the signed-in website user. Body: { "beatmapId": <long> }.
    // Response: { "id": <long> }.
    // ---------------------------------------------------------------------------------------------
    private static async Task<IResult> CreateTokenAsync(HttpContext ctx, Db db, IAntiforgery antiforgery)
    {
        var user = ctx.SessionUser();
        if (user is null)
            return WireJson.Error(StatusCodes.Status401Unauthorized, "authentication required");

        try
        {
            await antiforgery.ValidateRequestAsync(ctx);
        }
        catch (AntiforgeryValidationException)
        {
            return WireJson.Error(StatusCodes.Status400BadRequest, "invalid antiforgery token");
        }

        TokenRequest? request;
        try
        {
            using var reader = new StreamReader(ctx.Request.Body);
            string body = await reader.ReadToEndAsync(ctx.RequestAborted);
            request = JsonConvert.DeserializeObject<TokenRequest>(body);
        }
        catch (JsonException)
        {
            request = null;
        }

        if (request is null || (request.SetId <= 0 && request.BeatmapId <= 0))
            return WireJson.Error(StatusCodes.Status400BadRequest, "invalid request body");

        await using var conn = await db.OpenAsync(ctx.RequestAborted);

        // Resolve the playable beatmap: by SET id (the picker's path, the set's primary .osu diff)
        // or, for back-compat, by an explicit beatmap id. The set must be published (pending/ranked).
        var beatmap = request.SetId > 0
            ? await conn.QuerySingleOrDefaultAsync<BeatmapRow>(
                """
                SELECT b.id, b.checksum_md5 AS checksumMd5, b.drain_length_s AS drainLengthS, b.skippable_s AS skippableS,
                       b.difficulty_rating AS baseStars
                FROM beatmaps b
                JOIN beatmapsets bs ON bs.id = b.set_id
                WHERE b.set_id = @setId AND bs.status IN ('pending', 'unranked', 'ranked') AND b.filename LIKE '%.osu'
                ORDER BY b.id
                LIMIT 1
                """,
                new { setId = request.SetId })
            : await conn.QuerySingleOrDefaultAsync<BeatmapRow>(
                """
                SELECT b.id, b.checksum_md5 AS checksumMd5, b.drain_length_s AS drainLengthS, b.skippable_s AS skippableS,
                       b.difficulty_rating AS baseStars
                FROM beatmaps b
                JOIN beatmapsets bs ON bs.id = b.set_id
                WHERE b.id = @beatmapId AND bs.status IN ('pending', 'unranked', 'ranked')
                """,
                new { beatmapId = request.BeatmapId });

        if (beatmap is null)
            return WireJson.Error(status_unprocessable, "beatmap not found or not playable");

        // Register the web player's synthetic build on sight (record-don't-reject, like the bearer
        // path). blocked is enforced at submission time (buildBlocked → unranked), not here; an
        // admin blocking the web build stops new ranks, not play.
        await conn.ExecuteAsync(
            "INSERT INTO builds (version_hash) VALUES (@versionHash) ON CONFLICT (version_hash) DO NOTHING",
            new { versionHash = web_build_hash });

        var build = await conn.QuerySingleAsync<BuildRow>(
            "SELECT id, blocked FROM builds WHERE version_hash = @versionHash",
            new { versionHash = web_build_hash });

        // No client beatmap_hash to cross-check (we served the exact map); the NOT NULL column is
        // filled from the server's stored checksum for this beatmap.
        long tokenId = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO score_tokens (user_id, beatmap_id, ruleset_id, beatmap_hash, build_id)
            VALUES (@userId, @beatmapId, 0, @beatmapHash, @buildId)
            RETURNING id
            """,
            new { userId = user.Id, beatmapId = beatmap.Id, beatmapHash = beatmap.ChecksumMd5, buildId = build.Id });

        return WireJson.Ok(new { id = tokenId });
    }

    // ---------------------------------------------------------------------------------------------
    // POST /play/submit: complete a score token. Mirrors ScoreEndpoints.SubmitScore with the
    // session user substituted for the bearer user. Body shape below; response:
    // { ranked, rank, total_score, accuracy, completion, position }.
    // ---------------------------------------------------------------------------------------------
    private static async Task<IResult> SubmitScoreAsync(HttpContext ctx, Db db, IAntiforgery antiforgery, ILoggerFactory loggerFactory)
    {
        var user = ctx.SessionUser();
        if (user is null)
            return WireJson.Error(StatusCodes.Status401Unauthorized, "authentication required");

        try
        {
            await antiforgery.ValidateRequestAsync(ctx);
        }
        catch (AntiforgeryValidationException)
        {
            return WireJson.Error(StatusCodes.Status400BadRequest, "invalid antiforgery token");
        }

        var logger = loggerFactory.CreateLogger("PlaySubmit");

        SubmitRequest? submission;
        try
        {
            using var reader = new StreamReader(ctx.Request.Body);
            string body = await reader.ReadToEndAsync(ctx.RequestAborted);
            submission = JsonConvert.DeserializeObject<SubmitRequest>(body);
        }
        catch (JsonException)
        {
            submission = null;
        }

        if (submission is null)
            return WireJson.Error(status_unprocessable, "invalid request body");

        await using var conn = await db.OpenAsync(ctx.RequestAborted);
        await using var tx = await conn.BeginTransactionAsync(ctx.RequestAborted);

        // Lock the token for the duration of completion so a concurrent submit cannot double-use it.
        var token = await conn.QuerySingleOrDefaultAsync<TokenRow>(
            """
            SELECT id, user_id AS userId, beatmap_id AS beatmapId, build_id AS buildId,
                   score_id AS scoreId, created_at AS createdAt
            FROM score_tokens
            WHERE id = @tokenId
            FOR UPDATE
            """,
            new { tokenId = submission.Token }, tx);

        // Missing, not owned by the caller, or already used → "invalid token" (the body carries no
        // beatmapId; the token's own beatmap_id is authoritative, validated to exist just below).
        if (token is null || token.UserId != user.Id || token.ScoreId is not null)
            return WireJson.Error(status_unprocessable, "invalid token");

        long beatmapId = token.BeatmapId;

        var beatmap = await conn.QuerySingleOrDefaultAsync<BeatmapRow>(
            """
            SELECT id, checksum_md5 AS checksumMd5, drain_length_s AS drainLengthS, skippable_s AS skippableS,
                   difficulty_rating AS baseStars
            FROM beatmaps WHERE id = @beatmapId
            """,
            new { beatmapId }, tx);

        if (beatmap is null)
            return WireJson.Error(status_unprocessable, "invalid token");

        // Re-read set status NOW: a set un-ranked mid-play must resolve against its current state.
        bool setRanked = await conn.ExecuteScalarAsync<bool>(
            """
            SELECT bs.status = 'ranked'
            FROM beatmaps b JOIN beatmapsets bs ON bs.id = b.set_id
            WHERE b.id = @beatmapId
            """,
            new { beatmapId }, tx);

        var statistics = submission.Statistics ?? new Dictionary<string, int>();
        var maximumStatistics = submission.MaximumStatistics ?? new Dictionary<string, int>();

        var recomputed = ScoringContract.Recompute(statistics, maximumStatistics, submission.MaxCombo);
        bool withinBounds = ScoringContract.TotalScoreWithinBounds(submission.TotalScore, recomputed);

        // At least 90% of the map's SKIP-ADJUSTED drain length, in real time at the play's rate,
        // must have elapsed since the token was created; the allowance is what the skip button may
        // legally remove (see PlayTimeGate).
        //
        // The rate is pinned at 1.0 rather than read from anything: the browser player ships no mod
        // UI and this endpoint stores a hardcoded empty mod stack ('[]'::jsonb, below), so there is
        // no speed_change to honour and nothing a caller could claim one through. If the web player
        // ever gains rate mods, this and the stored stack move together.
        const double rate = 1.0;

        double elapsedSeconds = (DateTimeOffset.UtcNow - token.CreatedAt).TotalSeconds;
        bool playTimeOk = PlayTimeGate.Passes(elapsedSeconds, beatmap.DrainLengthS, beatmap.SkippableS, rate);

        bool buildBlocked = await conn.ExecuteScalarAsync<bool>(
            "SELECT blocked FROM builds WHERE id = @buildId", new { buildId = token.BuildId }, tx);

        bool passed = submission.Passed;
        bool fullyJudged = recomputed.AccuracyProgress >= 1;

        bool ranked = setRanked && passed && fullyJudged && recomputed.StatisticsValid && withinBounds && playTimeOk && !buildBlocked;

        double storedAccuracy = passed && fullyJudged ? recomputed.Accuracy : recomputed.JudgedAccuracy;

        if (!playTimeOk)
            logger.LogInformation("Play token {TokenId}: elapsed {Elapsed:0.0}s < required {Required:0.0}s (drain {Drain:0.0}s, skippable {Skippable:0.0}s, rate {Rate:0.00}x), storing unranked.",
                token.Id, elapsedSeconds, PlayTimeGate.RequiredSeconds(beatmap.DrainLengthS, beatmap.SkippableS, rate),
                beatmap.DrainLengthS, beatmap.SkippableS, rate);
        if (!recomputed.StatisticsValid || !withinBounds)
            logger.LogInformation("Play token {TokenId}: out of bounds (statisticsValid={Valid}, totalWithinBounds={Within}), storing unranked.",
                token.Id, recomputed.StatisticsValid, withinBounds);

        long storedTotal = withinBounds ? submission.TotalScore : recomputed.TotalScoreCeiling;
        int storedMaxCombo = Math.Clamp(submission.MaxCombo, 0, recomputed.TheoreticalMaxCombo);
        string rank = passed ? recomputed.Rank : "F";
        var endedAt = DateTimeOffset.UtcNow;

        // Performance points (docs/pp.md). The browser player stores a hardcoded empty mod stack
        // (below), so there is no mod multiplier and no rate to price: the play is always valued at
        // the map's base star rating, which is never NULL, so this row is always settled at the
        // current version and the backfill never has to revisit it.
        var (pp, ppSettled) = PerformancePoints.ForScore(
            ranked,
            mods: [],
            PerformancePoints.CountNotes(statistics),
            storedAccuracy,
            storedMaxCombo,
            beatmap.BaseStars,
            starsDoubleTime: null,
            starsHalfTime: null);

        long scoreId = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO scores
                (user_id, beatmap_id, ruleset_id, total_score, accuracy, completion, max_combo, rank, passed,
                 ranked, preserve, mods, statistics, maximum_statistics, build_id, started_at, ended_at,
                 pp, pp_version)
            VALUES
                (@userId, @beatmapId, 0, @totalScore, @accuracy, @completion, @maxCombo, @rank, @passed,
                 @ranked, @preserve, '[]'::jsonb, CAST(@statistics AS jsonb), CAST(@maximumStatistics AS jsonb),
                 @buildId, @startedAt, @endedAt, @pp, @ppVersion)
            RETURNING id
            """,
            new
            {
                pp,
                ppVersion = ppSettled ? PerformancePoints.VERSION : 0,
                userId = user.Id,
                beatmapId,
                totalScore = storedTotal,
                accuracy = storedAccuracy,
                completion = recomputed.Completion,
                maxCombo = storedMaxCombo,
                rank,
                passed,
                ranked,
                preserve = passed,
                statistics = JsonConvert.SerializeObject(statistics),
                maximumStatistics = JsonConvert.SerializeObject(maximumStatistics),
                buildId = token.BuildId,
                startedAt = token.CreatedAt,
                endedAt
            }, tx);

        // Denormalized play counters (one per submitted play, passed or failed, ranked or not).
        await conn.ExecuteAsync(
            """
            UPDATE beatmaps SET play_count = play_count + 1 WHERE id = @beatmapId;
            UPDATE beatmapsets SET play_count = play_count + 1
            WHERE id = (SELECT set_id FROM beatmaps WHERE id = @beatmapId)
            """,
            new { beatmapId }, tx);

        // Aggregate stats accrue only for submissions that held up to the tamper checks; the same
        // invariants that withhold ranking withhold accumulation.
        if (recomputed.StatisticsValid && withinBounds)
        {
            await conn.ExecuteAsync(
                "INSERT INTO user_stats (user_id) VALUES (@userId) ON CONFLICT (user_id) DO NOTHING",
                new { userId = user.Id }, tx);

            string existingHitCounts = await conn.ExecuteScalarAsync<string>(
                "SELECT hit_counts::text FROM user_stats WHERE user_id = @userId FOR UPDATE",
                new { userId = user.Id }, tx) ?? "{}";

            await conn.ExecuteAsync(
                """
                UPDATE user_stats
                SET play_count  = play_count + 1,
                    total_score = total_score + @totalScore,
                    play_time_s = play_time_s + @playTime,
                    hit_counts  = CAST(@hitCounts AS jsonb)
                WHERE user_id = @userId
                """,
                new
                {
                    userId = user.Id,
                    totalScore = storedTotal,
                    playTime = (long)Math.Round(Math.Min(elapsedSeconds, beatmap.DrainLengthS), MidpointRounding.AwayFromZero),
                    hitCounts = MergeHitCounts(existingHitCounts, statistics)
                }, tx);

            // Play history (024_play_history.sql), same rule as the game client's path: browser
            // plays are real plays, so they land in the same monthly rollup the profile charts.
            await PlayHistory.RecordPlayAsync(conn, tx, user.Id, endedAt, ctx.RequestAborted);
        }

        await conn.ExecuteAsync(
            "UPDATE score_tokens SET score_id = @scoreId WHERE id = @tokenId",
            new { scoreId, tokenId = token.Id }, tx);

        int? position = ranked ? await ComputeUserPosition(conn, tx, beatmapId, user.Id) : null;

        await tx.CommitAsync(ctx.RequestAborted);

        return WireJson.Ok(new
        {
            ranked,
            rank,
            total_score = storedTotal,
            accuracy = storedAccuracy,
            completion = recomputed.Completion,
            position,
        });
    }

    // ---- media helpers ----

    /// <summary>Published sets ('pending'/'ranked') are world-readable; hidden/removed media only for the owner.</summary>
    private static async Task<bool> CanSeeSetMediaAsync(HttpContext ctx, NpgsqlConnection conn, long setId)
    {
        var set = await conn.QuerySingleOrDefaultAsync<(long OwnerId, string Status)?>(
            "SELECT owner_id AS OwnerId, status AS Status FROM beatmapsets WHERE id = @setId",
            new { setId });

        if (set is not { } row)
            return false;

        if (row.Status is "pending" or "ranked")
            return true;

        var requester = ctx.SessionUser() ?? await ctx.ResolveBearerAsync();
        return requester?.Id == row.OwnerId;
    }

    /// <summary>The archive path of the set's primary difficulty .osu, or null if none is live.</summary>
    private static async Task<string?> ResolveOsuFilenameAsync(NpgsqlConnection conn, long setId)
        => await conn.ExecuteScalarAsync<string?>(
            """
            SELECT filename FROM beatmaps
            WHERE set_id = @setId AND filename LIKE '%.osu'
            ORDER BY id
            LIMIT 1
            """,
            new { setId });

    /// <summary>Resolves a filename to its blob sha256 within the set's current version manifest.</summary>
    private static async Task<byte[]?> ResolveManifestShaAsync(NpgsqlConnection conn, long setId, string filename)
        => await conn.ExecuteScalarAsync<byte[]?>(
            """
            SELECT vf.sha256
            FROM set_versions sv
            JOIN version_files vf ON vf.version_id = sv.id
            WHERE sv.set_id = @setId
              AND sv.version_no = (SELECT MAX(version_no) FROM set_versions WHERE set_id = @setId)
              AND lower(vf.filename) = lower(@filename)
            LIMIT 1
            """,
            new { setId, filename });

    /// <summary>Resolves a filename to its blob and opens it, or null when the manifest row or blob is missing.</summary>
    private static async Task<Stream?> OpenManifestBlobAsync(NpgsqlConnection conn, IFileStore store, long setId, string filename, CancellationToken ct)
    {
        byte[]? sha = await ResolveManifestShaAsync(conn, setId, filename);
        if (sha is null)
            return null;

        try
        {
            return await store.OpenBlobReadAsync(sha, ct);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Extracts the [General] AudioFilename value (rest of the line after the first ':').</summary>
    private static string? ParseAudioFilename(string osuText)
    {
        using var reader = new StringReader(osuText);
        bool inGeneral = false;

        for (string? line = reader.ReadLine(); line is not null; line = reader.ReadLine())
        {
            string trimmed = line.Trim();

            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                inGeneral = string.Equals(trimmed, "[General]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (inGeneral && trimmed.StartsWith("AudioFilename", StringComparison.OrdinalIgnoreCase))
            {
                int colon = trimmed.IndexOf(':');
                if (colon >= 0)
                    return trimmed[(colon + 1)..].Trim();
            }
        }

        return null;
    }

    private static string AudioContentType(string filename) => Path.GetExtension(filename).ToLowerInvariant() switch
    {
        ".mp3" => "audio/mpeg",
        ".ogg" => "audio/ogg",
        ".wav" => "audio/wav",
        ".mp4" => "video/mp4",
        ".webm" => "video/webm",
        _ => "application/octet-stream",
    };

    // ---- scoring helpers (mirror of ScoreEndpoints) ----

    private static async Task<int?> ComputeUserPosition(NpgsqlConnection conn, System.Data.Common.DbTransaction? tx, long beatmapId, long userId)
    {
        var best = await conn.QuerySingleOrDefaultAsync<BestScoreRow>(
            """
            SELECT s.id AS id, s.total_score AS totalScore
            FROM scores s
            WHERE s.beatmap_id = @beatmapId AND s.user_id = @userId AND s.ranked AND s.passed
            ORDER BY s.total_score DESC, s.id ASC
            LIMIT 1
            """,
            new { beatmapId, userId }, tx);

        if (best is null)
            return null;

        return await conn.ExecuteScalarAsync<int>(
            """
            SELECT 1 + COUNT(*)
            FROM (
                SELECT DISTINCT ON (s.user_id) s.user_id, s.total_score, s.id
                FROM scores s
                WHERE s.beatmap_id = @beatmapId AND s.ranked AND s.passed
                ORDER BY s.user_id, s.total_score DESC, s.id ASC
            ) b
            WHERE b.total_score > @totalScore
               OR (b.total_score = @totalScore AND b.id < @scoreId)
            """,
            new { beatmapId, totalScore = best.TotalScore, scoreId = best.Id }, tx);
    }

    private static string MergeHitCounts(string existingJson, IReadOnlyDictionary<string, int> add)
    {
        var merged = JsonConvert.DeserializeObject<Dictionary<string, int>>(existingJson) ?? new Dictionary<string, int>();

        foreach (var (key, count) in add)
        {
            if (count <= 0)
                continue;

            merged[key] = merged.GetValueOrDefault(key) + count;
        }

        return JsonConvert.SerializeObject(merged);
    }

    // ---- Dapper row shapes ----

    // Appended, never reordered: Dapper maps positional records by position (BaseStars is the
    // 020_performance_points.sql addition; this player never sends rate mods, so sr_dt/sr_ht are
    // not read here).
    private sealed record BeatmapRow(long Id, string ChecksumMd5, double DrainLengthS, double SkippableS, double BaseStars);

    private sealed record BuildRow(long Id, bool Blocked);

    private sealed record BestScoreRow(long Id, long TotalScore);

    // timestamptz arrives from Npgsql as UTC DateTime; DateTimeOffset ctor params break Dapper's
    // constructor matching at runtime.
    private sealed record TokenRow(long Id, long UserId, long BeatmapId, long BuildId, long? ScoreId, DateTime CreatedAt);

    // ---- request bodies ----

    private sealed class TokenRequest
    {
        [JsonProperty("setId")]
        public long SetId { get; set; }

        [JsonProperty("beatmapId")]
        public long BeatmapId { get; set; }
    }

    private sealed class SubmitRequest
    {
        [JsonProperty("token")]
        public long Token { get; set; }

        [JsonProperty("passed")]
        public bool Passed { get; set; }

        [JsonProperty("totalScore")]
        public long TotalScore { get; set; }

        [JsonProperty("maxCombo")]
        public int MaxCombo { get; set; }

        [JsonProperty("statistics")]
        public Dictionary<string, int>? Statistics { get; set; }

        [JsonProperty("maximumStatistics")]
        public Dictionary<string, int>? MaximumStatistics { get; set; }
    }
}
