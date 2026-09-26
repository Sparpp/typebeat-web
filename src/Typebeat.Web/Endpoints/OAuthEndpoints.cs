using System.Collections.Concurrent;
using Dapper;
using Newtonsoft.Json;
using Typebeat.Web.Auth;
using Typebeat.Web.Data;
using Typebeat.Web.Wire;

namespace Typebeat.Web.Endpoints;

/// <summary>
/// POST /oauth/token: the only token endpoint the client hits. Two grant types, both
/// form-encoded (see typebeat.Game.Online.API.OAuth.AccessTokenRequest.PrePerform):
///  - grant_type=password        → username, password, client_id, client_secret, scope
///  - grant_type=refresh_token   → refresh_token, client_id, client_secret, scope
///
/// The client (OAuth.AuthenticateWithLogin) decodes any error body as OAuthError
/// {error, hint, message} and surfaces <c>hint ?? error</c> to the user, so we put the
/// human-readable text in <c>hint</c>. Failed credentials are 400 invalid_grant, NEVER 401:
/// a 401 anywhere in the connect flow is the client's "session no longer valid → Logout"
/// signal (APIAccess.handleWebException), which we must not trip on a mere wrong password.
/// </summary>
public static class OAuthEndpoints
{
    // Baked into the official client (TypebeatEndpointConfiguration): a public client
    // credential, treated as an identifier not a proof of trust.
    private const string client_id = "1";
    private const string client_secret = "typebeat-official-client";

    // Per-IP fixed-window limiter on the password grant. In-memory backstop only; Cloudflare
    // WAF is the real production layer. static so it survives across requests (endpoints are
    // otherwise stateless).
    private const int rate_limit_max = 10;
    private static readonly TimeSpan rate_limit_window = TimeSpan.FromMinutes(5);
    private static readonly ConcurrentDictionary<string, RateWindow> password_attempts = new();

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/oauth/token", HandleAsync);
    }

    private static async Task<IResult> HandleAsync(HttpContext ctx, Db db, TokenService tokens, PasswordService passwords)
    {
        if (!ctx.Request.HasFormContentType)
            return OAuthError("unsupported_grant_type", "The authorization grant type is not supported.");

        var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);

        string grantType = form["grant_type"].ToString();

        // client_id / client_secret are sent on both grants; validate against the baked pair.
        if (form["client_id"].ToString() != client_id || form["client_secret"].ToString() != client_secret)
            return OAuthError("invalid_client", "Client authentication failed.");

        return grantType switch
        {
            "password"      => await handlePasswordGrantAsync(ctx, db, tokens, passwords, form),
            "refresh_token" => await handleRefreshGrantAsync(tokens, form),
            _               => OAuthError("unsupported_grant_type", "The authorization grant type is not supported."),
        };
    }

    private static async Task<IResult> handlePasswordGrantAsync(HttpContext ctx, Db db, TokenService tokens, PasswordService passwords, IFormCollection form)
    {
        if (!allowPasswordAttempt(ctx))
            return OAuthError("invalid_request", "Too many login attempts. Please wait a few minutes and try again.", StatusCodes.Status429TooManyRequests);

        // The client's login field is a single free-text box (APIAccess.ProvidedUsername); osu
        // accepts either the username or the email there, so we look up on both (citext columns).
        string login = form["username"].ToString();
        string password = form["password"].ToString();

        // Same 400 invalid_grant for every failure mode below (unknown login, wrong password,
        // restricted); never distinguish, so the endpoint leaks nothing about which accounts exist.
        const string bad_credentials = "The username or password is incorrect.";

        if (login.Length == 0 || password.Length == 0)
            return OAuthError("invalid_grant", bad_credentials);

        UserRow? user;
        await using (var conn = await db.OpenAsync(ctx.RequestAborted))
        {
            user = await conn.QuerySingleOrDefaultAsync<UserRow>(
                """
                SELECT id, password_hash AS passwordHash, restricted
                FROM users
                WHERE username = @login OR email = @login
                """,
                new { login });
        }

        if (user is null || !passwords.Verify(user.PasswordHash, password))
            return OAuthError("invalid_grant", bad_credentials);

        // Restricted accounts cannot obtain a token; surfaced as a generic credential failure.
        if (user.Restricted)
            return OAuthError("invalid_grant", bad_credentials);

        var pair = await tokens.IssueAsync(user.Id);
        return tokenResponse(pair);
    }

    private static async Task<IResult> handleRefreshGrantAsync(TokenService tokens, IFormCollection form)
    {
        string refreshToken = form["refresh_token"].ToString();

        if (refreshToken.Length == 0)
            return OAuthError("invalid_request", "The refresh token is missing.");

        var pair = await tokens.RefreshAsync(refreshToken);

        // Unknown / expired / revoked refresh token. The client (OAuth.AuthenticateWithRefresh)
        // treats a non-network error here as "clear token, fall back to a full password re-auth".
        if (pair is null)
            return OAuthError("invalid_grant", "The refresh token is invalid or has expired.");

        return tokenResponse(pair);
    }

    /// <summary>The success body consumed by <c>OAuthToken</c>; token_type is ignored by the client but sent for correctness.</summary>
    private static IResult tokenResponse(TokenPair pair)
        => WireJson.Ok(new TokenResponse
        {
            TokenType = "Bearer",
            ExpiresIn = pair.ExpiresInSeconds,
            AccessToken = pair.AccessToken,
            RefreshToken = pair.RefreshToken,
        });

    // Default 400 Bad Request: the OAuth2 status for invalid_grant / unsupported_grant_type, and
    // deliberately not 401 (which the client reads as session-invalidation → Logout).
    private static IResult OAuthError(string error, string hint, int statusCode = StatusCodes.Status400BadRequest)
        => WireJson.Ok(new OAuthErrorBody { Error = error, Hint = hint, Message = hint }, statusCode);

    private static bool allowPasswordAttempt(HttpContext ctx)
    {
        string ip = ctx.GetClientIp();
        var now = DateTimeOffset.UtcNow;

        var window = password_attempts.AddOrUpdate(
            ip,
            _ => new RateWindow(now, 1),
            (_, existing) => now - existing.Start >= rate_limit_window
                ? new RateWindow(now, 1)
                : existing with { Count = existing.Count + 1 });

        return window.Count <= rate_limit_max;
    }

    private sealed record RateWindow(DateTimeOffset Start, int Count);

    // password_hash is text, and NULL for an account created through Google (035), which has no
    // password and so can never take this grant until it sets one on the website (Verify refuses a
    // null hash). restricted gates token issue. Aliased to camelCase per the SELECT idiom.
    private sealed record UserRow(long Id, string? PasswordHash, bool Restricted);

    /// <summary>
    /// Success shape. Field order mirrors the task contract; <c>OAuthToken</c> reads by name
    /// (Newtonsoft, no order sensitivity) so only the [JsonProperty] names are load-bearing.
    /// </summary>
    public sealed class TokenResponse
    {
        [JsonProperty("token_type")]
        public required string TokenType { get; init; }

        [JsonProperty("expires_in")]
        public required long ExpiresIn { get; init; }

        [JsonProperty("access_token")]
        public required string AccessToken { get; init; }

        [JsonProperty("refresh_token")]
        public required string RefreshToken { get; init; }
    }

    /// <summary>
    /// The error envelope the client parses as OAuth.OAuthError. Property order matches that class
    /// (error, hint, message); the client surfaces <c>hint</c> when present, else <c>error</c>.
    /// </summary>
    public sealed class OAuthErrorBody
    {
        [JsonProperty("error")]
        public required string Error { get; init; }

        [JsonProperty("hint")]
        public required string Hint { get; init; }

        [JsonProperty("message")]
        public required string Message { get; init; }
    }
}
