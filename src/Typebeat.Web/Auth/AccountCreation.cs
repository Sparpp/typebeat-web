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
    public static async Task<AccountCreationResult> CreateAsync(
        Db db, PasswordService passwords, string username, string email, string password)
    {
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
            // verified_at stays NULL (email verification is a later milestone);
            // country_code defaults to 'XX' until GeoIP lands.
            id = await conn.ExecuteScalarAsync<long>(
                """
                INSERT INTO users (username, email, password_hash)
                VALUES (@username, @email, @hash)
                RETURNING id
                """,
                new { username, email, hash = passwords.Hash(password) });
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
}
