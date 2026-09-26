using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Typebeat.Web.Auth;

/// <summary>The part of a validated Google ID token the sign-in flow acts on.</summary>
/// <param name="Subject">Google's stable account id (<c>sub</c>): the only thing that identifies a returning user.</param>
/// <param name="Email">The account's address. Always verified by Google (the validator refuses otherwise).</param>
/// <param name="Name">The display name, when the profile scope returned one; used only to prefill a username.</param>
public sealed record GoogleIdentity(string Subject, string Email, string? Name);

/// <summary>
/// Validates a Google OpenID Connect ID token by hand: no JWT library, because the whole job is a
/// dozen checks and every one of them is visible here.
///
/// A token is accepted only when ALL of these hold:
///  - it is a three-part compact JWS whose header says <c>alg: RS256</c> (anything else, including
///    <c>none</c>, is refused before a key is looked at) and names a <c>kid</c> in Google's JWKS;
///  - the RS256 signature over <c>header.payload</c> verifies against that key;
///  - <c>iss</c> is Google (either of the two spellings Google documents);
///  - <c>aud</c> is our client id (a string, or an array containing it, in which case <c>azp</c>
///    must also be our client id, per OIDC core 3.1.3.7);
///  - <c>exp</c> is in the future and <c>iat</c> is not, both within <see cref="ClockSkew"/>;
///  - <c>nonce</c> equals the one this browser's flow cookie carried (constant-time compare);
///  - <c>sub</c> and <c>email</c> are present and <c>email_verified</c> is true (Google sends a JSON
///    boolean; the string "true" is also accepted, as older tokens used it).
///
/// Pure and static apart from the clock, which is a parameter, so every branch is unit tested
/// against a locally generated RSA key.
/// </summary>
public static class GoogleIdTokenValidator
{
    public static readonly string[] Issuers = ["https://accounts.google.com", "accounts.google.com"];

    public static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(2);

    public sealed record Result(GoogleIdentity? Identity, string? Failure)
    {
        public bool Succeeded => Identity is not null;
    }

    /// <param name="idToken">The compact JWS from the token endpoint's <c>id_token</c>.</param>
    /// <param name="keys">Google's signing keys by <c>kid</c> (see <see cref="ParseJwks"/>).</param>
    /// <param name="clientId">Our OAuth client id, the only acceptable audience.</param>
    /// <param name="expectedNonce">The nonce this browser's flow was started with.</param>
    /// <param name="now">The current time.</param>
    public static Result Validate(string idToken, IReadOnlyDictionary<string, RSAParameters> keys, string clientId, string expectedNonce, DateTimeOffset now)
    {
        string[] parts = idToken.Split('.');
        if (parts.Length != 3 || parts.Any(p => p.Length == 0))
            return fail("not a compact JWS");

        JsonDocument? headerDoc = null;
        JsonDocument? payloadDoc = null;

        try
        {
            byte[] signature;

            try
            {
                headerDoc = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[0]));
                payloadDoc = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[1]));
                signature = Base64Url.DecodeFromChars(parts[2]);
            }
            catch (Exception ex) when (ex is FormatException or JsonException)
            {
                return fail("malformed token");
            }

            return check(parts, headerDoc.RootElement, payloadDoc.RootElement, signature, keys, clientId, expectedNonce, now);
        }
        finally
        {
            headerDoc?.Dispose();
            payloadDoc?.Dispose();
        }
    }

    private static Result check(string[] parts, JsonElement header, JsonElement payload, byte[] signature,
                                IReadOnlyDictionary<string, RSAParameters> keys, string clientId, string expectedNonce, DateTimeOffset now)
    {
        if (header.ValueKind != JsonValueKind.Object || payload.ValueKind != JsonValueKind.Object)
            return fail("malformed token");

        if (stringClaim(header, "alg") != "RS256")
            return fail("unexpected alg");

        if (stringClaim(header, "kid") is not { } kid || !keys.TryGetValue(kid, out var key))
            return fail("unknown signing key");

        using (var rsa = RSA.Create())
        {
            rsa.ImportParameters(key);
            byte[] signed = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
            if (!rsa.VerifyData(signed, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                return fail("bad signature");
        }

        if (stringClaim(payload, "iss") is not { } iss || !Issuers.Contains(iss))
            return fail("wrong issuer");

        if (!audienceMatches(payload, clientId))
            return fail("wrong audience");

        if (numberClaim(payload, "exp") is not { } exp || DateTimeOffset.FromUnixTimeSeconds(exp) + ClockSkew <= now)
            return fail("expired");

        if (numberClaim(payload, "iat") is { } iat && DateTimeOffset.FromUnixTimeSeconds(iat) - ClockSkew > now)
            return fail("issued in the future");

        if (stringClaim(payload, "nonce") is not { } nonce || !fixedTimeEquals(nonce, expectedNonce))
            return fail("nonce mismatch");

        if (stringClaim(payload, "sub") is not { Length: > 0 } sub)
            return fail("no subject");

        if (stringClaim(payload, "email") is not { Length: > 0 } email)
            return fail("no email");

        if (!emailVerified(payload))
            return fail("email not verified");

        return new Result(new GoogleIdentity(sub, email, stringClaim(payload, "name")), null);
    }

    /// <summary>
    /// Parses a JWKS document (Google's <c>/oauth2/v3/certs</c>) into RSA public keys by kid.
    /// Non-RSA and kid-less entries are skipped; a malformed document throws.
    /// </summary>
    public static Dictionary<string, RSAParameters> ParseJwks(string json)
    {
        var keys = new Dictionary<string, RSAParameters>(StringComparer.Ordinal);
        using var doc = JsonDocument.Parse(json);

        foreach (var jwk in doc.RootElement.GetProperty("keys").EnumerateArray())
        {
            if (stringClaim(jwk, "kty") != "RSA" || stringClaim(jwk, "kid") is not { } kid
                || stringClaim(jwk, "n") is not { } n || stringClaim(jwk, "e") is not { } e)
                continue;

            keys[kid] = new RSAParameters { Modulus = Base64Url.DecodeFromChars(n), Exponent = Base64Url.DecodeFromChars(e) };
        }

        return keys;
    }

    private static Result fail(string reason) => new(null, reason);

    private static string? stringClaim(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long? numberClaim(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n) ? n : null;

    private static bool audienceMatches(JsonElement payload, string clientId)
    {
        if (!payload.TryGetProperty("aud", out var aud))
            return false;

        if (aud.ValueKind == JsonValueKind.String)
            return aud.GetString() == clientId;

        if (aud.ValueKind != JsonValueKind.Array)
            return false;

        bool listed = aud.EnumerateArray().Any(a => a.ValueKind == JsonValueKind.String && a.GetString() == clientId);
        return listed && stringClaim(payload, "azp") == clientId;
    }

    private static bool emailVerified(JsonElement payload)
    {
        if (!payload.TryGetProperty("email_verified", out var v))
            return false;

        return v.ValueKind == JsonValueKind.True
               || (v.ValueKind == JsonValueKind.String && string.Equals(v.GetString(), "true", StringComparison.OrdinalIgnoreCase));
    }

    private static bool fixedTimeEquals(string a, string b)
        => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
