using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Typebeat.WireCompat;

/// <summary>
/// The server-side alignment job flow end-to-end through the real HTTP surface: create (multipart)
/// → pending → simulate the worker terminating the job on disk → done with the timing payload.
/// The worker container itself is not under test (python/torch); its half of the file protocol is
/// played by the test writing into the job directory, which is exactly the contract seam.
/// </summary>
[TestFixture]
[NonParallelizable]
public class AlignFlowTest
{
    private static HttpClient client => ServerFixture.Client;

    private const string stamped_lyrics = "[00:01.00] hello world\n[00:03.50] second line\n";

    [Test]
    public async Task AlignJob_Create_Poll_WorkerCompletes_Done()
    {
        // --- create ---
        (HttpStatusCode status, JObject body) = await createJobAsync(stamped_lyrics);

        Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
        string id = body["id"]!.Value<string>()!;
        Assert.That(id, Is.Not.Empty);
        Assert.That(body["state"]!.Value<string>(), Is.EqualTo("pending"));

        // Inputs + manifest landed on disk, with the stamped lyrics detected as "ref" anchors.
        string jobDir = Path.Combine(ServerFixture.FileRoot, "align-jobs", id);
        Assert.That(File.Exists(Path.Combine(jobDir, "job.json")), Is.True);
        Assert.That(File.Exists(Path.Combine(jobDir, "input.mp3")), Is.True);
        Assert.That(File.Exists(Path.Combine(jobDir, "lyrics.txt")), Is.True);
        Assert.That(File.ReadAllText(Path.Combine(jobDir, "job.json")), Does.Contain("\"anchors\":\"ref\""));

        // --- a second create while the first is active must 409 ---
        (HttpStatusCode second, _) = await createJobAsync(stamped_lyrics);
        Assert.That(second, Is.EqualTo(HttpStatusCode.Conflict));

        // --- poll: pending (no worker in this harness) ---
        JObject polled = await getJobAsync(id);
        Assert.That(polled["state"]!.Value<string>(), Is.EqualTo("pending"));

        // --- worker's half of the protocol: progress, then the terminal timing.json ---
        await File.WriteAllTextAsync(Path.Combine(jobDir, "progress.log"), "separating vocals 42%\n");
        File.Create(Path.Combine(jobDir, ".running")).Dispose();

        polled = await getJobAsync(id);
        Assert.That(polled["state"]!.Value<string>(), Is.EqualTo("running"));
        Assert.That(polled["progress"]!.Value<string>(), Is.EqualTo("separating vocals 42%"));

        const string timing = /*lang=json*/ """{"version":2,"song_end_ms":4000,"lines":[]}""";
        await File.WriteAllTextAsync(Path.Combine(jobDir, "timing.json"), timing);

        polled = await getJobAsync(id);
        Assert.That(polled["state"]!.Value<string>(), Is.EqualTo("done"));
        Assert.That(polled["timing_json"]!.Value<string>(), Is.EqualTo(timing));
    }

    [Test]
    public async Task AlignJob_Cancel_DropsMarker_FreesSlot_AndReportsFailed()
    {
        // --- create, then confirm it holds the one-active-job slot ---
        (HttpStatusCode status, JObject body) = await createJobAsync(stamped_lyrics);
        Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
        string id = body["id"]!.Value<string>()!;
        string jobDir = Path.Combine(ServerFixture.FileRoot, "align-jobs", id);

        (HttpStatusCode second, _) = await createJobAsync(stamped_lyrics);
        Assert.That(second, Is.EqualTo(HttpStatusCode.Conflict), "a job is active");

        // --- cancel (DELETE) ---
        Assert.That(await cancelJobAsync(id), Is.EqualTo(HttpStatusCode.OK));

        // The worker's cancel signal is on disk...
        Assert.That(File.Exists(Path.Combine(jobDir, "cancel")), Is.True);

        // ...the job now reports a terminal failure with the cancellation reason...
        JObject polled = await getJobAsync(id);
        Assert.That(polled["state"]!.Value<string>(), Is.EqualTo("failed"));
        Assert.That(polled["error"]!.Value<string>(), Does.Contain("cancelled"));

        // ...and the slot is freed: a fresh create succeeds where the second was blocked.
        (HttpStatusCode third, JObject thirdBody) = await createJobAsync(stamped_lyrics);
        Assert.That(third, Is.EqualTo(HttpStatusCode.OK), "cancelling frees the one-active-job slot");

        // Clean up the job we just created so we don't leave an active one for sibling tests.
        await cancelJobAsync(thirdBody["id"]!.Value<string>()!);

        // Cancelling an unknown job id is a 404 (same visibility rule as GET).
        Assert.That(await cancelJobAsync("deadbeefdeadbeefdeadbeefdeadbeef"), Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task AlignJob_FailureAndValidation()
    {
        // Unknown job id → 404.
        using (var req = ServerFixture.Authed(HttpMethod.Get, "/api/v2/typebeat/align/deadbeefdeadbeefdeadbeefdeadbeef"))
        using (var resp = await client.SendAsync(req))
            Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        // Unsupported extension → 422.
        (HttpStatusCode status, _) = await createJobAsync(stamped_lyrics, fileName: "notes.txt");
        Assert.That(status, Is.EqualTo(HttpStatusCode.UnprocessableEntity));

        // Missing lyrics → 422.
        (status, _) = await createJobAsync(lyrics: "");
        Assert.That(status, Is.EqualTo(HttpStatusCode.UnprocessableEntity));

        // No bearer → 401.
        using (var content = buildForm(stamped_lyrics, "song.mp3"))
        using (var resp = await client.PostAsync("/api/v2/typebeat/align", content))
            Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    private static MultipartFormDataContent buildForm(string lyrics, string fileName)
    {
        var audio = new ByteArrayContent(Encoding.ASCII.GetBytes("ID3 fake mp3 payload"));
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");

        return new MultipartFormDataContent
        {
            { audio, "audio", fileName },
            { new StringContent(lyrics), "lyrics" },
            { new StringContent("Test Artist"), "artist" },
            { new StringContent("Test Title"), "title" },
        };
    }

    private static async Task<(HttpStatusCode Status, JObject Body)> createJobAsync(string lyrics, string fileName = "song.mp3")
    {
        using var req = ServerFixture.Authed(HttpMethod.Post, "/api/v2/typebeat/align");
        req.Content = buildForm(lyrics, fileName);

        using var resp = await client.SendAsync(req);
        string text = await resp.Content.ReadAsStringAsync();
        return (resp.StatusCode, text.StartsWith('{') ? JObject.Parse(text) : new JObject());
    }

    private static async Task<JObject> getJobAsync(string id)
    {
        using var req = ServerFixture.Authed(HttpMethod.Get, $"/api/v2/typebeat/align/{id}");
        using var resp = await client.SendAsync(req);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        return JObject.Parse(await resp.Content.ReadAsStringAsync());
    }

    private static async Task<HttpStatusCode> cancelJobAsync(string id)
    {
        using var req = ServerFixture.Authed(HttpMethod.Delete, $"/api/v2/typebeat/align/{id}");
        using var resp = await client.SendAsync(req);
        return resp.StatusCode;
    }
}
