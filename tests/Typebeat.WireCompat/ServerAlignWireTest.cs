using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Newtonsoft.Json;
using Typebeat.Web.Align;
using typebeat.Game.Rulesets.TypeBeat.Import;

namespace Typebeat.WireCompat;

/// <summary>
/// The server aligner's job responses (backlog 413) read through the game's OWN DTO,
/// <see cref="ServerAlignJob"/>, exactly as RemoteAlignClient reads them: create, poll to done with
/// the timing.json carried through, and cancel. The worker's part is played by hand through the job
/// directory, the same way the server-side flow test does it.
/// </summary>
[TestFixture]
public class ServerAlignWireTest
{
    private const string path = "/api/v2/typebeat/server-align";

    private static string jobsRoot => Path.Combine(ServerFixture.FileRoot, "align-jobs");

    [Test]
    public async Task CreatePollAndCancel_ReadThroughTheGameDto()
    {
        writeHeartbeat();

        // A fresh token, so the per-token submit budget is this test's alone.
        var (bearer, _) = await ServerFixture.IssueTokenAsync(ServerFixture.PlayerUserId);

        // Create: pending, a queue position and the worker's aligner version.
        var created = await sendAsync<ServerAlignJob>(HttpMethod.Post, path, bearer, buildForm());
        Assert.That(created.Id, Is.Not.Empty);
        Assert.That(created.State, Is.EqualTo(ServerAlignJob.STATE_PENDING));
        Assert.That(created.QueuePosition, Is.GreaterThanOrEqualTo(1));
        Assert.That(created.AlignerVersion, Is.EqualTo("11"));

        // Poll while pending: the same shape, the queue position still present.
        var pending = await sendAsync<ServerAlignJob>(HttpMethod.Get, $"{path}/{created.Id}", bearer);
        Assert.That(pending.Id, Is.EqualTo(created.Id));
        Assert.That(pending.State, Is.EqualTo(ServerAlignJob.STATE_PENDING));
        Assert.That(pending.QueuePosition, Is.Not.Null);
        Assert.That(pending.TimingJson, Is.Null);

        // The worker finishes: timing.json arrives verbatim and the queue position goes away.
        const string timing = """{"aligner_version":"11","lines":[{"text":"hello world","words":[]}]}""";
        File.WriteAllText(Path.Combine(jobsRoot, created.Id, ".running"), string.Empty);
        File.WriteAllText(Path.Combine(jobsRoot, created.Id, "timing.json"), timing);

        var done = await sendAsync<ServerAlignJob>(HttpMethod.Get, $"{path}/{created.Id}", bearer);
        Assert.That(done.State, Is.EqualTo(ServerAlignJob.STATE_DONE));
        Assert.That(done.TimingJson, Is.EqualTo(timing));
        Assert.That(done.QueuePosition, Is.Null);
        Assert.That(done.Error, Is.Null);

        // A second job, cancelled: DELETE and the following poll both read as cancelled.
        var second = await sendAsync<ServerAlignJob>(HttpMethod.Post, path, bearer, buildForm());
        var cancelled = await sendAsync<ServerAlignJob>(HttpMethod.Delete, $"{path}/{second.Id}", bearer);
        Assert.That(cancelled.Id, Is.EqualTo(second.Id));
        Assert.That(cancelled.State, Is.EqualTo(ServerAlignJob.STATE_CANCELLED));

        var afterCancel = await sendAsync<ServerAlignJob>(HttpMethod.Get, $"{path}/{second.Id}", bearer);
        Assert.That(afterCancel.State, Is.EqualTo(ServerAlignJob.STATE_CANCELLED));
    }

    private static void writeHeartbeat()
    {
        Directory.CreateDirectory(jobsRoot);
        File.WriteAllText(Path.Combine(jobsRoot, AlignJobStore.WorkerFileName),
            System.Text.Json.JsonSerializer.Serialize(new { aligner_version = "11", heartbeat_at = DateTimeOffset.UtcNow }));
    }

    private static MultipartFormDataContent buildForm()
    {
        var audio = new ByteArrayContent(Encoding.ASCII.GetBytes("ID3 fake mp3 payload"));
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");

        return new MultipartFormDataContent
        {
            { audio, "audio", "song.mp3" },
            { new StringContent(".mp3"), "extension" },
            { new StringContent("[00:01.00] hello world"), "lyrics" },
            { new StringContent("Wire Artist"), "artist" },
            { new StringContent("Wire Title"), "title" },
            { new StringContent("english"), "language" },
            { new StringContent("aligned"), "vocal_mode" },
        };
    }

    private static async Task<T> sendAsync<T>(HttpMethod method, string uri, string bearer, HttpContent? content = null)
    {
        using var req = new HttpRequestMessage(method, uri) { Content = content };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

        using var resp = await ServerFixture.Client.SendAsync(req);
        string body = await resp.Content.ReadAsStringAsync();
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK), body);

        return JsonConvert.DeserializeObject<T>(body) ?? throw new AssertionException($"null {typeof(T).Name} from {body}");
    }
}
