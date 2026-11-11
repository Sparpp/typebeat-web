using System.Net;
using Dapper;
using Npgsql;
using Typebeat.Web.Scoring;

namespace Typebeat.Web.Tests.Website;

/// <summary>
/// The stats card's WPM (backlog 458, <see cref="ProfileWpm"/>): the mean, over the top plays of the
/// Best performance list, of each map's TARGET pace at the play's rate, weighted by its accuracy.
/// It replaced the Maps played row.
/// </summary>
public class ProfileWpmTest
{
    private static long ownerId;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        await PublicSiteSeed.EnsureSeededAsync();

        await using var conn = new NpgsqlConnection(WebsiteFixture.ConnectionString);
        await conn.OpenAsync();

        ownerId = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO users (username, email, password_hash, country_code)
            VALUES ('wpm row owner', 'wpm.row.owner@example.com', 'x', 'US')
            RETURNING id
            """);

        // A bursty map (average 60, target 120) played at 1x with 90% accuracy reads 108: the target,
        // not the average and not the peak. A map with no target yet falls back to its average (100),
        // played at 1.5x with 80% accuracy: 120. Mean 114.
        long bursty = await insertBeatmapAsync(conn, "Wpm Row Bursty", wpm: 60, targetWpm: 120, peakWpm: 300);
        long plain = await insertBeatmapAsync(conn, "Wpm Row Plain", wpm: 100, targetWpm: null, peakWpm: 300);

        await insertScoreAsync(conn, bursty, accuracy: 0.9, mods: "[]", pp: 50);
        await insertScoreAsync(conn, plain, accuracy: 0.8, mods: """[{"acronym":"DT","settings":{"speed_change":1.5}}]""", pp: 40);
    }

    [Test]
    public void APlayProvesTheTargetPaceAtItsRateAndAccuracy()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ProfileWpm.Of(new ProfileWpm.Play(120, 0.9, "[]")), Is.EqualTo(108).Within(1e-9));
            Assert.That(ProfileWpm.Of(new ProfileWpm.Play(100, 0.8, """[{"acronym":"DT","settings":{"speed_change":1.5}}]""")), Is.EqualTo(120).Within(1e-9));
            Assert.That(ProfileWpm.Of(new ProfileWpm.Play(100, 1, """[{"acronym":"HT","settings":{"speed_change":0.75}}]""")), Is.EqualTo(75).Within(1e-9));
            Assert.That(ProfileWpm.Of(new ProfileWpm.Play(null, 1, "[]")), Is.Null, "a map with no pace proves none");
            Assert.That(ProfileWpm.Average(Array.Empty<ProfileWpm.Play>()), Is.Null);
            Assert.That(ProfileWpm.Average(new[] { new ProfileWpm.Play(null, 1, "[]"), new ProfileWpm.Play(100, 1, "[]") }), Is.EqualTo(100),
                "a play without a pace is left out, not counted as zero");
        });
    }

    [Test]
    public async Task StatsCard_ShowsTheWpm_AndNoLongerMapsPlayed()
    {
        using var response = await WebsiteFixture.Client.GetAsync($"/users/{ownerId}");
        string html = await response.Content.ReadAsStringAsync();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        int start = html.IndexOf("profile-stats", StringComparison.Ordinal);
        int end = html.IndexOf("</aside>", start, StringComparison.Ordinal);
        string card = html[start..end];

        Assert.Multiple(() =>
        {
            Assert.That(card, Does.Contain(">WPM</span><span class=\"stat-row__value\">114</span>"));
            Assert.That(card, Does.Not.Contain("Maps played"));
        });
    }

    private static async Task<long> insertBeatmapAsync(NpgsqlConnection conn, string title, double wpm, double? targetWpm, double peakWpm)
    {
        long setId = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmapsets (owner_id, title, artist, status, submitted_at, updated_at)
            VALUES (@ownerId, @title, 'The Paced', 'ranked', now() - interval '2 days', now() - interval '2 days')
            RETURNING id
            """,
            new { ownerId, title });

        return await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmaps (set_id, version_name, checksum_md5, total_length_s, drain_length_s, difficulty_rating, filename, wpm, target_wpm, peak_wpm)
            VALUES (@setId, 'only', @checksum, 60, 55, 2.0, 'map.osu', @wpm, @targetWpm, @peakWpm)
            RETURNING id
            """,
            new { setId, checksum = Guid.NewGuid().ToString("N"), wpm, targetWpm, peakWpm });
    }

    private static async Task insertScoreAsync(NpgsqlConnection conn, long beatmapId, double accuracy, string mods, double pp)
    {
        long scoreId = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO scores
                (user_id, beatmap_id, total_score, accuracy, completion, max_combo, rank, passed, ranked,
                 mods, statistics, maximum_statistics)
            VALUES
                (@ownerId, @beatmapId, 61000, @accuracy, 1.0, 50, 'A', true, true,
                 @mods::jsonb, '{"great":90,"ok":5,"meh":2,"miss":3}'::jsonb, '{"great":100}'::jsonb)
            RETURNING id
            """,
            new { ownerId, beatmapId, accuracy, mods });

        await conn.ExecuteAsync("UPDATE scores SET pp = @pp WHERE id = @scoreId", new { pp, scoreId });
    }
}
