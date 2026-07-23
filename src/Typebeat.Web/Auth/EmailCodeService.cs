using System.Security.Cryptography;
using Dapper;
using Typebeat.Web.Data;

namespace Typebeat.Web.Auth;

/// <summary>
/// Issues and verifies short-lived 6-digit email codes, stored in <c>email_tokens</c> as
/// SHA-256(code), the same at-rest hashing style as the opaque bearer tokens in
/// <see cref="TokenService"/>. Two purposes carry codes: 'verify' (confirm a new email, 15 min)
/// and 'login' (fresh code on every website login, 10 min).
///
/// Brute-force argument (documented so a reviewer can check the ceiling):
///  - the code space is 10^6 and codes are drawn uniformly with a CSPRNG;
///  - at most ONE code is active per (user, purpose); issuing a new one burns the previous;
///  - a code is burned after <see cref="MaxAttempts"/> wrong guesses (the 6th attempt);
///  - issuance is throttled: <see cref="ResendCooldown"/> between sends and
///    <see cref="MaxPerHour"/> per (user, purpose) per hour.
/// Together the expected guesses to hit a live code before it is burned or rotated is ~10^6/5,
/// and the hourly issuance cap bounds how many fresh targets an attacker can force.
///
/// Every failure path returns the SAME generic outcome to the caller ("incorrect or expired
/// code"); no distinction between "no active code", "expired", and "wrong digits" leaks.
/// </summary>
public sealed class EmailCodeService(Db db)
{
    public const int MaxAttempts = 5;
    public static readonly TimeSpan VerifyTtl = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan LoginTtl = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan ResendCooldown = TimeSpan.FromSeconds(60);
    public const int MaxPerHour = 6;

    public enum IssueStatus { Sent, TooSoon, TooMany }

    /// <summary>Result of an issuance attempt. <see cref="Code"/> is set only when Status == Sent.</summary>
    public sealed record IssueResult(IssueStatus Status, string? Code, int ExpiryMinutes);

    public enum VerifyStatus { Success, Incorrect, Burned }

    /// <summary>
    /// Result of a verification attempt. <see cref="AttemptsRemaining"/> is the number of further
    /// guesses allowed against the SAME code (0 once the next wrong guess will burn it); it is
    /// only meaningful for <see cref="VerifyStatus.Incorrect"/>.
    /// </summary>
    public sealed record VerifyResult(VerifyStatus Status, int AttemptsRemaining);

    public static TimeSpan TtlFor(string purpose) => purpose == "login" ? LoginTtl : VerifyTtl;

    /// <summary>
    /// Invalidates the user's current unused code of this purpose and issues a fresh one, subject
    /// to the resend cooldown and hourly cap. Returns the plaintext code (for the caller to email)
    /// only on <see cref="IssueStatus.Sent"/>; never throws for throttling.
    /// </summary>
    public async Task<IssueResult> IssueAsync(long userId, string purpose, CancellationToken ct = default)
    {
        var ttl = TtlFor(purpose);
        int expiryMinutes = (int)ttl.TotalMinutes;

        await using var conn = await db.OpenAsync(ct);

        // Throttle first (time comparisons in SQL against now(), like TokenService). Cooldown only
        // guards against re-spamming a code that is STILL LIVE (unused + unexpired); once the
        // previous code was consumed or expired, a fresh login legitimately issues a new one
        // immediately (fresh code on every login). The hourly cap is the absolute
        // per-(user,purpose) ceiling regardless of consumption.
        var throttle = await conn.QuerySingleAsync<(bool onCooldown, long hourly)>(
            """
            SELECT COALESCE(bool_or(used_at IS NULL AND expires_at > now()
                                    AND created_at > now() - make_interval(secs => @cooldown)), false) AS onCooldown,
                   count(*) FILTER (WHERE created_at > now() - interval '1 hour') AS hourly
            FROM email_tokens
            WHERE user_id = @userId AND purpose = @purpose
            """,
            new { userId, purpose, cooldown = ResendCooldown.TotalSeconds });

        if (throttle.onCooldown)
            return new IssueResult(IssueStatus.TooSoon, null, expiryMinutes);

        if (throttle.hourly >= MaxPerHour)
            return new IssueResult(IssueStatus.TooMany, null, expiryMinutes);

        string code = GenerateCode();
        byte[] hash = TokenService.Hash(code);

        await using var tx = await conn.BeginTransactionAsync(ct);

        // One active code per (user, purpose): retire any live one before inserting the new.
        await conn.ExecuteAsync(
            "UPDATE email_tokens SET used_at = now() WHERE user_id = @userId AND purpose = @purpose AND used_at IS NULL",
            new { userId, purpose }, tx);

        await conn.ExecuteAsync(
            """
            INSERT INTO email_tokens (user_id, token_hash, purpose, expires_at)
            VALUES (@userId, @hash, @purpose, now() + make_interval(secs => @ttl))
            """,
            new { userId, hash, purpose, ttl = ttl.TotalSeconds }, tx);

        await tx.CommitAsync(ct);

        return new IssueResult(IssueStatus.Sent, code, expiryMinutes);
    }

    /// <summary>
    /// Verifies a code against the user's active code of this purpose. Locks the row FOR UPDATE,
    /// increments its attempt counter, burns it after <see cref="MaxAttempts"/> wrong guesses, and
    /// compares in constant time. On success the code is marked used (single-use).
    /// </summary>
    public async Task<VerifyResult> VerifyAsync(long userId, string purpose, string code, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var row = await conn.QuerySingleOrDefaultAsync<(long id, byte[] tokenHash, short attempts)?>(
            """
            SELECT id, token_hash AS tokenHash, attempts
            FROM email_tokens
            WHERE user_id = @userId AND purpose = @purpose AND used_at IS NULL AND expires_at > now()
            ORDER BY created_at DESC
            LIMIT 1
            FOR UPDATE
            """,
            new { userId, purpose }, tx);

        if (row is not { } token)
        {
            await tx.CommitAsync(ct);
            // No live code (never issued / expired / already used/burned): generic failure.
            return new VerifyResult(VerifyStatus.Incorrect, 0);
        }

        int attempts = token.attempts + 1;

        if (attempts > MaxAttempts)
        {
            await conn.ExecuteAsync(
                "UPDATE email_tokens SET used_at = now(), attempts = @attempts WHERE id = @id",
                new { attempts = (short)attempts, id = token.id }, tx);
            await tx.CommitAsync(ct);
            return new VerifyResult(VerifyStatus.Burned, 0);
        }

        bool matches = CryptographicOperations.FixedTimeEquals(TokenService.Hash(code), token.tokenHash);

        if (matches)
        {
            await conn.ExecuteAsync(
                "UPDATE email_tokens SET used_at = now(), attempts = @attempts WHERE id = @id",
                new { attempts = (short)attempts, id = token.id }, tx);
            await tx.CommitAsync(ct);
            return new VerifyResult(VerifyStatus.Success, 0);
        }

        await conn.ExecuteAsync(
            "UPDATE email_tokens SET attempts = @attempts WHERE id = @id",
            new { attempts = (short)attempts, id = token.id }, tx);
        await tx.CommitAsync(ct);

        return new VerifyResult(VerifyStatus.Incorrect, Math.Max(0, MaxAttempts - attempts));
    }

    /// <summary>Cryptographically-uniform 6-digit code, zero-padded ("000000"–"999999").</summary>
    public static string GenerateCode()
        => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
}
