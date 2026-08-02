using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Dapper;
using Npgsql;
using Typebeat.Web;
using Typebeat.Web.Align;
using Typebeat.Web.Auth;
using Typebeat.Web.Data;
using Typebeat.Web.Email;
using Typebeat.Web.Endpoints;
using Typebeat.Web.Packages;
using Typebeat.Web.Scoring;
using Typebeat.Web.Storage;
using Typebeat.Web.Wire;

var builder = WebApplication.CreateBuilder(args);

// Error tracking: enabled only when a DSN is configured (TYPEBEAT_SENTRY_DSN), so local dev and
// tests run without it. Captures unhandled exceptions + failed requests; no tracing (cost/noise).
string? sentryDsn = builder.Configuration["TYPEBEAT_SENTRY_DSN"];
if (!string.IsNullOrEmpty(sentryDsn))
{
    builder.WebHost.UseSentry(o =>
    {
        o.Dsn = sentryDsn;
        o.TracesSampleRate = 0;
        o.SendDefaultPii = false;
    });
}

string connectionString = Db.ResolveConnectionString(builder.Configuration);

// Ensure required extensions exist BEFORE the pooled data source ever connects. The data source
// snapshots the database type catalog on its first physical connection; if citext doesn't exist
// yet at that moment, its OID is never learned and every citext read (usernames, emails) throws
// for the life of the process. That is exactly what happens on a first boot against an empty DB,
// where the migration creates citext only after the pool has already connected. citext is a
// trusted extension (PG13+), so the app's own DB-owner role can create it, no superuser needed.
await Db.EnsureExtensionsAsync(connectionString);

builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton<Db>(sp => new Db(sp.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddSingleton<TokenService>();
builder.Services.AddSingleton<PasswordService>();

// Email verification (M2): 6-digit codes on signup ('verify') and on EVERY website login
// ('login'), plus the Data-Protection-backed challenge cookie that carries the pending user
// between the password step and the code step.
builder.Services.AddSingleton<EmailCodeService>();
builder.Services.AddSingleton<ChallengeCookie>();
builder.Services.AddHttpClient();

// Real delivery via Resend when TYPEBEAT_RESEND_API_KEY is set; otherwise the LogEmailSender
// writes the code to the log so dev/tests and a not-yet-configured prod box still work. The
// chosen sender is logged at startup.
if (!string.IsNullOrEmpty(builder.Configuration["TYPEBEAT_RESEND_API_KEY"]))
    builder.Services.AddSingleton<IEmailSender, ResendEmailSender>();
else
    builder.Services.AddSingleton<IEmailSender, LogEmailSender>();

// Persist Data Protection keys (challenge cookies + antiforgery tokens) across container
// redeploys when a file root is configured (prod: the /data appdata volume). Without this,
// every redeploy would rotate the in-memory keys and invalidate in-flight challenge cookies and
// any rendered antiforgery tokens. Local/tests keep the default ephemeral keyring.
if (builder.Configuration["TYPEBEAT_FILE_ROOT"] is { Length: > 0 } fileRoot)
{
    var keyDir = new DirectoryInfo(Path.Combine(fileRoot, "dpkeys"));
    keyDir.Create();
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(keyDir)
        .SetApplicationName("typebeat-web");
}

// Upload/package pipeline (M3). File root: TYPEBEAT_FILE_ROOT (prod: the /data volume; dev
// default ./data). Everything under it is content-addressed or set-scoped; see StoreKeys.
builder.Services.AddSingleton<IFileStore>(_ => LocalFileStore.FromConfiguration(builder.Configuration));
builder.Services.AddSingleton<CoverGenerator>();
builder.Services.AddSingleton<PreviewGenerator>();
builder.Services.AddSingleton<PackageIngest>();

// Server-side lyric alignment (file-based job exchange with the aligner worker container).
builder.Services.AddSingleton<AlignJobStore>();

// The website (M3): server-rendered Razor Pages under Pages/, HTML only; every APIv2/BSS JSON
// response keeps going through WireJson (Newtonsoft), untouched by this. AddRazorPages also
// registers antiforgery, which the page pipeline validates on every POST handler (400 on a
// missing/invalid token); the bearer API endpoints below are unaffected (no cookies, no forms
// bound via the framework).
builder.Services.AddRazorPages();

// Sets the header the in-browser web player (PlayEndpoints) sends its antiforgery token in;
// AddRazorPages already registered the antiforgery services. Additive: the page pipeline still
// validates form tokens as before. The /play mutating endpoints validate explicitly via IAntiforgery.
builder.Services.AddAntiforgery(o => o.HeaderName = "X-CSRF-TOKEN");

var app = builder.Build();

await app.Services.GetRequiredService<Db>().MigrateAsync(app.Logger);

// Recompute stored wpm/star/word/char numbers for rows written under an older pace arithmetic
// (LyricPace.VERSION bumps). No-op when everything is current.
await PaceBackfill.RunAsync(
    app.Services.GetRequiredService<Db>(),
    app.Services.GetRequiredService<IFileStore>(),
    app.Logger);

// Detect the song language of every set that still has none (019_language.sql), offline, from the
// lyric text the backfill above is what fills in, so it MUST run after it. Only ever writes rows
// that are still unset, so a mapper's own language tag is never overwritten. No-op once every set
// is either classified or known-unclassifiable.
await LanguageBackfill.RunAsync(app.Services.GetRequiredService<Db>(), app.Logger);

// Re-rank scores the pre-task-47 play-time gate unranked purely for using the in-game skip button
// (016_refund_skip_gate.sql). Must run AFTER the backfill: it reads beatmaps.skippable_s, which the
// backfill is what fills in. No-op once every victim is recorded in score_refunds.
await SkipGateRefund.RunAsync(app.Services.GetRequiredService<Db>(), app.Logger);

// Re-rank scores the rate-blind play-time gate unranked purely for playing at an up-rate
// (017_rate_gate_refund.sql). Runs after the skip refund so a row that only needed THAT correction
// is already ranked, and therefore no longer a candidate here; a play that needed both is refunded
// by this one. No-op once every victim is recorded in score_refunds.
await RateGateRefund.RunAsync(app.Services.GetRequiredService<Db>(), app.Logger);

// Recompute stored per-score pp for rows below the current PerformancePoints.VERSION
// (020_performance_points.sql). Runs LAST of the sweeps: it reads beatmaps.sr_dt / sr_ht, which
// PaceBackfill is what fills in (and which it stamps every affected score back to version 0 for),
// and it reads scores.ranked, which the two refund sweeps above may have just flipped on. No-op
// once every score is current.
await PpBackfill.RunAsync(app.Services.GetRequiredService<Db>(), app.Logger);

// Which email path is live (helps confirm prod is actually sending, not just logging codes).
// The log fallback means verification/login codes are NOT delivered; the site still says
// "sent", so make it a startup WARNING behind the proxy (i.e. a real deployment), where that
// is almost certainly a missing TYPEBEAT_RESEND_API_KEY rather than an intended dev default.
var emailSender = app.Services.GetRequiredService<IEmailSender>();

if (emailSender is LogEmailSender && app.Configuration.GetValue<bool>("TYPEBEAT_BEHIND_PROXY"))
    app.Logger.LogWarning("Email sender: LogEmailSender, codes are only written to this log, NO emails are delivered. Set TYPEBEAT_RESEND_API_KEY to enable real delivery.");
else
    app.Logger.LogInformation("Email sender: {Sender}", emailSender.GetType().Name);

// Behind the Caddy reverse proxy, honor X-Forwarded-For / X-Forwarded-Proto so Request.Scheme is
// "https" (the notification_endpoint must be wss://, cover/avatar URLs must be https://) and
// Connection.RemoteIpAddress is the real client IP (the rate limiters key on it). Only enabled
// when TYPEBEAT_BEHIND_PROXY is set, so local dev keeps the direct connection info. KnownNetworks/
// Proxies are cleared because the proxy is a trusted same-host container on the Docker network.
// NOTE: flags are read with tolerant parsing (Flags.IsEnabled), never GetValue<bool>; compose
// passes unset flags as EMPTY STRINGS, which GetValue<bool> throws on (it crashed prod once).
if (Flags.IsEnabled(builder.Configuration, "TYPEBEAT_BEHIND_PROXY"))
{
    var forwarded = new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
    };
    forwarded.KnownNetworks.Clear();
    forwarded.KnownProxies.Clear();
    app.UseForwardedHeaders(forwarded);
}

// Styled error/status pages for the WEBSITE's navigable surface only: GET/HEAD requests
// outside every wire prefix. Scoped with UseWhen so the wire surfaces, /api/*, /bss/*,
// /oauth/*, /ws, /health, /menu-content.json, /debug and the game-client registration
// POST /users (all POSTs are excluded by the method gate), keep their exact envelopes,
// status codes and empty bodies (the client string-matches some of them;
// ApiRegressionGuardTest pins this). Website POST flows (login/favourite/report forms) also
// keep their raw statuses: re-executing a POST against the GET-only error page would REPLACE
// e.g. antiforgery's 400 with a 404. For in-scope requests:
//  - bodyless 4xx/5xx (bare NotFound(), unmatched routes) re-execute through /error/{code}
//    with the original status preserved;
//  - unhandled page exceptions render /error/500 (Sentry still captures them via the
//    IExceptionHandlerFeature its middleware inspects).
// The explicit UseRouting() below is load-bearing: it keeps route matching DOWNSTREAM of
// these handlers, so the re-executed /error/{code} path gets routed to the error page (with
// the implicit start-of-pipeline routing of minimal hosting, re-execution would find the
// original request's already-resolved endpoint and render nothing).
app.UseWhen(
    ctx => (HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsHead(ctx.Request.Method)) && !IsWireRoute(ctx),
    site =>
    {
        site.UseExceptionHandler("/error/500");
        site.UseStatusCodePagesWithReExecute("/error/{0}");
    });

app.UseWebSockets();

// Website assets (css/fonts/favicon) from wwwroot.
app.UseStaticFiles();

app.UseRouting();

// Website sessions: resolves the typebeat_session cookie (when present) to an AuthedUser in
// HttpContext.Items. Bearer-API and anonymous requests pass straight through.
app.UseSessionCookieAuth();

// Core routes (health, placeholder site, menu content).
// GET + HEAD: uptime monitors (e.g. UptimeRobot's plain HTTP checks) probe with HEAD, which
// MapGet alone would 405. The DB round-trip runs for both, so HEAD still proves the full chain.
app.MapMethods("/health", new[] { HttpMethods.Get, HttpMethods.Head }, async (Db db) =>
{
    await using var conn = await db.OpenAsync();
    await conn.ExecuteScalarAsync<int>("SELECT 1");
    return Results.Text("ok");
});

// The in-game menu banner polls this (repointed from assets.ppy.sh in the client).
app.MapGet("/menu-content.json", () => WireJson.Ok(new { images = Array.Empty<object>() }));

// Sentry verification: deliberately throws so error capture can be proven end-to-end. Gated
// behind an env flag (normally unset) so production has no open crash surface to spam.
if (Flags.IsEnabled(app.Configuration, "TYPEBEAT_ENABLE_DEBUG_THROW"))
{
    app.MapGet("/debug/throw", string () =>
        throw new InvalidOperationException("sentry verification: deliberate test exception"));
}

// Wire endpoint modules (each maps its own routes).
OAuthEndpoints.Map(app);
RegistrationEndpoints.Map(app);
MeEndpoints.Map(app);
UserEndpoints.Map(app);
StubEndpoints.Map(app);
NotificationsSocket.Map(app);
BeatmapLookupEndpoints.Map(app);
BeatmapsetEndpoints.Map(app);
ScoreEndpoints.Map(app);
ReplayEndpoints.Map(app);

// M3: lazer-compatible submission service (/bss/*) + stored media/package serving. The two
// BSS upload routes raise their own Kestrel body cap (~100 MB) via endpoint metadata; every
// other route keeps the default.
BssEndpoints.Map(app);
MediaEndpoints.Map(app);
AlignEndpoints.Map(app);

// Private score feed for the Discord bot (discord-buddybot). Self-disables (404s) unless
// TYPEBEAT_BUDDY_KEY is configured, so a deploy that has not opted in exposes nothing.
BuddyEndpoints.Map(app);

// The in-browser web player's backend (score tokens/submission + map/audio serving). Additive;
// cookie-session authed, mirrors the bearer score flow in ScoreEndpoints.
PlayEndpoints.Map(app);

// The website pages ("/", /login, /register, /legal/dmca, ...). Mapped after the wire modules;
// none of their routes overlap the API surface.
app.MapRazorPages();

app.Run();

// A wire (game-client / API) route: must NEVER be wrapped by the website's styled error
// pages. Purely path-based; the caller additionally gates on GET/HEAD, which is what keeps
// the registration POST /users on wire semantics while GET /users/{idOrName} (the website
// profile) stays styled.
static bool IsWireRoute(HttpContext ctx)
{
    var path = ctx.Request.Path;

    return path.StartsWithSegments("/api")
           || path.StartsWithSegments("/bss")
           || path.StartsWithSegments("/oauth")
           || path.StartsWithSegments("/ws")
           || path.StartsWithSegments("/health")
           || path.StartsWithSegments("/debug")
           || path == "/menu-content.json";
}

// Exposed for WebApplicationFactory-based tests (wire-compat harness, Stage C).
public partial class Program;
