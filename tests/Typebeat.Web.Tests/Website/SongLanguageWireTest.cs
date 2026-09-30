using System.Net;
using System.Net.Http.Headers;
using Dapper;
using Newtonsoft.Json.Linq;
using Npgsql;
using Typebeat.Web.Auth;
using Typebeat.Web.Data;

namespace Typebeat.Web.Tests.Website;

/// <summary>
/// The set's stored song language (beatmapsets.language) reaches the client as
/// <c>song_language</c> on every route that serves an APIBeatmapSet: the metadata lookup (by
/// checksum and by id), the beatmapset GET, and the profile sections' nested sets. The client's
/// metadata lookup reads it to fill a local Unspecified language for maps whose .osu predates the
/// Language: key. A set with no language emits the key as "", and no route emits a bare
/// <c>language</c> key, which the client binds to osu's {id, name} object.
/// </summary>
[TestFixture]
[NonParallelizable]
public class SongLanguageWireTest
{
    private static long knownSetId;
    private static long knownBeatmapId;
    private static string knownChecksum = null!;

    private static long unknownSetId;
    private static long unknownBeatmapId;
    private static string unknownChecksum = null!;

    private static long typistId;
    private static string bearer = null!;

    private static NpgsqlDataSource dataSource = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        await PublicSiteSeed.EnsureSeededAsync();

        dataSource = NpgsqlDataSource.Create(WebsiteFixture.ConnectionString);

        await using var conn = await dataSource.OpenConnectionAsync();

        typistId = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO users (username, email, password_hash, country_code)
            VALUES ('song language typist', 'song.language.typist@example.com', 'x', 'US')
            RETURNING id
            """);

        bearer = (await new TokenService(new Db(dataSource)).IssueAsync(typistId)).AccessToken;

        (knownSetId, knownBeatmapId, knownChecksum) = await seedSetAsync(conn, "Kotoba no Uta", "japanese");
        (unknownSetId, unknownBeatmapId, unknownChecksum) = await seedSetAsync(conn, "Wordless Oldie", "");
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (dataSource != null)
            await dataSource.DisposeAsync();
    }

    [Test]
    public async Task LookupByChecksum_EmitsTheStoredLanguage()
    {
        var set = (JObject)(await getObjectAsync($"/api/v2/beatmaps/lookup?checksum={knownChecksum}"))["beatmapset"]!;

        Assert.Multiple(() =>
        {
            Assert.That((long)set["id"]!, Is.EqualTo(knownSetId));
            Assert.That((string)set["song_language"]!, Is.EqualTo("japanese"));
            Assert.That(set.ContainsKey("language"), Is.False);
        });
    }

    [Test]
    public async Task LookupById_EmitsTheStoredLanguage()
    {
        var set = (JObject)(await getObjectAsync($"/api/v2/beatmaps/lookup?id={knownBeatmapId}"))["beatmapset"]!;

        Assert.Multiple(() =>
        {
            Assert.That((long)set["id"]!, Is.EqualTo(knownSetId));
            Assert.That((string)set["song_language"]!, Is.EqualTo("japanese"));
            Assert.That(set.ContainsKey("language"), Is.False);
        });
    }

    [Test]
    public async Task Lookup_OfASetWithNoLanguage_EmitsAnEmptyString()
    {
        var byChecksum = (JObject)(await getObjectAsync($"/api/v2/beatmaps/lookup?checksum={unknownChecksum}"))["beatmapset"]!;
        var byId = (JObject)(await getObjectAsync($"/api/v2/beatmaps/lookup?id={unknownBeatmapId}"))["beatmapset"]!;

        Assert.Multiple(() =>
        {
            foreach (var set in new[] { byChecksum, byId })
            {
                Assert.That((long)set["id"]!, Is.EqualTo(unknownSetId));
                Assert.That(set.ContainsKey("song_language"), Is.True, "the key is present even when empty");
                Assert.That(set["song_language"]!.Type, Is.EqualTo(JTokenType.String), "never null");
                Assert.That((string)set["song_language"]!, Is.EqualTo(""));
                Assert.That(set.ContainsKey("language"), Is.False);
            }
        });
    }

    [Test]
    public async Task BeatmapsetGet_EmitsTheStoredLanguage()
    {
        var known = await getObjectAsync($"/api/v2/beatmapsets/{knownSetId}");
        var unknown = await getObjectAsync($"/api/v2/beatmapsets/{unknownSetId}");

        Assert.Multiple(() =>
        {
            Assert.That((string)known["song_language"]!, Is.EqualTo("japanese"));
            Assert.That((string)unknown["song_language"]!, Is.EqualTo(""));
            Assert.That(known.ContainsKey("language"), Is.False);
            Assert.That(unknown.ContainsKey("language"), Is.False);
        });
    }

    [Test]
    public async Task ProfileSections_EmitTheStoredLanguageOnTheirNestedSets()
    {
        var recent = await getArrayAsync($"/api/v2/users/{typistId}/scores/recent");
        var mostPlayed = await getArrayAsync($"/api/v2/users/{typistId}/beatmapsets/most_played");

        var recentSets = recent.Select(s => (JObject)s["beatmap"]!["beatmapset"]!).ToList();
        var mostPlayedSets = mostPlayed.SelectMany(p => new[] { (JObject)p["beatmapset"]!, (JObject)p["beatmap"]!["beatmapset"]! }).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(recentSets, Has.Count.EqualTo(2));
            Assert.That(mostPlayedSets, Has.Count.EqualTo(4));

            foreach (var set in recentSets.Concat(mostPlayedSets))
            {
                string expected = (long)set["id"]! == knownSetId ? "japanese" : "";
                Assert.That((string?)set["song_language"], Is.EqualTo(expected));
                Assert.That(set.ContainsKey("language"), Is.False);
            }
        });
    }

    // ---- helpers ----

    /// <summary>A ranked set with one live difficulty and one ranked play of it by the typist.</summary>
    private static async Task<(long SetId, long BeatmapId, string Checksum)> seedSetAsync(NpgsqlConnection conn, string title, string language)
    {
        long setId = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmapsets (owner_id, title, artist, language, status, submitted_at, updated_at)
            VALUES (@ownerId, @title, 'Song Language Artist', @language, 'ranked', now() - interval '3 days', now() - interval '3 days')
            RETURNING id
            """,
            new { ownerId = PublicSiteSeed.MapperId, title, language });

        string checksum = Guid.NewGuid().ToString("N");

        long beatmapId = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmaps (set_id, version_name, checksum_md5, total_length_s, drain_length_s, difficulty_rating, filename, lyrics)
            VALUES (@setId, 'only', @checksum, 60, 0, 3.0, 'only.osu', 'some lyrics here')
            RETURNING id
            """,
            new { setId, checksum });

        await conn.ExecuteAsync(
            """
            INSERT INTO scores
                (user_id, beatmap_id, total_score, accuracy, completion, max_combo, rank, passed, ranked,
                 mods, statistics, maximum_statistics, ended_at)
            VALUES
                (@typistId, @beatmapId, 500000, 0.95, 0.98, 50, 'S', true, true,
                 '[]'::jsonb, '{"great":90}'::jsonb, '{"great":100}'::jsonb, now())
            """,
            new { typistId, beatmapId });

        return (setId, beatmapId, checksum);
    }

    private static async Task<string> getBodyAsync(string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

        using var response = await WebsiteFixture.Client.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), $"{url}: {body}");
        return body;
    }

    private static async Task<JObject> getObjectAsync(string url) => JObject.Parse(await getBodyAsync(url));

    private static async Task<JArray> getArrayAsync(string url) => JArray.Parse(await getBodyAsync(url));
}
