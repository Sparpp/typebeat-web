using System.Buffers.Text;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace Typebeat.Web.Auth;

/// <summary>
/// "Continue with Google" configuration: <c>TYPEBEAT_GOOGLE_CLIENT_ID</c> and
/// <c>TYPEBEAT_GOOGLE_CLIENT_SECRET</c>, both from a Google Cloud OAuth client of type "Web
/// application" (deploy/README.md). With either unset (or empty, which is how compose passes an
/// unset key) the feature is OFF: every Google button is hidden and every /auth/google route 404s.
/// </summary>
public sealed record GoogleOidcOptions(string? ClientId, string? ClientSecret)
{
    public bool Enabled => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);

    public static GoogleOidcOptions FromConfiguration(IConfiguration config)
        => new(config["TYPEBEAT_GOOGLE_CLIENT_ID"]?.Trim(), config["TYPEBEAT_GOOGLE_CLIENT_SECRET"]?.Trim());
}

/// <summary>
/// Google OpenID Connect, authorization-code flow with PKCE, done server side and by hand.
///
/// Deliberately NOT the ASP.NET authentication handlers (AddAuthentication().AddOpenIdConnect()):
/// the site has no authentication middleware at all (SessionCookieAuth is a hand-rolled
/// middleware over the same opaque tokens the game carries), and the OIDC handler would bring a
/// second cookie scheme and a ClaimsPrincipal model only to be translated straight back into ours.
/// The flow is short enough to own:
///
///  1. <see cref="Begin"/> mints a random state, nonce and PKCE verifier, stores them in the
///     short-lived protected <see cref="GoogleCookies"/> flow cookie, and redirects to Google with
///     the S256 challenge.
///  2. Google redirects back to <see cref="CallbackPath"/> with <c>code</c> and <c>state</c>. The
///     callback checks the state against the cookie, and <see cref="ExchangeAsync"/> redeems the
///     code (with the client secret AND the verifier) for an ID token.
///  3. <see cref="GoogleIdTokenValidator"/> verifies that token against Google's JWKS
///     (<see cref="GetKeysAsync"/>, cached) and our client id and the cookie's nonce.
///
/// The redirect URI is built from the request's own scheme and host, so it is whatever host the
/// user is on (typebeat.mingda.sh in prod; TYPEBEAT_BEHIND_PROXY makes the scheme https). It must
/// be registered, exactly, as an authorised redirect URI on the OAuth client.
/// </summary>
public sealed class GoogleOidc(GoogleOidcOptions options, IHttpClientFactory httpClients, ILogger<GoogleOidc> logger)
{
    public const string CallbackPath = "/auth/google/callback";

    private const string authorization_endpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string token_endpoint = "https://oauth2.googleapis.com/token";
    private const string jwks_endpoint = "https://www.googleapis.com/oauth2/v3/certs";

    // Google rotates its signing keys every few weeks and serves them with a Cache-Control max-age
    // of several hours. An hour is well inside that; an unknown kid also forces a refetch (at most
    // once a minute), so a rotation never strands a sign-in.
    private static readonly TimeSpan jwks_lifetime = TimeSpan.FromHours(1);
    private static readonly TimeSpan jwks_min_refetch = TimeSpan.FromMinutes(1);

    private readonly SemaphoreSlim jwksLock = new(1, 1);

    // One immutable snapshot swapped whole, so a reader never sees new keys with an old timestamp.
    private sealed record JwksSnapshot(IReadOnlyDictionary<string, RSAParameters> Keys, DateTimeOffset FetchedAt);

    private volatile JwksSnapshot jwks = new(new Dictionary<string, RSAParameters>(), DateTimeOffset.MinValue);

    public GoogleOidcOptions Options => options;

    public bool Enabled => options.Enabled;

    public static string RedirectUri(HttpRequest request) => $"{request.Scheme}://{request.Host}{CallbackPath}";

    /// <summary>
    /// Starts a flow: sets the flow cookie and returns the Google URL to redirect the browser to.
    /// <paramref name="linkUserId"/> non-null means link mode (Settings), bound to that signed-in
    /// user; null is sign-in mode.
    /// </summary>
    public string Begin(HttpContext ctx, GoogleCookies cookies, long? linkUserId)
    {
        string state = randomToken();
        string nonce = randomToken();
        string verifier = randomToken();

        cookies.IssueFlow(ctx, new GoogleCookies.Flow(state, nonce, verifier, linkUserId, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

        return QueryHelpers.AddQueryString(authorization_endpoint, new Dictionary<string, string?>
        {
            ["client_id"] = options.ClientId,
            ["redirect_uri"] = RedirectUri(ctx.Request),
            ["response_type"] = "code",
            ["scope"] = "openid email profile",
            ["state"] = state,
            ["nonce"] = nonce,
            ["code_challenge"] = PkceChallenge(verifier),
            ["code_challenge_method"] = "S256",
            // Always show the account chooser: someone signed in to several Google accounts must be
            // able to pick which one to use (and to link), not be handed the browser's default.
            ["prompt"] = "select_account",
        });
    }

    /// <summary>RFC 7636 S256: base64url(SHA-256(ASCII(verifier))).</summary>
    public static string PkceChallenge(string verifier)
        => Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    /// <summary>
    /// Redeems an authorization code for its validated identity. Null on any failure (the reason
    /// is logged, never shown: the user just gets "try again").
    /// </summary>
    public async Task<GoogleIdentity?> ExchangeAsync(HttpContext ctx, string code, GoogleCookies.Flow flow, CancellationToken ct)
    {
        string? idToken;

        try
        {
            using var client = httpClients.CreateClient(nameof(GoogleOidc));
            client.Timeout = TimeSpan.FromSeconds(10);

            using var response = await client.PostAsync(token_endpoint, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["client_id"] = options.ClientId!,
                ["client_secret"] = options.ClientSecret!,
                ["redirect_uri"] = RedirectUri(ctx.Request),
                ["code_verifier"] = flow.Verifier,
            }), ct);

            string body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                // Google's error body is {error, error_description}: no secrets, safe to log.
                logger.LogWarning("Google token exchange failed with {Status}: {Body}", (int)response.StatusCode, body);
                return null;
            }

            using var doc = JsonDocument.Parse(body);
            idToken = doc.RootElement.TryGetProperty("id_token", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(ex, "Google token exchange failed");
            return null;
        }

        if (idToken is null)
        {
            logger.LogWarning("Google token response carried no id_token");
            return null;
        }

        var keys = await GetKeysAsync(idToken, ct);
        var result = GoogleIdTokenValidator.Validate(idToken, keys, options.ClientId!, flow.Nonce, DateTimeOffset.UtcNow);

        if (!result.Succeeded)
        {
            logger.LogWarning("Google ID token rejected: {Reason}", result.Failure);
            return null;
        }

        return result.Identity;
    }

    /// <summary>
    /// Google's signing keys, cached for <see cref="jwks_lifetime"/>, refetched early when the
    /// token names a kid the cache does not hold (a key rotation). A fetch failure keeps whatever
    /// keys are cached, so the validator then refuses on "unknown signing key" rather than throwing.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, RSAParameters>> GetKeysAsync(string idToken, CancellationToken ct)
    {
        string? kid = peekKid(idToken);
        var now = DateTimeOffset.UtcNow;
        var cached = jwks;

        bool fresh = now - cached.FetchedAt < jwks_lifetime;
        if (fresh && (kid is null || cached.Keys.ContainsKey(kid)))
            return cached.Keys;

        await jwksLock.WaitAsync(ct);
        try
        {
            // Re-check under the lock (another request may have just refetched), and never hammer
            // Google for a kid it simply does not have.
            cached = jwks;
            if (now - cached.FetchedAt < jwks_min_refetch)
                return cached.Keys;

            using var client = httpClients.CreateClient(nameof(GoogleOidc));
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            string json = await client.GetStringAsync(jwks_endpoint, ct);
            jwks = new JwksSnapshot(GoogleIdTokenValidator.ParseJwks(json), DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or FormatException)
        {
            logger.LogWarning(ex, "Fetching Google's JWKS failed; keeping {Count} cached keys", jwks.Keys.Count);
        }
        finally
        {
            jwksLock.Release();
        }

        return jwks.Keys;
    }

    private static string? peekKid(string idToken)
    {
        try
        {
            int dot = idToken.IndexOf('.');
            if (dot <= 0)
                return null;

            using var header = JsonDocument.Parse(Base64Url.DecodeFromChars(idToken.AsSpan(0, dot)));
            return header.RootElement.TryGetProperty("kid", out var kid) ? kid.GetString() : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    // 32 CSPRNG bytes, base64url: 43 characters, which is also a valid PKCE verifier (43 to 128).
    private static string randomToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
}
