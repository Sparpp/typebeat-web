using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Typebeat.Web.Auth;

namespace Typebeat.Web.Tests;

/// <summary>
/// The hand-rolled Google ID-token validator, against a locally generated RSA key served through a
/// stubbed JWKS document (parsed by the same <see cref="GoogleIdTokenValidator.ParseJwks"/> the live
/// cache uses). No network and no database: every refusal branch is one mutated claim away from the
/// valid baseline, so each test proves exactly one check.
/// </summary>
public class GoogleIdTokenValidatorTest
{
    private const string client_id = "1234-test.apps.googleusercontent.com";
    private const string nonce = "n0nce-from-the-flow-cookie";
    private const string kid = "test-key-1";

    private static readonly DateTimeOffset now = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    private RSA key = null!;
    private IReadOnlyDictionary<string, RSAParameters> jwks = null!;

    [OneTimeSetUp]
    public void GenerateKey()
    {
        key = RSA.Create(2048);
        jwks = GoogleIdTokenValidator.ParseJwks(jwksJson(key, kid));
    }

    [OneTimeTearDown]
    public void DisposeKey() => key.Dispose();

    [Test]
    public void ValidToken_YieldsTheIdentity()
    {
        var result = validate(sign(claims()));

        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.True, result.Failure);
            Assert.That(result.Identity!.Subject, Is.EqualTo("109876543210"));
            Assert.That(result.Identity.Email, Is.EqualTo("player@gmail.com"));
            Assert.That(result.Identity.Name, Is.EqualTo("Ada Lovelace"));
        });
    }

    [Test]
    public void BothDocumentedIssuerSpellings_AreAccepted()
    {
        Assert.That(validate(sign(claims(c => c["iss"] = "accounts.google.com"))).Succeeded, Is.True);
    }

    [Test]
    public void AudienceArray_NeedsOurClientIdListed_AndAsAzp()
    {
        Assert.Multiple(() =>
        {
            Assert.That(validate(sign(claims(c =>
            {
                c["aud"] = new[] { "someone-else", client_id };
                c["azp"] = client_id;
            }))).Succeeded, Is.True, "listed and authorized party");

            Assert.That(validate(sign(claims(c =>
            {
                c["aud"] = new[] { "someone-else", client_id };
                c["azp"] = "someone-else";
            }))).Failure, Is.EqualTo("wrong audience"), "listed but issued to another party");
        });
    }

    [Test]
    public void WrongAudience_IsRefused()
        => assertRefused(claims(c => c["aud"] = "another-client.apps.googleusercontent.com"), "wrong audience");

    [Test]
    public void WrongIssuer_IsRefused()
        => assertRefused(claims(c => c["iss"] = "https://evil.example.com"), "wrong issuer");

    [Test]
    public void ExpiredToken_IsRefused()
        => assertRefused(claims(c => c["exp"] = now.AddMinutes(-10).ToUnixTimeSeconds()), "expired");

    [Test]
    public void TokenFromTheFuture_IsRefused()
        => assertRefused(claims(c => c["iat"] = now.AddMinutes(30).ToUnixTimeSeconds()), "issued in the future");

    [Test]
    public void WrongNonce_IsRefused()
        => assertRefused(claims(c => c["nonce"] = "a-different-flow"), "nonce mismatch");

    [Test]
    public void MissingNonce_IsRefused()
        => assertRefused(claims(c => c.Remove("nonce")), "nonce mismatch");

    [Test]
    public void UnverifiedEmail_IsRefused()
        => assertRefused(claims(c => c["email_verified"] = false), "email not verified");

    [Test]
    public void MissingEmailVerified_IsRefused()
        => assertRefused(claims(c => c.Remove("email_verified")), "email not verified");

    [Test]
    public void MissingSubject_IsRefused()
        => assertRefused(claims(c => c.Remove("sub")), "no subject");

    [Test]
    public void SignatureFromAnotherKey_IsRefused()
    {
        // Same kid, different private key: exactly what a forger without Google's key produces.
        using var forger = RSA.Create(2048);
        var result = validate(sign(claims(), forger));

        Assert.That(result.Failure, Is.EqualTo("bad signature"));
    }

    [Test]
    public void PayloadEditedAfterSigning_IsRefused()
    {
        string[] parts = sign(claims()).Split('.');
        string forged = encodeJson(claims(c => c["email"] = "victim@gmail.com"));

        Assert.That(validate($"{parts[0]}.{forged}.{parts[2]}").Failure, Is.EqualTo("bad signature"));
    }

    [Test]
    public void UnknownKid_IsRefused()
        => Assert.That(validate(sign(claims(), header: h => h["kid"] = "rotated-away")).Failure, Is.EqualTo("unknown signing key"));

    [Test]
    public void AlgNone_IsRefusedBeforeAnyKeyIsConsulted()
    {
        string unsigned = $"{encodeJson(new Dictionary<string, object> { ["alg"] = "none", ["kid"] = kid })}.{encodeJson(claims())}.AA";

        Assert.That(validate(unsigned).Failure, Is.EqualTo("unexpected alg"));
    }

    [Test]
    public void Garbage_IsRefusedNotThrown()
    {
        Assert.Multiple(() =>
        {
            Assert.That(validate("not-a-jwt").Succeeded, Is.False);
            Assert.That(validate("a.b.c").Succeeded, Is.False);
            Assert.That(validate("..").Succeeded, Is.False);
        });
    }

    [Test]
    public void PkceChallenge_MatchesTheRfc7636Example()
    {
        // RFC 7636 appendix B.
        Assert.That(GoogleOidc.PkceChallenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"),
            Is.EqualTo("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM"));
    }

    [TestCase("Ada Lovelace", "ada@gmail.com", "Ada Lovelace")]
    [TestCase("Zoë Ångström", "z@gmail.com", "Zoe Angstrom")]
    [TestCase("A very long display name indeed", "x@gmail.com", "A very long dis")]
    [TestCase("李", "speedy.typer@gmail.com", "speedytyper")]
    [TestCase(null, "ab@gmail.com", "")]
    public void SuggestUsername_FoldsToTheUsernameCharset(string? name, string email, string expected)
    {
        string suggestion = ExternalLogins.SuggestUsername(name, email);

        Assert.Multiple(() =>
        {
            Assert.That(suggestion, Is.EqualTo(expected));
            if (suggestion.Length > 0)
                Assert.That(AccountValidation.ValidateUsername(suggestion), Is.Empty, "a suggestion is always a valid username");
        });
    }

    // ---- helpers ----

    private GoogleIdTokenValidator.Result validate(string token)
        => GoogleIdTokenValidator.Validate(token, jwks, client_id, nonce, now);

    private void assertRefused(Dictionary<string, object> payload, string reason)
    {
        var result = validate(sign(payload));
        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Failure, Is.EqualTo(reason));
        });
    }

    private static Dictionary<string, object> claims(Action<Dictionary<string, object>>? edit = null)
    {
        var c = new Dictionary<string, object>
        {
            ["iss"] = "https://accounts.google.com",
            ["azp"] = client_id,
            ["aud"] = client_id,
            ["sub"] = "109876543210",
            ["email"] = "player@gmail.com",
            ["email_verified"] = true,
            ["name"] = "Ada Lovelace",
            ["nonce"] = nonce,
            ["iat"] = now.AddMinutes(-1).ToUnixTimeSeconds(),
            ["exp"] = now.AddMinutes(59).ToUnixTimeSeconds(),
        };
        edit?.Invoke(c);
        return c;
    }

    private string sign(Dictionary<string, object> payload, RSA? with = null, Action<Dictionary<string, object>>? header = null)
    {
        var h = new Dictionary<string, object> { ["alg"] = "RS256", ["kid"] = kid, ["typ"] = "JWT" };
        header?.Invoke(h);

        string signingInput = $"{encodeJson(h)}.{encodeJson(payload)}";
        byte[] signature = (with ?? key).SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{signingInput}.{Base64Url.EncodeToString(signature)}";
    }

    private static string encodeJson(object value) => Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(value));

    // The shape Google serves at https://www.googleapis.com/oauth2/v3/certs.
    private static string jwksJson(RSA rsa, string keyId)
    {
        var p = rsa.ExportParameters(false);
        return JsonSerializer.Serialize(new
        {
            keys = new object[]
            {
                new { kty = "EC", kid = "ignored-ec-key", crv = "P-256" },
                new { kty = "RSA", kid = keyId, use = "sig", alg = "RS256", n = Base64Url.EncodeToString(p.Modulus), e = Base64Url.EncodeToString(p.Exponent) },
            },
        });
    }
}
