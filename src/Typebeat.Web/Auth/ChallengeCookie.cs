using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Typebeat.Web.Auth;

/// <summary>
/// The pending-user token that carries someone between the password step and the code step
/// WITHOUT logging them in. It is a Data-Protection-encrypted cookie (tamper-proof, opaque to the
/// browser) holding only {userId, purpose, issuedUnix} — never a real session token. The /verify
/// page reads it to know who is mid-flow; a signed session is minted only after the code checks
/// out.
///
/// Data Protection keys are persisted to disk in prod (see Program.cs) so a container redeploy
/// does not invalidate every in-flight challenge cookie (and antiforgery token).
/// </summary>
public sealed class ChallengeCookie(IDataProtectionProvider provider)
{
    public const string CookieName = "typebeat_challenge";

    // Mid-flow window: long enough to receive the email and type the code, short enough to bound
    // exposure. Matches the cookie MaxAge below.
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    private readonly IDataProtector protector = provider.CreateProtector("typebeat.challenge.v1");

    public sealed record Payload(long UserId, string Purpose, long IssuedUnix);

    public void Issue(HttpContext ctx, long userId, string purpose)
    {
        string json = JsonSerializer.Serialize(new Payload(userId, purpose, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        string value = protector.Protect(json);

        ctx.Response.Cookies.Append(CookieName, value, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            MaxAge = Lifetime,
        });
    }

    /// <summary>
    /// Returns the challenge payload, or null if the cookie is absent, expired, or tampered with
    /// (unprotect failure). Fails closed on any error.
    /// </summary>
    public Payload? Read(HttpContext ctx)
    {
        if (!ctx.Request.Cookies.TryGetValue(CookieName, out string? raw) || string.IsNullOrEmpty(raw))
            return null;

        try
        {
            var payload = JsonSerializer.Deserialize<Payload>(protector.Unprotect(raw));
            if (payload is null)
                return null;

            var issued = DateTimeOffset.FromUnixTimeSeconds(payload.IssuedUnix);
            if (DateTimeOffset.UtcNow - issued > Lifetime)
                return null;

            return payload;
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Clear(HttpContext ctx)
        => ctx.Response.Cookies.Delete(CookieName, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Path = "/",
        });
}
