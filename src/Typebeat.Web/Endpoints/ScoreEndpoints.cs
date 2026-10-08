using System.Globalization;
using Dapper;
using Newtonsoft.Json;
using Microsoft.Extensions.Caching.Memory;
using Typebeat.Web.Auth;
using Typebeat.Web.Caching;
using Typebeat.Web.Data;
using Typebeat.Web.Scoring;
using Typebeat.Web.Storage;
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

    // THE ALWAYS-UNRANKED LIST LIVES IN Scoring/UnrankedMods.cs, exactly once (backlog 270). It
    // used to be declared here and again in GateRefund, whose copy claimed to mirror this one and
    // was three acronyms behind it, so a refund pass would re-rank a play this path had refused.

    /// <summary>
    /// Whether a single submitted mod is ranked at its submitted configuration; the server-side
    /// mirror of the client's per-mod <c>Ranked</c>. Unknown/null acronyms are treated as ranked
    /// (forward-compatible); the caller ANDs this across all mods.
    ///
    /// <para>
    /// The rate mods (DT/NC/HT/DC) are ranked at EVERY speed, not just their default: the client pays
    /// them on a continuous curve (<see cref="RateMultiplier"/>), so an odd rate is priced, not
    /// banned. Only the always-unranked set above disqualifies a play.
    /// </para>
    /// </summary>
    private static bool ModConfigRanked(SubmittedMod mod)
    {
        if (string.IsNullOrWhiteSpace(mod.Acronym))
            return true;

        return !UnrankedMods.IsAlwaysUnranked(mod.Acronym);
    }

    /// <summary>
    /// The mods as they will be STORED: uppercased acronym plus, for a rate mod, the submitted
    /// <c>speed_change</c> snapped to 0.01 and clamped into that mod's slider range. Every other
    /// setting is dropped, so a tampered payload cannot bloat the row, and an unparseable or absent
    /// rate simply yields no setting (read back as the client default).
    /// </summary>
    private static List<NormalizedMod> NormalizeMods(List<SubmittedMod>? mods)
    {
        var normalized = new List<NormalizedMod>();

        if (mods == null)
            return normalized;

        foreach (var mod in mods)
        {
            if (string.IsNullOrWhiteSpace(mod.Acronym))
                continue;

            string acronym = mod.Acronym.Trim().ToUpperInvariant();

            normalized.Add(new NormalizedMod(acronym, RateMods.ReadSpeedChange(acronym, mod.Settings)));
        }

        return normalized;
    }

    /// <summary>The stored mods jsonb: <c>[{"acronym":"DT","settings":{"speed_change":1.5}}]</c>.</summary>
    private static string SerializeMods(List<NormalizedMod> mods)
        => mods.Count == 0
            ? "[]"
            : JsonConvert.SerializeObject(mods.Select(m => m.SpeedChange is double speed
                ? (object)new { acronym = m.Acronym, settings = new { speed_change = speed } }
                : new { acronym = m.Acronym }));

    /// <summary>A submitted mod reduced to what the server keeps: acronym + rate (rate mods only).</summary>
    private sealed record NormalizedMod(string Acronym, double? SpeedChange);

    public static void Map(IEndpointRouteBuilder app)
    {
        // Rate limits (backlog 366, Auth/RateLimits.cs): speed bumps keyed by the bearer token. A
        // 429 here costs the player the score in both clients, so these budgets are deliberately
        // far above anything a person can play.
        app.MapPost("/api/v2/beatmaps/{beatmapId:long}/solo/scores", CreateToken).RequireBearer().RequireRateLimiting(RateLimits.ScoreToken);
        app.MapPut("/api/v2/beatmaps/{beatmapId:long}/solo/scores/{tokenId:long}", SubmitScore).RequireBearer().RequireRateLimiting(RateLimits.ScoreSubmit);
        // The board is a READ and is public on the set page, so a guest may fetch it too (backlog
        // 406); the bearer is optional and only adds the caller's own user_score.
        app.MapGet("/api/v2/beatmaps/{beatmapId:long}/scores", Leaderboard).RequireRateLimiting(RateLimits.Leaderboard);
    }

    // ---------------------------------------------------------------------------------------------
    // POST: issue a score token.
    // Form fields (CreateSoloScoreRequest.cs:30-32): version_hash, beatmap_hash, ruleset_id.
    // Response is APIScoreToken { "id": <long> }.
    // ---------------------------------------------------------------------------------------------
    private static async Task<IResult> CreateToken(long beatmapId, HttpContext ctx, Db db, DiskGuard disk)
    {
        var user = ctx.AuthedUser();

        // Disk guard (backlog 365): only at the CRITICAL level (Postgres' own headroom), and only the
        // token, so the player is told before playing (the game posts "Score will not be
        // submitted") rather than after. Submitting against a token already issued is never refused.
        if (disk.Current.TokensRefused)
            return WireJson.Error(StatusCodes.Status503ServiceUnavailable, DiskGuard.TokenRefusal);

        var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
        string versionHash = form["version_hash"].ToString();
        string beatmapHash = form["beatmap_hash"].ToString();

        // typebeat is the only ruleset (legacy id 0); the client always submits 0.
        if (!int.TryParse(form["ruleset_id"].ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int rulesetId)
            || rulesetId != 0)
            return WireJson.Error(status_unprocessable, "invalid ruleset");

        // A local-only mod (Polyglot, backlog 332) never asks for a token: the stock client keeps
        // the play on the device and sends no mods here at all. A modified client that names one
        // anyway is refused before anything is written, so not even a token row exists for it.
        if (LocalOnlyMods.AnyLocalOnly(form["mods"].Concat(form["mods[]"])))
            return WireJson.Error(status_unprocessable, LocalOnlyMods.REFUSAL);

        await using var conn = await db.OpenAsync(ctx.RequestAborted);

        var beatmap = await conn.QuerySingleOrDefaultAsync<BeatmapRow>(
            """
            SELECT b.id, b.checksum_md5 AS checksumMd5, b.drain_length_s AS drainLengthS, b.skippable_s AS skippableS,
                   b.difficulty_rating AS baseStars, b.sr_dt AS srDt, b.sr_ht AS srHt,
                   b.sr_literate AS srLiterate, b.sr_literate_dt AS srLiterateDt, b.sr_literate_ht AS srLiterateHt,
                   b.ratings::text AS ratings, b.played_duration_s AS playedDurationS
            FROM beatmaps b
            JOIN beatmapsets bs ON bs.id = b.set_id
            WHERE b.id = @beatmapId AND bs.status IN ('pending', 'unranked', 'ranked', 'loved')
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
    // PUT: complete a score token. Raw JSON SoloScoreInfo body (SubmitScoreRequest.cs:33).
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

        // A local-only mod (Polyglot, backlog 332) is REFUSED, not stored unranked the way an
        // unranked mod is: the stock client never submits such a play, so one arriving here came
        // from a modified client, and it is turned away before the token is even read, so no
        // score row, no pp, no rating cell and no board ever sees it.
        if (submission.Mods is { } submittedMods && LocalOnlyMods.StackIsLocalOnly(submittedMods.Select(m => m.Acronym)))
        {
            logger.LogInformation("Score token {TokenId}: local-only mod submitted, refusing.", tokenId);
            return WireJson.Error(status_unprocessable, LocalOnlyMods.REFUSAL);
        }

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
            """
            SELECT id, checksum_md5 AS checksumMd5, drain_length_s AS drainLengthS, skippable_s AS skippableS,
                   difficulty_rating AS baseStars, sr_dt AS srDt, sr_ht AS srHt,
                   sr_literate AS srLiterate, sr_literate_dt AS srLiterateDt, sr_literate_ht AS srLiterateHt,
                   ratings::text AS ratings, played_duration_s AS playedDurationS
            FROM beatmaps WHERE id = @beatmapId
            """,
            new { beatmapId }, tx);

        if (beatmap is null)
            return WireJson.Error(status_unprocessable, "invalid token");

        // A score ranks only on a reviewer-approved set (ranked or loved). Plays on pending maps
        // are accepted and stored (they show in the player's own history) but never reach the
        // ranked board, and the status is re-read here, not trusted from token time, so a set
        // removed or un-ranked mid-play resolves against its current state.
        string? setStatus = await conn.ExecuteScalarAsync<string?>(
            """
            SELECT bs.status
            FROM beatmaps b JOIN beatmapsets bs ON bs.id = b.set_id
            WHERE b.id = @beatmapId
            """,
            new { beatmapId }, tx);

        // A play is ranked (stored on the ranked board) on a 'ranked' or 'loved' set; only a 'ranked'
        // set's plays are priced. Loved is osu!'s "leaderboard, no pp" status: the row is stored with
        // pp 0 and the current pp_version, which every pp read already renders as no pp.
        bool setHasRankedBoard = setStatus is "ranked" or "loved";
        bool setAwardsPp = setStatus == "ranked";

        var statistics = submission.Statistics ?? new Dictionary<string, int>();
        var maximumStatistics = submission.MaximumStatistics ?? new Dictionary<string, int>();

        // The mods as they will be stored and priced: acronym + clamped rate, everything else dropped.
        var mods = NormalizeMods(submission.Mods);

        // Recompute accuracy exactly and bound the score against a provable ceiling. The ceiling is
        // the no-mod maximum, so bound the UNMODDED score against it; the mod score-multiplier then
        // legitimately scales the ranked TotalScore up (DT/NC/FL) or down (HT).
        //
        // The multiplied total is bounded by the EXACT multiplier the submitted stack earns
        // (ModMultiplier, mirroring the client's TypeBeatScoreMultiplierCalculator), not by the old
        // flat 2x allowance: the client sends round(base × multiplier), so the ceiling is that value
        // plus one unit of rounding slack. Rates are priced from the CLAMPED speed_change, so a
        // tampered "speed_change": 40 buys the 2.00x price and no more.
        var recomputed = ScoringContract.Recompute(statistics, maximumStatistics, submission.MaxCombo);
        double modMultiplier = ModMultiplier.MaxForStack(mods.Select(m => ((string?)m.Acronym, m.SpeedChange)));
        long modCeiling = ModMultiplier.TotalScoreCeiling(submission.TotalScoreWithoutMods, modMultiplier);

        bool withinBounds = ScoringContract.TotalScoreWithinBounds(submission.TotalScoreWithoutMods, recomputed)
                            && submission.TotalScore >= 0
                            // Tie the multiplied total to the (ceiling-bounded) base score, so a tampered
                            // client can't claim a huge total against a zero base. HalfTime legitimately
                            // lands below the base; the cap is only an upper bound.
                            && submission.TotalScore <= modCeiling;

        // Minimum-play-time gate: at least 90% of the map's SKIP-ADJUSTED drain length, converted
        // from map time into real time by the play's RATE, must have elapsed since the token was
        // created (created_at is the server wall-clock anchor, 001_init.sql:126). The allowance is
        // what the in-game skip button may legally remove from this map; the rate is the submitted
        // speed_change of DT/NC/HT/DC, read from the very same normalized stack that prices the score
        // just above, so the gate and the multiplier can never disagree about how fast the play was.
        // See PlayTimeGate. Too fast is not an error; osu accepts and flags; we take the safe route
        // and store it unranked.
        double elapsedSeconds = (DateTimeOffset.UtcNow - token.CreatedAt).TotalSeconds;
        double rate = RateMods.EffectiveRate(mods.Select(m => ((string?)m.Acronym, m.SpeedChange)));
        bool playTimeOk = PlayTimeGate.Passes(elapsedSeconds, beatmap.DrainLengthS, beatmap.SkippableS, rate);

        // The build may have been blocked after the token was issued.
        bool buildBlocked = await conn.ExecuteScalarAsync<bool>(
            "SELECT blocked FROM builds WHERE id = @buildId", new { buildId = token.BuildId }, tx);

        bool passed = submission.Passed;

        // An honestly passed play judges every cell; a "passed" submission with judged < map total
        // is untrustworthy (its judged-only accuracy could overstate the final value).
        bool fullyJudged = recomputed.AccuracyProgress >= 1;

        // Mirror the client's per-mod Ranked flag: a score is ranked only if every mod is ranked at
        // its submitted configuration. Always-unranked mods (Mashing/Relax "RX"; the time-ramp Wind
        // Up/Down "WU"/"WD") disqualify it. The rate mods (DT/NC/HT/DC) no longer do at ANY speed: the
        // rate is paid on a continuous curve instead of being banned off the default.
        bool modsRanked = submission.Mods is null || submission.Mods.All(ModConfigRanked);

        // Ranked only if the SET has a ranked board (ranked or loved) and it passed with every cell
        // judged, every hard invariant held, the total is within its ceiling, the play took long
        // enough, no unranked mod was used, and the build is not blocked. Anything else is stored unranked so it never
        // reaches a leaderboard, but the submission still "succeeds" from the client's view.
        bool ranked = setHasRankedBoard && passed && fullyJudged && recomputed.StatisticsValid && withinBounds && playTimeOk && modsRanked && !buildBlocked;

        if (!modsRanked)
            logger.LogInformation("Score token {TokenId}: unranked mod used, storing unranked.", tokenId);

        // What the player saw: final whole-map accuracy for completed plays, the running
        // (judged-only) accuracy at the moment of failure otherwise. Equal when fully judged.
        double storedAccuracy = passed && fullyJudged ? recomputed.Accuracy : recomputed.JudgedAccuracy;

        if (!playTimeOk)
            logger.LogInformation("Score token {TokenId}: elapsed {Elapsed:0.0}s < required {Required:0.0}s (drain {Drain:0.0}s, skippable {Skippable:0.0}s, rate {Rate:0.00}x), storing unranked.",
                tokenId, elapsedSeconds, PlayTimeGate.RequiredSeconds(beatmap.DrainLengthS, beatmap.SkippableS, rate),
                beatmap.DrainLengthS, beatmap.SkippableS, rate);
        if (!recomputed.StatisticsValid || !withinBounds)
            logger.LogInformation("Score token {TokenId}: out of bounds (statisticsValid={Valid}, totalWithinBounds={Within}), storing unranked.",
                tokenId, recomputed.StatisticsValid, withinBounds);

        // Store defensively-clamped values so a tampered total/combo can never pollute stats or boards.
        long storedTotal = withinBounds ? submission.TotalScore : recomputed.TotalScoreCeiling;
        int storedMaxCombo = Math.Clamp(submission.MaxCombo, 0, recomputed.TheoreticalMaxCombo);
        string rank = passed ? recomputed.Rank : "F"; // ScoreProcessor.FailScore sets rank F (ScoreProcessor.cs:504-513)
        var endedAt = DateTimeOffset.UtcNow;

        // Persist what the client reported so the site (and the game's own leaderboard strip) can
        // show which mods a play used, and at what rate. Normalized to the osu APIMod shape
        // [{acronym, settings}] with ONLY speed_change kept, and only on the rate mods, so a
        // tampered payload can't bloat the row while the score-affecting rate still survives.
        string modsJson = SerializeMods(mods);

        // Performance points for this play (docs/pp.md, Scoring/PerformancePoints.cs). Computed here
        // rather than on read because it is a pure function of values that never change again once
        // written, except the map's star ratings; when THOSE move, PaceBackfill/PackageIngest stamp
        // this row's pp_version back to 0 and PpBackfill recomputes it at the next boot.
        //
        // "Settled" is the whole reason the version is not stamped unconditionally: a base-rate
        // DT/HT play on a map whose sr_dt/sr_ht is not stored yet CANNOT be priced, so it is written
        // at version 0 (pp 0 for now) and picked up by the backfill once the column is filled,
        // instead of freezing at zero. Eligibility itself is inherited from the ranked flag computed
        // above, so fails, unranked mods and out-of-bounds submissions earn nothing for free, and is
        // narrowed to a 'ranked' set, so a loved play (ranked, on the board) is refused a price too:
        // it settles at 0 now and is priced only if its set is ranked later (the Set page's carry).
        // Reading the mods back out of the json just serialized (rather than off the normalized list)
        // is deliberate: it is byte-for-byte the input PpBackfill will later see for this row, so
        // the two paths cannot disagree about what a play was played with.
        var (pp, ppSettled) = PerformancePoints.ForScore(
            ranked && setAwardsPp,
            ScoreMods.Parse(modsJson),
            PerformancePoints.CountNotes(statistics),
            storedAccuracy,
            storedMaxCombo,
            // THE RATING MATRIX (034_ratings_matrix.sql): since PerformancePoints v22 a price needs
            // the map's DIFFICULT CHARACTERS as well as its stars, and since the difficulty rework
            // it needs the play's JUDGEMENT ARM too, so one lookup into this replaces the six
            // rating columns that used to be passed here. A map the sweep has not reached carries
            // no matrix at all and leaves the play unpriced and retried, exactly as an unfilled
            // sr_dt already did for a Double Time play.
            BeatmapRatings.Parse(beatmap.Ratings),
            // The short-map factor's input (044_played_duration.sql), the map's span at the BASE
            // rate (ForScore divides it by the play's rate). NULL is an unswept row, read as the
            // legacy no-cut price rather than the maximum cut.
            beatmap.PlayedDurationS ?? double.PositiveInfinity);

        long scoreId = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO scores
                (user_id, beatmap_id, ruleset_id, total_score, accuracy, completion, max_combo, rank, passed,
                 ranked, preserve, mods, statistics, maximum_statistics, build_id, started_at, ended_at,
                 pp, pp_version)
            VALUES
                (@userId, @beatmapId, 0, @totalScore, @accuracy, @completion, @maxCombo, @rank, @passed,
                 @ranked, @preserve, CAST(@mods AS jsonb), CAST(@statistics AS jsonb), CAST(@maximumStatistics AS jsonb),
                 @buildId, @startedAt, @endedAt, @pp, @ppVersion)
            RETURNING id
            """,
            new
            {
                // The column is NOT NULL, so a play the formula never ran for (unranked, or a
                // custom rate) stores 0. That stored 0 is NOT what goes on the wire; see the
                // response below.
                pp = pp ?? 0,
                ppVersion = ppSettled ? PerformancePoints.VERSION : 0,
                userId = user.Id,
                beatmapId,
                totalScore = storedTotal,
                accuracy = storedAccuracy,
                completion = recomputed.Completion, // whole-map % typed, what the rank is graded on
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
        // / favourite_count); osu clients read beatmaps.playcount. Count one per submitted play,
        // passed or failed, ranked or not, since every submission inserts a scores row above and
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

        // Upsert user_stats, but ONLY for submissions that held up to the tamper checks. The same
        // invariants that withhold ranking must withhold aggregate accumulation, or a rejected
        // submission could still inflate profile hit counts / totals with client-controlled data.
        // (Failed-but-honest plays pass these checks and do accrue stats, like osu.)
        if (recomputed.StatisticsValid && withinBounds)
        {
            // Ensure the row exists, lock it, then increment; read-modify-write of hit_counts
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
                    // A failed play didn't run the whole map; credit actual elapsed time, capped
                    // at the drain length (elapsed can exceed it via pauses).
                    playTime = (long)Math.Round(Math.Min(elapsedSeconds, beatmap.DrainLengthS), MidpointRounding.AwayFromZero),
                    hitCounts = MergeHitCounts(existingHitCounts, statistics)
                }, tx);

            // Play history (024_play_history.sql): the same play, credited to its UTC month for
            // the profile's Play History chart. Deliberately inside this block and nowhere else,
            // so the chart's total and the play_count printed above it move as one number.
            await PlayHistory.RecordPlayAsync(conn, tx, user.Id, endedAt, ctx.RequestAborted);
        }

        await conn.ExecuteAsync(
            "UPDATE score_tokens SET score_id = @scoreId WHERE id = @tokenId",
            new { scoreId, tokenId }, tx);

        // Position = rank of the caller's best ranked+passed score among distinct users. Null when
        // this submission is unranked and the caller has no other ranked score on the map.
        int? position = ranked ? await ComputeUserPosition(conn, tx, beatmapId, user.Id) : null;

        await tx.CommitAsync(ctx.RequestAborted);

        // Cached reads that show this play (backlog 366): its board memo, its set (play count) and
        // its player's profile. After the commit, so a concurrent reader cannot re-cache the old state.
        long? scoreSetId = await conn.ExecuteScalarAsync<long?>(
            "SELECT set_id FROM beatmaps WHERE id = @beatmapId", new { beatmapId });
        await ctx.RequestServices.GetRequiredService<CacheEviction>().AfterScoreAsync(scoreSetId, beatmapId, user.Id);

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
            // What this play was priced at, so the game can show the authoritative number on its
            // results screen instead of re-deriving one. Sent EXACTLY as PerformancePoints.ForScore
            // returned it, which makes the wire contract a single sentence: a non-null pp means the
            // server ran the formula for this play and this is the answer, and nothing else does.
            //
            // So a null here is never "worth zero". It is one of:
            //   - REFUSED (unranked score, which covers an unranked map, an unranked mod, a fail and
            //     every anti-cheat gate; a ranked play on a loved set; or a custom rate). No number
            //     can describe it, and the game shows a dash rather than a 0 that would read as an
            //     earned score of nothing.
            //   - NOT PRICED YET (a base-rate DT/HT run, or a Literate run, on a map whose matching
            //     sr_* column is not stored yet). The 0 in the column is a placeholder PpBackfill overwrites at the
            //     next boot, so asserting it would freeze the results screen on a number the
            //     database is about to disagree with. The game prices the play itself instead,
            //     which it can: unlike this server it computes star ratings on demand rather than
            //     reading three stored columns.
            // A genuinely priced play worth 0 (a give-up run on a ranked map) still sends 0, and
            // the game prints it, because that IS its price.
            Pp = pp,
            // Always false here by construction: the score id is minted by this very insert, so
            // the client cannot have uploaded its replay yet. It uploads right after reading this
            // response (PUT /api/v2/scores/{id}/replay), and the leaderboard reports it from then on.
            HasReplay = false,
            Ranked = ranked,
            RulesetId = 0,
            BeatmapId = beatmapId,
        };

        return WireJson.Ok(response);
    }

    // ---------------------------------------------------------------------------------------------
    // GET: beatmap leaderboard (global, best-per-user). GetScoresRequest.cs:46-59.
    // mode / mods[] are ignored, and so is type for a signed-in caller (always global; a guest's
    // type is read, below); limit is capped at 50.
    // The wire shape is the same whichever board a map serves (see the status switch below); the
    // client tells them apart from the beatmap status it already holds, plus each row's "ranked".
    //
    // Optional auth (backlog 406). A guest gets the GLOBAL board with no user_score. A guest asking
    // for a personal scope (country, friend, team) gets an EMPTY board, 200: it has no country or
    // friends to filter by, and a 401 would log a signed-in client out (APIAccess). A signed-in
    // caller is unchanged, every scope still answering the global board.
    // ---------------------------------------------------------------------------------------------
    private static async Task<IResult> Leaderboard(long beatmapId, HttpContext ctx, Db db)
    {
        var user = await ctx.ResolveBearerAsync();

        if (user is null && !IsGlobalScope(ctx.Request.Query["type"].ToString()))
            return WireJson.Ok(new ScoresCollectionWire { ScoreCount = 0, Scores = [], UserScore = null });

        int limit = 50;
        if (int.TryParse(ctx.Request.Query["limit"], NumberStyles.Integer, CultureInfo.InvariantCulture, out int requested))
            limit = Math.Clamp(requested, 1, 50);

        await using var conn = await db.OpenAsync(ctx.RequestAborted);

        // The board's SHARED slice (set status, top rows, participant count) is the same for every
        // caller on this host, and the game fetches it once per beatmap selected, so a busy map
        // would otherwise run the same three queries for every player scrolling past it. It is
        // memoised for CacheEviction.BoardMemoTtl (5 s) and dropped by every score submit, replay
        // upload and rank change; the caller's own row and position below stay live.
        var eviction = ctx.RequestServices.GetRequiredService<CacheEviction>();
        string memoKey = eviction.BoardKey(beatmapId, limit, ctx.Request);

        if (!eviction.Memo.TryGetValue(memoKey, out BoardSlice? slice) || slice is null)
        {
            slice = await readBoardSliceAsync(conn, ctx, beatmapId, limit);
            eviction.Memo.Set(memoKey, slice, CacheEviction.BoardMemoTtl);
        }

        if (slice.WantRanked is not bool wantRanked)
            return WireJson.Ok(new ScoresCollectionWire { ScoreCount = 0, Scores = [], UserScore = null });

        var scores = slice.Scores;
        int scoreCount = slice.ScoreCount;

        if (user is null)
            return WireJson.Ok(new ScoresCollectionWire { ScoreCount = scoreCount, Scores = scores, UserScore = null });

        // The caller's own best score + its global position, if they have one.
        var callerBest = await conn.QuerySingleOrDefaultAsync<LeaderboardRow>(
            $"""
            SELECT s.id                 AS scoreId,
                   s.user_id            AS userId,
                   s.total_score        AS totalScore,
                   s.accuracy           AS accuracy,
                   s.max_combo          AS maxCombo,
                   s.rank               AS rank,
                   s.ended_at           AS endedAt,
                   s.statistics::text   AS statisticsJson,
                   s.maximum_statistics::text AS maximumStatisticsJson,
                   s.mods::text         AS modsJson,
                   s.replay_key IS NOT NULL AS hasReplay,
                   u.username           AS username,
                   u.country_code       AS countryCode,
                   u.avatar_key         AS avatarKey,
                   s.pp                 AS pp
            FROM scores s
            JOIN users u ON u.id = s.user_id
            WHERE s.beatmap_id = @beatmapId AND s.user_id = @userId AND {BeatmapLeaderboard.OnBoard("s")}
            ORDER BY {BeatmapLeaderboard.Order("s")}
            LIMIT 1
            """,
            new { beatmapId, userId = user.Id, wantRanked });

        ScoreWithPositionWire? userScore = null;
        if (callerBest is not null)
        {
            int position = await PositionOf(conn, null, beatmapId, callerBest.TotalScore, callerBest.ScoreId, wantRanked);
            userScore = new ScoreWithPositionWire { Position = position, Score = ToSoloScoreWire(ctx, callerBest, beatmapId, wantRanked, slice.AwardsPp) };
        }

        return WireJson.Ok(new ScoresCollectionWire
        {
            ScoreCount = scoreCount,
            Scores = scores,
            UserScore = userScore,
        });
    }

    /// <summary>
    /// The part of a leaderboard response every caller shares: which board the map serves (null
    /// for none), its top rows as wire objects (their URLs are this request's host, which is part
    /// of the memo key), and the participant count. <c>AwardsPp</c> is whether the set's plays
    /// carry pp at all ('ranked' only; a loved set's board is ranked but unpriced), which the
    /// caller's own row below needs as much as the shared rows do.
    /// </summary>
    private sealed record BoardSlice(bool? WantRanked, List<SoloScoreWire> Scores, int ScoreCount, bool AwardsPp);

    private static async Task<BoardSlice> readBoardSliceAsync(Npgsql.NpgsqlConnection conn, HttpContext ctx, long beatmapId, int limit)
    {
        // Which board this map serves is decided by its set's CURRENT status, re-read here (never
        // trusted from the stored scores.ranked flags) so the reviewer's Rank/Unrank lever stays
        // authoritative in both directions:
        //
        //  - 'ranked' / 'loved'     → the ranked board: passed plays with scores.ranked. A loved
        //                             set is osu!'s "leaderboard, no pp": its plays store ranked
        //                             (ScoreEndpoints.SubmitScore) and list here exactly as a
        //                             ranked set's do, but no row on it is sent with pp, not even
        //                             a ranked-era row that still stores some from before an
        //                             Unrank and a Love (pp is the set's status, not the row's).
        //  - 'pending' / 'unranked' → the site's UNRANKED board: passed plays with ranked = false,
        //                             exactly the rows /beatmapsets/{id}?board=unranked lists (same
        //                             best-per-user DISTINCT ON, same total-score ordering). Every
        //                             play on a non-ranked set is stored unranked by construction,
        //                             so this is "everything typed here, none of it counting"; a
        //                             set's ranked-era rows stay buried after an un-rank, which is
        //                             what the website's Unranked tab shows too.
        //  - anything else          → no board (hidden, removed, or no such beatmap).
        //
        // A ranked map's board is therefore byte-identical to what it was before unranked boards
        // (or loved sets) existed: an unranked row can never cross onto it, in either direction.
        string? setStatus = await conn.ExecuteScalarAsync<string?>(
            """
            SELECT bs.status
            FROM beatmaps b JOIN beatmapsets bs ON bs.id = b.set_id
            WHERE b.id = @beatmapId
            """,
            new { beatmapId });

        bool? board = setStatus switch
        {
            "ranked" or "loved" => true,
            "pending" or "unranked" => false,
            _ => null,
        };

        if (board is not bool wantRanked)
            return new BoardSlice(null, [], 0, false);

        bool awardsPp = setStatus == "ranked";

        // Best score per user (DISTINCT ON over ix_scores_leaderboard), passed plays on the selected
        // board only, then the global ordering by total score. Failed scores never appear. Both the
        // eligibility and the ordering come from BeatmapLeaderboard, the one board definition the
        // set page and the profile's first places read too.
        var rows = (await conn.QueryAsync<LeaderboardRow>(
            $"""
            SELECT best.id                 AS scoreId,
                   best.user_id            AS userId,
                   best.total_score        AS totalScore,
                   best.accuracy           AS accuracy,
                   best.max_combo          AS maxCombo,
                   best.rank               AS rank,
                   best.ended_at           AS endedAt,
                   best.statistics         AS statisticsJson,
                   best.maximum_statistics AS maximumStatisticsJson,
                   best.mods               AS modsJson,
                   best.has_replay         AS hasReplay,
                   best.username           AS username,
                   best.country_code       AS countryCode,
                   best.avatar_key         AS avatarKey,
                   best.pp                 AS pp
            FROM (
                SELECT DISTINCT ON (s.user_id)
                       s.id, s.user_id, s.total_score, s.accuracy, s.max_combo, s.rank, s.ended_at,
                       s.statistics::text AS statistics, s.maximum_statistics::text AS maximum_statistics,
                       s.mods::text AS mods,
                       s.replay_key IS NOT NULL AS has_replay,
                       u.username, u.country_code, u.avatar_key, s.pp
                FROM scores s
                JOIN users u ON u.id = s.user_id
                WHERE s.beatmap_id = @beatmapId AND {BeatmapLeaderboard.OnBoard("s")}
                ORDER BY s.user_id, {BeatmapLeaderboard.Order("s")}
            ) best
            ORDER BY {BeatmapLeaderboard.Order("best")}
            LIMIT @limit
            """,
            new { beatmapId, limit, wantRanked })).ToList();

        var scores = rows.Select(r => ToSoloScoreWire(ctx, r, beatmapId, wantRanked, awardsPp)).ToList();

        // Total distinct participants (osu-web's score_count reflects the full board, not the page).
        int scoreCount = await conn.ExecuteScalarAsync<int>(
            $"SELECT COUNT(DISTINCT user_id) FROM scores WHERE beatmap_id = @beatmapId AND {BeatmapLeaderboard.OnBoard("scores")}",
            new { beatmapId, wantRanked });

        return new BoardSlice(wantRanked, scores, scoreCount, awardsPp);
    }

    // ---- helpers ----

    /// <summary>
    /// Whether a leaderboard <c>type</c> is the global board: <c>global</c> (any case), or absent,
    /// which every board on this server has always defaulted to. GetScoresRequest sends its
    /// <c>BeatmapLeaderboardScope</c> lowercased, so the personal scopes arrive as
    /// <c>country</c>, <c>friend</c> and <c>team</c>.
    /// </summary>
    private static bool IsGlobalScope(string? type)
        => string.IsNullOrEmpty(type) || string.Equals(type, "global", StringComparison.OrdinalIgnoreCase);

    /// <summary>Position of the caller's best ranked+passed score, or null if they have none.</summary>
    private static async Task<int?> ComputeUserPosition(System.Data.Common.DbConnection conn, System.Data.Common.DbTransaction? tx, long beatmapId, long userId)
    {
        var best = await conn.QuerySingleOrDefaultAsync<BestScoreRow>(
            $"""
            SELECT s.id AS id, s.total_score AS totalScore
            FROM scores s
            WHERE s.beatmap_id = @beatmapId AND s.user_id = @userId AND {BeatmapLeaderboard.OnBoard("s", "true")}
            ORDER BY {BeatmapLeaderboard.Order("s")}
            LIMIT 1
            """,
            new { beatmapId, userId }, tx);

        if (best is null)
            return null;

        return await PositionOf(conn, tx, beatmapId, best.TotalScore, best.Id, wantRanked: true);
    }

    /// <summary>
    /// 1-based rank of a (total_score, scoreId) among distinct users' best scores on one board:
    /// the ranked board when <paramref name="wantRanked"/>, the unranked board otherwise. The two
    /// are counted separately, so an unranked play is never positioned against ranked ones.
    /// </summary>
    private static async Task<int> PositionOf(System.Data.Common.DbConnection conn, System.Data.Common.DbTransaction? tx, long beatmapId, long totalScore, long scoreId, bool wantRanked)
    {
        return await conn.ExecuteScalarAsync<int>(
            $"""
            SELECT 1 + COUNT(*)
            FROM (
                SELECT DISTINCT ON (s.user_id) s.user_id, s.total_score, s.id
                FROM scores s
                WHERE s.beatmap_id = @beatmapId AND {BeatmapLeaderboard.OnBoard("s")}
                ORDER BY s.user_id, {BeatmapLeaderboard.Order("s")}
            ) b
            WHERE {BeatmapLeaderboard.Outranks("b")}
            """,
            new { beatmapId, totalScore, scoreId, wantRanked }, tx);
    }

    /// <summary>
    /// One leaderboard row on the wire. <paramref name="ranked"/> is the board it came from, echoed
    /// into SoloScoreInfo.ranked so the client can tell a counting play from an unranked-board one.
    /// <paramref name="awardsPp"/> is whether the set prices its plays ('ranked' only, not 'loved').
    /// </summary>
    private static SoloScoreWire ToSoloScoreWire(HttpContext ctx, LeaderboardRow r, long beatmapId, bool ranked, bool awardsPp) => new()
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
        Mods = ParseMods(r.ModsJson),
        Statistics = ParseCounts(r.StatisticsJson),
        MaximumStatistics = ParseCounts(r.MaximumStatisticsJson),
        // Drives the client's "watch replay" action; true once the owner has uploaded the .osr
        // through PUT /api/v2/scores/{id}/replay (ReplayEndpoints).
        // Only a ranked board's play on a set that awards pp can carry pp; an unranked-board row
        // has none by construction, and a loved board's rows have none by status.
        Pp = ranked && awardsPp && r.Pp > 0 ? r.Pp : null,
        HasReplay = r.HasReplay,
        Ranked = ranked,
        User = BuildUser(ctx, r.UserId, r.Username, r.CountryCode, r.AvatarKey),
    };

    /// <summary>
    /// The user object riding on leaderboard rows and the submit response. avatar_url is built by
    /// the shared <see cref="UserWire.AvatarUrl(string, string, string?)"/>; the stored avatar_key
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

    /// <summary>
    /// The stored statistics jsonb as the client's HitResult-keyed counts. Internal because the
    /// profile score rows (<see cref="ProfileScoreEndpoints"/>) carry the same two blobs and must
    /// decode them identically; a second copy is a second thing to keep in step.
    /// </summary>
    internal static Dictionary<string, int> ParseCounts(string? json)
        => JsonConvert.DeserializeObject<Dictionary<string, int>>(json ?? "{}") ?? new Dictionary<string, int>();

    /// <summary>
    /// The stored mods jsonb (<c>[{"acronym":"DT","settings":{"speed_change":1.5}}]</c>) passed
    /// straight through to the leaderboard wire, so the client's ModIcon strip renders on global
    /// scores exactly as it does for local plays, rate pill included. Empty/null → no mods.
    /// Internal for the same reason as <see cref="ParseCounts"/>.
    /// </summary>
    internal static object[] ParseMods(string? json)
        => string.IsNullOrWhiteSpace(json)
            ? Array.Empty<object>()
            : JsonConvert.DeserializeObject<object[]>(json) ?? Array.Empty<object>();

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

    // Appended, never reordered: Dapper maps positional records by position (BaseStars/SrDt/SrHt
    // are the 020_performance_points.sql additions, the three SrLiterate* the 029_literate_stars.sql
    // ones, Ratings the 034_ratings_matrix.sql one). The six ratings are no longer read by the
    // pricing path below, which reads the matrix alone; they stay on the row because the response
    // this endpoint builds still reports the play's own rating to the client.
    private sealed record BeatmapRow(
        long Id, string ChecksumMd5, double DrainLengthS, double SkippableS,
        double BaseStars, double? SrDt, double? SrHt,
        double? SrLiterate, double? SrLiterateDt, double? SrLiterateHt,
        string? Ratings, double? PlayedDurationS);

    private sealed record BestScoreRow(long Id, long TotalScore);

    private sealed record BuildRow(long Id, bool Blocked);

    // timestamptz arrives from Npgsql as UTC DateTime; DateTimeOffset ctor params break
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
        string? ModsJson,
        bool HasReplay,
        string Username,
        string CountryCode,
        string? AvatarKey,
        // Appended last: Dapper maps this positional record by column order.
        double Pp);

    // ---- request body (subset of SoloScoreInfo the client submits, SoloScoreInfo.ForSubmission) ----

    private sealed class SoloScoreSubmission
    {
        [JsonProperty("passed")]
        public bool Passed { get; set; }

        [JsonProperty("total_score")]
        public long TotalScore { get; set; }

        // The base standardised score before any mod multiplier. Bounded against the provable no-mod
        // ceiling; the mod multiplier legitimately moves the ranked TotalScore above (DT/NC/FL) or
        // below (HT) that ceiling, so the multiplied total must not be bounded by it directly.
        [JsonProperty("total_score_without_mods")]
        public long TotalScoreWithoutMods { get; set; }

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

    /// <summary>One entry of the client's submitted mod list (osu APIMod shape): acronym + settings.</summary>
    private sealed class SubmittedMod
    {
        [JsonProperty("acronym")]
        public string? Acronym { get; set; }

        // e.g. { "speed_change": 1.01 } on a DoubleTime. The client pins speed_change onto every
        // DT/NC/HT/DC it submits, even at the default; it prices the play and is stored for display.
        [JsonProperty("settings")]
        public Dictionary<string, object>? Settings { get; set; }
    }
}
