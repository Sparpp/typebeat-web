using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Typebeat.Web.Ops;

namespace Typebeat.Web.Tests.Website;

/// <summary>
/// Guards that the website build-out changed nothing about the pre-existing surface: /health
/// still answers the uptime monitor, and an APIv2 endpoint still speaks the exact WireJson
/// envelope (the game client special-cases these strings).
/// </summary>
public class ApiRegressionGuardTest
{
    [Test]
    public async Task Health_StillReturnsPlainOk()
    {
        using var response = await WebsiteFixture.Client.GetAsync("/health");
        string body = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            // The liveness contract since backlog 365 (DiskGuard.HealthBody): the body STARTS with
            // "ok" while nothing is refused, carrying a note when the disk is low or unreadable
            // ("ok disk-low 6.9 GiB free" is what CI's runner answers). The uptime monitor and
            // ci.yml's grep key on that prefix; the exact "ok" of a healthy disk is pinned in
            // DiskGuardTest, where the probe is under the test's control rather than the host's.
            Assert.That(body, Does.StartWith("ok"));
        });
    }

    /// <summary>
    /// Backlog 368: /health is LIVENESS and never waits for the startup sweeps (the deploy's health
    /// check and its automatic rollback key on it), while /health/ready reports them: 503 with
    /// Retry-After and "sweeping" until the chain is through, then 200 "ready". Driven through the
    /// gate's override on this host, whose real chain the fixture already waited for.
    /// </summary>
    [Test]
    public async Task Health_StaysLiveness_WhileTheStartupSweepsRun_AndReadyReportsThem()
    {
        var gate = WebsiteFixture.Services.GetRequiredService<StartupSweepGate>();

        HttpStatusCode liveStatus, readyStatus;
        string liveBody, readyBody;
        TimeSpan? readyRetry;

        gate.StateOverride = StartupSweepState.Running;

        try
        {
            using var live = await WebsiteFixture.Client.GetAsync("/health");
            liveStatus = live.StatusCode;
            liveBody = await live.Content.ReadAsStringAsync();

            using var ready = await WebsiteFixture.Client.GetAsync("/health/ready");
            readyStatus = ready.StatusCode;
            readyBody = await ready.Content.ReadAsStringAsync();
            readyRetry = ready.Headers.RetryAfter?.Delta;
        }
        finally
        {
            gate.StateOverride = null;
        }

        using var readyAfter = await WebsiteFixture.Client.GetAsync("/health/ready");
        string readyAfterBody = await readyAfter.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(liveStatus, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(liveBody, Does.StartWith("ok"), "liveness, whatever the host's disk reads");
            Assert.That(readyStatus, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
            Assert.That(readyBody, Is.EqualTo("sweeping"), "plain body, never the styled error page");
            Assert.That(readyRetry, Is.EqualTo(TimeSpan.FromSeconds(StartupSweepGate.RetryAfterSeconds)));
            Assert.That(readyAfter.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(readyAfterBody, Is.EqualTo("ready"));
        });
    }

    [Test]
    public async Task ApiV2Me_WithoutBearer_StillExact401Envelope()
    {
        using var response = await WebsiteFixture.Client.GetAsync("/api/v2/me/");
        string body = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/json"));
            Assert.That(body, Is.EqualTo("{\"error\":\"authentication failed\"}"));
        });
    }

    [Test]
    public async Task MenuContent_StillEmptyImagesArray()
    {
        using var response = await WebsiteFixture.Client.GetAsync("/menu-content.json");
        string body = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(body, Is.EqualTo("{\"images\":[]}"));
        });
    }

    // ---- The website's styled error pages must never wrap a wire response (byte-exact). ----

    [Test]
    public async Task ApiV2BeatmapsetMissing_StillExact404Envelope()
    {
        using var response = await WebsiteFixture.Client.GetAsync("/api/v2/beatmapsets/999999999");
        string body = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/json"));
            Assert.That(body, Is.EqualTo("{\"error\":\"not found\"}"));
        });
    }

    [Test]
    public async Task UnmappedApiPath_StillEmptyBodied404()
    {
        using var response = await WebsiteFixture.Client.GetAsync("/api/v2/no-such-endpoint");
        string body = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(body, Is.Empty, "an unmatched /api path must not grow an HTML error body");
        });
    }

    [Test]
    public async Task BssWithoutBearer_StillExact401Envelope()
    {
        using var response = await WebsiteFixture.Client.PutAsync("/bss/beatmapsets", null);
        string body = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/json"));
            Assert.That(body, Is.EqualTo("{\"error\":\"authentication failed\"}"));
        });
    }

    [Test]
    public async Task OpsDisk_WithNoBuddyKeyConfigured_IsInvisible()
    {
        // The ops readout (backlog 268) is gated exactly like the bot's score feed: with
        // TYPEBEAT_BUDDY_KEY unset, as it is on this host, it must 404 with no body, so an
        // un-opted-in deployment does not even advertise that the endpoint exists. A wrong key
        // 401s, but only once a key is configured, which this host deliberately never does.
        using var response = await WebsiteFixture.Client.GetAsync("/api/v2/ops/disk");
        string body = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(body, Is.Empty);
        });
    }

    [Test]
    public async Task OpsRankVersionAudit_WithNoBuddyKeyConfigured_IsInvisible()
    {
        // The dry run for the downward version rule (backlog 398) is on the same private footing:
        // with no key it 404s with no body, so an un-opted-in deploy exposes nothing.
        using var response = await WebsiteFixture.Client.GetAsync("/api/v2/ops/rank-version-audit");
        string body = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(body, Is.Empty);
        });
    }

    [Test]
    public async Task OpsBackups_WithNoBuddyKeyConfigured_IsInvisible()
    {
        // The offsite freshness readout (backlog 367) sits behind the same gate as the disk
        // readout, so with TYPEBEAT_BUDDY_KEY unset it must 404 with no body too.
        using var response = await WebsiteFixture.Client.GetAsync("/api/v2/ops/backups");
        string body = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(body, Is.Empty);
        });
    }

    [Test]
    public async Task OpsMirror_WithNoBuddyKeyConfigured_IsInvisible()
    {
        // The releases mirror readout (backlog 380) sits behind the same gate as the disk readout,
        // so with TYPEBEAT_BUDDY_KEY unset it must 404 with no body too.
        using var response = await WebsiteFixture.Client.GetAsync("/api/v2/ops/mirror");
        string body = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(body, Is.Empty);
        });
    }

    [Test]
    public async Task OpsStems_WithNoBuddyKeyConfigured_IsInvisible()
    {
        // The vocals-stem backfill ops surface (backlog 393) sits behind the same gate as the other
        // ops readouts: with TYPEBEAT_BUDDY_KEY unset, as on this host, every one of its routes must
        // 404 with no body, so an un-opted-in deployment does not expose an operator tool that would
        // write to stored sets.
        using var missing = await WebsiteFixture.Client.GetAsync("/api/v2/ops/stems/missing");
        using var audio = await WebsiteFixture.Client.GetAsync("/api/v2/ops/stems/audio/1?sha256=00");
        using var attach = await WebsiteFixture.Client.PutAsync("/api/v2/ops/stems/1",
            new System.Net.Http.ByteArrayContent([1, 2, 3]));

        Assert.Multiple(async () =>
        {
            Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(await missing.Content.ReadAsStringAsync(), Is.Empty);
            Assert.That(audio.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(await audio.Content.ReadAsStringAsync(), Is.Empty);
            Assert.That(attach.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(await attach.Content.ReadAsStringAsync(), Is.Empty);
        });
    }

    [Test]
    public async Task RegistrationPost_WrongUserAgent_StillExact403Envelope()
    {
        // POST /users is the game client's registration wire route; only GET/HEAD /users/* is
        // website surface. The UA gate fires before anything else; exact envelope pinned.
        using var response = await WebsiteFixture.Client.PostAsync("/users", new FormUrlEncodedContent([]));
        string body = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/json"));
            Assert.That(body, Is.EqualTo("{\"error\":\"forbidden\"}"));
        });
    }
}
