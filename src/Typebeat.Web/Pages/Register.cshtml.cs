using Microsoft.AspNetCore.Mvc;
using Typebeat.Web.Auth;
using Typebeat.Web.Data;
using Typebeat.Web.Email;

namespace Typebeat.Web.Pages;

/// <summary>
/// Website registration. Same rules and creation path as POST /users (AccountCreation /
/// AccountValidation are shared). The account is created with verified_at NULL; instead of the
/// old auto-login, we issue a 'verify' code, email it, set the challenge cookie, and redirect to
/// /verify. The session is minted there once the new user confirms their email.
/// </summary>
public sealed class RegisterModel(
    Db db, PasswordService passwords, EmailCodeService codes, IEmailSender email, ChallengeCookie challenge, ILogger<RegisterModel> logger) : TypebeatPageModel
{
    // Same budget as the in-client registration endpoint: 3 per IP per hour.
    private static readonly FixedWindowLimiter register_attempts = new(3, TimeSpan.FromHours(1));

    [BindProperty]
    public string Username { get; set; } = string.Empty;

    [BindProperty]
    public string Email { get; set; } = string.Empty;

    [BindProperty]
    public string Password { get; set; } = string.Empty;

    public string? Error { get; private set; }

    public IReadOnlyList<string> UsernameErrors { get; private set; } = [];
    public IReadOnlyList<string> EmailErrors { get; private set; } = [];
    public IReadOnlyList<string> PasswordErrors { get; private set; } = [];

    public IActionResult OnGet()
        => CurrentUser is not null ? Redirect("/") : Page();

    public async Task<IActionResult> OnPostAsync()
    {
        if (!register_attempts.Allow(HttpContext.GetClientIp()))
        {
            Error = "Too many registration attempts. Please try again later.";
            return Page();
        }

        string emailAddress = Email.Trim();
        var result = await AccountCreation.CreateAsync(db, passwords, Username.Trim(), emailAddress, Password);

        if (!result.Succeeded)
        {
            UsernameErrors = result.UsernameErrors;
            EmailErrors = result.EmailErrors;
            PasswordErrors = result.PasswordErrors;
            return Page();
        }

        long userId = result.UserId!.Value;

        // Account exists but is unverified: issue + email a 'verify' code and hand off to /verify.
        // We do NOT sign in yet. If the email fails to send the account still exists, but we tell
        // the user so they can retry (they can also just sign in later to get a fresh code).
        try
        {
            await EmailCodeFlow.IssueAndSendAsync(codes, email, emailAddress, userId, "verify", HttpContext.RequestAborted);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send verification code to new user {UserId}", userId);
            Error = "Your account was created, but we couldn't send the verification email. Please sign in to try again.";
            return Page();
        }

        challenge.Issue(HttpContext, userId, "verify");
        return Redirect("/verify");
    }
}
