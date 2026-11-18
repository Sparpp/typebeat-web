using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Dapper;
using Typebeat.Web.Data;

namespace Typebeat.Web.Auth;

/// <summary>
/// Records where signed-in accounts are used from (migration 060): the client address, the client
/// instance (a game install's x-client-instance header, or a website cookie) and the user agent,
/// one row per (account, address, instance). Only resolved users are recorded, never anonymous
/// traffic, and a key already written in the last <see cref="RefreshInterval"/> costs nothing.
///
/// <para>
/// The first time an account turns up at an address or instance a restricted account also used, a
/// <c>sign_in_reviews</c> row is raised for an operator. Nothing acts on it automatically: shared
/// addresses (a household, a school, a carrier NAT) make it a lead, not a verdict.
/// </para>
///
/// <para>
/// Rows of unrestricted accounts are dropped once unused for <see cref="Retention"/>; a restricted
/// account's rows are kept, since matching against them is the point.
/// </para>
/// </summary>
public static partial class SignInHistory
{
    /// <summary>The header a game install sends its instance id in.</summary>
    public const string InstanceHeader = "x-client-instance";

    /// <summary>The website's instance cookie, set on signed-in browsers that have none.</summary>
    public const string InstanceCookie = "typebeat_ci";

    /// <summary>How long a written key is left alone before it is refreshed again.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(15);

    /// <summary>How long an unrestricted account's unused row is kept.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(90);

    private static readonly TimeSpan prune_interval = TimeSpan.FromHours(6);

    private const int max_user_agent = 256;

    private static readonly ConcurrentDictionary<(long UserId, string Ip, string Instance), DateTime> recently_written = new();

    private static DateTime lastPrune = DateTime.MinValue;

    [GeneratedRegex("^[A-Za-z0-9-]{16,64}$")]
    private static partial Regex instancePattern();

    /// <summary>Whether <paramref name="value"/> is a well-formed instance id (anything else is ignored).</summary>
    public static bool IsValidInstance(string? value) => !string.IsNullOrEmpty(value) && instancePattern().IsMatch(value);

    /// <summary>
    /// The middleware. Runs after the session and bearer resolution so it sees the user, and before
    /// the handler, so the instance cookie can still be set. A failure here never fails the request.
    /// </summary>
    public static IApplicationBuilder UseSignInHistory(this IApplicationBuilder app)
        => app.Use(async (ctx, next) =>
        {
            var user = ctx.SessionUser() ?? ctx.ResolvedBearer();

            if (user is { Restricted: false })
            {
                try
                {
                    await RecordAsync(ctx, user.Id);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    ctx.RequestServices.GetService<ILoggerFactory>()?.CreateLogger(typeof(SignInHistory))
                       .LogDebug(e, "Sign-in history write failed for user {UserId}.", user.Id);
                }
            }

            await next(ctx);
        });

    /// <summary>Records <paramref name="userId"/> against this request's address and instance.</summary>
    public static async Task RecordAsync(HttpContext ctx, long userId)
    {
        string ip = ctx.GetClientIp();

        if (ip == AuthExtensions.UnknownClientIp)
            return;

        string instance = instanceOf(ctx);
        var key = (userId, ip, instance);
        var now = DateTime.UtcNow;

        if (recently_written.TryGetValue(key, out var last) && now - last < RefreshInterval)
            return;

        recently_written[key] = now;

        string userAgent = ctx.Request.Headers.UserAgent.ToString();
        if (userAgent.Length > max_user_agent)
            userAgent = userAgent[..max_user_agent];

        var db = ctx.RequestServices.GetRequiredService<Db>();
        await using var conn = await db.OpenAsync(ctx.RequestAborted);

        // xmax = 0 only on a freshly inserted row: a new (account, address, instance) is the one
        // moment worth comparing against restricted accounts.
        bool inserted = await conn.ExecuteScalarAsync<bool>(
            """
            INSERT INTO sign_ins (user_id, ip, instance, user_agent)
            VALUES (@userId, @ip, @instance, @userAgent)
            ON CONFLICT (user_id, ip, instance) DO UPDATE
                SET last_seen = now(), hits = sign_ins.hits + 1, user_agent = EXCLUDED.user_agent
            RETURNING (xmax = 0)
            """,
            new { userId, ip, instance, userAgent });

        if (inserted)
            await raiseReviewsAsync(conn, userId, ip, instance);

        if (now - lastPrune > prune_interval)
        {
            lastPrune = now;
            await conn.ExecuteAsync(
                """
                DELETE FROM sign_ins s USING users u
                WHERE u.id = s.user_id AND NOT u.restricted AND s.last_seen < now() - @retention
                """,
                new { retention = Retention });
            pruneMemo(now);
        }
    }

    private static Task raiseReviewsAsync(Npgsql.NpgsqlConnection conn, long userId, string ip, string instance)
        => conn.ExecuteAsync(
            """
            INSERT INTO sign_in_reviews (user_id, other_user_id, via)
            SELECT DISTINCT @userId, s.user_id,
                   CASE WHEN @instance <> '' AND s.instance = @instance THEN 'instance' ELSE 'ip' END
            FROM sign_ins s
            JOIN users u ON u.id = s.user_id
            WHERE u.restricted
              AND s.user_id <> @userId
              AND (s.ip = @ip OR (@instance <> '' AND s.instance = @instance))
            ON CONFLICT (user_id, other_user_id, via) DO NOTHING
            """,
            new { userId, ip, instance });

    /// <summary>
    /// The request's instance: the game's header, else the website cookie. A signed-in website
    /// session with no cookie is given one here (a signed-in response is private and never cached,
    /// so the Set-Cookie cannot leak into a shared cache entry).
    /// </summary>
    private static string instanceOf(HttpContext ctx)
    {
        string header = ctx.Request.Headers[InstanceHeader].ToString();

        if (IsValidInstance(header))
            return header;

        if (ctx.Request.Cookies.TryGetValue(InstanceCookie, out string? cookie) && IsValidInstance(cookie))
            return cookie!;

        if (ctx.SessionUser() is null)
            return string.Empty;

        string issued = Guid.NewGuid().ToString("N");
        ctx.Response.Cookies.Append(InstanceCookie, issued, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            MaxAge = TimeSpan.FromDays(730),
        });
        return issued;
    }

    private static void pruneMemo(DateTime now)
    {
        foreach (var entry in recently_written)
        {
            if (now - entry.Value >= RefreshInterval)
                recently_written.TryRemove(entry.Key, out _);
        }
    }
}
