using Dapper;
using Microsoft.AspNetCore.Mvc;
using Typebeat.Web.Auth;
using Typebeat.Web.Data;
using Typebeat.Web.Email;

namespace Typebeat.Web.Pages;

/// <summary>
/// Step 1 of "forgot my password": enter the account email, and we email a 'reset' code and hand
/// off to <see cref="ResetPasswordModel"/> (the pending user rides the same <see cref="ChallengeCookie"/>
/// the login/verify flow uses). The code + challenge machinery is entirely reused; 'reset' is a
/// first-class <c>email_tokens.purpose</c> already, so there is no new storage.
///
/// Enumeration-safe by construction. Unlike /login there is NO password gate before this side
/// effect, so every observable, status, body, redirect target, AND the Set-Cookie header, must be
/// identical whether or not the email is registered; otherwise the mere presence of the challenge
/// cookie would reveal which addresses have accounts. So a challenge cookie is ALWAYS issued: a real
/// account gets its real id and an emailed code; an unknown (or restricted) address gets a decoy
/// challenge carrying <see cref="decoy_user_id"/> and no email. A send failure is swallowed (logged)
/// for the same reason: a transient mail error must not become an existence oracle.
/// </summary>
public sealed class ForgotPasswordModel(
    Db db, EmailCodeService codes, IEmailSender email, ChallengeCookie challenge, ILogger<ForgotPasswordModel> logger) : TypebeatPageModel
{
    // A speed bump on top of the per-(user,purpose) issuance cap already enforced in
    // EmailCodeService (Cloudflare is the real layer): 5 requests per IP per 15 minutes.
    private static readonly FixedWindowLimiter reset_requests = new(5, TimeSpan.FromMinutes(15));

    // users.id is bigserial (from 1), so a challenge carrying 0 can never resolve to an account;
    // the decoy that keeps the Set-Cookie header uniform for unknown emails.
    private const long decoy_user_id = 0;

    [BindProperty]
    public string Email { get; set; } = string.Empty;

    public string? Error { get; private set; }

    public IActionResult OnGet()
        => CurrentUser is not null ? Redirect("/") : Page();

    public async Task<IActionResult> OnPostAsync()
    {
        if (!reset_requests.Allow(HttpContext.GetClientIp()))
        {
            Error = "Too many password-reset requests. Please wait a few minutes and try again.";
            return Page();
        }

        string address = Email.Trim();

        // Resolve the account, but NEVER let its existence branch the observable outcome below.
        (long Id, string Email)? account = null;
        if (address.Length > 0)
        {
            await using var conn = await db.OpenAsync(HttpContext.RequestAborted);
            // ::citext keeps the match case-insensitive (see LoginModel). Restricted accounts are
            // treated as non-existent, matching every other auth path.
            account = await conn.QuerySingleOrDefaultAsync<(long, string)?>(
                "SELECT id, email FROM users WHERE email = @address::citext AND restricted = false",
                new { address });
        }

        if (account is { } user)
        {
            try
            {
                await EmailCodeFlow.IssueAndSendAsync(codes, email, user.Email, user.Id, "reset", HttpContext.RequestAborted);
            }
            catch (Exception ex)
            {
                // Logged, not surfaced: a send failure must look the same as any other request.
                logger.LogError(ex, "Failed to send reset code to user {UserId}", user.Id);
            }

            challenge.Issue(HttpContext, user.Id, "reset");
        }
        else
        {
            // Unknown / blank / restricted: set a decoy challenge so the response is indistinguishable,
            // and send nothing.
            challenge.Issue(HttpContext, decoy_user_id, "reset");
        }

        return Redirect("/reset-password");
    }
}
