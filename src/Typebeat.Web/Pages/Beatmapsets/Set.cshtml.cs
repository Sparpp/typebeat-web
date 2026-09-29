using System.Globalization;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using Typebeat.Web.Auth;
using Typebeat.Web.Data;
using Typebeat.Web.Packages;
using Typebeat.Web.Scoring;
using Typebeat.Web.Social;

namespace Typebeat.Web.Pages.Beatmapsets;

/// <summary>
/// Beatmapset page (/beatmapsets/{id}): cover header with scrim, stats box, plain-text
/// description, tags, the global leaderboard (top 50 best-per-user, podium for #1),
/// rendered only on 'ranked' sets; anything else shows a "unlocks when ranked" note instead;
/// and, below the leaderboard, the selected difficulty's lyrics (beatmaps.lyrics, hidden
/// when empty).
/// POST handlers: Favourite (toggle + denormalized counter bump), Report (reports table),
/// Description (owner/reviewer edit of the set's plain-text description), Comment and
/// DeleteComment (the comments section below the lyrics, backlog 295),
/// and the reviewer-only Rank/Unrank pair (pending ⇄ ranked, nothing else).
/// Hidden sets are visible to their owner only. Removed sets 404 for the public but stay
/// viewable by their owner and by admins (the owner's profile deliberately lists them, and a
/// DMCA'd mapper deserves to see the Removed pill instead of a dead link; the download
/// endpoint already granted the owner the same access).
/// </summary>
public sealed class SetModel(Db db, ILogger<SetModel> logger) : TypebeatPageModel
{
    private const int max_report_reason_length = 4000;

    /// <summary>
    /// Comment-post speed bump, keyed on the signed-in user's id: 10 comments per 5 minutes is
    /// generous for a conversation and useless for a flood. Same in-memory pattern as the
    /// login/register limiters; a refusal bounces back as the ?comment=slow notice (the pin
    /// handlers' ?pin=limit idiom).
    /// </summary>
    private static readonly FixedWindowLimiter comment_posts = new(10, TimeSpan.FromMinutes(5));

    public SetDetails Set { get; private set; } = null!;

    /// <summary>Every difficulty in the set, hardest first (drives the difficulty selector).</summary>
    public IReadOnlyList<DiffStats> Diffs { get; private set; } = [];

    /// <summary>The selected difficulty (?diff=, else the hardest); its stats + leaderboard show.</summary>
    public DiffStats? Diff { get; private set; }
    public IReadOnlyList<ScoreRow> Scores { get; private set; } = [];

    /// <summary>Which board is shown: "ranked" (default) or "unranked" (passed plays that used an
    /// unranked mod / non-default rate, so they never reached the ranked board). Toggled by ?board=.</summary>
    public string Board { get; private set; } = "ranked";

    /// <summary>Set by the post-report redirect (?reported=1) to swap the form for a thanks note.</summary>
    public bool Reported { get; private set; }

    /// <summary>Set by the post-edit redirect (?saved=description): the edit box's saved note.</summary>
    public bool DescriptionSaved { get; private set; }

    /// <summary>Set by the description PRG when the posted text was over budget: rejected, not
    /// truncated (the Settings bio rule), so the note has to say nothing was saved.</summary>
    public bool DescriptionTooLong { get; private set; }

    /// <summary>The comments section's inline notice: "slow" (rate limited) or "long" (over
    /// budget, rejected). Null renders nothing.</summary>
    public string? CommentFlag { get; private set; }

    /// <summary>This page of the set's comments, oldest first (?comments_after= keyset cursor).</summary>
    public IReadOnlyList<CommentRowModel> Comments { get; private set; } = [];

    /// <summary>Live visible comments on the whole set (the section heading's count).</summary>
    public int CommentCount { get; private set; }

    public bool HasMoreComments { get; private set; }

    /// <summary>The ?diff= this request actually asked for (0 when it didn't, or asked for a
    /// difficulty that is not in this set), so <see cref="BuildUrl"/> preserves only what the
    /// visitor chose rather than pinning the default into every link.</summary>
    private long requestedDiff;

    /// <summary>Owner or reviewer: whether the description edit box renders. The POST handler is
    /// the boundary (its UPDATE carries the same predicate); this is just honesty about it.</summary>
    public bool CanEditDescription
        => CurrentUser is not null && (CurrentUser.Id == Set.OwnerId || CurrentUser.CanReviewMaps);

    /// <summary>Whether this viewer may delete this comment: its author, the set's owner, or a
    /// reviewer. Mirrors <see cref="BeatmapsetComments.SoftDeleteAsync"/>'s WHERE clause.</summary>
    public bool CanDeleteComment(CommentRowModel comment)
        => CurrentUser is not null
           && (comment.UserId == CurrentUser.Id || Set.OwnerId == CurrentUser.Id || CurrentUser.CanReviewMaps);

    /// <summary>
    /// Set-page URL preserving the visitor's diff/board choices; the comments cursor only when
    /// paging (the listing's BuildUrl shape). The caller appends #comments where it wants the
    /// scroll.
    /// </summary>
    public string BuildUrl(long? commentsAfter = null)
    {
        var parts = new List<string>();

        if (requestedDiff != 0)
            parts.Add("diff=" + requestedDiff.ToString(CultureInfo.InvariantCulture));

        if (Board != "ranked")
            parts.Add("board=" + Board);

        if (commentsAfter is long after)
            parts.Add("comments_after=" + after.ToString(CultureInfo.InvariantCulture));

        return $"/beatmapsets/{Set.Id}" + (parts.Count > 0 ? "?" + string.Join("&", parts) : "");
    }

    public async Task<IActionResult> OnGetAsync(long id, bool reported = false, long diff = 0, string board = "ranked",
        long comments_after = 0, string? saved = null, string? description = null, string? comment = null)
    {
        Reported = reported;
        DescriptionSaved = saved == "description";
        DescriptionTooLong = description == "long";
        CommentFlag = comment is "slow" or "long" ? comment : null;
        Board = board == "unranked" ? "unranked" : "ranked";

        await using var conn = await db.OpenAsync(HttpContext.RequestAborted);

        var set = await conn.QuerySingleOrDefaultAsync<SetDetails>(
            """
            SELECT s.id               AS Id,
                   s.title            AS Title,
                   s.artist           AS Artist,
                   s.title_unicode    AS TitleUnicode,
                   s.artist_unicode   AS ArtistUnicode,
                   s.source           AS Source,
                   s.tags             AS Tags,
                   s.description      AS Description,
                   s.status           AS Status,
                   s.explicit         AS Explicit,
                   u.username::text   AS Creator,
                   u.restricted       AS OwnerRestricted,
                   s.owner_id         AS OwnerId,
                   s.cover_key        AS CoverKey,
                   CASE WHEN s.preview_key IS NOT NULL THEN '/' || s.preview_key END AS PreviewUrl,
                   s.play_count       AS PlayCount,
                   s.favourite_count  AS FavouriteCount,
                   s.download_count   AS DownloadCount,
                   -- No s.bpm: the stats box's BPM row became the peak-WPM row (028_wpm_curve.sql),
                   -- and nothing else on this page ever read it. The column is still on the set and
                   -- still drives the bpm: search operator; it is only unread HERE.
                   s.submitted_at     AS SubmittedAt,
                   s.updated_at       AS UpdatedAt,
                   EXISTS (SELECT 1 FROM favourites f WHERE f.set_id = s.id AND f.user_id = @viewerId) AS IsFavourited,
                   -- Pre-M3 sets (backfilled by migration 004) have live diffs but no uploaded
                   -- package: the Download button must not render a dead /download link for them.
                   EXISTS (SELECT 1 FROM set_versions v WHERE v.set_id = s.id AND v.package_key IS NOT NULL) AS HasPackage,
                   -- LAST on purpose: Dapper binds a positional record's constructor parameters to
                   -- the reader's columns BY POSITION, so a new column has to be appended at the
                   -- same index in both this SELECT and SetDetails.
                   s.language         AS Language
            FROM beatmapsets s
            JOIN users u ON u.id = s.owner_id
            WHERE s.id = @id
            """,
            new { id, viewerId = CurrentUser?.Id ?? 0 });

        bool isOwner = CurrentUser?.Id == set?.OwnerId;
        bool isAdmin = CurrentUser?.IsAdmin == true;

        // Restricted mappers' sets 404 like their profiles do (owner exempt; in practice a
        // restricted owner cannot sign in, but the rule is symmetrical with the listings).
        if (set is null
            || (set.OwnerRestricted && !isOwner)
            || (set.Status == "removed" && !isOwner && !isAdmin)
            || (set.Status == "hidden" && !isOwner))
        {
            return NotFound();
        }

        Set = set;

        // Every difficulty in the set, hardest first. The stats box + leaderboard show one at a
        // time: the ?diff= beatmap when it belongs to this set, otherwise the hardest.
        Diffs = (await conn.QueryAsync<DiffStats>(
            """
            SELECT b.id                    AS Id,
                   b.version_name          AS Name,
                   b.total_length_s        AS TotalLengthS,
                   b.word_count            AS WordCount,
                   b.char_count            AS CharCount,
                   b.wpm::double precision AS Wpm,
                   b.difficulty_rating     AS Stars,
                   b.lyrics                AS Lyrics,
                   -- 028_wpm_curve.sql. NULL on any row the pace backfill has not reached, and on
                   -- any map too short to measure; DiffStats.HasCurve folds the two together.
                   b.peak_wpm              AS PeakWpm,
                   b.peak_cpm              AS PeakCpm,
                   -- 033_target_wpm.sql; NULL only until the v18 sweep fills it, since a per-line
                   -- figure needs no rolling window the way the two above do.
                   b.target_wpm            AS TargetWpm,
                   b.wpm_curve             AS WpmCurve,
                   -- 037_lyric_font.sql. NULL means "no font chosen", which hides the row; the
                   -- name is informational (only the desktop client renders the font).
                   b.lyric_font            AS LyricFont,
                   -- 039_lyrics_original.sql: the same lyrics in their original script, line for
                   -- line, or '' when the difficulty carries none (backlog 332).
                   b.lyrics_original       AS LyricsOriginal
            FROM beatmaps b
            WHERE b.set_id = @id AND b.filename IS NOT NULL
            ORDER BY b.difficulty_rating DESC, b.id ASC
            """,
            new { id })).ToList();

        Diff = Diffs.FirstOrDefault(d => d.Id == diff) ?? Diffs.FirstOrDefault();
        requestedDiff = diff != 0 && Diff?.Id == diff ? diff : 0;

        // Global leaderboard: best ranked+passed score per user across the set's difficulties
        // (the same eligibility and ordering the game-facing endpoint in ScoreEndpoints reads,
        // both built from BeatmapLeaderboard so they cannot drift). Only ranked
        // sets have one; pending plays are stored unranked, and the page renders an "unlocks
        // when ranked" note instead, so don't even run the query for non-ranked sets.
        // Per-difficulty leaderboard: best passed score per user on the SELECTED beatmap (each
        // difficulty has its own board). @wantRanked picks the ranked board (default) or the
        // Unranked board: passed plays stored ranked=false because they used an unranked mod or a
        // non-default rate. Only ranked sets have boards; pending/unranked sets show a note instead.
        bool wantRanked = Board == "ranked";
        if (Set.Status == "ranked" && Diff is not null)
            Scores = (await conn.QueryAsync<ScoreRow>(
            $"""
            SELECT best.id           AS ScoreId,
                   best.user_id      AS UserId,
                   best.username     AS Username,
                   best.avatar_key   AS AvatarKey,
                   best.total_score  AS TotalScore,
                   best.accuracy     AS Accuracy,
                   best.completion   AS Completion,
                   best.max_combo    AS MaxCombo,
                   best.rank         AS Rank,
                   best.ended_at     AS EndedAt,
                   best.mods         AS ModsJson,
                   best.statistics   AS StatisticsJson,
                   best.has_replay   AS HasReplay,
                   best.country_code AS CountryCode
            FROM (
                SELECT DISTINCT ON (sc.user_id)
                       sc.id, sc.user_id, sc.total_score, sc.accuracy, sc.completion, sc.max_combo, sc.rank,
                       sc.ended_at, sc.mods::text AS mods, sc.statistics::text AS statistics,
                       sc.replay_key IS NOT NULL AS has_replay,
                       u.username::text AS username, u.avatar_key, u.country_code::text AS country_code
                FROM scores sc
                JOIN users u ON u.id = sc.user_id
                WHERE sc.beatmap_id = @beatmapId AND {BeatmapLeaderboard.OnBoard("sc")}
                ORDER BY sc.user_id, {BeatmapLeaderboard.Order("sc")}
            ) best
            ORDER BY {BeatmapLeaderboard.Order("best")}
            LIMIT 50
            """,
                new { beatmapId = Diff.Id, wantRanked })).ToList();

        // The comments section (below the lyrics): live rows by non-delisted authors, oldest
        // first, keyset-paged. PageSize + 1 sentinel tells us whether a show-more link renders.
        CommentCount = await BeatmapsetComments.CountAsync(conn, id, HttpContext.RequestAborted);

        var comments = await BeatmapsetComments.ListAsync(
            conn, id, comments_after, BeatmapsetComments.PageSize + 1, HttpContext.RequestAborted);

        HasMoreComments = comments.Count > BeatmapsetComments.PageSize;
        if (HasMoreComments)
            comments.RemoveAt(BeatmapsetComments.PageSize);
        Comments = comments;

        ViewData["Title"] = $"{Set.Artist} - {Set.Title}";
        ViewData["MetaDescription"] =
            $"{Set.Artist}: {Set.Title}, mapped by {Set.Creator}. Type it in type!beat.";
        if (Set.CoverKey is not null)
            ViewData["OgImage"] = $"{Request.Scheme}://{Request.Host}/{Set.CoverKey}/cover.jpg";

        return Page();
    }

    public async Task<IActionResult> OnPostFavouriteAsync(long id, string? returnUrl)
    {
        if (CurrentUser is null)
            return Redirect("/login");

        await using var conn = await db.OpenAsync(HttpContext.RequestAborted);

        string? status = await conn.ExecuteScalarAsync<string?>(
            "SELECT status FROM beatmapsets WHERE id = @id", new { id });

        if (status is null or "removed")
            return NotFound();

        await using var tx = await conn.BeginTransactionAsync(HttpContext.RequestAborted);

        int deleted = await conn.ExecuteAsync(
            "DELETE FROM favourites WHERE user_id = @userId AND set_id = @id",
            new { userId = CurrentUser.Id, id }, tx);

        if (deleted > 0)
        {
            await conn.ExecuteAsync(
                "UPDATE beatmapsets SET favourite_count = greatest(favourite_count - 1, 0) WHERE id = @id",
                new { id }, tx);
        }
        else
        {
            int inserted = await conn.ExecuteAsync(
                "INSERT INTO favourites (user_id, set_id) VALUES (@userId, @id) ON CONFLICT DO NOTHING",
                new { userId = CurrentUser.Id, id }, tx);

            if (inserted > 0)
            {
                await conn.ExecuteAsync(
                    "UPDATE beatmapsets SET favourite_count = favourite_count + 1 WHERE id = @id",
                    new { id }, tx);
            }
        }

        await tx.CommitAsync(HttpContext.RequestAborted);

        // A fetch()-driven toggle (the card grids) gets JSON back so the page never reloads or
        // jumps to the top; a plain form submit still redirects for no-JS fallback.
        if (string.Equals(Request.Headers["X-Requested-With"], "fetch", StringComparison.Ordinal))
        {
            bool favourited = deleted == 0; // deleted a row → now off; else we inserted → now on
            int count = await conn.ExecuteScalarAsync<int>(
                "SELECT favourite_count FROM beatmapsets WHERE id = @id", new { id });
            return new JsonResult(new { favourited, count });
        }

        return Redirect(Url.IsLocalUrl(returnUrl) ? returnUrl : $"/beatmapsets/{id}");
    }

    public async Task<IActionResult> OnPostReportAsync(long id, string? reason)
    {
        reason = reason?.Trim() ?? string.Empty;

        if (reason.Length == 0)
            return Redirect($"/beatmapsets/{id}");
        if (reason.Length > max_report_reason_length)
            reason = reason[..max_report_reason_length];

        await using var conn = await db.OpenAsync(HttpContext.RequestAborted);

        bool exists = await conn.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM beatmapsets WHERE id = @id AND status <> 'removed')", new { id });

        if (!exists)
            return NotFound();

        // reporter_id is nullable by design (001_init.sql): anonymous reports are accepted.
        await conn.ExecuteAsync(
            "INSERT INTO reports (set_id, reporter_id, reason) VALUES (@id, @reporterId, @reason)",
            new { id, reporterId = CurrentUser?.Id, reason });

        // "true" not "1": the bool handler parameter binds via the default TypeConverter,
        // which rejects numeric strings.
        return Redirect($"/beatmapsets/{id}?reported=true");
    }

    // ---- description (owner or reviewer) ----

    /// <summary>
    /// Saves the set's plain-text description. Permission IS the UPDATE's WHERE clause
    /// (owner or map reviewer): an unpermitted or nonexistent target matches no row and answers
    /// NotFound, the page's convention (the custom cookie auth has no scheme for Forbid).
    /// Over-budget text is REJECTED, not truncated (the Settings bio rule: silently cutting
    /// someone's words is worse than making them shorten). updated_at is deliberately NOT
    /// bumped: the listing sorts on it, and a free re-newest lever on a text edit is the abuse
    /// vector the Discord feed already refuses to be (BuddyEndpoints).
    /// </summary>
    public async Task<IActionResult> OnPostDescriptionAsync(long id, string? description)
    {
        if (CurrentUser is null)
            return Redirect("/login");

        description = (description ?? string.Empty).Trim();

        if (description.Length > Settings.IndexModel.MaxDescriptionLength)
            return Redirect($"/beatmapsets/{id}?description=long");

        await using var conn = await db.OpenAsync(HttpContext.RequestAborted);

        int changed = await conn.ExecuteAsync(
            """
            UPDATE beatmapsets
            SET description = @description
            WHERE id = @id AND (owner_id = @uid OR @canReview)
            """,
            new { id, description, uid = CurrentUser.Id, canReview = CurrentUser.CanReviewMaps });

        if (changed == 0)
            return NotFound();

        return Redirect($"/beatmapsets/{id}?saved=description");
    }

    // ---- comments ----

    /// <summary>
    /// Posts one comment: any signed-in user (a restricted account can never reach here, the
    /// cookie middleware treats it as signed out), on a set that is visible and not removed
    /// (the favourite handler's status check). The insert and its owner notification share one
    /// transaction (<see cref="BeatmapsetComments.PostAsync"/>). No progressive enhancement:
    /// a comment post legitimately reloads the page.
    /// </summary>
    public async Task<IActionResult> OnPostCommentAsync(long id, string? body)
    {
        if (CurrentUser is null)
            return Redirect("/login");

        string normalized = BeatmapsetComments.Normalize(body);

        if (normalized.Length == 0)
            return Redirect($"/beatmapsets/{id}#comments");
        if (normalized.Length > BeatmapsetComments.MaxBodyLength)
            return Redirect($"/beatmapsets/{id}?comment=long#comments");

        await using var conn = await db.OpenAsync(HttpContext.RequestAborted);

        string? status = await conn.ExecuteScalarAsync<string?>(
            "SELECT status FROM beatmapsets WHERE id = @id", new { id });

        if (status is null or "removed")
            return NotFound();

        // After the checks, so a 404 or an over-long refusal never burns comment budget.
        if (!comment_posts.Allow(CurrentUser.Id.ToString(CultureInfo.InvariantCulture)))
            return Redirect($"/beatmapsets/{id}?comment=slow#comments");

        await BeatmapsetComments.PostAsync(conn, id, CurrentUser.Id, normalized, HttpContext.RequestAborted);

        return Redirect($"/beatmapsets/{id}#comments");
    }

    /// <summary>
    /// Soft-deletes one comment. Permission is <see cref="BeatmapsetComments.SoftDeleteAsync"/>'s
    /// WHERE clause (author, set owner, or reviewer); no row means NotFound, whether the comment
    /// never existed, is already gone, or is simply not the caller's to remove
    /// (indistinguishable on purpose, the pin handlers' rule). A REVIEWER removing somebody
    /// else's comment on somebody else's set is moderation and writes the audit row, best-effort
    /// like the rank/unrank audit; deleting your own comment, or tidying your own set's thread,
    /// is not.
    /// </summary>
    public async Task<IActionResult> OnPostDeleteCommentAsync(long id, long commentId)
    {
        if (CurrentUser is null)
            return Redirect("/login");

        await using var conn = await db.OpenAsync(HttpContext.RequestAborted);

        var deleted = await BeatmapsetComments.SoftDeleteAsync(
            conn, id, commentId, CurrentUser.Id, CurrentUser.CanReviewMaps, HttpContext.RequestAborted);

        if (deleted is null)
            return NotFound();

        if (CurrentUser.CanReviewMaps && deleted.AuthorId != CurrentUser.Id && deleted.OwnerId != CurrentUser.Id)
        {
            try
            {
                await conn.ExecuteAsync(
                    """
                    INSERT INTO moderation_actions (actor_id, set_id, action, note)
                    VALUES (@actorId, @id, 'comment_delete', @note)
                    """,
                    new { actorId = CurrentUser.Id, id, note = $"comment {commentId}" });
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Comment {CommentId} on set {SetId} was removed but the audit row failed.", commentId, id);
            }
        }

        return Redirect($"/beatmapsets/{id}#comments");
    }

    // ---- reviewer controls (map_reviewer or admin only) ----

    public Task<IActionResult> OnPostRankAsync(long id) => transitionAsync(id, from: "pending", to: "ranked");

    public Task<IActionResult> OnPostUnrankAsync(long id) => transitionAsync(id, from: "ranked", to: "pending");

    /// <summary>
    /// The only two review transitions are pending → ranked and ranked → pending; hidden and
    /// removed sets are untouchable from here (takedowns stay an admin-SQL lever). 404 for
    /// non-reviewers, and for a reviewer acting on their OWN set; the same nothing-to-see answer
    /// the buttons' absence gives them (the site's custom cookie auth has no ASP.NET
    /// authentication scheme for Forbid()).
    /// </summary>
    private async Task<IActionResult> transitionAsync(long id, string from, string to)
    {
        if (CurrentUser?.CanReviewMaps != true)
            return NotFound();

        await using var conn = await db.OpenAsync(HttpContext.RequestAborted);

        // Review is someone else's judgement: a map_reviewer may not rank or unrank a set they
        // own. The exemption is by ROLE and not by name, so an administrator (who can already
        // reach every other moderation lever) may still flip their own set.
        // This lookup doubles as the existence check the changed == 0 branch below used to run
        // as a second query: owner_id is NOT NULL, so a null here means there is no such set.
        long? ownerId = await conn.ExecuteScalarAsync<long?>(
            "SELECT owner_id FROM beatmapsets WHERE id = @id", new { id });

        if (ownerId is null || (ownerId == CurrentUser.Id && !CurrentUser.IsAdmin))
            return NotFound();

        int changed = await conn.ExecuteAsync(
            "UPDATE beatmapsets SET status = @to, updated_at = now() WHERE id = @id AND status = @from",
            new { id, from, to });

        if (changed == 1)
        {
            // Record the review in the audit table. Until now nothing wrote here, so there was no
            // way to ask "which sets were ranked since X": beatmapsets carries only updated_at,
            // which any edit bumps. This row IS that history, and its monotonic id is what the
            // Discord bot's ranked-map feed cursors over (BuddyEndpoints), the same way the score
            // feed cursors over scores.id. Best-effort: the transition itself already committed,
            // and a failed audit insert must not 500 a successful rank.
            try
            {
                await conn.ExecuteAsync(
                    """
                    INSERT INTO moderation_actions (actor_id, set_id, action, note)
                    VALUES (@actorId, @id, @action, @note)
                    """,
                    new { actorId = CurrentUser.Id, id, action = to == "ranked" ? "rank" : "unrank", note = $"{from} -> {to}" });
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Set {SetId} transitioned {From}->{To} but the audit row failed.", id, from, to);
            }
        }

        // Wrong-state POSTs (double-submit, stale tab) are benign: land back on the page, which
        // shows the current state. A nonexistent set is a real 404, already answered above.
        return Redirect($"/beatmapsets/{id}");
    }

    // ---- display helpers ----

    public static string FormatLength(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    }

    /// <summary>0..1 → "97.53%"; used for accuracy AND completion (both are stored as fractions).</summary>
    public static string FormatPercent(double fraction)
        => (fraction * 100).ToString("0.00", CultureInfo.InvariantCulture) + "%";

    /// <summary>The client's ScoreRank strings; X is the perfect rank, shown as SS like the game.
    /// Delegates to <see cref="GradeDisplay"/>, the one mapping every grade surface reads.</summary>
    public static string GradeLabel(string rank) => GradeDisplay.Label(rank);

    public static string GradeClass(string rank) => GradeDisplay.CssClass(rank);

    public sealed record SetDetails(
        long Id, string Title, string Artist, string? TitleUnicode, string? ArtistUnicode, string Source, string Tags, string Description,
        string Status, bool Explicit, string Creator, bool OwnerRestricted, long OwnerId, string? CoverKey, string? PreviewUrl,
        int PlayCount, int FavouriteCount, int DownloadCount,
        DateTime SubmittedAt, DateTime UpdatedAt, bool IsFavourited, bool HasPackage, string Language)
    {
        public string StatusLabel => BeatmapsetDisplay.StatusLabel(Status);
        public string PillClass => BeatmapsetDisplay.PillClass(Status);
        public string? CoverUrl => CoverKey is null ? null : $"/{CoverKey}/cover.jpg";
        public IEnumerable<string> TagList =>
            Tags.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct();

        /// <summary>Display casing of the stored language, or null when the set has none yet
        /// (019_language.sql), in which case the page renders no language chip at all.</summary>
        public string? LanguageDisplay => BeatmapLanguages.DisplayName(Language);

        /// <summary>Title, or its original non-romanized text when the viewer prefers that.</summary>
        public string DisplayTitle(bool preferOriginal) => MetadataDisplay.Pick(Title, TitleUnicode, preferOriginal);

        /// <summary>Artist, or its original non-romanized text when the viewer prefers that.</summary>
        public string DisplayArtist(bool preferOriginal) => MetadataDisplay.Pick(Artist, ArtistUnicode, preferOriginal);

        /// <summary>The BCP 47 tag of the song's language, or null (see <see cref="BeatmapLanguages.LangTag"/>).</summary>
        public string? LangTag => BeatmapLanguages.LangTag(Language);

        /// <summary>
        /// The tag for the ROMANISED lyrics: the language in Latin script (<c>ja-Latn</c>,
        /// <c>ru-Latn</c>) for a language that is not written in it, so a screen reader keeps its
        /// Japanese or Russian voice and still reads the romaji as the transliteration it is, and
        /// the bare tag for one that already is (<c>en</c>, <c>fr</c>).
        /// </summary>
        public string? RomanisedLangTag => BeatmapLanguages.RomanisedLangTag(Language);
    }

    /// <summary>One difficulty's stats. <see cref="Lyrics"/> is the stored per-difficulty lyric
    /// text (one lyric line per '\n', author's casing; ParsedDifficulty.LyricsText), empty when
    /// unknown (blank map, or the v8 backfill has not reached the row), which hides the lyrics
    /// section. <see cref="WpmCurve"/> and the two peaks come from 028_wpm_curve.sql and are null
    /// together, on the same terms: either the v11 backfill has not reached this row yet, or the
    /// map is too short for LyricWpmCurve to measure. Both mean "no graph", so the page tests
    /// <see cref="HasPaceCurve"/> and shows a note instead.</summary>
    /// <remarks>
    /// PROPERTIES, not a positional record like <see cref="SetDetails"/> above, and the array is
    /// why: Dapper matches a positional record's constructor by comparing each parameter's type
    /// against the reader's, and Npgsql reports an array column's type as the bare
    /// <see cref="Array"/> rather than <c>float[]</c>, so no constructor ever matches and the query
    /// throws. Its property path is the tolerant one. The upside is that this record binds BY NAME,
    /// so a new column can be added anywhere in the SELECT.
    /// </remarks>
    public sealed record DiffStats
    {
        public long Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public double TotalLengthS { get; init; }
        public int? WordCount { get; init; }
        public int? CharCount { get; init; }
        public double? Wpm { get; init; }
        public double Stars { get; init; }
        public string Lyrics { get; init; } = string.Empty;
        public double? PeakWpm { get; init; }
        public double? PeakCpm { get; init; }

        /// <summary>
        /// The pace to sustain: the average WPM across the fastest fifth of the map's lyric lines of
        /// at least three words (033_target_wpm.sql, LyricPace v18, the eligibility floor added in
        /// v20). NULL until the v18 sweep reaches the row, so the
        /// page drops it rather than printing a fabricated 0, exactly as it does for the peaks.
        /// </summary>
        public double? TargetWpm { get; init; }

        public float[]? WpmCurve { get; init; }

        /// <summary>
        /// The mapper-chosen lyric font family (037_lyric_font.sql, backlog 291), or null when the
        /// difficulty names none, which hides the stats row entirely. Display only: the desktop
        /// client is what renders the font; the browser player deliberately does not (its layout
        /// runs on JetBrains Mono's fixed advance).
        /// </summary>
        public string? LyricFont { get; init; }

        /// <summary>
        /// The lyrics in their ORIGINAL SCRIPT (039_lyrics_original.sql, backlog 332), aligned line
        /// for line with <see cref="Lyrics"/>, or empty when the difficulty carries none. A
        /// difficulty with originals is one the desktop client's local-only Polyglot mod can play.
        /// </summary>
        public string LyricsOriginal { get; init; } = string.Empty;

        /// <summary>Whether this difficulty carries originals, so Polyglot is available on it.</summary>
        public bool HasOriginals => LyricsOriginal.Length > 0;

        /// <summary>
        /// The lyric lines paired with their originals, one entry per stored line of
        /// <see cref="Lyrics"/>; <c>Original</c> is null on a line that has none. Built from the
        /// two aligned columns, so a stale or hand-edited original column that has more lines than
        /// the lyrics is cut to the lyrics, and one with fewer leaves the tail without originals,
        /// rather than shifting a line onto the wrong translation.
        /// </summary>
        public IReadOnlyList<(string Romanised, string? Original)> LyricLines
        {
            get
            {
                string[] romanised = Lyrics.Split('\n');
                string[] originals = HasOriginals ? LyricsOriginal.Split('\n') : [];

                return romanised
                    .Select((line, i) => (line, i < originals.Length && originals[i].Trim().Length > 0 ? originals[i] : null))
                    .ToList();
            }
        }

        /// <summary>A non-zero bar is never invisible, however small it is next to the peak
        /// (<c>BarChartModel</c>'s min_visible_height, in the percentage units used here).</summary>
        private const double min_visible_percent = 3;

        private IReadOnlyList<PaceBar>? paceBars;

        /// <summary>True when there is something to plot: at least one bar above zero.</summary>
        public bool HasPaceCurve => WpmCurve is { Length: > 0 } curve && curve.Any(v => v > 0);

        /// <summary>
        /// Typeable cells per word over the whole difficulty, the same quantity as
        /// <c>LyricPace.PaceStatistics.AverageCharsPerWord</c> and computed the same way, from the
        /// two counts already on the row: NO COLUMN OF ITS OWN, because there is nothing here that
        /// <c>char_count</c> and <c>word_count</c> do not already say.
        ///
        /// <para>It sits beside Average WPM on the page because it is what turns that number into
        /// something a reader can interpret: WPM is CPM/5 flat since LyricPace v15, so this says how
        /// far the map's own words are from the 5 the unit assumes. Null (row omitted) when either
        /// count is missing or the map has no words, so the page never prints a NaN or a fabricated
        /// 0, exactly as the Words and Characters rows above it already behave.</para>
        /// </summary>
        public double? AverageCharsPerWord =>
            WordCount is int words and > 0 && CharCount is int chars ? (double)chars / words : null;

        /// <summary>
        /// The stored curve laid out for the graph: one bar per point, each a percentage of the
        /// plot's height, tallest bar at 100. Computed here rather than in the .cshtml because
        /// Razor is a poor place for arithmetic (no test can reach it, and every expression has to
        /// fight the request's culture), the same split <c>BarChartModel</c> makes.
        /// </summary>
        public IReadOnlyList<PaceBar> PaceBars => paceBars ??= layOutPace();

        private IReadOnlyList<PaceBar> layOutPace()
        {
            if (WpmCurve is not { Length: > 0 } curve)
                return [];

            float peak = curve.Max();

            if (peak <= 0)
                return [];

            // Ties go to the FIRST occurrence, as in BarChartModel: with one bar to highlight, the
            // earlier moment is the one a reader is less likely to infer from context.
            int peakIndex = Array.IndexOf(curve, peak);

            var bars = new List<PaceBar>(curve.Length);

            for (int i = 0; i < curve.Length; i++)
            {
                double value = curve[i];

                bars.Add(new PaceBar(
                    HeightPercent: value <= 0 ? 0 : Math.Max(min_visible_percent, value / peak * 100),
                    IsPeak: i == peakIndex,
                    Label: value <= 0
                        ? "no window here"
                        : value.ToString("0", CultureInfo.InvariantCulture) + " WPM"));
            }

            return bars;
        }
    }

    /// <summary>One bar of the WPM graph, in percent of the plot height (0 for an empty bucket).</summary>
    public sealed record PaceBar(double HeightPercent, bool IsPeak, string Label)
    {
        /// <summary>A bucket no rolling window starts in. Drawn as a baseline stub by CSS, so it
        /// carries no inline height at all and can never be misread as a small value.</summary>
        public bool IsEmpty => HeightPercent <= 0;

        /// <summary>Invariant CSS length, never the request's culture ("12,5%" is not a length).</summary>
        public string HeightCss => HeightPercent.ToString("0.##", CultureInfo.InvariantCulture) + "%";
    }

    /// <summary>
    /// One leaderboard row. Judgement counts come from the statistics jsonb (wire keys
    /// great/ok/meh/miss), and the engine's own tier names are the same words, so the labels are
    /// the keys (see the fork's TypeBeatJudgements.cs). Completion (% of the map typed) is the
    /// metric the grade is awarded on; accuracy remains the timing-quality metric.
    /// </summary>
    public sealed record ScoreRow(
        long ScoreId, long UserId, string Username, string? AvatarKey, long TotalScore, double Accuracy, double Completion,
        int MaxCombo, string Rank, DateTime EndedAt, string ModsJson, string StatisticsJson, bool HasReplay, string CountryCode)
    {
        /// <summary>Uploaded avatar, or null → the initial-letter fallback.</summary>
        public string? AvatarUrl => AvatarKey is null ? null : $"/{AvatarKey}";

        private JObject? statistics;
        private JObject Statistics => statistics ??= JObject.Parse(string.IsNullOrEmpty(StatisticsJson) ? "{}" : StatisticsJson);

        /// <summary>
        /// The top quality tier. It counts <c>perfect</c> as well as <c>great</c>, and that is the
        /// one place on this page that has to know backlog 133's four-tier ladder SHIPPED: a row
        /// stored in that window carries a <c>perfect</c> key nothing else here reads, so leaving it
        /// out would print a row whose tier counts do not add up to the map. Only such a row can
        /// have one, so for every play judged under today's three tiers this is exactly
        /// <c>great</c>.
        /// </summary>
        public int Great => (Statistics.Value<int?>("perfect") ?? 0) + (Statistics.Value<int?>("great") ?? 0);

        public int Ok => Statistics.Value<int?>("ok") ?? 0;
        public int Meh => Statistics.Value<int?>("meh") ?? 0;

        /// <summary>
        /// Characters the play did not type right, or null for a play with none: cells the song
        /// scrolled past untyped PLUS cells left holding a wrong character (the <c>good</c> key),
        /// folded together by <see cref="JudgementDisplay.MissColumn"/> since backlog 213. Nullable,
        /// unlike the quality tiers above, so that it renders BLANK at zero the way
        /// <see cref="Typos"/> beside it always has (backlog 140): a clean run showing "0 misses"
        /// next to an empty typo cell read as two different kinds of nothing.
        ///
        /// <para>With the fold, <see cref="Great"/> + <see cref="Ok"/> + <see cref="Meh"/> + this
        /// column sum to the map's judged cell count, which they had not done since backlog 124 gave
        /// the uncorrected typo a key nothing displayed.</para>
        /// </summary>
        public int? Miss => JudgementDisplay.MissColumn(Statistics);

        /// <summary>
        /// TYPOS: wrong KEYPRESSES, one per press, carried on the wire under the <c>combo_break</c>
        /// key (backlog 72, renamed to the player's vocabulary by 140), or null when this play does
        /// not CARRY the stat. Absence is not zero: every score submitted before the key existed
        /// simply has none, and a clean play has none either (client and browser both omit
        /// zero-valued entries), so the column appears only once some row on this board has one and
        /// an old play never renders a fabricated clean run.
        ///
        /// <para>This is the ONE typo number the site shows (backlog 140). The other one used to sit
        /// beside it: the count of cells left holding a wrong character at the seal, the <c>good</c>
        /// key. Every such cell implied a wrong keypress, so this event count already covers it, and
        /// the seal-state count stopped being a surfaced statistic on both sides at once. Since
        /// backlog 213 that cell count is not invisible either: it is in <see cref="Miss"/>, where
        /// the character the player never typed right belongs.</para>
        ///
        /// <para>NOT ITSELF FOLDED, and a different statement from the one beside it: this counts
        /// wrong KEYPRESSES as events, the corrected ones included, where <see cref="Miss"/> counts
        /// CELLS. pp does subtract the uncorrected ones from its typo term so that one flub is
        /// priced by exactly one term (<see cref="PerformancePoints.CountNotes"/>), but that is
        /// pricing and this is accounting: a player who mistyped nine characters and fixed five made
        /// nine mistakes.</para>
        /// </summary>
        public int? Typos => Statistics.Value<int?>("combo_break");

        /// <summary>
        /// Mod badges from the mods jsonb ([{acronym, settings}] wire shape), each carrying the
        /// track rate when it is a rate mod (see <see cref="ScoreMods.Parse"/>).
        /// </summary>
        public IReadOnlyList<ScoreMod> Mods => mods ??= ScoreMods.Parse(ModsJson);

        private IReadOnlyList<ScoreMod>? mods;
    }
}
