using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Newtonsoft.Json;
using Npgsql;
using Typebeat.Web.Auth;
using Typebeat.Web.Caching;
using Typebeat.Web.Data;
using Typebeat.Web.Scoring;
using Typebeat.Web.Storage;
using Typebeat.Web.Wire;

namespace Typebeat.Web.Endpoints;

/// <summary>
/// The in-browser web player's backend (additive; the bearer game-client score flow in
/// <see cref="ScoreEndpoints"/> is untouched). Six routes:
///
///  - GET  /play/map/{setId}/diffs  → the set's live .osu difficulties (anonymous, same media gate)
///  - GET  /play/map/{setId}/osu    → one difficulty's .osu text (anonymous, same media gate as downloads)
///  - GET  /play/map/{setId}/audio  → that difficulty's audio blob, range-capable (anonymous, same gate)
///  - GET  /play/map/{setId}/font   → that difficulty's bundled lyric font, if it names one (anonymous, same gate)
///  - POST /play/token              → issue a score token (cookie session + antiforgery)
///  - POST /play/submit             → complete a score token (cookie session + antiforgery)
///
/// <para>WHICH DIFFICULTY. The media routes take an optional <c>?diff={beatmapId}</c>; without
/// it they serve the set's primary (lowest-id) difficulty, which is all the picker could address
/// before backlog 230. The audio and font names are read out of THAT difficulty's own .osu, so
/// two diffs pointing at different files each get their own.</para>
///
/// <para>TAMPER BOUND ON A NAMED DIFFICULTY. Once the client names a beatmap, "we serve the exact
/// map" stops being true by construction, so every route that takes a beatmap id resolves it as
/// <c>id = @beatmapId AND set_id = @setId AND filename IS NOT NULL AND filename LIKE '%.osu'</c>:
/// a beatmap belonging to another set, or a dropped/blank difficulty row that no longer has a file
/// in the current version, is not addressable at all. The token route applies the SAME predicate,
/// so the token, the play-time gate (<c>beatmaps.skippable_s</c> is per difficulty) and the
/// leaderboard row all key off the difficulty that was actually served.</para>
///
/// The two mutating routes are the cookie-session mirror of <see cref="ScoreEndpoints"/>'s
/// CreateToken/SubmitScore: identical recompute + tamper-bounds via <see cref="ScoringContract"/>,
/// identical scores/user_stats side effects, but authenticated by the website session cookie
/// (<see cref="SessionCookieAuth.SessionUser"/>) and CSRF-protected via <see cref="IAntiforgery"/>
/// rather than a bearer token. Tamper-shaped input on the mutating routes always resolves to a 4xx,
/// never a 500.
///
/// <para>SUBMISSION INTEGRITY (backlog 312), the browser's half of the desktop token's two
/// identities. BUILD: the server hashes the three /play scripts once at startup
/// (<see cref="Revision"/>), the page renders it, and the token route registers what the player
/// sends back as its own build <c>web-&lt;revision&gt;</c>, so a tab left open across a deploy
/// submits under its OWN build row, which an admin can block alone. MAP: the /osu route answers the
/// served difficulty's checksum in <see cref="ChecksumHeader"/>; the player hands it back with the
/// token and a mismatch (the map was re-uploaded after the tab fetched it) is refused with the
/// desktop's 422. A body without either (a tab from before this change) keeps the old behaviour:
/// the synthetic <c>web-player</c> build and the server's own checksum.</para>
/// </summary>
public static class PlayEndpoints
{
    private const int status_unprocessable = StatusCodes.Status422UnprocessableEntity;
    private const string web_build_hash = "web-player";

    /// <summary>The response header GET /play/map/{setId}/osu carries the served difficulty's
    /// stored checksum in (<c>beatmaps.checksum_md5</c>, the desktop's beatmap_hash).</summary>
    public const string ChecksumHeader = "X-Beatmap-Checksum";

    /// <summary>The scripts whose content IS the browser client: the engine, the player, the page
    /// glue. Their order is part of the hash.</summary>
    private static readonly string[] revision_scripts = ["/js/typebeat-core.js", "/js/typebeat-player.js", "/js/play.js"];

    /// <summary>What a client-sent revision must look like to be registered (the shape
    /// <see cref="ComputeRevision"/> produces). Anything else is refused rather than written into
    /// <c>builds</c>, so the table cannot be filled with arbitrary text.</summary>
    private static readonly Regex revision_shape = new("^[0-9a-f]{16}$", RegexOptions.CultureInvariant);

    /// <summary>
    /// The content hash of the /play scripts this server is serving, computed once at startup
    /// (<see cref="Map"/>) and rendered into the /play page, which sends it back on POST /play/token.
    /// Null when it could not be computed (a script missing from the web root): the page then sends
    /// none and the token falls back to <c>web-player</c>.
    /// </summary>
    public static string? Revision { get; private set; }

    /// <summary>
    /// Combines the per-file values asp-append-version stamps onto each script tag (the
    /// <see cref="IFileVersionProvider"/>'s SHA-256 of the file's content) into one 16-hex-digit id.
    /// Any byte of any of the three scripts changing changes it; nothing else does.
    /// </summary>
    public static string? ComputeRevision(IFileVersionProvider versions)
    {
        var sb = new StringBuilder();
        foreach (string script in revision_scripts)
        {
            string stamped = versions.AddFileVersionToPath(PathString.Empty, script);
            int at = stamped.IndexOf("?v=", StringComparison.Ordinal);
            if (at < 0)
                return null; // the provider leaves a path it cannot find unstamped
            sb.Append(script).Append(':').Append(stamped[(at + 3)..]).Append('\n');
        }

        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(digest, 0, 8).ToLowerInvariant();
    }

    public static void Map(IEndpointRouteBuilder app)
    {
        var versions = app.ServiceProvider.GetService<IFileVersionProvider>();
        Revision = versions is null ? null : ComputeRevision(versions);

        // Backlog 366: the picker's diffs and the .osu text are the same bytes for every anonymous
        // visitor of a published set, so they are output-cached (tag set:{setId}, evicted on every
        // upload and score). The audio and font streams are NOT: range requests make a poor cache
        // entry, so they only tell browsers and the edge they may keep them (see mediaCacheHeader).
        app.MapGet("/play/map/{setId:long}/diffs", GetDiffsAsync).CacheOutput(CachePolicies.PlayMap);
        app.MapGet("/play/map/{setId:long}/osu", GetOsuAsync).CacheOutput(CachePolicies.PlayMap);
        app.MapGet("/play/map/{setId:long}/audio", GetAudioAsync);
        app.MapGet("/play/map/{setId:long}/font", GetFontAsync);
        app.MapPost("/play/token", CreateTokenAsync).RequireRateLimiting(RateLimits.ScoreToken);
        app.MapPost("/play/submit", SubmitScoreAsync).RequireRateLimiting(RateLimits.ScoreSubmit);
    }

    // ---------------------------------------------------------------------------------------------
    // GET /play/map/{setId}/diffs: the set's live .osu difficulties, hardest first, which is what
    // the picker's difficulty step renders as .diff-pill buttons. Anonymous, same media gate as the
    // two routes below, so it can never advertise a difficulty those would refuse to serve.
    //
    // The star COLOUR is computed here rather than ported into JS: DifficultyColour is the one
    // ramp the whole site tints ★ readouts with (the set page's own selector included), and a
    // second copy in the player script would drift from it silently.
    // ---------------------------------------------------------------------------------------------
    private static async Task<IResult> GetDiffsAsync(long setId, HttpContext ctx, Db db)
    {
        await using var conn = await db.OpenAsync(ctx.RequestAborted);

        if (!await CanSeeSetMediaAsync(ctx, conn, setId))
            return Results.NotFound();

        var rows = await conn.QueryAsync<DiffRow>(
            """
            SELECT b.id                    AS id,
                   b.version_name          AS versionName,
                   b.difficulty_rating     AS stars,
                   -- The pill's pace is the TARGET WPM (033_target_wpm.sql), with the stored
                   -- average as the fallback so a difficulty the LyricPace v18 backfill has not
                   -- reached still shows a figure rather than losing the pill. Same substitution
                   -- and same fallback as the listing card's chip (BeatmapsetCardSql).
                   coalesce(b.target_wpm, b.wpm::double precision) AS wpm,
                   -- The plain whole-map average, which /play's underline pace hue draws its
                   -- map-relative bands against (the desktop's default since PR 5 reads the same
                   -- LyricPaceStatistics average). Null for a row LyricPace never priced, and the
                   -- player then falls back to its relative bands.
                   b.wpm::double precision AS avgWpm
            FROM beatmaps b
            WHERE b.set_id = @setId AND b.filename IS NOT NULL AND b.filename LIKE '%.osu'
            ORDER BY b.difficulty_rating DESC, b.id ASC
            """,
            new { setId });

        return WireJson.Ok(new
        {
            diffs = rows.Select(d => new
            {
                id = d.Id,
                version_name = d.VersionName,
                stars = d.Stars,
                wpm = d.Wpm,
                avg_wpm = d.AvgWpm,
                colour = DifficultyColour.ForStars(d.Stars),
            }).ToList(),
        });
    }

    // ---------------------------------------------------------------------------------------------
    // GET /play/map/{setId}/osu[?diff={beatmapId}]: one difficulty's .osu, served as text/plain.
    // Anonymous, same media-access gate as the download route (published set, or the owner).
    // ---------------------------------------------------------------------------------------------
    private static async Task<IResult> GetOsuAsync(long setId, HttpContext ctx, Db db, IFileStore store)
    {
        await using var conn = await db.OpenAsync(ctx.RequestAborted);

        if (!await CanSeeSetMediaAsync(ctx, conn, setId))
            return Results.NotFound();

        var served = await ResolveOsuAsync(conn, setId, RequestedDiff(ctx));
        if (served is null)
            return Results.NotFound();

        var stream = await OpenManifestBlobAsync(conn, store, setId, served.Filename, ctx.RequestAborted);
        if (stream is null)
            return Results.NotFound();

        // The checksum of exactly the difficulty served, which the player hands back with its
        // token (backlog 312): a map re-uploaded after this response is then refused, rather than
        // boarding a run of the old lyrics on the new version's leaderboard.
        ctx.Response.Headers[ChecksumHeader] = served.ChecksumMd5;
        return Results.Stream(stream, "text/plain; charset=utf-8");
    }

    // ---------------------------------------------------------------------------------------------
    // GET /play/map/{setId}/audio[?diff={beatmapId}]: the AudioFilename referenced by THAT
    // difficulty's .osu, streamed with range support (so <audio> can seek). Anonymous, same gate as
    // /osu. Reading the name from the chosen diff is what lets two difficulties of one set point at
    // different audio files.
    // ---------------------------------------------------------------------------------------------
    private static async Task<IResult> GetAudioAsync(long setId, HttpContext ctx, Db db, IFileStore store)
    {
        await using var conn = await db.OpenAsync(ctx.RequestAborted);

        if (!await CanSeeSetMediaAsync(ctx, conn, setId))
            return Results.NotFound();

        string? osuName = await ResolveOsuFilenameAsync(conn, setId, RequestedDiff(ctx));
        if (osuName is null)
            return Results.NotFound();

        // Read the .osu text to discover its AudioFilename, then resolve THAT to a blob.
        string? osuText = await ReadOsuTextAsync(conn, store, setId, osuName, ctx.RequestAborted);
        if (osuText is null)
            return Results.NotFound();

        string? audioName = ParseAudioFilename(osuText);
        if (string.IsNullOrEmpty(audioName))
            return Results.NotFound();

        var stream = await OpenManifestBlobAsync(conn, store, setId, audioName, ctx.RequestAborted);
        if (stream is null)
            return Results.NotFound();

        mediaCacheHeader(ctx);
        return Results.Stream(stream, AudioContentType(audioName), enableRangeProcessing: true);
    }

    // ---------------------------------------------------------------------------------------------
    // GET /play/map/{setId}/font[?diff={beatmapId}]: the bundled lyric font THAT difficulty's .osu
    // names in [General] LyricFontFile (backlog 291), streamed exactly as /audio streams the
    // AudioFilename it names. 404 when the difficulty bundles no font, which is nearly every map.
    //
    // SERVE-ONLY IN V1: typebeat-player.js does NOT consume this. Its whole layout (measureRow,
    // the caret, sweep and cue-bar x positions) runs on JetBrains Mono's fixed advance, and a
    // proportional map font would move every one of them; the per-glyph-advance rework is its own
    // follow-up. The route exists now so the player can adopt it without a server deploy then.
    // ---------------------------------------------------------------------------------------------
    private static async Task<IResult> GetFontAsync(long setId, HttpContext ctx, Db db, IFileStore store)
    {
        await using var conn = await db.OpenAsync(ctx.RequestAborted);

        if (!await CanSeeSetMediaAsync(ctx, conn, setId))
            return Results.NotFound();

        string? osuName = await ResolveOsuFilenameAsync(conn, setId, RequestedDiff(ctx));
        if (osuName is null)
            return Results.NotFound();

        string? osuText = await ReadOsuTextAsync(conn, store, setId, osuName, ctx.RequestAborted);
        if (osuText is null)
            return Results.NotFound();

        string? fontName = ParseLyricFontFilename(osuText);
        if (string.IsNullOrEmpty(fontName))
            return Results.NotFound();

        var stream = await OpenManifestBlobAsync(conn, store, setId, fontName, ctx.RequestAborted);
        if (stream is null)
            return Results.NotFound();

        mediaCacheHeader(ctx);
        return Results.Stream(stream, FontContentType(fontName), enableRangeProcessing: true);
    }

    // ---------------------------------------------------------------------------------------------
    // POST /play/token: issue a score token for the signed-in website user.
    // Body: { "setId": <long>, "beatmapId": <long>, "revision": <string?>, "beatmapHash": <string?> }
    // (the player sends both ids since backlog 230, and the last two since backlog 312; a set-only
    // body still resolves the set's primary difficulty). Response: { "id": <long> }.
    // ---------------------------------------------------------------------------------------------
    private static async Task<IResult> CreateTokenAsync(HttpContext ctx, Db db, IAntiforgery antiforgery, DiskGuard disk)
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

        // Disk guard (backlog 365): the same rule as the bearer token route, critical level only.
        if (disk.Current.TokensRefused)
            return WireJson.Error(StatusCodes.Status503ServiceUnavailable, DiskGuard.TokenRefusal);

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

        // The build this run is played on: the revision of the scripts the tab loaded, or the
        // synthetic web-player build for a body that names none (a tab from before backlog 312).
        string versionHash;
        if (string.IsNullOrEmpty(request.Revision))
            versionHash = web_build_hash;
        else if (revision_shape.IsMatch(request.Revision))
            versionHash = "web-" + request.Revision;
        else
            return WireJson.Error(StatusCodes.Status400BadRequest, "invalid request body");

        await using var conn = await db.OpenAsync(ctx.RequestAborted);

        // Resolve the playable beatmap. The set must be published (pending/unranked/ranked), and the
        // difficulty must be LIVE in the current version (filename IS NOT NULL AND LIKE '%.osu'),
        // which is the same predicate the media routes resolve through.
        //
        // Two shapes, and the branch is the beatmap id rather than the set id: since backlog 230 the
        // player names the difficulty it was served, so `beatmapId` is the normal path and the
        // set-only body is the back-compat one (the set's primary, lowest-id difficulty).
        //
        // WHEN BOTH ARE SENT the beatmap must BELONG to the set. That is the tamper bound the diff
        // picker made necessary: without it a caller could name any published beatmap on the site
        // while claiming to play another set's map, and the token, the skip allowance and the
        // leaderboard row would all follow the named one.
        var beatmap = request.BeatmapId > 0
            ? await conn.QuerySingleOrDefaultAsync<BeatmapRow>(
                """
                SELECT b.id, b.checksum_md5 AS checksumMd5, b.drain_length_s AS drainLengthS, b.skippable_s AS skippableS,
                       b.difficulty_rating AS baseStars, b.ratings::text AS ratings
                FROM beatmaps b
                JOIN beatmapsets bs ON bs.id = b.set_id
                WHERE b.id = @beatmapId
                  AND (@setId <= 0 OR b.set_id = @setId)
                  AND bs.status IN ('pending', 'unranked', 'ranked')
                  AND b.filename IS NOT NULL AND b.filename LIKE '%.osu'
                """,
                new { beatmapId = request.BeatmapId, setId = request.SetId })
            : await conn.QuerySingleOrDefaultAsync<BeatmapRow>(
                """
                SELECT b.id, b.checksum_md5 AS checksumMd5, b.drain_length_s AS drainLengthS, b.skippable_s AS skippableS,
                       b.difficulty_rating AS baseStars, b.ratings::text AS ratings
                FROM beatmaps b
                JOIN beatmapsets bs ON bs.id = b.set_id
                WHERE b.set_id = @setId AND bs.status IN ('pending', 'unranked', 'ranked')
                  AND b.filename IS NOT NULL AND b.filename LIKE '%.osu'
                ORDER BY b.id
                LIMIT 1
                """,
                new { setId = request.SetId });

        if (beatmap is null)
            return WireJson.Error(status_unprocessable, "beatmap not found or not playable");

        // The map the tab is holding must still be the map this difficulty IS. The player fetched
        // the .osu once, at load, and "play again" re-mints against that same text for as long as
        // the tab stays open, so a re-upload in between would otherwise board the old lyrics on the
        // new version. Same check and same wording as the desktop (ScoreEndpoints.CreateToken). A
        // body that sends no checksum (an older tab) is filled from the stored one, as before.
        if (!string.IsNullOrEmpty(request.BeatmapHash)
            && !string.Equals(beatmap.ChecksumMd5, request.BeatmapHash, StringComparison.OrdinalIgnoreCase))
            return WireJson.Error(status_unprocessable, "invalid or missing beatmap_hash");

        // Record-don't-reject, exactly as the bearer path: register the build on sight, then refuse
        // a BLOCKED one with the desktop's wording (play.js turns it into a reload prompt). The
        // submit route re-reads blocked as well, so a build blocked mid-play still lands unranked.
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
            VALUES (@userId, @beatmapId, 0, @beatmapHash, @buildId)
            RETURNING id
            """,
            new { userId = user.Id, beatmapId = beatmap.Id, beatmapHash = beatmap.ChecksumMd5, buildId = build.Id });

        return WireJson.Ok(new { id = tokenId });
    }

    // ---------------------------------------------------------------------------------------------
    // POST /play/submit: complete a score token. Mirrors ScoreEndpoints.SubmitScore with the
    // session user substituted for the bearer user. Body shape below; response:
    // { ranked, rank, total_score, accuracy, completion, pp, pp_pending, board, position,
    //   personal_best } (each field is documented where the response is built).
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
                   difficulty_rating AS baseStars, ratings::text AS ratings
            FROM beatmaps WHERE id = @beatmapId
            """,
            new { beatmapId }, tx);

        if (beatmap is null)
            return WireJson.Error(status_unprocessable, "invalid token");

        // Re-read set status NOW: a set un-ranked mid-play must resolve against its current state.
        string? setStatus = await conn.ExecuteScalarAsync<string?>(
            """
            SELECT bs.status
            FROM beatmaps b JOIN beatmapsets bs ON bs.id = b.set_id
            WHERE b.id = @beatmapId
            """,
            new { beatmapId }, tx);
        bool setRanked = setStatus == "ranked";

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
        // (below), so there is no mod multiplier, no rate, no CONVERSION mod and no JUDGEMENT ARM to
        // price: the play always reads the matrix's none/plain/1.00 cell.
        //
        // THAT CELL IS NOT ALWAYS THERE, which is a change from the six-column pricing this
        // replaced (034_ratings_matrix.sql). difficulty_rating is NOT NULL and has been since 001,
        // so a browser play used to be settled unconditionally; the matrix is a new column and is
        // NULL on every row PaceBackfill has not swept, so a browser play on such a map is now
        // PENDING and is priced on a later boot, exactly as a Double Time desktop play on an
        // unswept map already was. Nothing else about the path changes, and the whole stack is
        // still passed rather than assumed, so if /play ever gains mods this call already reads the
        // cell they select.
        var (pp, ppSettled) = PerformancePoints.ForScore(
            ranked,
            mods: [],
            PerformancePoints.CountNotes(statistics),
            storedAccuracy,
            storedMaxCombo,
            BeatmapRatings.Parse(beatmap.Ratings));

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
                // The column is NOT NULL: a play the formula never ran for (unranked here, since
                // the browser player has no mods to make it rate-ineligible) stores 0.
                pp = pp ?? 0,
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

        // WHICH BOARD this play sits on, by the set's CURRENT status, the same switch the game
        // client's leaderboard makes (ScoreEndpoints.Leaderboard): a ranked set serves the ranked
        // board, a pending or unranked set serves the unranked one. A play is only ON a board when
        // it passed and its stored flag matches (BeatmapLeaderboard.OnBoard), so a fail, and a
        // gate-refused play on a ranked set (stored unranked, but its map serves the RANKED board),
        // sit on none and are told no position.
        bool? board = setStatus switch
        {
            "ranked" => true,
            "pending" or "unranked" => false,
            _ => null,
        };
        bool? onBoard = board is bool wantRanked && passed && ranked == wantRanked ? wantRanked : null;
        long? scoreSetId = await conn.ExecuteScalarAsync<long?>(
            "SELECT set_id FROM beatmaps WHERE id = @beatmapId", new { beatmapId }, tx);

        // The position reported is the player's BEST row on that board (what the board itself
        // lists), and personal_best says whether that best is THIS play, so a run that did not
        // beat it is never told its best's rank as though it had earned it.
        var standing = onBoard is bool boardRanked
            ? await ComputeUserPosition(conn, tx, beatmapId, user.Id, boardRanked)
            : null;

        await tx.CommitAsync(ctx.RequestAborted);

        // Cached reads that show this play (backlog 366), dropped after the commit.
        await ctx.RequestServices.GetRequiredService<CacheEviction>().AfterScoreAsync(scoreSetId, beatmapId, user.Id);

        return WireJson.Ok(new
        {
            ranked,
            rank,
            total_score = storedTotal,
            accuracy = storedAccuracy,
            completion = recomputed.Completion,
            // Sent EXACTLY as PerformancePoints.ForScore returned it, the contract the game's
            // submit response already has (ScoreEndpoints.SubmitScore): a number means the formula
            // ran and this is the price, and null is never "worth zero". pp_pending tells the two
            // nulls apart: true is a ranked play on a map whose rating cell is not stored yet
            // (docs/pp.md, "NULL IS THE UNFILLED STATE"), priced by PpBackfill on a later boot;
            // false with a null pp is a refused (unranked) play, which has no price at all.
            pp,
            pp_pending = pp is null && !ppSettled,
            // "ranked" or "unranked" (the board this play is listed on), or null for none.
            board = onBoard switch { true => "ranked", false => "unranked", null => null },
            position = standing?.Position,
            personal_best = standing is { } s && s.BestScoreId == scoreId,
        });
    }

    // ---- media helpers ----

    /// <summary>Published sets (BeatmapsetEndpoints.IsPublished) are world-readable; hidden/removed media only for the owner.</summary>
    private static async Task<bool> CanSeeSetMediaAsync(HttpContext ctx, NpgsqlConnection conn, long setId)
    {
        var set = await conn.QuerySingleOrDefaultAsync<(long OwnerId, string Status)?>(
            "SELECT owner_id AS OwnerId, status AS Status FROM beatmapsets WHERE id = @setId",
            new { setId });

        if (set is not { } row)
            return false;

        if (BeatmapsetEndpoints.IsPublished(row.Status))
            return true;

        // An unpublished set's media is its owner's alone: never stored by the output cache, and
        // private to every cache downstream (backlog 366).
        CachePolicies.DoNotStore(ctx);

        var requester = ctx.SessionUser() ?? await ctx.ResolveBearerAsync();
        return requester?.Id == row.OwnerId;
    }

    /// <summary>
    /// Audio and font bytes of a PUBLISHED set may be kept by browsers and the Cloudflare edge for
    /// five minutes (an upload changes the .osu that names them, so a stale blob is never asked for
    /// for long); an unpublished set's already carries private, no-store from the media gate.
    /// </summary>
    private static void mediaCacheHeader(HttpContext ctx)
    {
        if (string.IsNullOrEmpty(ctx.Response.Headers.CacheControl))
            ctx.Response.Headers.CacheControl = "public, max-age=300";
    }

    /// <summary>The <c>?diff={beatmapId}</c> a media request named, or 0 for "the set's primary".</summary>
    private static long RequestedDiff(HttpContext ctx)
        => long.TryParse(ctx.Request.Query["diff"], out long id) && id > 0 ? id : 0;

    /// <summary>
    /// The archive path of a difficulty's .osu, or null when there is none to serve.
    /// <paramref name="beatmapId"/> 0 means the set's primary (lowest-id) difficulty, which is what
    /// the set-addressed player asked for before the difficulty picker existed.
    ///
    /// <para>A NAMED difficulty is bound to the set being served (<c>set_id = @setId</c>) as well as
    /// to being live: a caller cannot pull another set's .osu, or a dropped diff's stale row, through
    /// a set whose media they are allowed to see.</para>
    /// </summary>
    private static async Task<string?> ResolveOsuFilenameAsync(NpgsqlConnection conn, long setId, long beatmapId = 0)
        => beatmapId > 0
            ? await conn.ExecuteScalarAsync<string?>(
                """
                SELECT filename FROM beatmaps
                WHERE id = @beatmapId AND set_id = @setId
                  AND filename IS NOT NULL AND filename LIKE '%.osu'
                """,
                new { setId, beatmapId })
            : await conn.ExecuteScalarAsync<string?>(
                """
                SELECT filename FROM beatmaps
                WHERE set_id = @setId AND filename IS NOT NULL AND filename LIKE '%.osu'
                ORDER BY id
                LIMIT 1
                """,
                new { setId });

    /// <summary>
    /// <see cref="ResolveOsuFilenameAsync"/> for the /osu route, which also answers the resolved
    /// difficulty's stored checksum (same predicate, same primary-difficulty fallback).
    /// </summary>
    private static async Task<ServedOsuRow?> ResolveOsuAsync(NpgsqlConnection conn, long setId, long beatmapId = 0)
        => beatmapId > 0
            ? await conn.QuerySingleOrDefaultAsync<ServedOsuRow>(
                """
                SELECT filename, checksum_md5 AS checksumMd5 FROM beatmaps
                WHERE id = @beatmapId AND set_id = @setId
                  AND filename IS NOT NULL AND filename LIKE '%.osu'
                """,
                new { setId, beatmapId })
            : await conn.QuerySingleOrDefaultAsync<ServedOsuRow>(
                """
                SELECT filename, checksum_md5 AS checksumMd5 FROM beatmaps
                WHERE set_id = @setId AND filename IS NOT NULL AND filename LIKE '%.osu'
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

    /// <summary>Reads a difficulty's .osu blob as text via the set's current version manifest,
    /// or null when the manifest row or blob is missing.</summary>
    private static async Task<string?> ReadOsuTextAsync(NpgsqlConnection conn, IFileStore store, long setId, string osuName, CancellationToken ct)
    {
        byte[]? osuSha = await ResolveManifestShaAsync(conn, setId, osuName);
        if (osuSha is null)
            return null;

        try
        {
            await using var osuStream = await store.OpenBlobReadAsync(osuSha, ct);
            using var reader = new StreamReader(osuStream);
            return await reader.ReadToEndAsync(ct);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Extracts the [General] AudioFilename value (rest of the line after the first ':').</summary>
    private static string? ParseAudioFilename(string osuText) => ParseGeneralValue(osuText, "AudioFilename");

    /// <summary>Extracts the [General] LyricFontFile value: the bundled lyric font's name inside
    /// the set (backlog 291), or null when the map bundles none. An EXACT key match, which matters
    /// here more than for the audio: "LyricFont" (the family) is a prefix of this key, so a prefix
    /// match would hand back the family for a map that sets one without bundling a file.</summary>
    private static string? ParseLyricFontFilename(string osuText) => ParseGeneralValue(osuText, "LyricFontFile");

    private static string? ParseGeneralValue(string osuText, string key)
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

            if (!inGeneral)
                continue;

            int colon = trimmed.IndexOf(':');

            if (colon > 0 && string.Equals(trimmed[..colon].Trim(), key, StringComparison.OrdinalIgnoreCase))
                return trimmed[(colon + 1)..].Trim();
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

    /// <summary>By extension, over the three formats PackageValidator admits as fonts; anything
    /// else in the column position falls back like an unknown audio extension does above.</summary>
    private static string FontContentType(string filename) => Path.GetExtension(filename).ToLowerInvariant() switch
    {
        ".ttf" => "font/ttf",
        ".otf" => "font/otf",
        ".woff2" => "font/woff2",
        _ => "application/octet-stream",
    };

    // ---- scoring helpers (the board rules come from BeatmapLeaderboard, as ScoreEndpoints' do) ----

    /// <summary>
    /// The user's best row on one board of a map (the ranked board when <paramref name="wantRanked"/>,
    /// the unranked one otherwise) and its 1-based position among every player's best there, or
    /// null when they have no row on it. Built entirely from the <see cref="BeatmapLeaderboard"/>
    /// fragments, so eligibility, the per-player fold and the tie-break are the board's own rules
    /// rather than a copy of them.
    /// </summary>
    private static async Task<(long BestScoreId, int Position)?> ComputeUserPosition(
        NpgsqlConnection conn, System.Data.Common.DbTransaction? tx, long beatmapId, long userId, bool wantRanked)
    {
        var best = await conn.QuerySingleOrDefaultAsync<BestScoreRow>(
            $"""
            SELECT s.id AS id, s.total_score AS totalScore
            FROM scores s
            WHERE s.beatmap_id = @beatmapId AND s.user_id = @userId AND {BeatmapLeaderboard.OnBoard("s")}
            ORDER BY {BeatmapLeaderboard.Order("s")}
            LIMIT 1
            """,
            new { beatmapId, userId, wantRanked }, tx);

        if (best is null)
            return null;

        int position = await conn.ExecuteScalarAsync<int>(
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
            new { beatmapId, totalScore = best.TotalScore, scoreId = best.Id, wantRanked }, tx);

        return (best.Id, position);
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
    // Appended, never reordered: Dapper maps positional records by position (Ratings is the
    // 034_ratings_matrix.sql addition).
    private sealed record BeatmapRow(long Id, string ChecksumMd5, double DrainLengthS, double SkippableS, double BaseStars, string? Ratings);

    private sealed record BuildRow(long Id, bool Blocked);

    private sealed record ServedOsuRow(string Filename, string ChecksumMd5);

    /// <summary>One live difficulty, as the picker's difficulty step renders it.</summary>
    private sealed record DiffRow(long Id, string VersionName, double Stars, double? Wpm, double? AvgWpm);

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

        /// <summary>The page's <see cref="Revision"/>, as rendered when the tab loaded.</summary>
        [JsonProperty("revision")]
        public string? Revision { get; set; }

        /// <summary>The <see cref="ChecksumHeader"/> value of the .osu the tab is playing.</summary>
        [JsonProperty("beatmapHash")]
        public string? BeatmapHash { get; set; }
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
