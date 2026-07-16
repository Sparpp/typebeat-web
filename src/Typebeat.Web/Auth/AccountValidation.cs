using System.Text.RegularExpressions;

namespace Typebeat.Web.Auth;

/// <summary>
/// Account field validation shared by the in-client registration endpoint (POST /users) and the
/// website /register form — one rule set, so an account is valid or invalid identically on both
/// paths. Pure and DB-free (uniqueness lives in <see cref="AccountCreation"/>); every method
/// returns human-readable errors, empty list = valid.
/// </summary>
public static class AccountValidation
{
    // Permissive superset of osu's username charset: letters, digits, space, and _ - [ ].
    // Deliberately does NOT enforce osu's finer rules (no mixed space/underscore, no
    // leading/trailing space) — kept permissive for M1; tighten later if abuse shows up.
    private static readonly Regex username_charset = new(@"^[A-Za-z0-9 _\-\[\]]+$", RegexOptions.Compiled);

    /// <summary>Username: 3–15 chars, restricted charset.</summary>
    public static IReadOnlyList<string> ValidateUsername(string username)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(username))
        {
            errors.Add("Username is required.");
            return errors;
        }

        if (username.Length < 3 || username.Length > 15)
            errors.Add("Username must be between 3 and 15 characters.");

        if (!username_charset.IsMatch(username))
            errors.Add("Username may only contain letters, digits, spaces, and the characters _ - [ ].");

        return errors;
    }

    /// <summary>Email: syntactically valid (single @, dotted domain, no spaces).</summary>
    public static IReadOnlyList<string> ValidateEmail(string email)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(email))
        {
            errors.Add("Email address is required.");
            return errors;
        }

        if (!LooksLikeEmail(email))
            errors.Add("Please enter a valid email address.");

        return errors;
    }

    /// <summary>Password: >= 8 chars and not equal (case-insensitively) to the username.</summary>
    public static IReadOnlyList<string> ValidatePassword(string password, string username)
    {
        var errors = new List<string>();

        if (string.IsNullOrEmpty(password))
        {
            errors.Add("Password is required.");
            return errors;
        }

        if (password.Length < 8)
            errors.Add("Password must be at least 8 characters.");

        if (!string.IsNullOrEmpty(username) && string.Equals(password, username, StringComparison.OrdinalIgnoreCase))
            errors.Add("Password must not match your username.");

        return errors;
    }

    private static bool LooksLikeEmail(string email)
    {
        if (email.Contains(' '))
            return false;

        int at = email.IndexOf('@');
        if (at <= 0 || at != email.LastIndexOf('@'))
            return false;

        string domain = email[(at + 1)..];
        if (domain.Length == 0 || domain.StartsWith('.') || domain.EndsWith('.') || !domain.Contains('.'))
            return false;

        try
        {
            // MailAddress is lenient; require it to round-trip to the exact input.
            var parsed = new System.Net.Mail.MailAddress(email);
            return parsed.Address == email;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
