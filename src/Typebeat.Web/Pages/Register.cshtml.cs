using Microsoft.AspNetCore.Mvc;
using Typebeat.Web.Auth;
using Typebeat.Web.Data;

namespace Typebeat.Web.Pages;

/// <summary>
/// Website registration. Same rules and creation path as POST /users (AccountCreation /
/// AccountValidation are shared), then auto-login: a fresh token pair goes straight into the
/// session cookie so the new user lands signed in.
/// </summary>
public sealed class RegisterModel(Db db, PasswordService passwords, TokenService tokens) : TypebeatPageModel
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

        var result = await AccountCreation.CreateAsync(db, passwords, Username.Trim(), Email.Trim(), Password);

        if (!result.Succeeded)
        {
            UsernameErrors = result.UsernameErrors;
            EmailErrors = result.EmailErrors;
            PasswordErrors = result.PasswordErrors;
            return Page();
        }

        SessionCookieAuth.SignIn(HttpContext, await tokens.IssueAsync(result.UserId!.Value));
        return Redirect("/");
    }
}
