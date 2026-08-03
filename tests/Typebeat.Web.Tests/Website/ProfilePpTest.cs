using System.Net;
using Dapper;
using Npgsql;

namespace Typebeat.Web.Tests.Website;

/// <summary>
/// pp on the profile: the Best scores section (task 71) and the Player stats card (task 78).
///
/// Each Best row shows its stored <c>scores.pp</c> (docs/pp.md), rounded to a whole number with a
/// "pp" suffix, and every other score section (Recent, at minimum) shows nothing for the same
/// underlying score, since <see cref="Typebeat.Web.Pages.ScoreRowModel.Pp"/> is opt-in per section,
/// not a column on the shared row select. The card carries the user's pp total and pp rank beside
/// the (now labelled) cumulative-score rank. The section's ORDER is
/// <see cref="ProfileBestScoresOrderTest"/>'s.
///
/// Own throwaway user/set, own beatmaps, low score totals so nothing here can move
/// <see cref="ProfilePageTest"/>'s or <see cref="RankingsPageTest"/>'s exact-figure assertions.
/// </summary>
public class ProfilePpTest
{
    private static long ownerId;
    private static long nonZeroBeatmapId;
    private static long zeroBeatmapId;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        await PublicSiteSeed.EnsureSeededAsync();

        await using var conn = new NpgsqlConnection(WebsiteFixture.ConnectionString);
        await conn.OpenAsync();

        ownerId = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO users (username, email, password_hash, country_code)
            VALUES ('pp row owner', 'pp.row.owner@example.com', 'x', 'US')
            RETURNING id
            """);

        long setId = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmapsets (owner_id, title, artist, status, submitted_at, updated_at)
            VALUES (@ownerId, 'Pp Row Anthem', 'The Priced', 'ranked', now() - interval '2 days', now() - interval '2 days')
            RETURNING id
            """,
            new { ownerId });

        nonZeroBeatmapId = await insertBeatmapAsync(conn, setId, "priced");
        zeroBeatmapId = await insertBeatmapAsync(conn, setId, "unpriced");

        // One best play per map, kept in the same low band FirstPlacesTest uses, so this fixture
        // cannot disturb another test's global-rank assertion. pp is set directly on the row
        // (a raw INSERT bypasses the submission pipeline that would otherwise compute it), one
        // fractional (rounding must show) and one exactly zero (the "still shown" case).
        await insertScoreAsync(conn, nonZeroBeatmapId, totalScore: 61_210, pp: 219.4);
        await insertScoreAsync(conn, zeroBeatmapId, totalScore: 61_110, pp: 0);
    }

    [Test]
    public async Task BestScores_ShowRoundedPp_IncludingTheHonestZero()
    {
        using var response = await WebsiteFixture.Client.GetAsync($"/users/{ownerId}");
        string html = await response.Content.ReadAsStringAsync();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        string bestSection = sectionOf(html, "id=\"best-scores\"");

        Assert.Multiple(() =>
        {
            // Rounding: 219.4 rounds to 219, never printed with a decimal.
            Assert.That(bestSection, Does.Contain("score-row__pp"), "Best rows carry a pp element");
            Assert.That(bestSection, Does.Contain(">219pp<"), "219.4 rounds to 219pp");
            Assert.That(bestSection, Does.Not.Contain("219.4"));

            // Zero is a real, shown value, muted rather than hidden.
            Assert.That(bestSection, Does.Contain("score-row__pp--zero"));
            Assert.That(bestSection, Does.Contain(">0pp<"));
        });
    }

    /// <summary>
    /// The Player stats card carries the pp total and the pp RANK (task 78), read from the same
    /// <see cref="Typebeat.Web.Scoring.PpRanking"/> the /rankings performance board is built from,
    /// and it says which rank is which: the performance rank is the headline (that board is the
    /// site's main ranking) and the cumulative-score rank is a labelled row beside the score it is
    /// computed from, so neither number can be mistaken for the other.
    /// </summary>
    [Test]
    public async Task StatsCard_HeadlinesThePpRank_WithTotalPp_AndLabelsTheScoreRank()
    {
        using var response = await WebsiteFixture.Client.GetAsync($"/users/{ownerId}");
        string html = await response.Content.ReadAsStringAsync();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        string card = statsCard(html);

        Assert.Multiple(() =>
        {
            // Total pp: 219.4 (the one priced play) weighted at decay^0, rounded like the board.
            // The 0pp play contributes nothing, so this is the whole total.
            Assert.That(card, Does.Contain("Performance rank"));
            Assert.That(card, Does.Contain("profile-rank__pp"));
            Assert.That(card, Does.Contain(">219pp<"));

            // One priced play is enough to be ranked on that board.
            Assert.That(card, Does.Contain("profile-rank__value\">#"));
            Assert.That(card, Does.Not.Contain("profile-rank__value\">Unranked<"));

            // The cumulative-score rank is still here, still real, and no longer unlabelled.
            Assert.That(card, Does.Contain(">Score rank</span>"));
            Assert.That(card, Does.Contain(">Ranked score</span>"));
        });
    }

    [Test]
    public async Task OtherSections_NeverShowPp()
    {
        using var response = await WebsiteFixture.Client.GetAsync($"/users/{ownerId}");
        string html = await response.Content.ReadAsStringAsync();

        string recentSection = sectionOf(html, "id=\"recent-scores\"");

        Assert.Multiple(() =>
        {
            // The same two scores appear under Recent (they are this user's only plays), but the
            // section never opts into ScoreRowModel.Pp, so no row there carries the element at all.
            Assert.That(recentSection, Does.Contain("Pp Row Anthem"), "precondition: the scores are in Recent too");
            Assert.That(recentSection, Does.Not.Contain("score-row__pp"), "Recent must not render pp");
        });
    }

    // ---- helpers ----

    /// <summary>One profile section's markup, sliced from its anchor to the next section heading
    /// (or the end of the main column), so an assertion cannot match a different section.</summary>
    private static string sectionOf(string html, string marker)
    {
        int start = html.IndexOf(marker, StringComparison.Ordinal);
        Assert.That(start, Is.GreaterThan(-1), $"the profile must render {marker}");

        int end = html.IndexOf("</section>", start, StringComparison.Ordinal);
        Assert.That(end, Is.GreaterThan(-1), $"{marker}'s section must close");

        return html[start..end];
    }

    /// <summary>The Player stats card's markup, sliced off the side column so a card assertion
    /// cannot match the identical figure on a score row below.</summary>
    private static string statsCard(string html)
    {
        int start = html.IndexOf("profile-stats", StringComparison.Ordinal);
        Assert.That(start, Is.GreaterThan(-1), "the profile must render the stats card");

        int end = html.IndexOf("</aside>", start, StringComparison.Ordinal);
        Assert.That(end, Is.GreaterThan(-1), "the side column must close");

        return html[start..end];
    }

    private static Task<long> insertBeatmapAsync(NpgsqlConnection conn, long setId, string versionName)
        => conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmaps (set_id, version_name, checksum_md5, total_length_s, drain_length_s, difficulty_rating, filename)
            VALUES (@setId, @versionName, @checksum, 60, 55, 2.0, 'map.osu')
            RETURNING id
            """,
            new { setId, versionName, checksum = Guid.NewGuid().ToString("N") });

    private static async Task insertScoreAsync(NpgsqlConnection conn, long beatmapId, long totalScore, double pp)
    {
        long scoreId = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO scores
                (user_id, beatmap_id, total_score, accuracy, completion, max_combo, rank, passed, ranked,
                 mods, statistics, maximum_statistics)
            VALUES
                (@ownerId, @beatmapId, @totalScore, 0.90, 1.0, 50, 'S', true, true,
                 '[]'::jsonb, '{"great":90,"ok":5,"meh":2,"miss":3}'::jsonb, '{"great":100}'::jsonb)
            RETURNING id
            """,
            new { ownerId, beatmapId, totalScore });

        // The submission pipeline is what would normally price this; the raw INSERT above leaves
        // pp at its column default (0), so the fractional case is stamped directly here instead of
        // routing a whole play through PerformancePoints just to get one specific value.
        await conn.ExecuteAsync("UPDATE scores SET pp = @pp WHERE id = @scoreId", new { pp, scoreId });
    }
}
