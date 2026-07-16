using System.Security.Cryptography;
using System.Text;
using Dapper;
using Typebeat.Web.Data;

namespace Typebeat.Web.Auth;

public sealed record TokenPair(string AccessToken, string RefreshToken, long ExpiresInSeconds);

public sealed record AuthedUser(long Id, string Username, string CountryCode, bool IsAdmin, bool Restricted);

/// <summary>
/// Opaque bearer tokens: 32 random bytes, base64url on the wire, SHA-256 hashed at rest.
///
/// Refresh contract (the client logs the user out on ANY non-5xx auth failure, so this must
/// never reject a token a live client can legitimately hold):
///  - access tokens live 24h (client treats expires_in &lt;= 30 as already invalid);
///  - refresh tokens live 30 days and ROTATE on use;
///  - the PREVIOUS refresh token stays valid for a 60s grace window after rotation, so a
///    client retry racing a successful rotation still succeeds.
/// </summary>
public sealed class TokenService(Db db)
{
    public const long ACCESS_LIFETIME_SECONDS = 86_400;
    public const int REFRESH_LIFETIME_DAYS = 30;
    public const int ROTATION_GRACE_SECONDS = 60;

    public async Task<TokenPair> IssueAsync(long userId)
    {
        (string access, byte[] accessHash) = newToken();
        (string refresh, byte[] refreshHash) = newToken();

        await using var conn = await db.OpenAsync();

        await conn.ExecuteAsync(
            """
            INSERT INTO oauth_tokens (user_id, access_hash, refresh_hash, access_expires_at, refresh_expires_at)
            VALUES (@userId, @accessHash, @refreshHash,
                    now() + make_interval(secs => @accessLifetime),
                    now() + make_interval(days => @refreshDays))
            """,
            new { userId, accessHash, refreshHash, accessLifetime = (double)ACCESS_LIFETIME_SECONDS, refreshDays = REFRESH_LIFETIME_DAYS });

        return new TokenPair(access, refresh, ACCESS_LIFETIME_SECONDS);
    }

    /// <summary>
    /// Exchanges a refresh token for a fresh pair, or returns null if it is unknown, expired,
    /// or revoked. Each exchange INSERTS a new token row — previously issued access tokens
    /// live out their natural expiry, so a rotation can never log out a live client. The used
    /// refresh token is marked consumed on first use but remains exchangeable within a short
    /// grace window (each use minting an independent pair), so a client retry racing a
    /// successful rotation also never gets logged out.
    /// </summary>
    public async Task<TokenPair?> RefreshAsync(string refreshToken)
    {
        byte[] hash = Hash(refreshToken);

        await using var conn = await db.OpenAsync();

        // Single statement: consume-or-grace-reuse. Sets consumed_at on first use; matches
        // again only while inside the grace window. Returns the owning user when valid.
        long? userId = await conn.ExecuteScalarAsync<long?>(
            """
            UPDATE oauth_tokens
            SET consumed_at = COALESCE(consumed_at, now())
            WHERE refresh_hash = @hash
              AND revoked_at IS NULL
              AND refresh_expires_at > now()
              AND (consumed_at IS NULL OR consumed_at > now() - make_interval(secs => @grace))
            RETURNING user_id
            """,
            new { hash, grace = (double)ROTATION_GRACE_SECONDS });

        if (userId is not long id)
            return null;

        return await IssueAsync(id);
    }

    /// <summary>Resolves a bearer access token to its (non-restricted) user, or null.</summary>
    public async Task<AuthedUser?> ResolveAsync(string accessToken)
    {
        byte[] hash = Hash(accessToken);

        await using var conn = await db.OpenAsync();

        return await conn.QuerySingleOrDefaultAsync<AuthedUser>(
            """
            SELECT u.id, u.username, u.country_code AS countryCode, u.is_admin AS isAdmin, u.restricted
            FROM oauth_tokens t
            JOIN users u ON u.id = t.user_id
            WHERE t.access_hash = @hash
              AND t.revoked_at IS NULL
              AND t.access_expires_at > now()
            """,
            new { hash });
    }

    /// <summary>
    /// Revokes the single token row holding this access token (website logout). A no-op for
    /// unknown tokens; never touches the user's other sessions.
    /// </summary>
    public async Task RevokeByAccessTokenAsync(string accessToken)
    {
        byte[] hash = Hash(accessToken);

        await using var conn = await db.OpenAsync();
        await conn.ExecuteAsync("UPDATE oauth_tokens SET revoked_at = now() WHERE access_hash = @hash AND revoked_at IS NULL", new { hash });
    }

    public async Task RevokeAllForUserAsync(long userId)
    {
        await using var conn = await db.OpenAsync();
        await conn.ExecuteAsync("UPDATE oauth_tokens SET revoked_at = now() WHERE user_id = @userId AND revoked_at IS NULL", new { userId });
    }

    public static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    private static (string token, byte[] hash) newToken()
    {
        Span<byte> raw = stackalloc byte[32];
        RandomNumberGenerator.Fill(raw);

        string token = Convert.ToBase64String(raw).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        return (token, Hash(token));
    }
}
