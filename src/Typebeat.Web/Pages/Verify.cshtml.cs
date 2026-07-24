using Dapper;
using Microsoft.AspNetCore.Mvc;
using Typebeat.Web.Auth;
using Typebeat.Web.Data;
using Typebeat.Web.Email;

namespace Typebeat.Web.Pages;

/// <summary>
/// The code step. Reached only mid-flow, carrying a <see cref="ChallengeCookie"/> that names the
/// pending user and whether they are confirming a new email ('verify') or finishing a login
/// ('login'). GET requires the challenge (else back to /login); POST checks the code via
/// <see cref="EmailCodeService.VerifyAsync"/> and, only on success, mints the session, marking
/// users.verified_at (idempotent) on ANY completed code, since entering an emailed code proves
/// email control. That verified flag is the self-service gate for beatmap submission. Both POST
/// handlers are antiforgery-validated by the Razor Pages pipeline.
///
/// Fails closed: a challenge whose user was since deleted or restricted is discarded and the flow
/// restarts at /login.
/// </summary>
public sealed class VerifyModel(
    Db db, TokenService tokens, EmailCodeService codes, IEmailSender email, ChallengeCookie challenge, ILogger<VerifyModel> logger)
    : TypebeatPageModel
{
    [BindProperty]
    public string Code { get; set; } = string.Empty;

    /// <summary>"verify" | "login"; drives the page copy.</summary>
    public string Purpose { get; private set; } = "verify";

    public string MaskedEmail { get; private set; } = string.Empty;

    public string? Error { get; private set; }
    public string? Status { get; private set; }

    public bool IsLogin => Purpose == "login";

    public async Task<IActionResult> OnGetAsync()
    {
        var pending = challenge.Read(HttpContext);
        if (pending is null)
            // Already signed in with no challenge → home; otherwise nothing to verify → login.
            return CurrentUser is not null ? Redirect("/") : Redirect("/login");

        var user = await loadPendingAsync(pending.UserId);
        if (user is null)
            return Redirect("/login");

        Purpose = pending.Purpose;
        MaskedEmail = EmailCodeFlow.Mask(user.Email);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var pending = challenge.Read(HttpContext);
        if (pending is null)
            return Redirect("/login");

        var user = await loadPendingAsync(pending.UserId);
        if (user is null)
            return Redirect("/login");

        Purpose = pending.Purpose;
        MaskedEmail = EmailCodeFlow.Mask(user.Email);

        var result = await codes.VerifyAsync(pending.UserId, pending.Purpose, Code.Trim(), HttpContext.RequestAborted);

        if (result.Status != EmailCodeService.VerifyStatus.Success)
        {
            Error = result.Status == EmailCodeService.VerifyStatus.Burned
                ? "Too many incorrect attempts. Request a new code."
                : remainingHint(result.AttemptsRemaining);
            return Page();
        }

        // Completing any email code proves control of the account's email, so mark it verified
        // (idempotent) before signing in. This is the self-service path to beatmap submission; it
        // covers accounts created in-game, whose OAuth password grant has no email step: signing in
        // on the website is what verifies them. Beatmap SUBMISSION gates on verified_at; only RANKING
        // needs the reviewer/admin role.
        await using (var conn = await db.OpenAsync(HttpContext.RequestAborted))
        {
            await conn.ExecuteAsync(
                "UPDATE users SET verified_at = now() WHERE id = @id AND verified_at IS NULL",
                new { id = pending.UserId });
        }

        // "Remember me" (carried from the login form through the challenge cookie) mints a 30-day
        // session instead of the default 24h; it only ever extends a login that has just cleared the
        // email code, so it never weakens the verification step itself.
        long lifetime = pending.RememberMe
            ? SessionCookieAuth.RememberMeLifetimeSeconds
            : TokenService.ACCESS_LIFETIME_SECONDS;

        challenge.Clear(HttpContext);
        SessionCookieAuth.SignIn(HttpContext, await tokens.IssueAsync(pending.UserId, lifetime));
        return Redirect("/");
    }

    public async Task<IActionResult> OnPostResendAsync()
    {
        var pending = challenge.Read(HttpContext);
        if (pending is null)
            return Redirect("/login");

        var user = await loadPendingAsync(pending.UserId);
        if (user is null)
            return Redirect("/login");

        Purpose = pending.Purpose;
        MaskedEmail = EmailCodeFlow.Mask(user.Email);

        try
        {
            var result = await EmailCodeFlow.IssueAndSendAsync(codes, email, user.Email, pending.UserId, pending.Purpose, HttpContext.RequestAborted);
            Status = result.Status switch
            {
                EmailCodeService.IssueStatus.Sent => "We sent a new code to your email.",
                EmailCodeService.IssueStatus.TooSoon => "Please wait a moment before requesting another code.",
                _ => "You've requested too many codes recently. Please try again later.",
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to resend {Purpose} code to user {UserId}", pending.Purpose, pending.UserId);
            Error = "We couldn't send the email. Please try again.";
        }

        return Page();
    }

    private static string remainingHint(int attemptsRemaining)
        => attemptsRemaining > 0
            ? $"That code is incorrect or has expired. You have {attemptsRemaining} attempt{(attemptsRemaining == 1 ? "" : "s")} left."
            : "That code is incorrect or has expired. Your next incorrect attempt will require a new code.";

    // Fails closed: restricted users are treated as non-existent here (no session is ever minted
    // for them), matching the bearer/cookie auth paths.
    private async Task<PendingUser?> loadPendingAsync(long userId)
    {
        await using var conn = await db.OpenAsync(HttpContext.RequestAborted);
        var user = await conn.QuerySingleOrDefaultAsync<PendingUser>(
            "SELECT email, restricted FROM users WHERE id = @userId",
            new { userId });

        if (user is null || user.Restricted)
        {
            challenge.Clear(HttpContext);
            return null;
        }

        return user;
    }

    private sealed record PendingUser(string Email, bool Restricted);
}
