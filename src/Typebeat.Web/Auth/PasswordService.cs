using Microsoft.AspNetCore.Identity;

namespace Typebeat.Web.Auth;

/// <summary>
/// Thin wrapper over ASP.NET Identity's versioned PBKDF2 hasher (V3: PBKDF2-HMAC-SHA512,
/// 100k iterations, format-versioned so parameters can be upgraded without breaking stored
/// hashes). Deliberately not hand-rolled.
/// </summary>
public sealed class PasswordService
{
    private readonly PasswordHasher<object> hasher = new PasswordHasher<object>();
    private static readonly object dummy_user = new object();

    public string Hash(string password) => hasher.HashPassword(dummy_user, password);

    public bool Verify(string hash, string password)
    {
        var result = hasher.VerifyHashedPassword(dummy_user, hash, password);
        return result is PasswordVerificationResult.Success or PasswordVerificationResult.SuccessRehashNeeded;
    }
}
