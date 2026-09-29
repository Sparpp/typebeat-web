using Dapper;
using Npgsql;
using Typebeat.Web.Data;

namespace Typebeat.Web.Auth;

/// <summary>
/// Outcome of an account creation attempt: either <see cref="UserId"/> is set, or at least one
/// per-field error list is non-empty. Field granularity matches the client's registration form
/// (username / email / password) so both the wire envelope and the website form can map errors
/// straight onto their fields.
/// </summary>
public sealed record AccountCreationResult(
    long? UserId,
    IReadOnlyList<string> UsernameErrors,
    IReadOnlyList<string> EmailErrors,
    IReadOnlyList<string> PasswordErrors)
{
    public bool Succeeded => UserId is not null;
}

/// <summary>
/// The one place an account comes into existence, shared by POST /users (in-client registration)
/// and the website /register form. Validates via <see cref="AccountValidation"/>, checks
/// uniqueness, inserts users + user_stats, and maps the insert race back to field errors.
/// </summary>
public static class AccountCreation
{
    /// <param name="countryCode">The account's country, resolved from the sign-up request by
    /// <see cref="CountryResolver"/> (<see cref="Countries.Unknown"/> when there is none). Stored as
    /// given; anything that is not a storable country is stored as unknown.</param>
    public static async Task<AccountCreationResult> CreateAsync(
        Db db, PasswordService passwords, string username, string email, string password, string countryCode = Countries.Unknown)
    {
        string country = Countries.IsCountry(countryCode) ? countryCode : Countries.Unknown;

        var usernameErrors = new List<string>(AccountValidation.ValidateUsername(username));
        var emailErrors = new List<string>(AccountValidation.ValidateEmail(email));
        var passwordErrors = new List<string>(AccountValidation.ValidatePassword(password, username));

        await using var conn = await db.OpenAsync();

        // Uniqueness is checked per-field only when the field is otherwise well-formed, so
        // "is already taken" never stacks on top of a "malformed" error. citext columns make
        // both comparisons case-insensitive.
        if (usernameErrors.Count == 0 &&
            await conn.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM users WHERE username = @username)", new { username }))
            usernameErrors.Add("Username is already taken.");

        if (emailErrors.Count == 0 &&
            await conn.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM users WHERE email = @email)", new { email }))
            emailErrors.Add("Email address is already in use.");

        if (usernameErrors.Count > 0 || emailErrors.Count > 0 || passwordErrors.Count > 0)
            return new AccountCreationResult(null, usernameErrors, emailErrors, passwordErrors);

        long id;
        try
        {
            // verified_at stays NULL until the emailed code is entered. country_code is detected,
            // not chosen, so country_chosen keeps its default false (038_country_chosen.sql).
            id = await conn.ExecuteScalarAsync<long>(
                """
                INSERT INTO users (username, email, password_hash, country_code)
                VALUES (@username, @email, @hash, @country)
                RETURNING id
                """,
                new { username, email, hash = passwords.Hash(password), country });
        }
        catch (PostgresException pg) when (pg.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // Lost a race between the EXISTS check and the INSERT; map the violated
            // constraint back to its field.
            if (pg.ConstraintName is not null && pg.ConstraintName.Contains("email"))
                emailErrors.Add("Email address is already in use.");
            else
                usernameErrors.Add("Username is already taken.");

            return new AccountCreationResult(null, usernameErrors, emailErrors, passwordErrors);
        }

        await conn.ExecuteAsync("INSERT INTO user_stats (user_id) VALUES (@id)", new { id });

        return new AccountCreationResult(id, [], [], []);
    }

    /// <summary>
    /// The account a first-time "Continue with Google" creates, after the user picks a username.
    /// Same username and email rules and the same uniqueness checks as <see cref="CreateAsync"/>,
    /// but with NO password (password_hash NULL, 035_google_sign_in.sql; the user can set one in
    /// Settings to play in the game client) and with the email already verified, because Google
    /// asserted email_verified for it. The user, their stats row and the Google link are written in
    /// one transaction, so there is never an account that exists but cannot be signed in to.
    /// </summary>
    public static async Task<AccountCreationResult> CreateExternalAsync(Db db, string username, GoogleIdentity identity,
        string countryCode = Countries.Unknown, CancellationToken ct = default)
    {
        string country = Countries.IsCountry(countryCode) ? countryCode : Countries.Unknown;

        string email = identity.Email;
        var usernameErrors = new List<string>(AccountValidation.ValidateUsername(username));
        var emailErrors = new List<string>(AccountValidation.ValidateEmail(email));

        await using var conn = await db.OpenAsync(ct);

        if (usernameErrors.Count == 0 &&
            await conn.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM users WHERE username = @username)", new { username }))
            usernameErrors.Add("Username is already taken.");

        // The sign-in step only sends someone here when no account has this email, so a hit means
        // one was created in between. Signing in with Google again takes the email-match branch.
        if (emailErrors.Count == 0 &&
            await conn.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM users WHERE email = @email)", new { email }))
            emailErrors.Add(email_now_taken);

        if (usernameErrors.Count > 0 || emailErrors.Count > 0)
            return new AccountCreationResult(null, usernameErrors, emailErrors, []);

        await using var tx = await conn.BeginTransactionAsync(ct);
        long id;

        try
        {
            id = await conn.ExecuteScalarAsync<long>(
                """
                INSERT INTO users (username, email, password_hash, verified_at, country_code)
                VALUES (@username, @email, NULL, now(), @country)
                RETURNING id
                """,
                new { username, email, country }, tx);

            await conn.ExecuteAsync("INSERT INTO user_stats (user_id) VALUES (@id)", new { id }, tx);

            await conn.ExecuteAsync(
                "INSERT INTO user_external_logins (user_id, provider, subject, email) VALUES (@id, @provider, @subject, @email)",
                new { id, provider = ExternalLogins.Google, subject = identity.Subject, email }, tx);

            await tx.CommitAsync(ct);
        }
        catch (PostgresException pg) when (pg.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await tx.RollbackAsync(ct);

            if (pg.ConstraintName == ExternalLogins.SubjectConstraint)
                emailErrors.Add("This Google account was just linked to another type!beat account. Sign in with Google again.");
            else if (pg.ConstraintName is not null && pg.ConstraintName.Contains("email"))
                emailErrors.Add(email_now_taken);
            else
                usernameErrors.Add("Username is already taken.");

            return new AccountCreationResult(null, usernameErrors, emailErrors, []);
        }

        return new AccountCreationResult(id, [], [], []);
    }

    private const string email_now_taken = "An account with this email already exists. Sign in with Google again to use it.";
}
