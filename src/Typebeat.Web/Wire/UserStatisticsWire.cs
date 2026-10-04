using Dapper;
using Npgsql;
using Typebeat.Web.Scoring;

namespace Typebeat.Web.Wire;

/// <summary>
/// Reads one user's client-facing statistics block, the payload
/// <see cref="UserWire.ProfileStatistics"/> shapes. The single assembler for it, shared by the
/// login fetch (GET /api/v2/me/) and the profile fetch (GET /api/v2/users/{lookup}), so the two
/// cannot disagree about a player's pp, rank or totals: the client keeps the login copy in
/// <c>api.LocalUser</c> and the profile copy in <c>LocalUserStatisticsProvider</c>, and a
/// difference between them would surface as a number that changes when you open a panel.
///
/// <para>
/// Every metric comes from the board that owns it rather than from a query written here:
/// <see cref="PpRanking"/> for total pp and the pp rank (the main global ranking since task 61),
/// <see cref="GlobalRanking"/> for the cumulative ranked score. That is what makes an admin
/// un-ranking a set move the in-game profile at the same moment it moves the website.
/// </para>
/// </summary>
public static class UserStatisticsWire
{
    public static async Task<object> ForUserAsync(NpgsqlConnection conn, long userId, CancellationToken ct = default)
    {
        // The pp board: total + rank. Its rank is what the client's single global_rank slot carries
        // (see UserWire.ProfileStatistics for why), and a user with no pp-earning play comes back
        // as (0, null), which is exactly the "0 pp, unranked" the wire wants to say.
        var performance = await PpRanking.ForUserAsync(conn, userId, ct);

        // The profile header's "Country Ranking", off the same pp board partitioned by country.
        long? countryRank = await PpRanking.CountryRankForUserAsync(conn, userId, ct);

        // The header's ranking-tier colour reads the rank as a fraction of everyone ranked (osu's global_rank_percent).
        double? rankPercent = performance.GlobalRank is { } rank
            ? (double)rank / Math.Max(1, await PpRanking.RankedPlayerCountAsync(conn, ct))
            : null;

        // The cumulative-score board, still read for its VALUE (ranked_score). Its rank has no slot
        // on the client and is deliberately dropped rather than blended with the pp rank.
        var score = await GlobalRanking.ForUserAsync(conn, userId, ct);

        // Lifetime aggregates. Absent row (a user who has never submitted) → all zeros.
        var totals = await conn.QuerySingleOrDefaultAsync<(int PlayCount, long TotalScore, long PlayTimeS)>(
            new CommandDefinition(
                "SELECT play_count, total_score, play_time_s FROM user_stats WHERE user_id = @userId",
                new { userId },
                cancellationToken: ct));

        // The profile's extended details. Total hits are osu's: every judged hit, misses excluded, summed
        // from the lifetime per-judgement counts submissions accumulate (user_stats.hit_counts). Maximum
        // combo is the best combo of any stored play; replays watched is every view of this player's
        // replays by anyone (scores.replay_views, 025_replay_views.sql).
        var extended = await conn.QuerySingleAsync<(long TotalHits, int MaximumCombo, long ReplaysWatched)>(
            new CommandDefinition(
                """
                SELECT
                    COALESCE((SELECT sum(h.value::bigint)
                              FROM user_stats us, jsonb_each_text(us.hit_counts) h
                              WHERE us.user_id = @userId AND h.key NOT LIKE '%miss%'), 0)::bigint AS TotalHits,
                    COALESCE((SELECT max(max_combo) FROM scores WHERE user_id = @userId), 0)::int AS MaximumCombo,
                    COALESCE((SELECT sum(replay_views) FROM scores WHERE user_id = @userId), 0)::bigint AS ReplaysWatched
                """,
                new { userId },
                cancellationToken: ct));

        var (ss, s, a, accuracyPercent) = await gradeCountsAsync(conn, userId, ct);

        return UserWire.ProfileStatistics(
            performance.TotalPp, performance.GlobalRank, rankPercent, countryRank, score.RankedScore,
            totals.TotalScore, totals.PlayCount, totals.PlayTimeS,
            accuracyPercent, ss, s, a,
            extended.TotalHits, extended.MaximumCombo, extended.ReplaysWatched);
    }

    /// <summary>
    /// SS/S/A counts + mean accuracy (0–100) over the user's best ranked+passed score per map; the
    /// same per-map-best fold the website profile uses. B/C/D exist in our grading but have no slot
    /// in the client grade_counts DTO, so they are folded away here.
    ///
    /// <para>
    /// The fold is built from <see cref="BeatmapLeaderboard"/>'s own fragments rather than a fourth
    /// hand-written copy of "ranked and passed, best score first", so a change to what a board row
    /// means (or to its tie-break) moves the in-game grade counts and the website's together
    /// instead of silently splitting them.
    /// </para>
    /// </summary>
    private static async Task<(int Ss, int S, int A, double AccuracyPercent)> gradeCountsAsync(
        NpgsqlConnection conn, long userId, CancellationToken ct)
    {
        var rows = await conn.QueryAsync<(string Rank, long Count, double AccSum)>(
            new CommandDefinition(
                $"""
                SELECT best.rank AS Rank, count(*) AS Count, sum(best.accuracy) AS AccSum
                FROM (
                    SELECT DISTINCT ON (sc.beatmap_id) sc.rank, sc.accuracy
                    FROM scores sc
                    WHERE sc.user_id = @userId AND {BeatmapLeaderboard.OnBoard("sc", "true")}
                    ORDER BY sc.beatmap_id, {BeatmapLeaderboard.Order("sc")}
                ) best
                GROUP BY best.rank
                """,
                new { userId },
                cancellationToken: ct));

        int ss = 0, s = 0, a = 0;
        long total = 0;
        double accSum = 0;

        foreach (var r in rows)
        {
            total += r.Count;
            accSum += r.AccSum;

            switch (r.Rank)
            {
                case "X" or "XH": ss += (int)r.Count; break;
                case "S" or "SH": s += (int)r.Count; break;
                case "A": a += (int)r.Count; break;
            }
        }

        double accuracyPercent = total > 0 ? accSum / total * 100.0 : 0.0;
        return (ss, s, a, accuracyPercent);
    }
}
