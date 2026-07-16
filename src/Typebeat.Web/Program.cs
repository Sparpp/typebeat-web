using Microsoft.AspNetCore.HttpOverrides;
using Dapper;
using Npgsql;
using Typebeat.Web;
using Typebeat.Web.Auth;
using Typebeat.Web.Data;
using Typebeat.Web.Endpoints;
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
// trusted extension (PG13+), so the app's own DB-owner role can create it — no superuser needed.
await Db.EnsureExtensionsAsync(connectionString);

builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton<Db>(sp => new Db(sp.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddSingleton<TokenService>();
builder.Services.AddSingleton<PasswordService>();

var app = builder.Build();

await app.Services.GetRequiredService<Db>().MigrateAsync(app.Logger);

// Behind the Caddy reverse proxy, honor X-Forwarded-For / X-Forwarded-Proto so Request.Scheme is
// "https" (the notification_endpoint must be wss://, cover/avatar URLs must be https://) and
// Connection.RemoteIpAddress is the real client IP (the rate limiters key on it). Only enabled
// when TYPEBEAT_BEHIND_PROXY is set, so local dev keeps the direct connection info. KnownNetworks/
// Proxies are cleared because the proxy is a trusted same-host container on the Docker network.
// NOTE: flags are read with tolerant parsing (Flags.IsEnabled), never GetValue<bool> — compose
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

app.UseWebSockets();

// Core routes (health, placeholder site, menu content).
// GET + HEAD: uptime monitors (e.g. UptimeRobot's plain HTTP checks) probe with HEAD, which
// MapGet alone would 405. The DB round-trip runs for both, so HEAD still proves the full chain.
app.MapMethods("/health", new[] { HttpMethods.Get, HttpMethods.Head }, async (Db db) =>
{
    await using var conn = await db.OpenAsync();
    await conn.ExecuteScalarAsync<int>("SELECT 1");
    return Results.Text("ok");
});

app.MapGet("/", () => Results.Content(
    "<!doctype html><title>type!beat</title><h1>type!beat</h1><p>online services are running.</p>"
    + "<p><a href=\"https://stats.uptimerobot.com/E7XRJ7vfer\">service status</a></p>",
    "text/html"));

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
StubEndpoints.Map(app);
NotificationsSocket.Map(app);
BeatmapLookupEndpoints.Map(app);
ScoreEndpoints.Map(app);

app.Run();

// Exposed for WebApplicationFactory-based tests (wire-compat harness, Stage C).
public partial class Program;
