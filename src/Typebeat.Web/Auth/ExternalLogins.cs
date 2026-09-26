using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using Npgsql;
using Typebeat.Web.Data;

namespace Typebeat.Web.Auth;

/// <summary>
/// The account rules behind "Continue with Google" (user_external_logins, 035_google_sign_in.sql),
/// kept apart from the HTTP flow so every branch is testable against the database alone.
///
/// SIGN-IN (<see cref="ResolveSignInAsync"/>) decides, for a validated Google identity, in order:
///  1. LINKED: a user is linked to that Google subject: sign them in.
///  2. EMAIL MATCH: a user holds that (Google-verified) email and has no Google link: link it and
///     sign in. Refused when that account is already linked to a DIFFERENT Google account, and when
///     the account's own email was never confirmed (see below).
///  3. NEW: nobody has the subject or the email: the caller sends them to choose a username, and
///     <see cref="AccountCreation.CreateExternalAsync"/> makes the account.
/// Restricted and deleted accounts never sign in, matching every other auth path.
///
/// WHY AN UNCONFIRMED EMAIL IS NOT AUTO-LINKED. An account created in the game (POST /users) has no
/// email step, so anyone can register one under an address they do not own and set its password.
/// Auto-linking the real owner's Google sign-in to it would hand that person an account whose
/// password somebody else still holds, and the game's password grant would keep letting them in
/// (the classic account pre-hijack). So a match on an unconfirmed account is refused with a way
/// forward: sign in with the password once (the emailed login code confirms the address), or reset
/// the password by email, then link Google from Settings. Confirmed accounts link straight away.
///
/// LINK and UNLINK are the Settings page's rules: linking refuses a Google account already linked
/// to someone else and an account that already has one; unlinking refuses an account with no
/// password, which would be left with no way to sign in at all.
/// </summary>
public static class ExternalLogins
{
    public const string Google = "google";

    // Named in 035 so a racing INSERT can be told apart by which one it lost on.
    public const string SubjectConstraint = "uq_user_external_logins_subject";
    private const string user_constraint = "uq_user_external_logins_user";

    public enum SignInKind { SignedIn, NeedsAccount, Refused }

    public sealed record SignInOutcome(SignInKind Kind, long? UserId = null, string? Error = null)
    {
        public static SignInOutcome In(long userId) => new(SignInKind.SignedIn, userId);
        public static SignInOutcome New() => new(SignInKind.NeedsAccount);
        public static SignInOutcome No(string error) => new(SignInKind.Refused, null, error);
    }

    public const string GenericRefusal = "We couldn't sign you in with that Google account.";

    public const string OtherGoogleRefusal =
        "The type!beat account with this email is linked to a different Google account. Sign in with that Google account, or with your password.";

    public const string UnconfirmedRefusal =
        "A type!beat account already uses this email, but the address was never confirmed. Sign in with its password first (that confirms the email), or reset the password by email, then link Google from Settings.";

    public static async Task<SignInOutcome> ResolveSignInAsync(Db db, GoogleIdentity identity, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);

        // 1) Linked: the subject, never the email, identifies a returning Google user.
        var linked = await conn.QuerySingleOrDefaultAsync<(long Id, bool Restricted, bool Deleted)?>(
            """
            SELECT u.id AS Id, u.restricted AS Restricted, u.deleted_at IS NOT NULL AS Deleted
            FROM user_external_logins l
            JOIN users u ON u.id = l.user_id
            WHERE l.provider = @provider AND l.subject = @subject
            """,
            new { provider = Google, subject = identity.Subject });

        if (linked is { } link)
        {
            if (link.Restricted || link.Deleted)
                return SignInOutcome.No(GenericRefusal);

            // Keep the displayed address current (a Google account's email can change).
            await conn.ExecuteAsync(
                "UPDATE user_external_logins SET email = @email WHERE provider = @provider AND subject = @subject",
                new { email = identity.Email, provider = Google, subject = identity.Subject });

            return SignInOutcome.In(link.Id);
        }

        // 2) Email match. ::citext keeps the comparison case-insensitive (see LoginModel).
        var match = await conn.QuerySingleOrDefaultAsync<(long Id, bool Restricted, bool Verified, bool HasGoogle)?>(
            """
            SELECT u.id AS Id, u.restricted AS Restricted, u.verified_at IS NOT NULL AS Verified,
                   EXISTS (SELECT 1 FROM user_external_logins l WHERE l.user_id = u.id AND l.provider = @provider) AS HasGoogle
            FROM users u
            WHERE u.email = @email::citext AND u.deleted_at IS NULL
            """,
            new { email = identity.Email, provider = Google });

        if (match is not { } user)
            return SignInOutcome.New(); // 3) nobody: choose a username.

        if (user.Restricted)
            return SignInOutcome.No(GenericRefusal);

        if (user.HasGoogle)
            return SignInOutcome.No(OtherGoogleRefusal);

        if (!user.Verified)
            return SignInOutcome.No(UnconfirmedRefusal);

        return await insertLinkAsync(conn, user.Id, identity, ct) switch
        {
            LinkResult.Linked => SignInOutcome.In(user.Id),
            LinkResult.UserAlreadyLinked => SignInOutcome.No(OtherGoogleRefusal),
            // Lost a race to another account linking this subject between our two reads.
            _ => SignInOutcome.No(GenericRefusal),
        };
    }

    public enum LinkResult { Linked, SubjectTaken, UserAlreadyLinked }

    /// <summary>
    /// Settings' "Link Google account": attaches <paramref name="identity"/> to <paramref name="userId"/>.
    /// Linking the subject that is already this user's is a harmless success.
    /// </summary>
    public static async Task<LinkResult> LinkAsync(Db db, long userId, GoogleIdentity identity, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);

        long? owner = await conn.ExecuteScalarAsync<long?>(
            "SELECT user_id FROM user_external_logins WHERE provider = @provider AND subject = @subject",
            new { provider = Google, subject = identity.Subject });

        if (owner == userId)
            return LinkResult.Linked;

        if (owner is not null)
            return LinkResult.SubjectTaken;

        return await insertLinkAsync(conn, userId, identity, ct);
    }

    public enum UnlinkResult { Unlinked, NotLinked, NoPassword }

    /// <summary>
    /// Settings' "Unlink": refused for an account with no password, in the same statement that
    /// deletes, so a password cannot be cleared between the check and the delete.
    /// </summary>
    public static async Task<UnlinkResult> UnlinkAsync(Db db, long userId, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);

        int deleted = await conn.ExecuteAsync(
            """
            DELETE FROM user_external_logins l
            USING users u
            WHERE l.user_id = @userId AND l.provider = @provider
              AND u.id = l.user_id AND COALESCE(u.password_hash, '') <> ''
            """,
            new { userId, provider = Google });

        if (deleted > 0)
            return UnlinkResult.Unlinked;

        bool linked = await conn.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM user_external_logins WHERE user_id = @userId AND provider = @provider)",
            new { userId, provider = Google });

        return linked ? UnlinkResult.NoPassword : UnlinkResult.NotLinked;
    }

    /// <summary>The Google email linked to <paramref name="userId"/>, or null when unlinked.</summary>
    public static async Task<(bool Linked, string? Email)> GetGoogleLinkAsync(NpgsqlConnection conn, long userId)
    {
        var row = await conn.QuerySingleOrDefaultAsync<(long UserId, string? Email)?>(
            "SELECT user_id AS UserId, email AS Email FROM user_external_logins WHERE user_id = @userId AND provider = @provider",
            new { userId, provider = Google });

        return row is { } r ? (true, r.Email) : (false, null);
    }

    private static async Task<LinkResult> insertLinkAsync(NpgsqlConnection conn, long userId, GoogleIdentity identity, CancellationToken ct)
    {
        try
        {
            await conn.ExecuteAsync(
                new CommandDefinition(
                    "INSERT INTO user_external_logins (user_id, provider, subject, email) VALUES (@userId, @provider, @subject, @email)",
                    new { userId, provider = Google, subject = identity.Subject, email = identity.Email },
                    cancellationToken: ct));
            return LinkResult.Linked;
        }
        catch (PostgresException pg) when (pg.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return pg.ConstraintName == user_constraint ? LinkResult.UserAlreadyLinked : LinkResult.SubjectTaken;
        }
    }

    private static readonly Regex disallowed = new(@"[^A-Za-z0-9 _\-\[\]]", RegexOptions.Compiled);
    private static readonly Regex spaces = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// A starting point for the "choose a username" box: the Google display name reduced to the
    /// charset <see cref="AccountValidation.ValidateUsername"/> accepts (accents folded to their base
    /// letter, everything else dropped) and cut to 15, falling back to the email's local part. Only a
    /// suggestion: the user edits it and the normal rules (and uniqueness) still decide. Empty when
    /// nothing usable survives.
    /// </summary>
    public static string SuggestUsername(string? name, string email)
    {
        string fromName = clean(name);
        if (fromName.Length >= 3)
            return fromName;

        int at = email.IndexOf('@');
        string fromEmail = clean(at > 0 ? email[..at] : email);
        return fromEmail.Length >= 3 ? fromEmail : string.Empty;
    }

    private static string clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        // Fold accents (é to e) before dropping what is left outside the charset.
        var folded = new StringBuilder();
        foreach (char c in raw.Normalize(NormalizationForm.FormD))
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
                folded.Append(c);
        }

        string s = spaces.Replace(disallowed.Replace(folded.ToString(), string.Empty), " ").Trim();
        if (s.Length > 15)
            s = s[..15].TrimEnd();

        return s;
    }
}
