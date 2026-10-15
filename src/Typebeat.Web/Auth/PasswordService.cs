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

    // A hash nobody's password matches, verified in place of a missing one so that an unknown login
    // or a passwordless account takes as long to refuse as a wrong password does.
    private static readonly string no_password_hash = new PasswordHasher<object>().HashPassword(dummy_user, Guid.NewGuid().ToString("N"));

    public string Hash(string password) => hasher.HashPassword(dummy_user, password);

    /// <summary>
    /// True only when <paramref name="hash"/> is a real stored hash and <paramref name="password"/>
    /// matches it. A null or empty hash is an account with NO password (created through Google, or
    /// scrubbed by account deletion; see 035_google_sign_in.sql) and never authenticates, whatever
    /// is typed: it is refused here, so no caller can forget to. Callers pass a null hash for an
    /// unknown login too. Either way the password is still run through the hasher (against a hash
    /// it cannot match), so the refusal costs what a wrong password costs and response time does
    /// not tell which accounts exist.
    /// </summary>
    public bool Verify(string? hash, string password)
    {
        if (string.IsNullOrEmpty(password))
            return false;

        if (string.IsNullOrEmpty(hash))
        {
            hasher.VerifyHashedPassword(dummy_user, no_password_hash, password);
            return false;
        }

        var result = hasher.VerifyHashedPassword(dummy_user, hash, password);
        return result is PasswordVerificationResult.Success or PasswordVerificationResult.SuccessRehashNeeded;
    }

    /// <summary>Whether a stored hash is a usable password at all (see <see cref="Verify"/>).</summary>
    public static bool HasPassword(string? hash) => !string.IsNullOrEmpty(hash);
}
