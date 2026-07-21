using System.Globalization;
using Dapper;
using Newtonsoft.Json;
using Typebeat.Web.Auth;
using Typebeat.Web.Data;
using Typebeat.Web.Scoring;
using Typebeat.Web.Wire;

namespace Typebeat.Web.Endpoints;

/// <summary>
/// The three solo-score endpoints (osu's two-phase submission plus the beatmap leaderboard):
///  - POST /api/v2/beatmaps/{beatmapId}/solo/scores          → issue a score token (CreateSoloScoreRequest)
///  - PUT  /api/v2/beatmaps/{beatmapId}/solo/scores/{tokenId} → complete it (SubmitSoloScoreRequest)
///  - GET  /api/v2/beatmaps/{beatmapId}/scores               → leaderboard (GetScoresRequest)
///
/// Wire ground truth: CreateSoloScoreRequest / SubmitSoloScoreRequest / SubmitScoreRequest,
/// SoloScoreInfo, MultiplayerScore, APIScoresCollection. Error strings the client exact-matches
/// ("invalid token", "expired token", "invalid or missing beatmap_hash", "outdated client") are
/// emitted verbatim; the client reads the "error" field of any non-2xx body (APIRequest.cs:214-224),
/// so the status code only needs to be non-success. Tamper-shaped input never yields a 5xx.
/// </summary>
public static class ScoreEndpoints
{
    private const int status_unprocessable = StatusCodes.Status422UnprocessableEntity;

    /// <summary>Mod acronyms that make a score unranked (mirror the client's Mod.Ranked=false). Only
    /// Mashing (Relax, "RX") today; every other type!beat mod (DT/HT/NC/NF/SD/FL) is ranked. Add any
    /// future unranked mod's acronym here.</summary>
    private static readonly HashSet<string> unranked_mod_acronyms = new(StringComparer.OrdinalIgnoreCase) { "RX" };

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v2/beatmaps/{beatmapId:long}/solo/scores", CreateToken).RequireBearer();
        app.MapPut("/api/v2/beatmaps/{beatmapId:long}/solo/scores/{tokenId:long}", SubmitScore).RequireBearer();
        app.MapGet("/api/v2/beatmaps/{beatmapId:long}/scores", Leaderboard).RequireBearer();
    }

    // ---------------------------------------------------------------------------------------------
    // POST — issue a score token.
    // Form fields (CreateSoloScoreRequest.cs:30-32): version_hash, beatmap_hash, ruleset_id.
    // Response is APIScoreToken { "id": <long> }.
    // ---------------------------------------------------------------------------------------------
    private static async Task<IResult> CreateToken(long beatmapId, HttpContext ctx, Db db)
    {
        var user = ctx.AuthedUser();

        var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
        string versionHash = form["version_hash"].ToString();
        string beatmapHash = form["beatmap_hash"].ToString();

        // typebeat is the only ruleset (legacy id 0); the client always submits 0.
        if (!int.TryParse(form["ruleset_id"].ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int rulesetId)
            || rulesetId != 0)
            return WireJson.Error(status_unprocessable, "invalid ruleset");

        await using var conn = await db.OpenAsync(ctx.RequestAborted);

        var beatmap = await conn.QuerySingleOrDefaultAsync<BeatmapRow>(
            """
            SELECT b.id, b.checksum_md5 AS checksumMd5, b.drain_length_s AS drainLengthS
            FROM beatmaps b
            JOIN beatmapsets bs ON bs.id = b.set_id
            WHERE b.id = @beatmapId AND bs.status IN ('pending', 'unranked', 'ranked')
            """,
            new { beatmapId });

        // Beatmap must exist on a published set (hidden shells and removed/DMCA'd sets take no
        // scores) AND the submitted hash must equal the stored final-.osu MD5 (the beatmap_hash
        // identity contract, 001_init.sql:88-90).
        if (beatmap is null || beatmapHash.Length == 0
            || !string.Equals(beatmap.ChecksumMd5, beatmapHash, StringComparison.OrdinalIgnoreCase))
            return WireJson.Error(status_unprocessable, "invalid or missing beatmap_hash");

        // Record-don't-reject: register the build on sight, then look it up. A blocked build is the
        // retroactive-invalidation lever (001_init.sql:49-51) and fails token creation.
        await conn.ExecuteAsync(
            "INSERT INTO builds (version_hash) VALUES (@versionHash) ON CONFLICT (version_hash) DO NOTHING",
            new { versionHash });

        var build = await conn.QuerySingleAsync<BuildRow>(
            "SELECT id, blocked FROM builds WHERE version_hash = @versionHash",
            new { versionHash });

        if (build.Blocked)
            return WireJson.Error(status_unprocessable, "outdated client");

        long tokenId = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO score_tokens (user_id, beatmap_id, ruleset_id, beatmap_hash, build_id)
            VALUES (@userId, @beatmapId, @rulesetId, @beatmapHash, @buildId)
            RETURNING id
            """,
            new { userId = user.Id, beatmapId, rulesetId, beatmapHash, buildId = build.Id });

        // APIScoreToken { "id": <long> } (APIScoreToken.cs:10-11).
        return WireJson.Ok(new { id = tokenId });
    }

    // ---------------------------------------------------------------------------------------------
    // PUT — complete a score token. Raw JSON SoloScoreInfo body (SubmitScoreRequest.cs:33).
    // Response is MultiplayerScore (SubmitScoreRequest : APIRequest<MultiplayerScore>).
    // ---------------------------------------------------------------------------------------------
    private static async Task<IResult> SubmitScore(long beatmapId, long tokenId, HttpContext ctx, Db db, ILoggerFactory loggerFactory)
    {
        var user = ctx.AuthedUser();
        var logger = loggerFactory.CreateLogger("ScoreSubmit");

        // Parse the SoloScoreInfo body with Newtonsoft (never System.Text.Json). Statistics are read
        // as raw string keys (HitResult EnumMember names) to sidestep the enum converter and to echo
        // them back byte-for-byte.
        SoloScoreSubmission? submission;
        try
        {
            using var reader = new StreamReader(ctx.Request.Body);
            string body = await reader.ReadToEndAsync(ctx.RequestAborted);
            submission = JsonConvert.DeserializeObject<SoloScoreSubmission>(body);
        }
        catch (JsonException)
        {
            submission = null;
        }

        if (submission is null)
            return WireJson.Error(status_unprocessable, "invalid request body");

        await using var conn = await db.OpenAsync(ctx.RequestAborted);
        await using var tx = await conn.BeginTransactionAsync(ctx.RequestAborted);

        // Lock the token for the duration of completion so a concurrent PUT cannot double-submit it.
        var token = await conn.QuerySingleOrDefaultAsync<TokenRow>(
            """
            SELECT id, user_id AS userId, beatmap_id AS beatmapId, build_id AS buildId,
                   score_id AS scoreId, created_at AS createdAt
            FROM score_tokens
            WHERE id = @tokenId
            FOR UPDATE
            """,
            new { tokenId }, tx);

        // Missing, not owned by the caller, for a different beatmap, or already used → "invalid token".
        if (token is null || token.UserId != user.Id || token.BeatmapId != beatmapId || token.ScoreId is not null)
            return WireJson.Error(status_unprocessable, "invalid token");

        var beatmap = await conn.QuerySingleOrDefaultAsync<BeatmapRow>(
            "SELECT id, checksum_md5 AS checksumMd5, drain_length_s AS drainLengthS FROM beatmaps WHERE id = @beatmapId",
            new { beatmapId }, tx);

        if (beatmap is null)
            return WireJson.Error(status_unprocessable, "invalid token");

        // A score ranks only on a reviewer-approved set. Plays on pending maps are accepted
        // and stored (they show in the player's own history) but never reach a leaderboard —
        // and the status is re-read here, not trusted from token time, so a set removed or
        // un-ranked mid-play resolves against its current state.
        bool setRanked = await conn.ExecuteScalarAsync<bool>(
            """
            SELECT bs.status = 'ranked'
            FROM beatmaps b JOIN beatmapsets bs ON bs.id = b.set_id
            WHERE b.id = @beatmapId
            """,
            new { beatmapId }, tx);

        var statistics = submission.Statistics ?? new Dictionary<string, int>();
        var maximumStatistics = submission.MaximumStatistics ?? new Dictionary<string, int>();

        // Recompute accuracy exactly and bound the total score against a provable ceiling.
        var recomputed = ScoringContract.Recompute(statistics, maximumStatistics, submission.MaxCombo);
        bool withinBounds = ScoringContract.TotalScoreWithinBounds(submission.TotalScore, recomputed);

        // Minimum-play-time gate: at least 90% of the map's drain length must have elapsed since the
        // token was created (created_at is the server wall-clock anchor, 001_init.sql:126). Too fast
        // is not an error — osu accepts and flags; we take the safe route and store it unranked.
        double elapsedSeconds = (DateTimeOffset.UtcNow - token.CreatedAt).TotalSeconds;
        bool playTimeOk = elapsedSeconds >= 0.9 * beatmap.DrainLengthS;

        // The build may have been blocked after the token was issued.
        bool buildBlocked = await conn.ExecuteScalarAsync<bool>(
            "SELECT blocked FROM builds WHERE id = @buildId", new { buildId = token.BuildId }, tx);

        bool passed = submission.Passed;

        // An honestly passed play judges every cell; a "passed" submission with judged < map total
        // is untrustworthy (its judged-only accuracy could overstate the final value).
        bool fullyJudged = recomputed.AccuracyProgress >= 1;

        // Scores set with an unranked mod (currently only Mashing/Relax, "RX") never rank — the
        // client honestly reports its mods, so an unranked acronym in the submission disqualifies
        // the score from leaderboards even though everything else checks out.
        bool modsRanked = submission.Mods is null
                          || submission.Mods.All(m => m.Acronym is null || !unranked_mod_acronyms.Contains(m.Acronym));

        // Ranked only if the SET is ranked and it passed with every cell judged, every hard
        // invariant held, the total is within its ceiling, the play took long enough, no unranked
        // mod was used, and the build is not blocked. Anything else is stored unranked so it never
        // reaches a leaderboard, but the submission still "succeeds" from the client's view.
        bool ranked = setRanked && passed && fullyJudged && recomputed.StatisticsValid && withinBounds && playTimeOk && modsRanked && !buildBlocked;

        if (!modsRanked)
            logger.LogInformation("Score token {TokenId}: unranked mod used — storing unranked.", tokenId);

        // What the player saw: final whole-map accuracy for completed plays, the running
        // (judged-only) accuracy at the moment of failure otherwise. Equal when fully judged.
        double storedAccuracy = passed && fullyJudged ? recomputed.Accuracy : recomputed.JudgedAccuracy;

        if (!playTimeOk)
            logger.LogInformation("Score token {TokenId}: elapsed {Elapsed:0.0}s < 90% of drain {Drain:0.0}s — storing unranked.",
                tokenId, elapsedSeconds, beatmap.DrainLengthS);
        if (!recomputed.StatisticsValid || !withinBounds)
            logger.LogInformation("Score token {TokenId}: out of bounds (statisticsValid={Valid}, totalWithinBounds={Within}) — storing unranked.",
                tokenId, recomputed.StatisticsValid, withinBounds);

        // Store defensively-clamped values so a tampered total/combo can never pollute stats or boards.
        long storedTotal = withinBounds ? submission.TotalScore : recomputed.TotalScoreCeiling;
        int storedMaxCombo = Math.Clamp(submission.MaxCombo, 0, recomputed.TheoreticalMaxCombo);
        string rank = passed ? recomputed.Rank : "F"; // ScoreProcessor.FailScore sets rank F (ScoreProcessor.cs:504-513)
        var endedAt = DateTimeOffset.UtcNow;

        // Persist the acronyms the client reported so the site can show which mods a play used.
        // Normalized to [{acronym}] (the shape the website already parses) — only the acronym is
        // kept, never the raw client settings blob, so a tampered payload can't bloat the row.
        string modsJson = submission.Mods is { Count: > 0 }
            ? JsonConvert.SerializeObject(
                submission.Mods
                          .Where(m => !string.IsNullOrWhiteSpace(m.Acronym))
                          .Select(m => new { acronym = m.Acronym!.Trim().ToUpperInvariant() }))
            : "[]";

        long scoreId = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO scores
                (user_id, beatmap_id, ruleset_id, total_score, accuracy, completion, max_combo, rank, passed,
                 ranked, preserve, mods, statistics, maximum_statistics, build_id, started_at, ended_at)
            VALUES
                (@userId, @beatmapId, 0, @totalScore, @accuracy, @completion, @maxCombo, @rank, @passed,
                 @ranked, @preserve, CAST(@mods AS jsonb), CAST(@statistics AS jsonb), CAST(@maximumStatistics AS jsonb),
                 @buildId, @startedAt, @endedAt)
            RETURNING id
            """,
            new
            {
                userId = user.Id,
                beatmapId,
                totalScore = storedTotal,
                accuracy = storedAccuracy,
                completion = recomputed.Completion, // whole-map % typed — what the rank is graded on
                maxCombo = storedMaxCombo,
                rank,
                passed,
                ranked,
                preserve = passed, // osu: non-passed rows are prunable (001_init.sql:154-155)
                mods = modsJson,
                statistics = JsonConvert.SerializeObject(statistics),
                maximumStatistics = JsonConvert.SerializeObject(maximumStatistics),
                buildId = token.BuildId,
                startedAt = token.CreatedAt,
                endedAt
            }, tx);

        // Denormalized play counters. The website reads beatmapsets.play_count (like download_count
        // / favourite_count); osu clients read beatmaps.playcount. Count one per submitted play —
        // passed or failed, ranked or not — since every submission inserts a scores row above and
        // is a genuine attempt. The parent set is bumped through the beatmap's set_id. (This is the
        // only place either counter is touched; user_stats.play_count below is the player's own
        // aggregate, a separate thing.)
        await conn.ExecuteAsync(
            """
            UPDATE beatmaps SET play_count = play_count + 1 WHERE id = @beatmapId;
            UPDATE beatmapsets SET play_count = play_count + 1
            WHERE id = (SELECT set_id FROM beatmaps WHERE id = @beatmapId)
            """,
            new { beatmapId }, tx);

        // Upsert user_stats — but ONLY for submissions that held up to the tamper checks. The same
        // invariants that withhold ranking must withhold aggregate accumulation, or a rejected
        // submission could still inflate profile hit counts / totals with client-controlled data.
        // (Failed-but-honest plays pass these checks and do accrue stats, like osu.)
        if (recomputed.StatisticsValid && withinBounds)
        {
            // Ensure the row exists, lock it, then increment — read-modify-write of hit_counts
            // under the row lock keeps concurrent submissions by the same user consistent.
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
                    // A failed play didn't run the whole map — credit actual elapsed time, capped
                    // at the drain length (elapsed can exceed it via pauses).
                    playTime = (long)Math.Round(Math.Min(elapsedSeconds, beatmap.DrainLengthS), MidpointRounding.AwayFromZero),
                    hitCounts = MergeHitCounts(existingHitCounts, statistics)
                }, tx);
        }

        await conn.ExecuteAsync(
            "UPDATE score_tokens SET score_id = @scoreId WHERE id = @tokenId",
            new { scoreId, tokenId }, tx);

        // Position = rank of the caller's best ranked+passed score among distinct users. Null when
        // this submission is unranked and the caller has no other ranked score on the map.
        int? position = ranked ? await ComputeUserPosition(conn, tx, beatmapId, user.Id) : null;

        await tx.CommitAsync(ctx.RequestAborted);

        var response = new MultiplayerScoreWire
        {
            Id = scoreId,
            User = BuildUser(ctx, user.Id, user.Username, user.CountryCode, user.AvatarKey),
            Rank = rank,
            TotalScore = storedTotal,
            Accuracy = storedAccuracy,
            MaxCombo = storedMaxCombo,
            Mods = Array.Empty<object>(),
            Statistics = statistics,
            MaximumStatistics = maximumStatistics,
            Passed = passed,
            EndedAt = endedAt,
            Position = position,
            Pp = null,
            HasReplay = false,
            Ranked = ranked,
            RulesetId = 0,
            BeatmapId = beatmapId,
        };

        return WireJson.Ok(response);
    }

    // ---------------------------------------------------------------------------------------------
    // GET — beatmap leaderboard (global, best-per-user). GetScoresRequest.cs:46-59.
    // type / mode / mods[] are ignored (always global); limit is capped at 50.
    // ---------------------------------------------------------------------------------------------
    private static async Task<IResult> Leaderboard(long beatmapId, HttpContext ctx, Db db)
    {
        var user = ctx.AuthedUser();

        int limit = 50;
        if (int.TryParse(ctx.Request.Query["limit"], NumberStyles.Integer, CultureInfo.InvariantCulture, out int requested))
            limit = Math.Clamp(requested, 1, 50);

        await using var conn = await db.OpenAsync(ctx.RequestAborted);

        // A board is served only while its set is currently ranked. Ranked-era scores keep their
        // scores.ranked flag, so without this a reviewer un-ranking a set would still leak its old
        // board through the API (the website already hides it). Re-reading status here makes the
        // Rank/Unrank lever authoritative over the leaderboard in both directions.
        bool setRanked = await conn.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS (
                SELECT 1 FROM beatmaps b JOIN beatmapsets bs ON bs.id = b.set_id
                WHERE b.id = @beatmapId AND bs.status = 'ranked')
            """,
            new { beatmapId });

        if (!setRanked)
            return WireJson.Ok(new ScoresCollectionWire { ScoreCount = 0, Scores = [], UserScore = null });

        // Best score per user (DISTINCT ON over ix_scores_leaderboard), ranked+passed only, then the
        // global ordering by total score. Failed/unranked scores never appear.
        var rows = (await conn.QueryAsync<LeaderboardRow>(
            """
            SELECT best.id                 AS scoreId,
                   best.user_id            AS userId,
                   best.total_score        AS totalScore,
                   best.accuracy           AS accuracy,
                   best.max_combo          AS maxCombo,
                   best.rank               AS rank,
                   best.ended_at           AS endedAt,
                   best.statistics         AS statisticsJson,
                   best.maximum_statistics AS maximumStatisticsJson,
                   best.username           AS username,
                   best.country_code       AS countryCode,
                   best.avatar_key         AS avatarKey
            FROM (
                SELECT DISTINCT ON (s.user_id)
                       s.id, s.user_id, s.total_score, s.accuracy, s.max_combo, s.rank, s.ended_at,
                       s.statistics::text AS statistics, s.maximum_statistics::text AS maximum_statistics,
                       u.username, u.country_code, u.avatar_key
                FROM scores s
                JOIN users u ON u.id = s.user_id
                WHERE s.beatmap_id = @beatmapId AND s.ranked AND s.passed
                ORDER BY s.user_id, s.total_score DESC, s.id ASC
            ) best
            ORDER BY best.total_score DESC, best.id ASC
            LIMIT @limit
            """,
            new { beatmapId, limit })).ToList();

        var scores = rows.Select(r => ToSoloScoreWire(ctx, r, beatmapId)).ToList();

        // Total distinct participants (osu-web's score_count reflects the full board, not the page).
        int scoreCount = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(DISTINCT user_id) FROM scores WHERE beatmap_id = @beatmapId AND ranked AND passed",
            new { beatmapId });

        // The caller's own best score + its global position, if they have one.
        var callerBest = await conn.QuerySingleOrDefaultAsync<LeaderboardRow>(
            """
            SELECT s.id                 AS scoreId,
                   s.user_id            AS userId,
                   s.total_score        AS totalScore,
                   s.accuracy           AS accuracy,
                   s.max_combo          AS maxCombo,
                   s.rank               AS rank,
                   s.ended_at           AS endedAt,
                   s.statistics::text   AS statisticsJson,
                   s.maximum_statistics::text AS maximumStatisticsJson,
                   u.username           AS username,
                   u.country_code       AS countryCode,
                   u.avatar_key         AS avatarKey
            FROM scores s
            JOIN users u ON u.id = s.user_id
            WHERE s.beatmap_id = @beatmapId AND s.user_id = @userId AND s.ranked AND s.passed
            ORDER BY s.total_score DESC, s.id ASC
            LIMIT 1
            """,
            new { beatmapId, userId = user.Id });

        ScoreWithPositionWire? userScore = null;
        if (callerBest is not null)
        {
            int position = await PositionOf(conn, null, beatmapId, callerBest.TotalScore, callerBest.ScoreId);
            userScore = new ScoreWithPositionWire { Position = position, Score = ToSoloScoreWire(ctx, callerBest, beatmapId) };
        }

        return WireJson.Ok(new ScoresCollectionWire
        {
            ScoreCount = scoreCount,
            Scores = scores,
            UserScore = userScore,
        });
    }

    // ---- helpers ----

    /// <summary>Position of the caller's best ranked+passed score, or null if they have none.</summary>
    private static async Task<int?> ComputeUserPosition(System.Data.Common.DbConnection conn, System.Data.Common.DbTransaction? tx, long beatmapId, long userId)
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

        return await PositionOf(conn, tx, beatmapId, best.TotalScore, best.Id);
    }

    /// <summary>1-based rank of a (total_score, scoreId) among distinct users' best scores.</summary>
    private static async Task<int> PositionOf(System.Data.Common.DbConnection conn, System.Data.Common.DbTransaction? tx, long beatmapId, long totalScore, long scoreId)
    {
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
            new { beatmapId, totalScore, scoreId }, tx);
    }

    private static SoloScoreWire ToSoloScoreWire(HttpContext ctx, LeaderboardRow r, long beatmapId) => new()
    {
        Id = r.ScoreId,
        BeatmapId = beatmapId,
        RulesetId = 0,
        Passed = true, // leaderboard rows are passed by construction
        TotalScore = r.TotalScore,
        Accuracy = r.Accuracy,
        UserId = r.UserId,
        MaxCombo = r.MaxCombo,
        Rank = r.Rank,
        EndedAt = r.EndedAt,
        Mods = Array.Empty<object>(),
        Statistics = ParseCounts(r.StatisticsJson),
        MaximumStatistics = ParseCounts(r.MaximumStatisticsJson),
        Ranked = true,
        User = BuildUser(ctx, r.UserId, r.Username, r.CountryCode, r.AvatarKey),
    };

    /// <summary>
    /// The user object riding on leaderboard rows and the submit response. avatar_url is built by
    /// the shared <see cref="UserWire.AvatarUrl(string, string, string?)"/> — the stored avatar_key
    /// when the user has one, the same self-hosted default the me-payload uses otherwise (never
    /// null, or the client falls back to a ppy CDN URL).
    /// </summary>
    private static ScoreUserWire BuildUser(HttpContext ctx, long id, string username, string countryCode, string? avatarKey) => new()
    {
        Id = id,
        Username = username,
        CountryCode = countryCode,
        AvatarUrl = UserWire.AvatarUrl(ctx.Request.Scheme, ctx.Request.Host.Value ?? string.Empty, avatarKey),
    };

    private static Dictionary<string, int> ParseCounts(string? json)
        => JsonConvert.DeserializeObject<Dictionary<string, int>>(json ?? "{}") ?? new Dictionary<string, int>();

    /// <summary>Sums per-key hit counts of <paramref name="add"/> into the stored hit_counts JSON.</summary>
    private static string MergeHitCounts(string existingJson, IReadOnlyDictionary<string, int> add)
    {
        var merged = JsonConvert.DeserializeObject<Dictionary<string, int>>(existingJson) ?? new Dictionary<string, int>();

        foreach (var (key, count) in add)
        {
            if (count <= 0)
                continue; // ignore zero/negative (tamper) counts

            merged[key] = merged.GetValueOrDefault(key) + count;
        }

        return JsonConvert.SerializeObject(merged);
    }

    // ---- Dapper row shapes ----

    private sealed record BeatmapRow(long Id, string ChecksumMd5, double DrainLengthS);

    private sealed record BestScoreRow(long Id, long TotalScore);

    private sealed record BuildRow(long Id, bool Blocked);

    // timestamptz arrives from Npgsql as UTC DateTime — DateTimeOffset ctor params break
    // Dapper's constructor matching at runtime ("no matching signature").
    private sealed record TokenRow(long Id, long UserId, long BeatmapId, long BuildId, long? ScoreId, DateTime CreatedAt);

    private sealed record LeaderboardRow(
        long ScoreId,
        long UserId,
        long TotalScore,
        double Accuracy,
        int MaxCombo,
        string Rank,
        DateTime EndedAt,
        string? StatisticsJson,
        string? MaximumStatisticsJson,
        string Username,
        string CountryCode,
        string? AvatarKey);

    // ---- request body (subset of SoloScoreInfo the client submits, SoloScoreInfo.ForSubmission) ----

    private sealed class SoloScoreSubmission
    {
        [JsonProperty("passed")]
        public bool Passed { get; set; }

        [JsonProperty("total_score")]
        public long TotalScore { get; set; }

        [JsonProperty("accuracy")]
        public double Accuracy { get; set; } // overridden by the server-side recompute

        [JsonProperty("max_combo")]
        public int MaxCombo { get; set; }

        [JsonProperty("ruleset_id")]
        public int RulesetId { get; set; }

        [JsonProperty("rank")]
        public string? Rank { get; set; }

        [JsonProperty("statistics")]
        public Dictionary<string, int>? Statistics { get; set; }

        [JsonProperty("maximum_statistics")]
        public Dictionary<string, int>? MaximumStatistics { get; set; }

        [JsonProperty("mods")]
        public List<SubmittedMod>? Mods { get; set; }
    }

    /// <summary>One entry of the client's submitted mod list (osu APIMod shape); we only read the acronym.</summary>
    private sealed class SubmittedMod
    {
        [JsonProperty("acronym")]
        public string? Acronym { get; set; }
    }
}
