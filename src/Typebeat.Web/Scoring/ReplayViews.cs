using System.Globalization;
using Dapper;
using Npgsql;
using Typebeat.Web.Pages;

namespace Typebeat.Web.Scoring;

/// <summary>
/// Replay views (025_replay_views.sql): one place owns the counted-view rule, the write that
/// applies it, and the reads the profile draws from it.
///
/// <para>
/// WHAT COUNTS AS A VIEW, in full (025's header argues each clause): a stored replay was actually
/// served by <c>GET /api/v2/scores/{id}/replay</c>, to a requester who is identified (bearer or
/// session cookie) and is NOT the score's owner, and it is the first such serve for that
/// (score, viewer) pair today in UTC. Anonymous serves count nothing, deliberately, because there
/// is no honest per-person key for one.
/// </para>
///
/// <para>
/// The endpoint decides clauses 1 to 3 (it is the only place that knows whether bytes were served
/// and who asked); <see cref="RecordAsync"/> owns clause 4 and every counter. Nothing else
/// increments these numbers.
/// </para>
/// </summary>
public static class ReplayViews
{
    /// <summary>How far back the profile chart goes when the account has older views than that.</summary>
    public const int WindowMonths = 24;

    /// <summary>
    /// Records one serve of <paramref name="scoreId"/>'s replay to <paramref name="viewerId"/>,
    /// and reports whether it counted. The caller has already established that a replay was served
    /// and that the viewer is a signed-in non-owner.
    ///
    /// <para>
    /// One statement, so the ledger row and both counters commit together or not at all, with no
    /// transaction for the caller to manage and no window where a claimed day has no view behind
    /// it. The ledger INSERT is the whole concurrency story: <c>ON CONFLICT DO NOTHING</c> makes a
    /// double request from one viewer serialize on the primary key, and its <c>RETURNING</c> is
    /// what the other two statements read to decide whether to fire. (Data-modifying CTEs cannot
    /// see each other's effects on tables, but RETURNING output is explicitly how they communicate,
    /// which is exactly what this needs.) The rowcount of the final upsert, 1 or 0, is the answer.
    /// </para>
    /// </summary>
    /// <param name="ownerId">The score's own user, whose monthly rollup the view is credited to.</param>
    /// <returns>True when this serve was counted; false when the viewer already had it today.</returns>
    public static async Task<bool> RecordAsync(
        NpgsqlConnection conn, long scoreId, long ownerId, long viewerId, CancellationToken ct = default)
        => await conn.ExecuteAsync(new CommandDefinition(
            """
            WITH claim AS (
                INSERT INTO replay_views (score_id, viewer_id, viewed_on)
                VALUES (@scoreId, @viewerId, (now() AT TIME ZONE 'UTC')::date)
                ON CONFLICT (score_id, viewer_id, viewed_on) DO NOTHING
                RETURNING score_id, viewed_on
            ),
            counted AS (
                UPDATE scores SET replay_views = replay_views + 1
                WHERE id = (SELECT score_id FROM claim)
            )
            INSERT INTO user_month_replay_views (user_id, month, views)
            SELECT @ownerId, date_trunc('month', c.viewed_on)::date, 1
            FROM claim c
            ON CONFLICT (user_id, month) DO UPDATE SET views = user_month_replay_views.views + 1
            """,
            new { scoreId, ownerId, viewerId }, cancellationToken: ct)) > 0;

    /// <summary>
    /// How many times other players have watched this user's replays, all time: the stats card's
    /// "Replays watched by others". Summed from the monthly rollup rather than from
    /// <c>scores.replay_views</c>, so the number the card prints and the bars the chart draws are
    /// the same data (a score that is later unranked or whose set is hidden drops out of the
    /// most-viewed LIST, but the views it earned still happened and stay in the total).
    /// </summary>
    public static async Task<long> TotalForUserAsync(NpgsqlConnection conn, long userId, CancellationToken ct = default)
        => await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COALESCE(sum(views), 0)::bigint FROM user_month_replay_views WHERE user_id = @userId",
            new { userId }, cancellationToken: ct));

    /// <summary>
    /// Views of this user's replays as a CONTINUOUS run of months, newest last, with the months
    /// that have no row filled in at zero. Empty (not a run of zeroes) when nobody has ever watched
    /// one, which is what lets the profile hide the section.
    ///
    /// <para>
    /// Same window rule as the play-history chart (<see cref="PlayHistory.ForUserAsync"/>): the
    /// last <see cref="WindowMonths"/> months or everything since the first view, whichever is
    /// shorter, always running up to the CURRENT month, so a chart that stops at the last view
    /// cannot read as "still being watched". The two are deliberately separate implementations of
    /// one shape for now: this one owns its own rollup and noun, and folding both into a shared
    /// month-window helper is a refactor of landed code rather than part of this feature.
    /// </para>
    /// </summary>
    /// <param name="today">The current date, injectable so tests can pin a month boundary.</param>
    public static async Task<IReadOnlyList<MonthViews>> ForUserAsync(
        NpgsqlConnection conn, long userId, DateOnly? today = null, CancellationToken ct = default)
    {
        var stored = (await conn.QueryAsync<(DateOnly Month, int Views)>(new CommandDefinition(
            """
            SELECT month AS Month, views AS Views
            FROM user_month_replay_views
            WHERE user_id = @userId AND views > 0
            ORDER BY month
            """,
            new { userId }, cancellationToken: ct))).ToList();

        if (stored.Count == 0)
            return [];

        var counts = stored.ToDictionary(r => r.Month, r => (long)r.Views);

        var now = today ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var currentMonth = new DateOnly(now.Year, now.Month, 1);

        var first = counts.Keys.Min();
        var last = counts.Keys.Max();

        // A stored row in the future (clock skew) extends the axis rather than being dropped.
        var end = last > currentMonth ? last : currentMonth;
        var windowStart = end.AddMonths(-(WindowMonths - 1));
        var start = first > windowStart ? first : windowStart;

        var months = new List<MonthViews>();
        for (var m = start; m <= end; m = m.AddMonths(1))
            months.Add(new MonthViews(m, counts.GetValueOrDefault(m)));

        return months;
    }

    /// <summary>
    /// Renders a window from <see cref="ForUserAsync"/> as the shared bar chart's view model
    /// (Pages/Shared/_BarChart.cshtml), the same way the play-history section does: invariant month
    /// names on the axis, and a caption carrying the range and the total so the bare month names
    /// never leave the reader guessing which years they span.
    /// </summary>
    public static BarChartModel Chart(IReadOnlyList<MonthViews> months)
    {
        var bars = months
            .Select(m => new BarChartBar(
                Label: m.Month.ToString("MMM", CultureInfo.InvariantCulture),
                Period: m.Month.ToString("MMMM yyyy", CultureInfo.InvariantCulture),
                Value: m.Views))
            .ToList();

        long total = months.Sum(m => m.Views);

        string range = months.Count == 0
            ? string.Empty
            : $"{months[0].Month.ToString("MMM yyyy", CultureInfo.InvariantCulture)} to {months[^1].Month.ToString("MMM yyyy", CultureInfo.InvariantCulture)}";

        return new BarChartModel(
            bars,
            valueNoun: "views",
            ariaLabel: "Replay views per month",
            caption: $"{range} · {total.ToString("N0", CultureInfo.InvariantCulture)} views");
    }

    /// <summary>One month of the chart. <paramref name="Month"/> is the first of the month, UTC.</summary>
    public sealed record MonthViews(DateOnly Month, long Views);
}
