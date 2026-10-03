using System.Net;
using System.Net.Http.Headers;
using Dapper;
using Newtonsoft.Json.Linq;
using Npgsql;
using Typebeat.Web.Auth;
using Typebeat.Web.Data;

namespace Typebeat.Web.Tests.Website;

/// <summary>
/// The set-level vocals-stem flag (backlog 396) reaches the client as <c>has_vocals_stem</c> on the
/// two routes that serve an APIBeatmapSet: the import-time metadata lookup and the beatmapset GET.
/// It is true when the set's CURRENT version carries a <c>vocals.ogg</c>/<c>vocals.wav</c> file (a
/// backfilled set), false otherwise, and always present. The client's UPDATE offer reads it against
/// the stem its local copy holds, which is how a stem-only version cut (every <c>.osu</c> MD5
/// unchanged) finally offers an update.
/// </summary>
[TestFixture]
[NonParallelizable]
public class VocalsStemWireTest
{
    private static long stemSetId;
    private static long stemBeatmapId;
    private static string stemChecksum = null!;

    private static long plainSetId;
    private static long plainBeatmapId;
    private static string plainChecksum = null!;

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
            VALUES ('vocals stem typist', 'vocals.stem.typist@example.com', 'x', 'US')
            RETURNING id
            """);

        bearer = (await new TokenService(new Db(dataSource)).IssueAsync(typistId)).AccessToken;

        // A set whose current version carries the stem: version 1 is the pre-stem snapshot, version 2
        // adds vocals.ogg (exactly what the backfill cuts) and current_version points at 2.
        (stemSetId, stemBeatmapId, stemChecksum) = await seedSetAsync(conn, "Stemmed Song", withStem: true);

        // An ordinary set: its current version's manifest has no stem file.
        (plainSetId, plainBeatmapId, plainChecksum) = await seedSetAsync(conn, "Plain Song", withStem: false);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (dataSource != null)
            await dataSource.DisposeAsync();
    }

    [Test]
    public async Task LookupByChecksum_ReportsTheStemFlag()
    {
        var stem = (JObject)(await getObjectAsync($"/api/v2/beatmaps/lookup?checksum={stemChecksum}"))["beatmapset"]!;
        var plain = (JObject)(await getObjectAsync($"/api/v2/beatmaps/lookup?checksum={plainChecksum}"))["beatmapset"]!;

        Assert.Multiple(() =>
        {
            Assert.That((long)stem["id"]!, Is.EqualTo(stemSetId));
            Assert.That((bool)stem["has_vocals_stem"]!, Is.True, "a backfilled set reports its stem");

            Assert.That((long)plain["id"]!, Is.EqualTo(plainSetId));
            Assert.That((bool)plain["has_vocals_stem"]!, Is.False, "an ordinary set has none");
        });
    }

    [Test]
    public async Task LookupById_ReportsTheStemFlag()
    {
        var stem = (JObject)(await getObjectAsync($"/api/v2/beatmaps/lookup?id={stemBeatmapId}"))["beatmapset"]!;
        var plain = (JObject)(await getObjectAsync($"/api/v2/beatmaps/lookup?id={plainBeatmapId}"))["beatmapset"]!;

        Assert.Multiple(() =>
        {
            Assert.That((bool)stem["has_vocals_stem"]!, Is.True);
            Assert.That((bool)plain["has_vocals_stem"]!, Is.False);
        });
    }

    [Test]
    public async Task TheKeyIsAlwaysPresent_NeverNull()
    {
        var set = (JObject)(await getObjectAsync($"/api/v2/beatmaps/lookup?checksum={plainChecksum}"))["beatmapset"]!;

        Assert.Multiple(() =>
        {
            Assert.That(set.ContainsKey("has_vocals_stem"), Is.True, "the key is emitted even when false");
            Assert.That(set["has_vocals_stem"]!.Type, Is.EqualTo(JTokenType.Boolean), "never null");
        });
    }

    [Test]
    public async Task BeatmapsetGet_ReportsTheStemFlag()
    {
        var stem = await getObjectAsync($"/api/v2/beatmapsets/{stemSetId}");
        var plain = await getObjectAsync($"/api/v2/beatmapsets/{plainSetId}");

        Assert.Multiple(() =>
        {
            Assert.That((bool)stem["has_vocals_stem"]!, Is.True);
            Assert.That((bool)plain["has_vocals_stem"]!, Is.False);
        });
    }

    [Test]
    public async Task AWavStemAlsoCounts()
    {
        // The game accepts vocals.wav when the producing machine had no Vorbis encoder (VocalsStem
        // .Filenames), so the server's presence test must accept it too.
        await using var conn = await dataSource.OpenConnectionAsync();

        long setId = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmapsets (owner_id, title, artist, status)
            VALUES (@ownerId, 'Wav Stem Song', 'Stem Artist', 'ranked')
            RETURNING id
            """,
            new { ownerId = PublicSiteSeed.MapperId });

        byte[] sha = System.Security.Cryptography.SHA256.HashData(new byte[] { 9, 9, 9 });
        await conn.ExecuteAsync("INSERT INTO files (sha256, size) VALUES (@sha, 3) ON CONFLICT DO NOTHING", new { sha });
        long versionId = await conn.ExecuteScalarAsync<long>(
            "INSERT INTO set_versions (set_id, version_no) VALUES (@setId, 1) RETURNING id", new { setId });
        await conn.ExecuteAsync(
            "INSERT INTO version_files (version_id, sha256, filename) VALUES (@versionId, @sha, 'vocals.wav')",
            new { versionId, sha });

        string checksum = Guid.NewGuid().ToString("N");
        await conn.ExecuteAsync(
            """
            INSERT INTO beatmaps (set_id, version_name, checksum_md5, total_length_s, drain_length_s, difficulty_rating, filename)
            VALUES (@setId, 'only', @checksum, 60, 0, 3.0, 'only.osu')
            """,
            new { setId, checksum });

        var set = (JObject)(await getObjectAsync($"/api/v2/beatmaps/lookup?checksum={checksum}"))["beatmapset"]!;
        Assert.That((bool)set["has_vocals_stem"]!, Is.True, "vocals.wav is a stem too");
    }

    // ---- helpers ----

    /// <summary>
    /// A ranked set with one live difficulty. When <paramref name="withStem"/> the manifest carries a
    /// <c>vocals.ogg</c> in the CURRENT version (version 2, with a stem-less version 1 behind it, the
    /// backfill's exact shape); otherwise its current version carries only the difficulty's files.
    /// </summary>
    private static async Task<(long SetId, long BeatmapId, string Checksum)> seedSetAsync(NpgsqlConnection conn, string title, bool withStem)
    {
        long setId = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmapsets (owner_id, title, artist, status, submitted_at, updated_at)
            VALUES (@ownerId, @title, 'Stem Artist', 'ranked', now() - interval '3 days', now() - interval '3 days')
            RETURNING id
            """,
            new { ownerId = PublicSiteSeed.MapperId, title });

        string checksum = Guid.NewGuid().ToString("N");

        long beatmapId = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmaps (set_id, version_name, checksum_md5, total_length_s, drain_length_s, difficulty_rating, filename, lyrics)
            VALUES (@setId, 'only', @checksum, 60, 0, 3.0, 'only.osu', 'some lyrics here')
            RETURNING id
            """,
            new { setId, checksum });

        byte[] audio = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(title + "-audio"));
        byte[] osu = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(title + "-osu"));
        byte[] stem = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(title + "-stem"));

        await conn.ExecuteAsync("INSERT INTO files (sha256, size) VALUES (@sha, 100) ON CONFLICT DO NOTHING", new { sha = audio });
        await conn.ExecuteAsync("INSERT INTO files (sha256, size) VALUES (@sha, 100) ON CONFLICT DO NOTHING", new { sha = osu });

        long version1 = await conn.ExecuteScalarAsync<long>(
            "INSERT INTO set_versions (set_id, version_no) VALUES (@setId, 1) RETURNING id", new { setId });

        await conn.ExecuteAsync(
            "INSERT INTO version_files (version_id, sha256, filename) VALUES (@versionId, @sha, 'only.osu')",
            new { versionId = version1, sha = osu });
        await conn.ExecuteAsync(
            "INSERT INTO version_files (version_id, sha256, filename) VALUES (@versionId, @sha, 'audio.mp3')",
            new { versionId = version1, sha = audio });

        if (withStem)
        {
            await conn.ExecuteAsync("INSERT INTO files (sha256, size) VALUES (@sha, 200) ON CONFLICT DO NOTHING", new { sha = stem });

            long version2 = await conn.ExecuteScalarAsync<long>(
                "INSERT INTO set_versions (set_id, version_no) VALUES (@setId, 2) RETURNING id", new { setId });

            await conn.ExecuteAsync(
                "INSERT INTO version_files (version_id, sha256, filename) VALUES (@versionId, @sha, 'only.osu')",
                new { versionId = version2, sha = osu });
            await conn.ExecuteAsync(
                "INSERT INTO version_files (version_id, sha256, filename) VALUES (@versionId, @sha, 'audio.mp3')",
                new { versionId = version2, sha = audio });
            await conn.ExecuteAsync(
                "INSERT INTO version_files (version_id, sha256, filename) VALUES (@versionId, @sha, 'vocals.ogg')",
                new { versionId = version2, sha = stem });

            await conn.ExecuteAsync("UPDATE beatmapsets SET current_version = 2 WHERE id = @setId", new { setId });
        }

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
}
