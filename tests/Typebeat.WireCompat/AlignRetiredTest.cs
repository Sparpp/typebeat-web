using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Typebeat.WireCompat;

/// <summary>
/// The first server-side aligner route (backlog 287) answers pre-287 clients honestly.
///
/// This replaced AlignFlowTest when the aligner was retired. Backlog 413 brought the server aligner
/// back on a NEW route (/api/v2/typebeat/server-align, pinned by ServerAlignFlowTest in
/// Typebeat.Web.Tests), so what is left to pin here is the old route's tombstone, because the
/// desktop import fallback (RemoteAlignClient) in every build shipped before 287 keeps POSTing to
/// it with the old protocol: a 410 carrying the error envelope, no auth needed to get it, nothing
/// queued, and the id-bearing routes genuinely gone rather than tombstoned too.
/// </summary>
[TestFixture]
public class AlignRetiredTest
{
    private static HttpClient client => ServerFixture.Client;

    [Test]
    public async Task Align_Post_Is410_WithLocalAlignerMessage_AndNeedsNoBearer()
    {
        // No bearer at all: the tombstone must reach a client whose token has expired since the
        // build shipped, so the answer is 410 and not 401.
        using var content = buildForm();
        using var resp = await client.PostAsync("/api/v2/typebeat/align", content);

        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.Gone));

        var body = JObject.Parse(await resp.Content.ReadAsStringAsync());
        string error = body["error"]!.Value<string>()!;

        // The osu-web error envelope, so the client's existing error path reads it unchanged.
        Assert.That(body.Properties().Select(p => p.Name), Is.EqualTo(new[] { "error" }));

        // It has to say where to go: a newer game (the server aligner on its import screen), or
        // either way the import flow can take without a server.
        Assert.That(error, Does.Contain("retired"));
        Assert.That(error, Does.Contain("import screen"));
        Assert.That(error, Does.Contain("local auto-aligner"));
        Assert.That(error, Does.Contain("Settings"));
        Assert.That(error, Does.Contain("[mm:ss.xx]"));
    }

    [Test]
    public async Task Align_Post_WithBearer_Is410_AndQueuesNothing()
    {
        string jobsDir = Path.Combine(ServerFixture.FileRoot, "align-jobs");
        int jobsBefore = Directory.Exists(jobsDir) ? Directory.EnumerateDirectories(jobsDir).Count() : 0;

        using var req = ServerFixture.Authed(HttpMethod.Post, "/api/v2/typebeat/align");
        req.Content = buildForm();

        using var resp = await client.SendAsync(req);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.Gone));

        // The old route never feeds the server aligner's job store (backlog 413): no job directory
        // appears, whatever the new route's own tests may have left there.
        Assert.That(Directory.Exists(jobsDir) ? Directory.EnumerateDirectories(jobsDir).Count() : 0, Is.EqualTo(jobsBefore));
    }

    [Test]
    public async Task Align_JobIdRoutes_AreGone_Not410()
    {
        const string id = "deadbeefdeadbeefdeadbeefdeadbeef";

        // GET/DELETE by job id are unmapped (no POST can hand out an id, so nothing reaches them).
        using (var req = ServerFixture.Authed(HttpMethod.Get, $"/api/v2/typebeat/align/{id}"))
        using (var resp = await client.SendAsync(req))
            Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        using (var req = ServerFixture.Authed(HttpMethod.Delete, $"/api/v2/typebeat/align/{id}"))
        using (var resp = await client.SendAsync(req))
            Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    /// <summary>The multipart body a shipped client sends, which the tombstone never reads.</summary>
    private static MultipartFormDataContent buildForm()
    {
        var audio = new ByteArrayContent(Encoding.ASCII.GetBytes("ID3 fake mp3 payload"));
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");

        return new MultipartFormDataContent
        {
            { audio, "audio", "song.mp3" },
            { new StringContent("[00:01.00] hello world\n"), "lyrics" },
            { new StringContent("Test Artist"), "artist" },
            { new StringContent("Test Title"), "title" },
        };
    }
}
