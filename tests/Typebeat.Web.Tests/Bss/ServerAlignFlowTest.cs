using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Newtonsoft.Json.Linq;
using Typebeat.Web.Align;
using Typebeat.Web.Endpoints;

namespace Typebeat.Web.Tests.Bss;

/// <summary>
/// The server-hosted aligner (backlog 413) end to end through the real HTTP surface, on the BSS
/// host (its throwaway file root is where the job store lives). The worker container itself is not
/// under test (python/torch); its half of the file protocol is played by the test writing into the
/// jobs directory (the heartbeat, the claim, progress, the terminal files), which is exactly the
/// contract seam. Each test signs in as its own fresh player, because submissions are rate limited
/// per token and capped per player.
/// </summary>
[TestFixture]
[NonParallelizable]
public class ServerAlignFlowTest
{
    private const string path = ServerAlignEndpoints.Path;
    private const string stamped_lyrics = "[00:01.00] hello world\n[00:03.50] second line\n";
    private const string bare_lyrics = "hello world\nsecond line\n";

    private static string jobsRoot => Path.Combine(BssFixture.FileRoot, "align-jobs");

    [SetUp]
    public void SetUp()
    {
        if (Directory.Exists(jobsRoot))
            Directory.Delete(jobsRoot, recursive: true);

        writeHeartbeat(DateTimeOffset.UtcNow);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(jobsRoot))
            Directory.Delete(jobsRoot, recursive: true);
    }

    [Test]
    public async Task Create_Poll_WorkerCompletes_Done()
    {
        var (_, bearer) = await newPlayerAsync();

        var (status, body) = await createAsync(bearer, stamped_lyrics, language: "English", vocalMode: "estimated");

        Assert.That(status, Is.EqualTo(HttpStatusCode.OK), body.ToString());
        string id = (string)body["id"]!;

        Assert.Multiple(() =>
        {
            Assert.That(body.Properties().Select(p => p.Name), Is.EqualTo(new[] { "id", "state", "queue_position", "aligner_version" }));
            Assert.That(id, Is.Not.Empty);
            Assert.That((string)body["state"]!, Is.EqualTo("pending"));
            Assert.That((int)body["queue_position"]!, Is.EqualTo(1));
            Assert.That((string)body["aligner_version"]!, Is.EqualTo("11"));
        });

        // Inputs and manifest landed on disk; the manifest carries what the worker turns into flags.
        string jobDir = Path.Combine(jobsRoot, id);
        using (var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(jobDir, "job.json"))))
        {
            var m = manifest.RootElement;
            Assert.Multiple(() =>
            {
                Assert.That(File.Exists(Path.Combine(jobDir, "input.mp3")), Is.True);
                Assert.That(File.ReadAllText(Path.Combine(jobDir, "lyrics.txt")), Is.EqualTo(stamped_lyrics));
                Assert.That(m.GetProperty("anchors").GetString(), Is.EqualTo("ref"));
                Assert.That(m.GetProperty("vocal_mode").GetString(), Is.EqualTo("estimated"));
                Assert.That(m.GetProperty("language").GetString(), Is.EqualTo("english"), "the server canonicalises the language");
                Assert.That(m.GetProperty("audio_file").GetString(), Is.EqualTo("input.mp3"));
                Assert.That(m.GetProperty("aligner_version").GetString(), Is.EqualTo("11"));
            });
        }

        // A second submission while the first is active is a 409 naming the job.
        var (second, secondBody) = await createAsync(bearer, stamped_lyrics);
        Assert.That(second, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That((string)secondBody["error"]!, Does.Contain(id));

        // Pending (no worker in this harness): the full shape, nulls included.
        var polled = await getAsync(bearer, id);
        Assert.Multiple(() =>
        {
            Assert.That(polled.Properties().Select(p => p.Name),
                Is.EqualTo(new[] { "id", "state", "queue_position", "progress", "timing_json", "error", "aligner_version" }));
            Assert.That((string)polled["state"]!, Is.EqualTo("pending"));
            Assert.That((int)polled["queue_position"]!, Is.EqualTo(1));
            Assert.That(polled["progress"]!.Type, Is.EqualTo(JTokenType.Null));
            Assert.That(polled["timing_json"]!.Type, Is.EqualTo(JTokenType.Null));
            Assert.That(polled["error"]!.Type, Is.EqualTo(JTokenType.Null));
        });

        // The worker's half: claim, progress, then the terminal timing.json.
        await File.WriteAllTextAsync(Path.Combine(jobDir, "progress.log"), "[12:00:00] separating vocals\n[12:00:05] separating vocals 42%\n");
        File.Create(Path.Combine(jobDir, ".running")).Dispose();

        polled = await getAsync(bearer, id);
        Assert.Multiple(() =>
        {
            Assert.That((string)polled["state"]!, Is.EqualTo("running"));
            Assert.That((string)polled["progress"]!, Is.EqualTo("[12:00:05] separating vocals 42%"));
            Assert.That(polled["queue_position"]!.Type, Is.EqualTo(JTokenType.Null), "only a pending job has a queue position");
        });

        const string timing = /*lang=json*/ """{"version":2,"song_end_ms":4000,"lines":[]}""";
        await File.WriteAllTextAsync(Path.Combine(jobDir, "timing.json"), timing);

        polled = await getAsync(bearer, id);
        Assert.Multiple(() =>
        {
            Assert.That((string)polled["state"]!, Is.EqualTo("done"));
            Assert.That((string)polled["timing_json"]!, Is.EqualTo(timing));
            Assert.That((string)polled["aligner_version"]!, Is.EqualTo("11"));
        });

        // A finished job frees the slot.
        var (third, _) = await createAsync(bearer, stamped_lyrics);
        Assert.That(third, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task BareLyrics_RunAuto_AndEstimatedVocalsFallBackToAligned()
    {
        var (_, bearer) = await newPlayerAsync();

        var (status, body) = await createAsync(bearer, bare_lyrics, language: "", vocalMode: "estimated", fileName: "blob", extension: "flac");
        Assert.That(status, Is.EqualTo(HttpStatusCode.OK), body.ToString());

        string jobDir = Path.Combine(jobsRoot, (string)body["id"]!);
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(jobDir, "job.json")));
        var m = manifest.RootElement;

        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(Path.Combine(jobDir, "input.flac")), Is.True, "the extension field wins over a filename with none");
            Assert.That(m.GetProperty("anchors").GetString(), Is.EqualTo("auto"));
            Assert.That(m.GetProperty("vocal_mode").GetString(), Is.EqualTo("aligned"), "estimated vocals need a stamp to pace from");
            Assert.That(m.GetProperty("language").GetString(), Is.EqualTo(""));
        });
    }

    [TestCase("[00:01.00] hello\n[00:02.00] world", "ref")]
    [TestCase("hello\n[00:05.00] world\nagain", "ref")] // sparse anchors: one stamped line is enough
    [TestCase("[ar:Someone]\n[ti:Song]\nhello\nworld", "auto")] // metadata tags are not stamps
    [TestCase("hello\nworld\n[01:00.00]", "auto")] // a bare stamp (the end marker) carries no line
    [TestCase("[1:02.5] hello", "ref")]
    [TestCase("[aa:bb] hello", "auto")]
    [TestCase("", "auto")]
    public void AnchorMode_MirrorsTheGame(string lyrics, string expected)
        => Assert.That(AlignJobStore.AnchorMode(lyrics), Is.EqualTo(expected));

    [Test]
    public async Task Cancel_IsIdempotent_FreesTheSlot_AndReadsCancelled()
    {
        var (_, bearer) = await newPlayerAsync();

        var (status, body) = await createAsync(bearer, stamped_lyrics);
        Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
        string id = (string)body["id"]!;
        string jobDir = Path.Combine(jobsRoot, id);

        var (code, cancelled) = await cancelAsync(bearer, id);
        Assert.Multiple(() =>
        {
            Assert.That(code, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(cancelled.Properties().Select(p => p.Name), Is.EqualTo(new[] { "id", "state" }));
            Assert.That((string)cancelled["id"]!, Is.EqualTo(id));
            Assert.That((string)cancelled["state"]!, Is.EqualTo("cancelled"));
            Assert.That(File.Exists(Path.Combine(jobDir, "cancel")), Is.True, "the worker's cancel signal is on disk");
        });

        var polled = await getAsync(bearer, id);
        Assert.That((string)polled["state"]!, Is.EqualTo("cancelled"));

        // The worker halting writes its own error.json; the job still reads cancelled, not failed.
        await File.WriteAllTextAsync(Path.Combine(jobDir, "error.json"), """{"error":"alignment cancelled"}""");
        polled = await getAsync(bearer, id);
        Assert.That((string)polled["state"]!, Is.EqualTo("cancelled"));

        // Idempotent for the owner, both before and after the worker terminated it.
        var (again, againBody) = await cancelAsync(bearer, id);
        Assert.That(again, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((string)againBody["state"]!, Is.EqualTo("cancelled"));

        // The slot is free.
        var (next, nextBody) = await createAsync(bearer, stamped_lyrics);
        Assert.That(next, Is.EqualTo(HttpStatusCode.OK));

        // Cancelling a finished job is a no-op success too.
        await File.WriteAllTextAsync(Path.Combine(jobsRoot, (string)nextBody["id"]!, "timing.json"), "{}");
        var (finished, _) = await cancelAsync(bearer, (string)nextBody["id"]!);
        Assert.That(finished, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(File.Exists(Path.Combine(jobsRoot, (string)nextBody["id"]!, "cancel")), Is.False);

        // Unknown ids, and path-like ones, are 404.
        Assert.That((await cancelAsync(bearer, "deadbeefdeadbeefdeadbeefdeadbeef")).Status, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That((await cancelAsync(bearer, "..")).Status, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task AJob_IsOnlyItsOwners()
    {
        var (_, owner) = await newPlayerAsync();
        var (_, stranger) = await newPlayerAsync();

        var (_, body) = await createAsync(owner, stamped_lyrics);
        string id = (string)body["id"]!;

        using (var resp = await sendAsync(HttpMethod.Get, $"{path}/{id}", stranger))
            Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        Assert.That((await cancelAsync(stranger, id)).Status, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(File.Exists(Path.Combine(jobsRoot, id, "cancel")), Is.False, "a stranger's DELETE touches nothing");

        Assert.That((string)(await getAsync(owner, id))["state"]!, Is.EqualTo("pending"));
    }

    [Test]
    public async Task AGuest_Gets401_OnEveryRoute()
    {
        using (var content = buildForm(stamped_lyrics))
        using (var resp = await BssFixture.Client.PostAsync(path, content))
            Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));

        using (var resp = await BssFixture.Client.GetAsync($"{path}/deadbeefdeadbeefdeadbeefdeadbeef"))
            Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));

        using (var resp = await BssFixture.Client.DeleteAsync($"{path}/deadbeefdeadbeefdeadbeefdeadbeef"))
            Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));

        Assert.That(Directory.EnumerateDirectories(jobsRoot), Is.Empty);
    }

    [Test]
    public async Task TheDailyCap_CountsTodaysJobs_FromTheStore()
    {
        var (userId, bearer) = await newPlayerAsync();
        var now = DateTimeOffset.UtcNow;

        // Nine finished jobs today, and one from yesterday that no longer counts.
        for (int i = 0; i < AlignJobStore.JobsPerDay - 1; i++)
            seedJob(userId, now, terminal: "error.json");
        seedJob(userId, new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero).AddMinutes(-1), terminal: "timing.json");

        // The tenth goes through...
        var (tenth, tenthBody) = await createAsync(bearer, stamped_lyrics);
        Assert.That(tenth, Is.EqualTo(HttpStatusCode.OK), tenthBody.ToString());

        // ...and cancelling it does not hand the submission back.
        await cancelAsync(bearer, (string)tenthBody["id"]!);

        var (eleventh, body) = await createAsync(bearer, stamped_lyrics);
        string error = (string)body["error"]!;

        Assert.Multiple(() =>
        {
            Assert.That(eleventh, Is.EqualTo(HttpStatusCode.TooManyRequests));
            Assert.That(error, Does.Contain($"{AlignJobStore.JobsPerDay} server alignments"));
            Assert.That(error, Does.Contain("00:00 UTC"));
            Assert.That(error, Does.Contain("local aligner"));
        });
    }

    [TestCase(19, 0, 0, "in 5 h 0 min")]
    [TestCase(23, 59, 30, "in 1 min")]
    [TestCase(0, 0, 0, "in 24 h 0 min")]
    [TestCase(10, 47, 20, "in 13 h 13 min")]
    public void TheDailyCapMessage_SaysWhenItResets(int hour, int minute, int second, string expected)
    {
        var now = new DateTimeOffset(2026, 10, 9, hour, minute, second, TimeSpan.Zero);
        Assert.That(ServerAlignEndpoints.DailyCapMessage(now), Does.Contain($"resets at 00:00 UTC ({expected})"));
    }

    [Test]
    public async Task QueuePosition_CountsTheActiveJobsAhead()
    {
        var (_, bearer) = await newPlayerAsync();
        var earlier = DateTimeOffset.UtcNow.AddMinutes(-5);

        // One running and three pending jobs from other players, all ahead; one finished one, not counted.
        seedJob(9_000_001, earlier, running: true);
        string firstPending = seedJob(9_000_002, earlier.AddSeconds(1));
        seedJob(9_000_003, earlier.AddSeconds(2));
        seedJob(9_000_004, earlier.AddSeconds(3));
        seedJob(9_000_005, earlier, terminal: "timing.json");

        var (status, body) = await createAsync(bearer, stamped_lyrics);
        Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((int)body["queue_position"]!, Is.EqualTo(5));

        string id = (string)body["id"]!;
        Assert.That((int)(await getAsync(bearer, id))["queue_position"]!, Is.EqualTo(5));

        // The worker finishes one ahead: the queue moves up.
        await File.WriteAllTextAsync(Path.Combine(jobsRoot, firstPending, "error.json"), """{"error":"x"}""");
        Assert.That((int)(await getAsync(bearer, id))["queue_position"]!, Is.EqualTo(4));
    }

    [Test]
    public async Task AFullQueue_Is503_BeforeTheBodyCounts()
    {
        var (_, bearer) = await newPlayerAsync();
        var earlier = DateTimeOffset.UtcNow.AddMinutes(-1);

        for (int i = 0; i < AlignJobStore.MaxPendingJobs; i++)
            seedJob(9_100_000 + i, earlier.AddSeconds(i));

        using var resp = await sendAsync(HttpMethod.Post, path, bearer, buildForm(stamped_lyrics));
        var body = JObject.Parse(await resp.Content.ReadAsStringAsync());

        Assert.Multiple(() =>
        {
            Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
            Assert.That((string)body["error"]!, Is.EqualTo(AlignJobStore.RefusalQueueFull));
            Assert.That(resp.Headers.RetryAfter, Is.Not.Null);
            Assert.That(Directory.EnumerateDirectories(jobsRoot).Count(), Is.EqualTo(AlignJobStore.MaxPendingJobs), "nothing was written");
        });

        // A running job is not waiting: with one of them claimed there is room again.
        File.Create(Path.Combine(Directory.EnumerateDirectories(jobsRoot).First(), ".running")).Dispose();
        var (status, _) = await createAsync(bearer, stamped_lyrics);
        Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task NoWorker_Is503_AndFailsWhatIsWaiting()
    {
        var (userId, bearer) = await newPlayerAsync();
        string waiting = seedJob(userId, DateTimeOffset.UtcNow.AddMinutes(-1));

        // Never started.
        File.Delete(Path.Combine(jobsRoot, AlignJobStore.WorkerFileName));
        var (absent, absentBody) = await createAsync(bearer, stamped_lyrics);
        Assert.That(absent, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        Assert.That((string)absentBody["error"]!, Is.EqualTo(AlignJobStore.RefusalWorkerDown));

        // Stopped beating.
        writeHeartbeat(DateTimeOffset.UtcNow - AlignJobStore.WorkerStaleAfter - TimeSpan.FromSeconds(5));
        var (stale, _) = await createAsync(bearer, stamped_lyrics);
        Assert.That(stale, Is.EqualTo(HttpStatusCode.ServiceUnavailable));

        // The job that was waiting fails, and is terminated on disk so a returning worker skips it.
        var polled = await getAsync(bearer, waiting);
        Assert.Multiple(() =>
        {
            Assert.That((string)polled["state"]!, Is.EqualTo("failed"));
            Assert.That((string)polled["error"]!, Is.EqualTo(AlignJobStore.ErrorWorkerStopped));
            Assert.That(File.Exists(Path.Combine(jobsRoot, waiting, "error.json")), Is.True);
        });
    }

    [Test]
    public async Task StaleJobs_Fail_WithTheirReason()
    {
        var (userId, bearer) = await newPlayerAsync();
        var now = DateTimeOffset.UtcNow;

        string hung = seedJob(userId, now.AddMinutes(-40), running: true);
        File.SetLastWriteTimeUtc(Path.Combine(jobsRoot, hung, ".running"), (now - AlignJobStore.JobTimeout - AlignJobStore.RunningGrace - TimeSpan.FromMinutes(1)).UtcDateTime);

        string forgotten = seedJob(userId, now - AlignJobStore.PendingMaxAge - TimeSpan.FromMinutes(1));

        string failed = seedJob(userId, now.AddMinutes(-2), terminal: "error.json");

        Assert.Multiple(async () =>
        {
            Assert.That((string)(await getAsync(bearer, hung))["error"]!, Is.EqualTo(AlignJobStore.ErrorRunTooLong));
            Assert.That((string)(await getAsync(bearer, forgotten))["error"]!, Is.EqualTo(AlignJobStore.ErrorWaitedTooLong));

            var worker = await getAsync(bearer, failed);
            Assert.That((string)worker["state"]!, Is.EqualTo("failed"));
            Assert.That((string)worker["error"]!, Is.EqualTo("seeded failure"), "the worker's own message, verbatim");
        });

        // None of them holds the player's slot.
        var (status, _) = await createAsync(bearer, stamped_lyrics);
        Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task Validation_Is422()
    {
        var (_, bearer) = await newPlayerAsync();

        Assert.Multiple(async () =>
        {
            Assert.That((await createAsync(bearer, stamped_lyrics, fileName: "notes.txt")).Status, Is.EqualTo(HttpStatusCode.UnprocessableEntity), "extension");
            Assert.That((await createAsync(bearer, "")).Status, Is.EqualTo(HttpStatusCode.UnprocessableEntity), "lyrics");
            Assert.That((await createAsync(bearer, stamped_lyrics, vocalMode: "shouted")).Status, Is.EqualTo(HttpStatusCode.UnprocessableEntity), "vocal_mode");
            Assert.That((await createAsync(bearer, new string('a', AlignJobStore.MAX_LYRICS_BYTES + 1))).Status, Is.EqualTo(HttpStatusCode.UnprocessableEntity), "lyrics size");

            using var noAudio = new MultipartFormDataContent { { new StringContent(stamped_lyrics), "lyrics" } };
            using var resp = await sendAsync(HttpMethod.Post, path, bearer, noAudio);
            Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.UnprocessableEntity), "audio");
        });

        Assert.That(Directory.EnumerateDirectories(jobsRoot), Is.Empty);
    }

    [Test]
    public async Task TheOldRoute_Is410_PointingAtTheImportScreen()
    {
        using var content = buildForm(stamped_lyrics);
        using var resp = await BssFixture.Client.PostAsync("/api/v2/typebeat/align", content);
        string error = (string)JObject.Parse(await resp.Content.ReadAsStringAsync())["error"]!;

        Assert.Multiple(() =>
        {
            Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.Gone));
            Assert.That(error, Is.EqualTo(AlignEndpoints.RETIRED_MESSAGE));
            Assert.That(error, Does.Contain("import screen"));
            Assert.That(Directory.EnumerateDirectories(jobsRoot), Is.Empty, "the old route never feeds the new queue");
        });
    }

    // ---- helpers ----

    private static int playerCounter;

    private static Task<(long Id, string Bearer)> newPlayerAsync()
        => BssFixture.CreateUserAsync($"aligner player {Interlocked.Increment(ref playerCounter)} {Guid.NewGuid().ToString("N")[..8]}", verified: true);

    private static void writeHeartbeat(DateTimeOffset at)
    {
        Directory.CreateDirectory(jobsRoot);
        File.WriteAllText(Path.Combine(jobsRoot, AlignJobStore.WorkerFileName),
            JsonSerializer.Serialize(new { aligner_version = "11", heartbeat_at = at }));
    }

    /// <summary>A job directory as the app would have written it, optionally claimed or terminated.</summary>
    private static string seedJob(long userId, DateTimeOffset createdAt, bool running = false, string? terminal = null)
    {
        string id = Guid.NewGuid().ToString("N");
        string dir = Path.Combine(jobsRoot, id);
        Directory.CreateDirectory(dir);

        File.WriteAllText(Path.Combine(dir, "job.json"), JsonSerializer.Serialize(new
        {
            id,
            user_id = userId,
            artist = "Seeded",
            title = "Seeded",
            audio_file = "input.mp3",
            anchors = "ref",
            vocal_mode = "aligned",
            language = "",
            aligner_version = "11",
            created_at = createdAt,
        }));

        if (running)
            File.Create(Path.Combine(dir, ".running")).Dispose();

        if (terminal == "error.json")
            File.WriteAllText(Path.Combine(dir, terminal), """{"error":"seeded failure"}""");
        else if (terminal != null)
            File.WriteAllText(Path.Combine(dir, terminal), "{}");

        return id;
    }

    private static MultipartFormDataContent buildForm(string lyrics, string fileName = "song.mp3", string? extension = null,
                                                      string language = "english", string vocalMode = "aligned")
    {
        var audio = new ByteArrayContent(Encoding.ASCII.GetBytes("ID3 fake mp3 payload"));
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");

        var form = new MultipartFormDataContent
        {
            { audio, "audio", fileName },
            { new StringContent(lyrics), "lyrics" },
            { new StringContent("Test Artist"), "artist" },
            { new StringContent("Test Title"), "title" },
            { new StringContent(language), "language" },
            { new StringContent(vocalMode), "vocal_mode" },
        };

        if (extension != null)
            form.Add(new StringContent(extension), "extension");

        return form;
    }

    private static async Task<HttpResponseMessage> sendAsync(HttpMethod method, string uri, string bearer, HttpContent? content = null)
    {
        var req = new HttpRequestMessage(method, uri) { Content = content };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return await BssFixture.Client.SendAsync(req);
    }

    private static async Task<(HttpStatusCode Status, JObject Body)> createAsync(
        string bearer, string lyrics, string fileName = "song.mp3", string? extension = null, string language = "english", string vocalMode = "aligned")
    {
        using var resp = await sendAsync(HttpMethod.Post, path, bearer, buildForm(lyrics, fileName, extension, language, vocalMode));
        string text = await resp.Content.ReadAsStringAsync();
        return (resp.StatusCode, text.StartsWith('{') ? JObject.Parse(text) : new JObject());
    }

    private static async Task<JObject> getAsync(string bearer, string id)
    {
        using var resp = await sendAsync(HttpMethod.Get, $"{path}/{id}", bearer);
        string text = await resp.Content.ReadAsStringAsync();
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK), text);
        return JObject.Parse(text);
    }

    private static async Task<(HttpStatusCode Status, JObject Body)> cancelAsync(string bearer, string id)
    {
        using var resp = await sendAsync(HttpMethod.Delete, $"{path}/{id}", bearer);
        string text = await resp.Content.ReadAsStringAsync();
        return (resp.StatusCode, text.StartsWith('{') ? JObject.Parse(text) : new JObject());
    }
}
