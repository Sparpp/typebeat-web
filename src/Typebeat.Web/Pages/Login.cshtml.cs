using Dapper;
using Microsoft.AspNetCore.Mvc;
using Typebeat.Web.Auth;
using Typebeat.Web.Data;

namespace Typebeat.Web.Pages;

/// <summary>
/// Website sign-in. Verifies against the same users.password_hash the OAuth password grant
/// uses (one identity for game and web), then issues a token via TokenService and delivers it
/// as the typebeat_session cookie. Failure messaging is a single generic line — like
/// /oauth/token, the form never distinguishes unknown-user / wrong-password / restricted.
/// </summary>
public sealed class LoginModel(Db db, PasswordService passwords, TokenService tokens) : TypebeatPageModel
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
                SELECT id, password_hash AS passwordHash, restricted
                FROM users
                WHERE username = @login::citext OR email = @login::citext
                """,
                new { login });
        }

        if (user is null || !passwords.Verify(user.PasswordHash, Password) || user.Restricted)
        {
            Error = bad_credentials;
            return Page();
        }

        SessionCookieAuth.SignIn(HttpContext, await tokens.IssueAsync(user.Id));
        return Redirect("/");
    }

    private sealed record UserRow(long Id, string PasswordHash, bool Restricted);
}
