using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;

namespace Typebeat.Web.Auth;

/// <summary>
/// The two short-lived cookies the Google flow carries between requests, built exactly like
/// <see cref="ChallengeCookie"/>: Data-Protection-encrypted (tamper-proof and opaque to the
/// browser), HttpOnly, Secure, SameSite=Lax, with the issue time inside the payload so an old value
/// is refused even if the browser kept it. Each has its own protector purpose, so one can never be
/// replayed as the other.
///
///  - FLOW (<see cref="FlowCookieName"/>): state, nonce, PKCE verifier, and in link mode the id of
///    the user who started it. Set by <see cref="GoogleOidc.Begin"/>, read and cleared once by the
///    callback. Lax is what lets it ride along on Google's top-level GET redirect back to us.
///  - PENDING SIGN-UP (<see cref="PendingCookieName"/>): the validated Google identity of someone
///    with no account yet, carried to the "choose a username" step. Never a session: nothing is
///    signed in until that step creates the account.
/// </summary>
public sealed class GoogleCookies(IDataProtectionProvider provider)
{
    public const string FlowCookieName = "typebeat_google";
    public const string PendingCookieName = "typebeat_google_signup";

    // Long enough to pick an account on Google's chooser (and, the first time, read its consent
    // screen); short enough that an abandoned flow is dead well before anyone could reuse it.
    public static readonly TimeSpan FlowLifetime = TimeSpan.FromMinutes(10);

    // Same window as ChallengeCookie: time to think of a username, no more.
    public static readonly TimeSpan PendingLifetime = TimeSpan.FromMinutes(15);

    private readonly IDataProtector flowProtector = provider.CreateProtector("typebeat.google.flow.v1");
    private readonly IDataProtector pendingProtector = provider.CreateProtector("typebeat.google.pending.v1");

    /// <param name="LinkUserId">Non-null in link mode: the signed-in user who pressed "Link Google account".</param>
    public sealed record Flow(string State, string Nonce, string Verifier, long? LinkUserId, long IssuedUnix)
    {
        [JsonIgnore]
        public bool IsLink => LinkUserId is not null;
    }

    public sealed record Pending(string Subject, string Email, string? Name, long IssuedUnix)
    {
        [JsonIgnore]
        public GoogleIdentity Identity => new(Subject, Email, Name);
    }

    public void IssueFlow(HttpContext ctx, Flow flow) => issue(ctx, FlowCookieName, flowProtector, flow, FlowLifetime);

    public Flow? ReadFlow(HttpContext ctx) => read<Flow>(ctx, FlowCookieName, flowProtector, f => f.IssuedUnix, FlowLifetime);

    public void ClearFlow(HttpContext ctx) => clear(ctx, FlowCookieName);

    public void IssuePending(HttpContext ctx, GoogleIdentity identity)
        => issue(ctx, PendingCookieName, pendingProtector,
            new Pending(identity.Subject, identity.Email, identity.Name, DateTimeOffset.UtcNow.ToUnixTimeSeconds()), PendingLifetime);

    public Pending? ReadPending(HttpContext ctx) => read<Pending>(ctx, PendingCookieName, pendingProtector, p => p.IssuedUnix, PendingLifetime);

    public void ClearPending(HttpContext ctx) => clear(ctx, PendingCookieName);

    private static void issue<T>(HttpContext ctx, string name, IDataProtector protector, T payload, TimeSpan lifetime)
        => ctx.Response.Cookies.Append(name, protector.Protect(JsonSerializer.Serialize(payload)), new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            MaxAge = lifetime,
        });

    // Fails closed on anything: absent, expired, tampered with, or from an older payload shape.
    private static T? read<T>(HttpContext ctx, string name, IDataProtector protector, Func<T, long> issuedUnix, TimeSpan lifetime) where T : class
    {
        if (!ctx.Request.Cookies.TryGetValue(name, out string? raw) || string.IsNullOrEmpty(raw))
            return null;

        try
        {
            var payload = JsonSerializer.Deserialize<T>(protector.Unprotect(raw));
            if (payload is null)
                return null;

            var issued = DateTimeOffset.FromUnixTimeSeconds(issuedUnix(payload));
            return DateTimeOffset.UtcNow - issued > lifetime ? null : payload;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    private static void clear(HttpContext ctx, string name)
        => ctx.Response.Cookies.Delete(name, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Path = "/",
        });
}
