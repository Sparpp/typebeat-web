using Microsoft.AspNetCore.Mvc;
using Typebeat.Web.Auth;
using Typebeat.Web.Data;

namespace Typebeat.Web.Pages.GoogleSignIn;

/// <summary>
/// /auth/google/username: the one question a first-time Google sign-in has to answer, since a
/// username is required and Google does not have one. Reached only with the pending cookie the
/// callback sets for a validated identity nobody owns yet (else back to /login); the box is
/// prefilled from the Google display name (<see cref="ExternalLogins.SuggestUsername"/>).
///
/// The account is made by <see cref="AccountCreation.CreateExternalAsync"/>: the same username
/// rules and uniqueness /register applies, no password, email already verified. Register's only
/// sign-up gate, its per-IP rate limit, is honoured by drawing on the SAME budget
/// (<see cref="RegisterModel.RegisterAttempts"/>), so Google is not a way around it. Success signs
/// in with the same session every other sign-in path mints.
/// </summary>
public sealed class UsernameModel(Db db, GoogleOidc google, GoogleCookies cookies, TokenService tokens, ChallengeCookie challenge)
    : TypebeatPageModel
{
    [BindProperty]
    public string Username { get; set; } = string.Empty;

    public string Email { get; private set; } = string.Empty;

    public string? Error { get; private set; }
    public IReadOnlyList<string> UsernameErrors { get; private set; } = [];
    public IReadOnlyList<string> EmailErrors { get; private set; } = [];

    public IActionResult OnGet()
    {
        if (!google.Enabled)
            return NotFound();

        var pending = cookies.ReadPending(HttpContext);
        if (pending is null)
            return Redirect("/login");

        Email = pending.Email;
        Username = ExternalLogins.SuggestUsername(pending.Name, pending.Email);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!google.Enabled)
            return NotFound();

        var pending = cookies.ReadPending(HttpContext);
        if (pending is null)
            return Redirect("/login");

        Email = pending.Email;

        if (!RegisterModel.RegisterAttempts.Allow(HttpContext.GetClientIp()))
        {
            Error = "Too many registration attempts. Please try again later.";
            return Page();
        }

        var result = await AccountCreation.CreateExternalAsync(db, Username.Trim(), pending.Identity, CountryResolver.Resolve(HttpContext), HttpContext.RequestAborted);

        if (!result.Succeeded)
        {
            UsernameErrors = result.UsernameErrors;
            EmailErrors = result.EmailErrors;
            return Page();
        }

        cookies.ClearPending(HttpContext);
        challenge.Clear(HttpContext);
        SessionCookieAuth.SignIn(HttpContext, await tokens.IssueAsync(result.UserId!.Value));
        return Redirect("/");
    }
}
