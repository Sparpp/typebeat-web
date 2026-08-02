using System.Globalization;
using Dapper;
using Npgsql;
using Typebeat.Web.Pages;

namespace Typebeat.Web.Scoring;

/// <summary>
/// The monthly play rollup behind the profile's Play History chart (024_play_history.sql): one
/// place owns the increment both submission paths make, the windowed read the profile does, and
/// the gap filling that turns stored rows into a continuous axis.
///
/// <para>
/// WHAT COUNTS AS A PLAY here is not a decision this class makes, it is a decision it MIRRORS.
/// The number the chart has to agree with is <c>user_stats.play_count</c>, printed in the stats
/// card directly above it, so <see cref="RecordPlayAsync"/> is called from exactly one place in
/// each submission path: inside the same <c>StatisticsValid &amp;&amp; withinBounds</c> block that
/// increments that counter, from the same transaction, with the same <c>ended_at</c> the score row
/// is stamped with. Fails count (they count there), unranked plays count (they count there), and a
/// submission whose statistics fail the tamper checks counts in neither.
/// </para>
///
/// <para>
/// UTC, for the reason 024's header spells out: the bucket a play lands in must never change
/// afterwards, or an incremented counter is the wrong data structure for it.
/// </para>
/// </summary>
public static class PlayHistory
{
    /// <summary>How far back the profile chart goes when the account is older than that.</summary>
    public const int WindowMonths = 24;

    /// <summary>
    /// Credits one play to the submitting user's month. Runs in the submission transaction, so it
    /// commits or rolls back with the score row and the <c>user_stats</c> update it accompanies.
    ///
    /// <para>
    /// The upsert is what makes this concurrency-safe without any lock of its own: the first play
    /// of a month inserts, every later one takes the DO UPDATE branch, and two submissions racing
    /// on the same (user, month) serialize on the primary key rather than on a read-then-write the
    /// caller would have to guard. (Contrast the <c>hit_counts</c> merge next to it, which cannot
    /// be expressed as an atomic increment and does need <c>FOR UPDATE</c>.)
    /// </para>
    /// </summary>
    /// <param name="endedAt">When the play finished; the same instant stored as scores.ended_at.</param>
    public static Task RecordPlayAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, long userId, DateTimeOffset endedAt, CancellationToken ct = default)
        => conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO user_month_playcounts (user_id, month, plays)
            VALUES (@userId, date_trunc('month', @endedAt AT TIME ZONE 'UTC')::date, 1)
            ON CONFLICT (user_id, month) DO UPDATE SET plays = user_month_playcounts.plays + 1
            """,
            new { userId, endedAt }, tx, cancellationToken: ct));

    /// <summary>
    /// The user's play history as a CONTINUOUS run of months, newest last, with the months that
    /// have no row filled in at zero. Empty (not a run of zeroes) when the user has never played,
    /// which is what lets the profile hide the whole section.
    ///
    /// <para>
    /// The window is the last <see cref="WindowMonths"/> months or everything since the first
    /// recorded play, whichever is shorter, and it always runs up to the CURRENT month even when
    /// the account has been idle for a year: a play history that quietly ends at the last play
    /// reads as "still active", which is the opposite of the truth. The end is nudged past the
    /// current month if a stored row somehow sits in the future (clock skew on a submission),
    /// because dropping a row that exists would be worse than a slightly long axis.
    /// </para>
    /// </summary>
    /// <param name="today">The current date, injectable so tests can pin a month boundary.</param>
    public static async Task<IReadOnlyList<MonthPlays>> ForUserAsync(
        NpgsqlConnection conn, long userId, DateOnly? today = null, CancellationToken ct = default)
    {
        // Npgsql maps a `date` column to DateOnly, which is exactly the type the month axis wants.
        var stored = (await conn.QueryAsync<(DateOnly Month, int Plays)>(new CommandDefinition(
            """
            SELECT month AS Month, plays AS Plays
            FROM user_month_playcounts
            WHERE user_id = @userId AND plays > 0
            ORDER BY month
            """,
            new { userId }, cancellationToken: ct))).ToList();

        if (stored.Count == 0)
            return [];

        var counts = stored.ToDictionary(r => r.Month, r => (long)r.Plays);

        var now = today ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var currentMonth = new DateOnly(now.Year, now.Month, 1);

        var first = counts.Keys.Min();
        var last = counts.Keys.Max();

        var end = last > currentMonth ? last : currentMonth;
        var windowStart = end.AddMonths(-(WindowMonths - 1));
        var start = first > windowStart ? first : windowStart;

        var months = new List<MonthPlays>();
        for (var m = start; m <= end; m = m.AddMonths(1))
            months.Add(new MonthPlays(m, counts.GetValueOrDefault(m)));

        return months;
    }

    /// <summary>
    /// Renders a window from <see cref="ForUserAsync"/> as the shared bar chart's view model
    /// (Pages/Shared/_BarChart.cshtml). Month names are invariant, like every other date on the
    /// site; the caption carries the range and the total, so the axis can stay bare month names
    /// without the reader losing track of which years they span.
    /// </summary>
    public static BarChartModel Chart(IReadOnlyList<MonthPlays> months)
    {
        var bars = months
            .Select(m => new BarChartBar(
                Label: m.Month.ToString("MMM", CultureInfo.InvariantCulture),
                Period: m.Month.ToString("MMMM yyyy", CultureInfo.InvariantCulture),
                Value: m.Plays))
            .ToList();

        long total = months.Sum(m => m.Plays);

        string range = months.Count == 0
            ? string.Empty
            : $"{months[0].Month.ToString("MMM yyyy", CultureInfo.InvariantCulture)} to {months[^1].Month.ToString("MMM yyyy", CultureInfo.InvariantCulture)}";

        return new BarChartModel(
            bars,
            valueNoun: "plays",
            ariaLabel: "Plays per month",
            caption: $"{range} · {total.ToString("N0", CultureInfo.InvariantCulture)} plays");
    }

    /// <summary>One month of the chart. <paramref name="Month"/> is the first of the month, UTC.</summary>
    public sealed record MonthPlays(DateOnly Month, long Plays);
}
