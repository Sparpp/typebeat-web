using Typebeat.Web.Data;

namespace Typebeat.Web.Auth;

/// <summary>
/// Website session auth: the same opaque access tokens the game client carries as a bearer
/// header, delivered in an HttpOnly cookie instead. Login issues a row via
/// <see cref="TokenService.IssueAsync"/> and stores the RAW access token in the cookie; the
/// middleware resolves it per-request via <see cref="TokenService.ResolveAsync"/>, so the
/// website and the API share one identity and one token store, no parallel session scheme.
///
/// Deliberately NOT ASP.NET authentication middleware: the repo's bearer path is a hand-rolled
/// endpoint filter (AuthExtensions), and this mirrors it for cookies. CSRF is covered by Razor
/// Pages' built-in antiforgery validation on every POST handler.
/// </summary>
public static class SessionCookieAuth
{
    public const string CookieName = "typebeat_session";

    /// <summary>
    /// Access-token (and therefore cookie) lifetime for a "remember me" website login: 30 days,
    /// absolute. Absolute, not sliding, because the web has no refresh flow (see TokenService); a
    /// sliding window would mean re-issuing the token on every request, which the token model
    /// deliberately avoids. After 30 days the user signs in and re-verifies once.
    /// </summary>
    public const long RememberMeLifetimeSeconds = 30L * 24 * 60 * 60;

    private const string item_key = "typebeat.web_user";

    /// <summary>
    /// Resolves the session cookie (when present) into an <see cref="AuthedUser"/> stashed in
    /// <c>HttpContext.Items</c>. Anonymous and bearer-API requests pay one cookie-dictionary
    /// miss and nothing else.
    /// </summary>
    public static IApplicationBuilder UseSessionCookieAuth(this IApplicationBuilder app)
        => app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Cookies.TryGetValue(CookieName, out string? token) && !string.IsNullOrEmpty(token))
            {
                var user = await ctx.RequestServices.GetRequiredService<TokenService>().ResolveAsync(token);

                // Mirror the bearer path (AuthExtensions.ResolveBearerAsync): restricted users
                // are treated as signed out.
                if (user is { Restricted: false })
                {
                    ctx.Items[item_key] = user;

                    // Website page loads keep users.last_visit fresh (throttled; never anonymous;
                    // this branch only runs for a resolved cookie user).
                    await LastVisit.TouchAsync(ctx.RequestServices.GetRequiredService<Db>(), user.Id, ctx.RequestAborted);
                }
            }

            await next(ctx);
        });

    /// <summary>The signed-in website user, or null. Available to pages and views alike.</summary>
    public static AuthedUser? SessionUser(this HttpContext ctx)
        => ctx.Items[item_key] as AuthedUser;

    /// <summary>
    /// Issues the session cookie carrying <paramref name="pair"/>'s access token. Cookie life
    /// matches the token's life (24h normally, or <see cref="RememberMeLifetimeSeconds"/> for a
    /// "remember me" login). There is no refresh flow on the web; users just sign in again. Secure
    /// is safe on localhost dev (browsers exempt localhost).
    /// </summary>
    public static void SignIn(HttpContext ctx, TokenPair pair)
    {
        ctx.Response.Cookies.Append(CookieName, pair.AccessToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            MaxAge = TimeSpan.FromSeconds(pair.ExpiresInSeconds),
        });
    }

    /// <summary>
    /// Deletes the cookie and revokes its token row (only that row; the user's game-client
    /// sessions keep their own tokens).
    /// </summary>
    public static async Task SignOutAsync(HttpContext ctx, TokenService tokens)
    {
        if (ctx.Request.Cookies.TryGetValue(CookieName, out string? token) && !string.IsNullOrEmpty(token))
            await tokens.RevokeByAccessTokenAsync(token);

        ctx.Response.Cookies.Delete(CookieName, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Path = "/",
        });
    }
}
