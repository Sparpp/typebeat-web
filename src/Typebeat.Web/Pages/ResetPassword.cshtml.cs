using Dapper;
using Microsoft.AspNetCore.Mvc;
using Typebeat.Web.Auth;
using Typebeat.Web.Data;
using Typebeat.Web.Email;

namespace Typebeat.Web.Pages;

/// <summary>
/// Step 2 of "forgot my password": enter the emailed 'reset' code and a new password. The pending
/// account rides the <see cref="ChallengeCookie"/> set by <see cref="ForgotPasswordModel"/> and the
/// code is checked with the same <see cref="EmailCodeService.VerifyAsync"/> the /verify page uses.
/// On success the password hash is replaced, EVERY existing session (web + game) is revoked so any
/// attacker session dies, and a fresh session is minted; the code already proved control of the
/// account's email, which is exactly the bar M2 signs a user in on.
///
/// Enumeration-safe:
///  - GET always renders the form (no redirect, no per-account branch), so a direct visit and a real
///    mid-flow visit are identical, and no masked email is shown (which would differ by account).
///  - Every existence-dependent failure, a missing/decoy/expired challenge, a since-deleted user, a
///    wrong code, AND a burned code, collapses to ONE generic message; distinguishing "burned" would
///    reveal that a live code exists for this account (decoy accounts can never burn).
///  - New-password FIELD validation (match + length) runs BEFORE the code is verified, so fumbling
///    the password never burns a good code; those checks are account-independent, so they leak nothing.
/// </summary>
public sealed class ResetPasswordModel(
    Db db, PasswordService passwords, TokenService tokens, EmailCodeService codes, IEmailSender email, ChallengeCookie challenge, ILogger<ResetPasswordModel> logger)
    : TypebeatPageModel
{
    private const string generic_code_error = "That code is incorrect or has expired. Request a new code below.";

    [BindProperty]
    public string Code { get; set; } = string.Empty;

    [BindProperty]
    public string NewPassword { get; set; } = string.Empty;

    [BindProperty]
    public string ConfirmPassword { get; set; } = string.Empty;

    public string? Error { get; private set; }
    public string? Status { get; private set; }
    public IReadOnlyList<string> PasswordErrors { get; private set; } = [];

    // GET is deliberately inert: render the form regardless of any challenge state. The only thing
    // it reads is the neutral resend line carried across OnPostResendAsync's redirect.
    public void OnGet() => Status = TempData[status_key] as string;

    private const string status_key = "reset.status";

    public async Task<IActionResult> OnPostAsync()
    {
        // 1) New-password field checks: account-independent, and BEFORE the code is consumed so a
        //    mismatch or too-short password never burns a valid code. Passing an empty username to
        //    the shared validator runs the length/non-empty rules and skips only the
        //    username-equality rule (which would need the resolved account and so cannot run here
        //    without leaking existence).
        if (NewPassword != ConfirmPassword)
        {
            Error = "The passwords do not match.";
            return Page();
        }

        var fieldErrors = AccountValidation.ValidatePassword(NewPassword, username: string.Empty);
        if (fieldErrors.Count > 0)
        {
            PasswordErrors = fieldErrors;
            return Page();
        }

        // 2) Resolve the pending account from the challenge. A missing/decoy/expired cookie or a
        //    since-deleted/restricted user all fail closed to the SAME generic code error.
        var pending = challenge.Read(HttpContext);
        var user = pending is not null ? await loadPendingAsync(pending.UserId) : null;
        if (user is null)
        {
            Error = generic_code_error;
            return Page();
        }

        // 3) Verify (and consume) the code. Burned and incorrect share one message on purpose.
        var result = await codes.VerifyAsync(user.Id, "reset", Code.Trim(), HttpContext.RequestAborted);
        if (result.Status != EmailCodeService.VerifyStatus.Success)
        {
            Error = generic_code_error;
            return Page();
        }

        // 4) Full password policy now that we hold the username (catches new-password == username).
        //    Only reachable with a valid code, so it is not an existence oracle.
        var policyErrors = AccountValidation.ValidatePassword(NewPassword, user.Username);
        if (policyErrors.Count > 0)
        {
            PasswordErrors = policyErrors;
            return Page();
        }

        // 5) Replace the hash, kill every existing session, then mint a fresh one.
        await using (var conn = await db.OpenAsync(HttpContext.RequestAborted))
        {
            await conn.ExecuteAsync(
                // Completing the emailed reset code also proves email control → mark verified (idempotent),
                // the same self-service submission gate the login/signup code paths set.
                "UPDATE users SET password_hash = @hash, verified_at = COALESCE(verified_at, now()) WHERE id = @id",
                new { hash = passwords.Hash(NewPassword), id = user.Id });
        }

        await tokens.RevokeAllForUserAsync(user.Id);
        challenge.Clear(HttpContext);
        SessionCookieAuth.SignIn(HttpContext, await tokens.IssueAsync(user.Id));

        logger.LogInformation("Password reset completed for user {UserId}", user.Id);
        return Redirect("/");
    }

    public async Task<IActionResult> OnPostResendAsync()
    {
        var pending = challenge.Read(HttpContext);
        var user = pending is not null ? await loadPendingAsync(pending.UserId) : null;

        // Real account → issue + send another 'reset' code (subject to the per-user throttle inside
        // IssueAndSend). Decoy / missing challenge → do nothing. Either way the SAME neutral line is
        // shown, and the throttle status is never surfaced, so resend can't probe existence either.
        if (user is not null)
        {
            try
            {
                await EmailCodeFlow.IssueAndSendAsync(codes, email, user.Email, user.Id, "reset", HttpContext.RequestAborted);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to resend reset code to user {UserId}", user.Id);
            }
        }

        // Post/Redirect/Get, like /verify: a refresh must not send another code, and the code form
        // must not be left sitting on ?handler=Resend.
        TempData[status_key] = "If an account exists for that email, we've sent a new code.";
        return Redirect("/reset-password");
    }

    // Fails closed like VerifyModel.loadPendingAsync: a since-deleted or restricted user, or the
    // id-0 decoy, resolves to null and the challenge is cleared.
    private async Task<PendingUser?> loadPendingAsync(long userId)
    {
        await using var conn = await db.OpenAsync(HttpContext.RequestAborted);
        var user = await conn.QuerySingleOrDefaultAsync<PendingUser>(
            "SELECT id, username, email FROM users WHERE id = @userId AND restricted = false",
            new { userId });

        if (user is null)
        {
            challenge.Clear(HttpContext);
            return null;
        }

        return user;
    }

    private sealed record PendingUser(long Id, string Username, string Email);
}
