using System.Globalization;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using Typebeat.Web.Data;
using Typebeat.Web.Packages;

namespace Typebeat.Web.Pages.Beatmapsets;

/// <summary>
/// Beatmapset page (/beatmapsets/{id}): cover header with scrim, stats box, plain-text
/// description, tags, the global leaderboard (top 50 best-per-user, podium for #1),
/// rendered only on 'ranked' sets; anything else shows a "unlocks when ranked" note instead;
/// and, below the leaderboard, the selected difficulty's lyrics (beatmaps.lyrics, hidden
/// when empty).
/// POST handlers: Favourite (toggle + denormalized counter bump), Report (reports table),
/// and the reviewer-only Rank/Unrank pair (pending ⇄ ranked, nothing else).
/// Hidden sets are visible to their owner only. Removed sets 404 for the public but stay
/// viewable by their owner and by admins (the owner's profile deliberately lists them, and a
/// DMCA'd mapper deserves to see the Removed pill instead of a dead link; the download
/// endpoint already granted the owner the same access).
/// </summary>
public sealed class SetModel(Db db) : TypebeatPageModel
{
    private const int max_report_reason_length = 4000;

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

    public async Task<IActionResult> OnGetAsync(long id, bool reported = false, long diff = 0, string board = "ranked")
    {
        Reported = reported;
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
                   s.bpm::double precision AS Bpm,
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
                   b.lyrics                AS Lyrics
            FROM beatmaps b
            WHERE b.set_id = @id AND b.filename IS NOT NULL
            ORDER BY b.difficulty_rating DESC, b.id ASC
            """,
            new { id })).ToList();

        Diff = Diffs.FirstOrDefault(d => d.Id == diff) ?? Diffs.FirstOrDefault();

        // Global leaderboard: best ranked+passed score per user across the set's difficulties
        // (same DISTINCT ON shape as the game-facing endpoint in ScoreEndpoints). Only ranked
        // sets have one; pending plays are stored unranked, and the page renders an "unlocks
        // when ranked" note instead, so don't even run the query for non-ranked sets.
        // Per-difficulty leaderboard: best passed score per user on the SELECTED beatmap (each
        // difficulty has its own board). @wantRanked picks the ranked board (default) or the
        // Unranked board: passed plays stored ranked=false because they used an unranked mod or a
        // non-default rate. Only ranked sets have boards; pending/unranked sets show a note instead.
        bool wantRanked = Board == "ranked";
        if (Set.Status == "ranked" && Diff is not null)
            Scores = (await conn.QueryAsync<ScoreRow>(
            """
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
                   best.has_replay   AS HasReplay
            FROM (
                SELECT DISTINCT ON (sc.user_id)
                       sc.id, sc.user_id, sc.total_score, sc.accuracy, sc.completion, sc.max_combo, sc.rank,
                       sc.ended_at, sc.mods::text AS mods, sc.statistics::text AS statistics,
                       sc.replay_key IS NOT NULL AS has_replay,
                       u.username::text AS username, u.avatar_key
                FROM scores sc
                JOIN users u ON u.id = sc.user_id
                WHERE sc.beatmap_id = @beatmapId AND sc.passed AND sc.ranked = @wantRanked
                ORDER BY sc.user_id, sc.total_score DESC, sc.id ASC
            ) best
            ORDER BY best.total_score DESC, best.id ASC
            LIMIT 50
            """,
                new { beatmapId = Diff.Id, wantRanked })).ToList();

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

    // ---- reviewer controls (map_reviewer or admin only) ----

    public Task<IActionResult> OnPostRankAsync(long id) => transitionAsync(id, from: "pending", to: "ranked");

    public Task<IActionResult> OnPostUnrankAsync(long id) => transitionAsync(id, from: "ranked", to: "pending");

    /// <summary>
    /// The only two review transitions are pending → ranked and ranked → pending; hidden and
    /// removed sets are untouchable from here (takedowns stay an admin-SQL lever). 404 for
    /// non-reviewers; the same nothing-to-see answer the buttons' absence gives them (the
    /// site's custom cookie auth has no ASP.NET authentication scheme for Forbid()).
    /// </summary>
    private async Task<IActionResult> transitionAsync(long id, string from, string to)
    {
        if (CurrentUser?.CanReviewMaps != true)
            return NotFound();

        await using var conn = await db.OpenAsync(HttpContext.RequestAborted);

        int changed = await conn.ExecuteAsync(
            "UPDATE beatmapsets SET status = @to, updated_at = now() WHERE id = @id AND status = @from",
            new { id, from, to });

        if (changed == 0)
        {
            // Wrong-state POSTs (double-submit, stale tab) are benign: land back on the page,
            // which shows the current state. Only a nonexistent set is a real 404.
            bool exists = await conn.ExecuteScalarAsync<bool>(
                "SELECT EXISTS (SELECT 1 FROM beatmapsets WHERE id = @id)", new { id });

            if (!exists)
                return NotFound();
        }

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

    /// <summary>The client's ScoreRank strings; X is the perfect rank, shown as SS like the game.</summary>
    public static string GradeLabel(string rank) => rank == "X" ? "SS" : rank;

    public static string GradeClass(string rank) => "grade--" + (rank == "X" ? "ss" : rank.ToLowerInvariant());

    public sealed record SetDetails(
        long Id, string Title, string Artist, string? TitleUnicode, string? ArtistUnicode, string Source, string Tags, string Description,
        string Status, bool Explicit, string Creator, bool OwnerRestricted, long OwnerId, string? CoverKey, string? PreviewUrl,
        int PlayCount, int FavouriteCount, int DownloadCount, double? Bpm,
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
    }

    /// <summary><paramref name="Lyrics"/> is the stored per-difficulty lyric text (one lyric
    /// line per '\n', author's casing; ParsedDifficulty.LyricsText), empty when unknown (blank
    /// map, or the v8 backfill has not reached the row), which hides the lyrics section.</summary>
    public sealed record DiffStats(long Id, string Name, double TotalLengthS, int? WordCount, int? CharCount, double? Wpm, double Stars, string Lyrics);

    /// <summary>
    /// One leaderboard row. Judgement counts come from the statistics jsonb (wire keys
    /// great/ok/meh/miss) but are DISPLAYED with the game engine's own judgement names:
    /// Perfect/Good/Ok/Miss per the mapping in the fork's TypeBeatJudgements.cs
    /// ("Perfect->Great, Good->Ok, Ok->Meh, ...->Miss"). Completion (% of the map typed) is
    /// the metric the grade is awarded on; accuracy remains the timing-quality metric.
    /// </summary>
    public sealed record ScoreRow(
        long ScoreId, long UserId, string Username, string? AvatarKey, long TotalScore, double Accuracy, double Completion,
        int MaxCombo, string Rank, DateTime EndedAt, string ModsJson, string StatisticsJson, bool HasReplay)
    {
        /// <summary>Uploaded avatar, or null → the initial-letter fallback.</summary>
        public string? AvatarUrl => AvatarKey is null ? null : $"/{AvatarKey}";

        private JObject? statistics;
        private JObject Statistics => statistics ??= JObject.Parse(string.IsNullOrEmpty(StatisticsJson) ? "{}" : StatisticsJson);

        public int Perfect => Statistics.Value<int?>("great") ?? 0;
        public int Good => Statistics.Value<int?>("ok") ?? 0;
        public int Ok => Statistics.Value<int?>("meh") ?? 0;
        public int Miss => Statistics.Value<int?>("miss") ?? 0;

        /// <summary>
        /// Mod badges from the mods jsonb ([{acronym, settings}] wire shape), each carrying the
        /// track rate when it is a rate mod (see <see cref="ScoreMods.Parse"/>).
        /// </summary>
        public IReadOnlyList<ScoreMod> Mods => mods ??= ScoreMods.Parse(ModsJson);

        private IReadOnlyList<ScoreMod>? mods;
    }
}
