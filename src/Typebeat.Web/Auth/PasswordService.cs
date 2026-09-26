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

    /// <summary>
    /// True only when <paramref name="hash"/> is a real stored hash and <paramref name="password"/>
    /// matches it. A null or empty hash is an account with NO password (created through Google, or
    /// scrubbed by account deletion; see 035_google_sign_in.sql) and never authenticates, whatever
    /// is typed: it is refused here, before the hasher is consulted, so no caller can forget to.
    /// </summary>
    public bool Verify(string? hash, string password)
    {
        if (string.IsNullOrEmpty(hash) || string.IsNullOrEmpty(password))
            return false;

        var result = hasher.VerifyHashedPassword(dummy_user, hash, password);
        return result is PasswordVerificationResult.Success or PasswordVerificationResult.SuccessRehashNeeded;
    }

    /// <summary>Whether a stored hash is a usable password at all (see <see cref="Verify"/>).</summary>
    public static bool HasPassword(string? hash) => !string.IsNullOrEmpty(hash);
}
