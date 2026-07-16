using System.Globalization;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using Typebeat.Web.Data;

namespace Typebeat.Web.Pages.Beatmapsets;

/// <summary>
/// Beatmapset page (/beatmapsets/{id}): cover header with scrim, stats box, plain-text
/// description, tags, and the global leaderboard (top 50 best-per-user, podium for #1).
/// POST handlers: Favourite (toggle + denormalized counter bump) and Report (reports table).
/// Hidden sets are visible to their owner only; removed sets are gone (404).
/// </summary>
public sealed class SetModel(Db db) : TypebeatPageModel
{
    private const int max_report_reason_length = 4000;

    public SetDetails Set { get; private set; } = null!;
    public DiffStats? Diff { get; private set; }
    public IReadOnlyList<ScoreRow> Scores { get; private set; } = [];

    /// <summary>Set by the post-report redirect (?reported=1) to swap the form for a thanks note.</summary>
    public bool Reported { get; private set; }

    public async Task<IActionResult> OnGetAsync(long id, bool reported = false)
    {
        Reported = reported;

        await using var conn = await db.OpenAsync(HttpContext.RequestAborted);

        var set = await conn.QuerySingleOrDefaultAsync<SetDetails>(
            """
            SELECT s.id               AS Id,
                   s.title            AS Title,
                   s.artist           AS Artist,
                   s.source           AS Source,
                   s.tags             AS Tags,
                   s.description      AS Description,
                   s.status           AS Status,
                   u.username::text   AS Creator,
                   s.owner_id         AS OwnerId,
                   s.cover_key        AS CoverKey,
                   CASE WHEN s.preview_key IS NOT NULL THEN '/' || s.preview_key END AS PreviewUrl,
                   s.play_count       AS PlayCount,
                   s.favourite_count  AS FavouriteCount,
                   s.download_count   AS DownloadCount,
                   s.bpm::double precision AS Bpm,
                   s.submitted_at     AS SubmittedAt,
                   s.updated_at       AS UpdatedAt,
                   EXISTS (SELECT 1 FROM favourites f WHERE f.set_id = s.id AND f.user_id = @viewerId) AS IsFavourited
            FROM beatmapsets s
            JOIN users u ON u.id = s.owner_id
            WHERE s.id = @id
            """,
            new { id, viewerId = CurrentUser?.Id ?? 0 });

        if (set is null || set.Status == "removed" || (set.Status == "hidden" && CurrentUser?.Id != set.OwnerId))
            return NotFound();

        Set = set;

        // Representative difficulty for the stats box: the hardest one (sets are effectively
        // single-difficulty today — song select collapsed to a single panel).
        Diff = await conn.QuerySingleOrDefaultAsync<DiffStats>(
            """
            SELECT b.total_length_s        AS TotalLengthS,
                   b.word_count            AS WordCount,
                   b.char_count            AS CharCount,
                   b.wpm::double precision AS Wpm,
                   b.difficulty_rating     AS Stars
            FROM beatmaps b
            WHERE b.set_id = @id AND b.filename IS NOT NULL
            ORDER BY b.difficulty_rating DESC, b.id ASC
            LIMIT 1
            """,
            new { id });

        // Global leaderboard: best ranked+passed score per user across the set's difficulties
        // (same DISTINCT ON shape as the game-facing endpoint in ScoreEndpoints).
        Scores = (await conn.QueryAsync<ScoreRow>(
            """
            SELECT best.id           AS ScoreId,
                   best.user_id      AS UserId,
                   best.username     AS Username,
                   best.total_score  AS TotalScore,
                   best.accuracy     AS Accuracy,
                   best.max_combo    AS MaxCombo,
                   best.rank         AS Rank,
                   best.ended_at     AS EndedAt,
                   best.mods         AS ModsJson,
                   best.statistics   AS StatisticsJson
            FROM (
                SELECT DISTINCT ON (sc.user_id)
                       sc.id, sc.user_id, sc.total_score, sc.accuracy, sc.max_combo, sc.rank,
                       sc.ended_at, sc.mods::text AS mods, sc.statistics::text AS statistics,
                       u.username::text AS username
                FROM scores sc
                JOIN beatmaps b ON b.id = sc.beatmap_id
                JOIN users u ON u.id = sc.user_id
                WHERE b.set_id = @id AND sc.ranked AND sc.passed
                ORDER BY sc.user_id, sc.total_score DESC, sc.id ASC
            ) best
            ORDER BY best.total_score DESC, best.id ASC
            LIMIT 50
            """,
            new { id })).ToList();

        ViewData["Title"] = $"{Set.Artist} - {Set.Title}";
        ViewData["MetaDescription"] =
            $"{Set.Artist} — {Set.Title}, mapped by {Set.Creator}. Type it in type!beat.";
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

    // ---- display helpers ----

    public static string FormatLength(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    }

    public static string FormatAccuracy(double accuracy)
        => (accuracy * 100).ToString("0.00", CultureInfo.InvariantCulture) + "%";

    /// <summary>The client's ScoreRank strings; X is the perfect rank, shown as SS like the game.</summary>
    public static string GradeLabel(string rank) => rank == "X" ? "SS" : rank;

    public static string GradeClass(string rank) => "grade--" + (rank == "X" ? "ss" : rank.ToLowerInvariant());

    public sealed record SetDetails(
        long Id, string Title, string Artist, string Source, string Tags, string Description,
        string Status, string Creator, long OwnerId, string? CoverKey, string? PreviewUrl,
        int PlayCount, int FavouriteCount, int DownloadCount, double? Bpm,
        DateTime SubmittedAt, DateTime UpdatedAt, bool IsFavourited)
    {
        public string StatusLabel => BeatmapsetDisplay.StatusLabel(Status);
        public string PillClass => BeatmapsetDisplay.PillClass(Status);
        public string? CoverUrl => CoverKey is null ? null : $"/{CoverKey}/cover.jpg";
        public IEnumerable<string> TagList =>
            Tags.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct();
    }

    public sealed record DiffStats(double TotalLengthS, int? WordCount, int? CharCount, double? Wpm, double Stars);

    /// <summary>
    /// One leaderboard row. Judgement counts come from the statistics jsonb (wire keys
    /// great/ok/meh/miss) but are DISPLAYED with the game engine's own judgement names —
    /// Perfect/Good/Ok/Miss per the mapping in the fork's TypeBeatJudgements.cs
    /// ("Perfect->Great, Good->Ok, Ok->Meh, ...->Miss").
    /// </summary>
    public sealed record ScoreRow(
        long ScoreId, long UserId, string Username, long TotalScore, double Accuracy,
        int MaxCombo, string Rank, DateTime EndedAt, string ModsJson, string StatisticsJson)
    {
        private JObject? statistics;
        private JObject Statistics => statistics ??= JObject.Parse(string.IsNullOrEmpty(StatisticsJson) ? "{}" : StatisticsJson);

        public int Perfect => Statistics.Value<int?>("great") ?? 0;
        public int Good => Statistics.Value<int?>("ok") ?? 0;
        public int Ok => Statistics.Value<int?>("meh") ?? 0;
        public int Miss => Statistics.Value<int?>("miss") ?? 0;

        /// <summary>Mod acronyms from the mods jsonb ([{acronym, settings}] wire shape).</summary>
        public string Mods => string.Join(" ",
            JArray.Parse(string.IsNullOrEmpty(ModsJson) ? "[]" : ModsJson)
                  .Select(m => m?["acronym"]?.Value<string>())
                  .Where(a => !string.IsNullOrEmpty(a)));
    }
}
