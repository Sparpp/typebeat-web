using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using Typebeat.Web.Ops;

namespace Typebeat.Web.Tests.Bss;

/// <summary>
/// While the startup sweeps are still running (backlog 368) every BSS route that hands a package to
/// the ingest answers 503 with Retry-After, before the disk guard, the rate limiter or any body read,
/// and the transport-only session routes stay open so a complete can simply be retried. Pinned on the
/// EXISTING BSS host through <see cref="StartupSweepGate.StateOverride"/> (its real gate is Done, the
/// fixture waits for it).
/// </summary>
[TestFixture]
[NonParallelizable]
public class BssStartupSweepGateTest
{
    private StartupSweepGate gate = null!;
    private string bearer = null!;
    private long setId;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        gate = BssFixture.Services.GetRequiredService<StartupSweepGate>();
        (_, bearer) = await BssFixture.CreateUserAsync("sweep gate mapper", verified: true);

        using var response = await BssSubmissionFlowTest.SendAsync(HttpMethod.Put, "/bss/beatmapsets", bearer, BssSubmissionFlowTest.JsonBody(new
        {
            beatmapset_id = (long?)null,
            beatmaps_to_create = 1,
            beatmaps_to_keep = Array.Empty<long>(),
            target = "WIP",
            notify_on_discussion_replies = false,
        }));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), "creating the set shell reaches no ingest and is never held");
        setId = (long)JObject.Parse(await response.Content.ReadAsStringAsync())["beatmapset_id"]!;
    }

    [TearDown]
    public void ReopenTheGate() => gate.StateOverride = null;

    [OneTimeTearDown]
    public void OneTimeTearDown() => gate.StateOverride = null;

    [Test]
    public async Task FullUpload_WhileSweeping_Is503WithRetryAfter_AndCutsNoVersion()
    {
        gate.StateOverride = StartupSweepState.Running;

        using var package = BssSubmissionFlowTest.PackageBody([1, 2, 3, 4]);
        using var response = await BssSubmissionFlowTest.SendAsync(HttpMethod.Put, $"/bss/beatmapsets/{setId}", bearer, package);
        string body = await response.Content.ReadAsStringAsync();

        await using var conn = await BssFixture.OpenDbAsync();
        long versions = await conn.ExecuteScalarAsync<long>("SELECT count(*) FROM set_versions WHERE set_id = @setId", new { setId });

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable), body);
            Assert.That(response.Headers.RetryAfter?.Delta, Is.EqualTo(TimeSpan.FromSeconds(StartupSweepGate.RetryAfterSeconds)));
            Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/json"));
            Assert.That((string?)JObject.Parse(body)["error"], Does.Contain("startup maintenance"));
            Assert.That(versions, Is.Zero);
        });
    }

    [Test]
    public async Task Patch_WhileSweeping_Is503WithRetryAfter()
    {
        gate.StateOverride = StartupSweepState.Running;

        using var response = await BssSubmissionFlowTest.SendAsync(HttpMethod.Patch, $"/bss/beatmapsets/{setId}", bearer);

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
            Assert.That(response.Headers.RetryAfter?.Delta, Is.EqualTo(TimeSpan.FromSeconds(StartupSweepGate.RetryAfterSeconds)));
        });
    }

    [Test]
    public async Task FailedBeforeFingerprints_KeepsIngestRefused_WithTheRepairMessage()
    {
        // The real gate is Done with fingerprints current, so a Failed override reads as "failed
        // after them" and opens ingest; this pins the other branch's wording through a fresh gate.
        var fresh = new StartupSweepGate();
        fresh.Finish("GameplayFingerprintBackfill");

        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        await fresh.IngestRefusal().ExecuteAsync(context);
        context.Response.Body.Position = 0;
        string body = await new StreamReader(context.Response.Body).ReadToEndAsync();

        Assert.Multiple(() =>
        {
            Assert.That(fresh.IngestOpen, Is.False);
            Assert.That(context.Response.StatusCode, Is.EqualTo(503));
            Assert.That(context.Response.Headers.RetryAfter.ToString(), Is.EqualTo(StartupSweepGate.RetryAfterSeconds.ToString()));
            Assert.That((string?)JObject.Parse(body)["error"], Does.Contain("repairs its catalogue"));
        });
    }

    [Test]
    public async Task SessionTransport_StaysOpenWhileSweeping_AndOnlyTheCompleteWaits()
    {
        byte[] payload = new byte[2048];
        Random.Shared.NextBytes(payload);
        string sha = Convert.ToHexStringLower(SHA256.HashData(payload));

        gate.StateOverride = StartupSweepState.Running;

        string sessionId;

        using (var created = await BssSubmissionFlowTest.SendAsync(
                   HttpMethod.Post, $"/bss/beatmapsets/{setId}/upload-sessions", bearer,
                   BssSubmissionFlowTest.JsonBody(new
                   {
                       kind = "full",
                       content_type = "multipart/form-data; boundary=----typebeat-test-boundary",
                       total_bytes = payload.LongLength,
                       sha256 = sha,
                   })))
        {
            string createdBody = await created.Content.ReadAsStringAsync();
            Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK), createdBody);
            sessionId = (string)JObject.Parse(createdBody)["session_id"]!;
        }

        using (var chunk = new HttpRequestMessage(HttpMethod.Put, $"/bss/upload-sessions/{sessionId}/chunks/0"))
        {
            chunk.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            chunk.Headers.Add("X-Chunk-Sha256", sha);
            chunk.Content = new ByteArrayContent(payload);
            chunk.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

            using var chunkResponse = await BssFixture.Client.SendAsync(chunk);
            Assert.That(chunkResponse.IsSuccessStatusCode, Is.True, await chunkResponse.Content.ReadAsStringAsync());
        }

        using var complete = await BssSubmissionFlowTest.SendAsync(HttpMethod.Post, $"/bss/upload-sessions/{sessionId}/complete", bearer);
        using var status = await BssSubmissionFlowTest.SendAsync(HttpMethod.Get, $"/bss/upload-sessions/{sessionId}", bearer);
        string statusBody = await status.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(complete.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
            Assert.That(complete.Headers.RetryAfter?.Delta, Is.EqualTo(TimeSpan.FromSeconds(StartupSweepGate.RetryAfterSeconds)));
            Assert.That(status.StatusCode, Is.EqualTo(HttpStatusCode.OK), "a refused complete leaves the session for the retry");
            Assert.That(JObject.Parse(statusBody)["received"]!.Values<int>(), Is.EquivalentTo(new[] { 0 }));
        });

        // Once the gate opens, the very same complete gets past it (the junk payload then fails the
        // normal validation instead, which is the point: it was the gate, and only the gate).
        gate.StateOverride = null;

        using var retried = await BssSubmissionFlowTest.SendAsync(HttpMethod.Post, $"/bss/upload-sessions/{sessionId}/complete", bearer);
        Assert.That(retried.StatusCode, Is.Not.EqualTo(HttpStatusCode.ServiceUnavailable), await retried.Content.ReadAsStringAsync());
    }

    [Test]
    public async Task OnceTheSweepsAreDone_TheSameUploadReachesTheNormalValidation()
    {
        Assert.That(gate.State, Is.EqualTo(StartupSweepState.Done), "the fixture waited for the real chain");

        using var package = BssSubmissionFlowTest.PackageBody([1, 2, 3, 4]);
        using var response = await BssSubmissionFlowTest.SendAsync(HttpMethod.Put, $"/bss/beatmapsets/{setId}", bearer, package);

        Assert.That(response.StatusCode, Is.Not.EqualTo(HttpStatusCode.ServiceUnavailable));
    }
}
