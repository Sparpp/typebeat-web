using System.Net;
using Dapper;
using Npgsql;
using Typebeat.Web.Scoring;

namespace Typebeat.Web.Tests.Website;

/// <summary>
/// The profile's Best scores section is ordered by PP (task 77), not by total score, and the
/// ordering is done in SQL so it decides WHICH rows the section's LIMIT keeps, not merely how the
/// kept rows are arranged.
///
/// <list type="bullet">
/// <item>pp descending, so the number each row headlines is the number that put it there;</item>
/// <item>ties (every custom-rate or not-yet-priced play sits at exactly 0) fall back to the old
/// ordering, total score descending, then score id, so the order is total;</item>
/// <item>the fold picks the best-PP play, the same row <see cref="PpRanking"/> counts, not the
/// biggest-scoring one, both within one map and across a song's difficulties (backlog 162: the
/// section keeps one row per SET);</item>
/// <item>the LIMIT boundary: a top-pp play must reach the list even when 20 bigger-scoring 0pp
/// plays exist. Sorting the top-20-by-score in C# afterwards would silently lose it.</item>
/// </list>
///
/// Own throwaway users, sets and maps, with deliberately TINY score totals (a few thousand) so
/// this fixture cannot move <see cref="ProfilePageTest"/>'s cumulative-rank assertions or
/// <see cref="RankingsPageTest"/>'s exact figures. pp is stamped directly on the rows, like
/// <see cref="RankingsPageTest"/> does, so the ordering is tested independently of the per-play
/// formula.
/// </summary>
public class ProfileBestScoresOrderTest
{
    private static long orderUserId;
    private static long limitUserId;
    private static long songUserId;

    /// <summary>How many 0pp plays the limit fixture stacks above the section's cap of 20.</summary>
    private const int zero_pp_plays = 20;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        await PublicSiteSeed.EnsureSeededAsync();

        await using var conn = new NpgsqlConnection(WebsiteFixture.ConnectionString);
        await conn.OpenAsync();

        // ---- ordering + tie-break + per-map fold ----
        //
        // ONE SET PER SEEDED SONG throughout this fixture (backlog 162). The section keeps one row
        // per SET, so putting these maps in a shared set would collapse each user's whole list to a
        // single row and every assertion below would be measuring the set fold instead of ordering.
        // The only place two beatmaps deliberately share a set is BestScores_KeepOneRowPerSong's
        // seed at the bottom, which is the test of that rule.

        orderUserId = await insertUserAsync(conn, "bs order");

        // A small play that is worth a lot of pp, and a huge (by this fixture's scale) one worth
        // none: total score alone would put them in exactly the opposite order.
        await insertScoreAsync(conn, orderUserId, await insertSongAsync(conn, "bso-priced"), 1_000, pp: 150);
        await insertScoreAsync(conn, orderUserId, await insertSongAsync(conn, "bso-bigzero"), 9_000, pp: 0);
        await insertScoreAsync(conn, orderUserId, await insertSongAsync(conn, "bso-smallzero"), 5_000, pp: 0);

        // One map, two plays: the better-scoring one is worth less pp. The section must show the
        // 90 pp row, because that is the play the pp ranking counts for this map.
        long foldMap = await insertSongAsync(conn, "bso-fold");
        await insertScoreAsync(conn, orderUserId, foldMap, 9_500, pp: 10);
        await insertScoreAsync(conn, orderUserId, foldMap, 1_500, pp: 90);

        // ---- the LIMIT boundary ----

        limitUserId = await insertUserAsync(conn, "bs limit");

        // Twenty 0pp plays on twenty songs, already filling the section on their own, every one of
        // them scoring more than the priced play below.
        for (int i = 0; i < zero_pp_plays; i++)
            await insertScoreAsync(conn, limitUserId, await insertSongAsync(conn, $"bsl-{i:00}"), 2_000 - i * 10, pp: 0);

        // The player's only priced play, and the worst-scoring thing they have ever submitted.
        // Ordering by score first drops it off the list entirely; ordering by pp makes it row one.
        await insertScoreAsync(conn, limitUserId, await insertSongAsync(conn, "bsl-priced"), 100, pp: 5);

        // ---- the set fold: one row per SONG, not per difficulty (backlog 162) ----
        //
        // The only shared set in this fixture: two difficulties of one song, the better-pp one
        // deliberately the lower-SCORING one, so a fold resolving the song by score picks wrong.
        songUserId = await insertUserAsync(conn, "bs song");
        long songSet = await insertSetAsync(conn, "Bs One Song Anthem");

        await insertScoreAsync(conn, songUserId, await insertBeatmapAsync(conn, songSet, "bss-hard"), 9_000, pp: 20);
        await insertScoreAsync(conn, songUserId, await insertBeatmapAsync(conn, songSet, "bss-easy"), 1_000, pp: 70);
    }

    [Test]
    public async Task BestScores_OrderByPp_ThenScore_ThenId()
    {
        string best = await bestSectionAsync(orderUserId);

        Assert.Multiple(() =>
        {
            // 150 pp on a 1,000-point play outranks a 9,000-point play worth nothing.
            Assert.That(indexOf(best, "bso-priced"), Is.LessThan(indexOf(best, "bso-bigzero")),
                "the top-pp play leads the section however small its score");
            Assert.That(indexOf(best, "bso-fold"), Is.LessThan(indexOf(best, "bso-bigzero")),
                "90 pp still outranks a bigger 0pp score");
            Assert.That(indexOf(best, "bso-priced"), Is.LessThan(indexOf(best, "bso-fold")),
                "150 pp above 90 pp");

            // The tie-break: both 0pp, so they keep the section's old ordering, best score first.
            Assert.That(indexOf(best, "bso-bigzero"), Is.LessThan(indexOf(best, "bso-smallzero")),
                "equal pp falls back to total score descending");
        });
    }

    [Test]
    public async Task BestScores_ShowTheMapsBestPpPlay_NotItsBestScore()
    {
        string best = await bestSectionAsync(orderUserId);

        Assert.Multiple(() =>
        {
            // The 1,500-point / 90 pp row represents the map; the 9,500-point / 10 pp one does not.
            Assert.That(best, Does.Contain(">90pp<"));
            Assert.That(best, Does.Contain("1,500"));
            Assert.That(best, Does.Not.Contain(">10pp<"), "the map's lower-pp play must not represent it");
            Assert.That(best, Does.Not.Contain("9,500"));
        });
    }

    /// <summary>
    /// ONE ROW PER SONG (backlog 162). The section is the pp board's view of the user, and pp is
    /// earned per set, so a player who cleared two difficulties of one song sees the play that
    /// actually banks and not the other. The better-pp difficulty is the lower-scoring one, so a
    /// fold resolving the song by total score would show the wrong row.
    /// </summary>
    [Test]
    public async Task BestScores_KeepOneRowPerSong_NotPerDifficulty()
    {
        string best = await bestSectionAsync(songUserId);

        Assert.Multiple(() =>
        {
            Assert.That(countOf(best, "<div class=\"score-row\">"), Is.EqualTo(1),
                "two difficulties of one song are one Best row");
            Assert.That(best, Does.Contain("bss-easy"), "the better-pp difficulty represents the song");
            Assert.That(best, Does.Contain(">70pp<"));
            Assert.That(best, Does.Not.Contain("bss-hard"), "the song's weaker-pp difficulty is not a second row");
            Assert.That(best, Does.Not.Contain(">20pp<"));
        });
    }

    [Test]
    public async Task BestScores_TopPpPlay_SurvivesTheLimit_AndPushesOutTheWeakest()
    {
        string best = await bestSectionAsync(limitUserId);

        Assert.Multiple(() =>
        {
            // Exactly the section cap, so this really is the boundary case.
            Assert.That(countOf(best, "<div class=\"score-row\">"), Is.EqualTo(20));

            // The priced play is IN, and first, though 20 higher-scoring plays exist. Re-sorting a
            // top-20-by-score page in C# could never have produced this row: it was never fetched.
            Assert.That(best, Does.Contain("bsl-priced"));
            Assert.That(best, Does.Contain(">5pp<"));
            Assert.That(indexOf(best, "bsl-priced"), Is.LessThan(indexOf(best, "bsl-00")));

            // Something had to give, and it is the weakest 0pp play, not the priced one.
            Assert.That(best, Does.Contain("bsl-18"), "the 19th-best 0pp play still fits");
            Assert.That(best, Does.Not.Contain("bsl-19"), "the weakest 0pp play is pushed off the end");
        });
    }

    // ---- helpers ----

    private static async Task<string> bestSectionAsync(long userId)
    {
        using var response = await WebsiteFixture.Client.GetAsync($"/users/{userId}");
        string html = await response.Content.ReadAsStringAsync();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        int start = html.IndexOf("id=\"best-scores\"", StringComparison.Ordinal);
        Assert.That(start, Is.GreaterThan(-1), "the profile must render the Best scores section");

        int end = html.IndexOf("</section>", start, StringComparison.Ordinal);
        Assert.That(end, Is.GreaterThan(-1), "the Best scores section must close");

        return html[start..end];
    }

    private static int indexOf(string section, string needle)
    {
        int at = section.IndexOf(needle, StringComparison.Ordinal);
        Assert.That(at, Is.GreaterThan(-1), $"the Best scores section must contain {needle}");
        return at;
    }

    private static int countOf(string section, string needle)
    {
        int count = 0;

        for (int at = section.IndexOf(needle, StringComparison.Ordinal); at >= 0;
             at = section.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
            count++;

        return count;
    }

    private static Task<long> insertUserAsync(NpgsqlConnection conn, string username)
        => conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO users (username, email, password_hash, country_code)
            VALUES (@username, @email, 'not-a-real-hash', 'US')
            RETURNING id
            """,
            new { username, email = username.Replace(' ', '.') + "@example.com" });

    /// <summary>
    /// A SONG: its own set with a single difficulty in it, named for the version so an assertion can
    /// find the rendered row. The default shape in this fixture, because the Best section keeps one
    /// row per set and every seed here except the set-fold pair means a different song.
    /// </summary>
    private static async Task<long> insertSongAsync(NpgsqlConnection conn, string versionName)
        => await insertBeatmapAsync(conn, await insertSetAsync(conn, $"Bs Anthem {versionName}"), versionName);

    private static Task<long> insertSetAsync(NpgsqlConnection conn, string title)
        => conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmapsets (owner_id, title, artist, status, submitted_at, updated_at)
            VALUES (@ownerId, @title, 'The Ordered', 'ranked', now() - interval '5 days', now() - interval '5 days')
            RETURNING id
            """,
            new { ownerId = PublicSiteSeed.MapperId, title });

    private static Task<long> insertBeatmapAsync(NpgsqlConnection conn, long setId, string versionName)
        => conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmaps
                (set_id, version_name, checksum_md5, total_length_s, drain_length_s,
                 difficulty_rating, filename, word_count, char_count, wpm)
            VALUES (@setId, @versionName, @checksum, 90, 80, 3.0, 'map.osu', 100, 500, 75)
            RETURNING id
            """,
            new { setId, versionName, checksum = Guid.NewGuid().ToString("N") });

    private static Task insertScoreAsync(NpgsqlConnection conn, long userId, long beatmapId, long totalScore, double pp)
        => conn.ExecuteAsync(
            """
            INSERT INTO scores
                (user_id, beatmap_id, total_score, accuracy, completion, max_combo, rank, passed, ranked,
                 mods, statistics, maximum_statistics, pp, pp_version)
            VALUES
                (@userId, @beatmapId, @totalScore, 0.90, 1.0, 50, 'A', true, true,
                 '[]'::jsonb, '{"great":90,"ok":5,"meh":2,"miss":3}'::jsonb, '{"great":100}'::jsonb, @pp, @ppVersion)
            """,
            new { userId, beatmapId, totalScore, pp, ppVersion = PerformancePoints.VERSION });
}
