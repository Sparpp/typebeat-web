using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Npgsql;
using Typebeat.Web.Endpoints;

namespace Typebeat.Web.Tests.Website;

/// <summary>
/// /play submission integrity (backlog 312), the server half: the browser's per-revision build
/// identity and the served-map checksum on POST /play/token. The player half (the playback-validity
/// veto and the token body) is pinned in <c>PlaybackValidityTest</c> and <c>PlaySubmitStatusTest</c>.
///
/// <para>One DEDICATED user signs in once for the whole fixture: EmailCodeService caps login codes
/// per (user, purpose) per hour, and the shared players elsewhere already spend that budget.</para>
/// </summary>
[TestFixture]
[NonParallelizable]
public class PlaySubmitIntegrityTest
{
    private const string username = "integrity player";
    private const string password = "integritypass-123456";

    private static HttpClient client = null!;
    private static string csrf = null!;
    private static long beatmapId;
    private static string checksum = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        await PublicSiteSeed.EnsureSeededAsync();
        await WebsiteFixture.SeedUserAsync(username, "integrity.player@example.com", password, verified: true);

        (client, _) = WebsiteFixture.CreateBrowser();
        await WebsiteFixture.LoginAndVerifyAsync(client, username, password);

        string page = await (await client.GetAsync("/play")).Content.ReadAsStringAsync();
        var csrfMatch = Regex.Match(page, "csrf:\\s*\"([^\"]+)\"");
        Assert.That(csrfMatch.Success, Is.True, "the /play page must publish an antiforgery token");
        csrf = csrfMatch.Groups[1].Value;

        await using var conn = new NpgsqlConnection(WebsiteFixture.ConnectionString);
        await conn.OpenAsync();
        (beatmapId, checksum) = await conn.QuerySingleAsync<(long, string)>(
            """
            SELECT id, checksum_md5 FROM beatmaps
            WHERE set_id = @setId AND filename LIKE '%.osu'
            ORDER BY id LIMIT 1
            """,
            new { setId = PublicSiteSeed.CoveredSetId });
    }

    /// <summary>
    /// The page renders the revision the server computed at startup, in the shape the token route
    /// accepts, and it is the hash of the three scripts' asp-append-version values: the same
    /// <c>?v=</c> strings the page's own script tags carry.
    /// </summary>
    [Test]
    public async Task ThePlayPage_RendersTheScriptRevision()
    {
        string page = await (await client.GetAsync("/play")).Content.ReadAsStringAsync();
        var match = Regex.Match(page, "revision:\\s*\"([0-9a-f]{16})\"");
        Assert.That(match.Success, Is.True, "the /play page must publish a 16-hex revision");

        var sb = new StringBuilder();
        foreach (string script in new[] { "/js/typebeat-core.js", "/js/typebeat-player.js", "/js/play.js" })
        {
            var v = Regex.Match(page, Regex.Escape(script) + "\\?v=([^\"&]+)\"");
            Assert.That(v.Success, Is.True, $"{script} is stamped by asp-append-version");
            sb.Append(script).Append(':').Append(v.Groups[1].Value).Append('\n');
        }

        string expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())), 0, 8).ToLowerInvariant();
        Assert.Multiple(() =>
        {
            Assert.That(match.Groups[1].Value, Is.EqualTo(expected));
            Assert.That(PlayEndpoints.Revision, Is.EqualTo(expected));
        });
    }

    /// <summary>
    /// Record-don't-reject: a revision the server has never seen is registered as its own build row
    /// ('web-' + revision) and the token points at it. A body naming none falls back to the
    /// synthetic web-player build, as every token did before.
    /// </summary>
    [Test]
    public async Task ARevision_IsRegisteredAsItsOwnBuild_AndNoRevisionIsWebPlayer()
    {
        string revision = freshRevision();

        long withRevision = await tokenAsync(new { setId = PublicSiteSeed.CoveredSetId, beatmapId, revision, beatmapHash = checksum });
        long without = await tokenAsync(new { setId = PublicSiteSeed.CoveredSetId, beatmapId });

        await using var conn = new NpgsqlConnection(WebsiteFixture.ConnectionString);
        await conn.OpenAsync();
        string buildOf(long tokenId) => conn.ExecuteScalar<string>(
            "SELECT b.version_hash FROM score_tokens t JOIN builds b ON b.id = t.build_id WHERE t.id = @tokenId", new { tokenId })!;

        Assert.Multiple(() =>
        {
            Assert.That(buildOf(withRevision), Is.EqualTo("web-" + revision));
            Assert.That(conn.ExecuteScalar<bool>("SELECT blocked FROM builds WHERE version_hash = @h", new { h = "web-" + revision }), Is.False);
            Assert.That(buildOf(without), Is.EqualTo("web-player"));
        });
    }

    /// <summary>
    /// The blocked-build lever, per revision: blocking one browser revision refuses ITS tokens with
    /// the desktop's wording, while another revision (the current page) is still served. A revision
    /// that is not the shape the server produces is refused rather than written into builds.
    /// </summary>
    [Test]
    public async Task ABlockedRevision_IsRefusedAsAnOutdatedClient_WhileAnotherStillPlays()
    {
        string blocked = freshRevision();
        string other = freshRevision();

        await using (var conn = new NpgsqlConnection(WebsiteFixture.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("INSERT INTO builds (version_hash, blocked) VALUES (@h, true)", new { h = "web-" + blocked });
        }

        using var refused = await tokenResponseAsync(new { setId = PublicSiteSeed.CoveredSetId, beatmapId, revision = blocked, beatmapHash = checksum });
        using var served = await tokenResponseAsync(new { setId = PublicSiteSeed.CoveredSetId, beatmapId, revision = other, beatmapHash = checksum });
        using var garbage = await tokenResponseAsync(new { setId = PublicSiteSeed.CoveredSetId, beatmapId, revision = "not a revision", beatmapHash = checksum });

        string? refusedError = await errorOf(refused);

        Assert.Multiple(() =>
        {
            Assert.That(refused.StatusCode, Is.EqualTo(HttpStatusCode.UnprocessableEntity));
            Assert.That(refusedError, Is.EqualTo("outdated client"));
            Assert.That(served.StatusCode, Is.EqualTo(HttpStatusCode.OK), "the control: an unblocked revision");
            Assert.That(garbage.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        });
    }

    /// <summary>
    /// The /osu route answers the served difficulty's stored checksum in its header; handing that
    /// back is accepted, and any other value (a map re-uploaded since the tab loaded it) is refused
    /// with the desktop's 422 and wording. The checksum comparison is case-insensitive, as the
    /// desktop's is.
    /// </summary>
    [Test]
    public async Task TheServedChecksum_IsAccepted_AndAMismatchIsRefusedWithTheDesktops422()
    {
        // The multi-difficulty set, because it is the seed whose .osu blobs are really stored:
        // the header must be each difficulty's OWN checksum, not the set's primary one.
        long setId = PublicSiteSeed.MultiDiffSetId, hardId = PublicSiteSeed.MultiDiffHardId, easyId = PublicSiteSeed.MultiDiffEasyId;
        string servedHard = await servedChecksumAsync(setId, hardId);
        string servedEasy = await servedChecksumAsync(setId, easyId);

        await using var conn = new NpgsqlConnection(WebsiteFixture.ConnectionString);
        await conn.OpenAsync();
        string storedHard = await conn.ExecuteScalarAsync<string>("SELECT checksum_md5 FROM beatmaps WHERE id = @hardId", new { hardId }) ?? "";
        string storedEasy = await conn.ExecuteScalarAsync<string>("SELECT checksum_md5 FROM beatmaps WHERE id = @easyId", new { easyId }) ?? "";

        string revision = freshRevision();
        using var matching = await tokenResponseAsync(new { setId, beatmapId = hardId, revision, beatmapHash = servedHard.ToUpperInvariant() });
        using var otherDiff = await tokenResponseAsync(new { setId, beatmapId = hardId, revision, beatmapHash = servedEasy });
        using var stale = await tokenResponseAsync(new { setId, beatmapId = hardId, revision, beatmapHash = "0123456789abcdef0123456789abcdef" });
        string? staleError = await errorOf(stale);

        Assert.Multiple(() =>
        {
            Assert.That(servedHard, Is.EqualTo(storedHard), "the header is the served difficulty's stored checksum_md5");
            Assert.That(servedEasy, Is.EqualTo(storedEasy));
            Assert.That(servedHard, Is.Not.EqualTo(servedEasy), "precondition: the two difficulties differ");
            Assert.That(matching.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(otherDiff.StatusCode, Is.EqualTo(HttpStatusCode.UnprocessableEntity), "another difficulty's text");
            Assert.That(stale.StatusCode, Is.EqualTo(HttpStatusCode.UnprocessableEntity));
            Assert.That(staleError, Is.EqualTo("invalid or missing beatmap_hash"));
        });
    }

    private static async Task<string> servedChecksumAsync(long setId, long diffId)
    {
        using var osu = await client.GetAsync($"/play/map/{setId}/osu?diff={diffId}");
        Assert.That(osu.StatusCode, Is.EqualTo(HttpStatusCode.OK), $"/osu for diff {diffId}");
        return osu.Headers.GetValues(PlayEndpoints.ChecksumHeader).Single();
    }

    // ---- helpers ----

    private static string freshRevision() => Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();

    private static async Task<HttpResponseMessage> tokenResponseAsync(object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/play/token");
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        request.Content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");
        return await client.SendAsync(request);
    }

    private static async Task<long> tokenAsync(object body)
    {
        using var response = await tokenResponseAsync(body);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), "play token");
        return (long)JObject.Parse(await response.Content.ReadAsStringAsync())["id"]!;
    }

    private static async Task<string?> errorOf(HttpResponseMessage response)
        => (string?)JObject.Parse(await response.Content.ReadAsStringAsync())["error"];
}
