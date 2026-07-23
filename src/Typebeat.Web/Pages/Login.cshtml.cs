using Dapper;
using Microsoft.AspNetCore.Mvc;
using Typebeat.Web.Auth;
using Typebeat.Web.Data;
using Typebeat.Web.Email;

namespace Typebeat.Web.Pages;

/// <summary>
/// Website sign-in. Verifies against the same users.password_hash the OAuth password grant
/// uses (one identity for game and web). Per the M2 policy, a correct password does NOT sign the
/// user in: it issues a fresh emailed 'login' code and sets the challenge cookie, then hands off
/// to /verify (the session is minted only after the code checks out). Failure messaging is a
/// single generic line, like /oauth/token, the form never distinguishes unknown-user /
/// wrong-password / restricted, and a bad password sends NO code (no enumeration signal).
/// </summary>
public sealed class LoginModel(
    Db db, PasswordService passwords, EmailCodeService codes, IEmailSender email, ChallengeCookie challenge, ILogger<LoginModel> logger) : TypebeatPageModel
{
    // Same budget as the OAuth password grant: 10 attempts per IP per 5 minutes (speed bump;
    // Cloudflare is the real layer). Static so it survives across requests.
    private static readonly FixedWindowLimiter login_attempts = new(10, TimeSpan.FromMinutes(5));

    private const string bad_credentials = "The username or password is incorrect.";

    [BindProperty]
    public string Login { get; set; } = string.Empty;

    [BindProperty]
    public string Password { get; set; } = string.Empty;

    public string? Error { get; private set; }

    public IActionResult OnGet()
        => CurrentUser is not null ? Redirect("/") : Page();

    public async Task<IActionResult> OnPostAsync()
    {
        if (!login_attempts.Allow(HttpContext.GetClientIp()))
        {
            Error = "Too many login attempts. Please wait a few minutes and try again.";
            return Page();
        }

        string login = Login.Trim();

        if (login.Length == 0 || Password.Length == 0)
        {
            Error = bad_credentials;
            return Page();
        }

        UserRow? user;
        await using (var conn = await db.OpenAsync(HttpContext.RequestAborted))
        {
            // The login box accepts username or email, like the game client. The casts matter:
            // a bare text parameter against a citext column resolves the comparison as text=text
            // (case-SENSITIVE); casting the parameter keeps citext's case-insensitive operator.
            user = await conn.QuerySingleOrDefaultAsync<UserRow>(
                """
                SELECT id, email, password_hash AS passwordHash, restricted
                FROM users
                WHERE username = @login::citext OR email = @login::citext
                """,
                new { login });
        }

        if (user is null || !passwords.Verify(user.PasswordHash, Password) || user.Restricted)
        {
            // No code is issued on a failed credential; nothing distinguishes this from an
            // unknown user, so the form cannot be used to probe which accounts exist.
            Error = bad_credentials;
            return Page();
        }

        // Password OK, but not signed in yet: issue a fresh 'login' code, email it, and hand off
        // to /verify carrying the pending user in the challenge cookie. A throttled resend
        // (IssueStatus.TooSoon/TooMany) still routes to /verify; the user has a live code there.
        try
        {
            await EmailCodeFlow.IssueAndSendAsync(codes, email, user.Email, user.Id, "login", HttpContext.RequestAborted);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send login code to user {UserId}", user.Id);
            Error = "We couldn't send your verification email. Please try again.";
            return Page();
        }

        challenge.Issue(HttpContext, user.Id, "login");
        return Redirect("/verify");
    }

    private sealed record UserRow(long Id, string Email, string PasswordHash, bool Restricted);
}
