using Microsoft.AspNetCore.Mvc;
using Typebeat.Web.Auth;
using Typebeat.Web.Data;

namespace Typebeat.Web.Pages.GoogleSignIn;

/// <summary>
/// GET /auth/google/callback: where Google sends the browser back with <c>code</c> and <c>state</c>.
///
/// The flow cookie is read and cleared FIRST, so a callback URL can be used at most once, and its
/// state must match the one in the query (else this is not a flow this browser started). The code
/// is then redeemed and the ID token validated (<see cref="GoogleOidc.ExchangeAsync"/>). What
/// happens next depends on the mode the flow was started in:
///
///  - SIGN-IN (/login, /register): <see cref="ExternalLogins.ResolveSignInAsync"/> picks linked,
///    email match, or new. Linked and email match are signed in here with the SAME session the
///    emailed-code step mints (a 24h token in the session cookie): Google's sign-in replaces both
///    the password and the emailed code, since it is itself a stronger proof of the same email. New
///    goes to the username step carrying the identity in a pending cookie, still signed out.
///  - LINK (Settings): only for the SAME signed-in user who started it (the id is bound into the
///    flow cookie), then back to Settings with the outcome.
///
/// Failure details are logged, never shown; the page only ever says to try again. 404 while Google
/// sign-in is not configured.
/// </summary>
public sealed class CallbackModel(
    Db db, GoogleOidc google, GoogleCookies cookies, TokenService tokens, ChallengeCookie challenge, ILogger<CallbackModel> logger)
    : TypebeatPageModel
{
    private const string try_again = "That sign-in didn't go through. Please try again.";

    public string Error { get; private set; } = try_again;

    public async Task<IActionResult> OnGetAsync(string? code, string? state, string? error)
    {
        if (!google.Enabled)
            return NotFound();

        var flow = cookies.ReadFlow(HttpContext);
        cookies.ClearFlow(HttpContext);

        if (flow is null || string.IsNullOrEmpty(state) || !fixedTimeEquals(flow.State, state))
        {
            logger.LogInformation("Google callback without a matching flow (cookie present: {HasFlow})", flow is not null);
            return Page();
        }

        // The user backed out on Google's side (error=access_denied) or Google refused the request.
        if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code))
        {
            logger.LogInformation("Google callback returned error {Error}", error);
            return flow.IsLink ? Redirect("/settings?google=cancelled") : Redirect("/login");
        }

        var identity = await google.ExchangeAsync(HttpContext, code, flow, HttpContext.RequestAborted);
        if (identity is null)
            return flow.IsLink ? Redirect("/settings?google=failed") : Page();

        if (flow.IsLink)
            return await linkAsync(flow.LinkUserId!.Value, identity);

        var outcome = await ExternalLogins.ResolveSignInAsync(db, identity, HttpContext.RequestAborted);

        switch (outcome.Kind)
        {
            case ExternalLogins.SignInKind.SignedIn:
                // Any half-finished password login in this browser is moot now.
                challenge.Clear(HttpContext);
                cookies.ClearPending(HttpContext);
                SessionCookieAuth.SignIn(HttpContext, await tokens.IssueAsync(outcome.UserId!.Value));
                return Redirect("/");

            case ExternalLogins.SignInKind.NeedsAccount:
                cookies.IssuePending(HttpContext, identity);
                return Redirect("/auth/google/username");

            default:
                Error = outcome.Error ?? try_again;
                return Page();
        }
    }

    private async Task<IActionResult> linkAsync(long linkUserId, GoogleIdentity identity)
    {
        // The session must still be the one that pressed "Link": a sign-out, or a different
        // account signing in in another tab, in between voids the link.
        if (CurrentUser is null || CurrentUser.Id != linkUserId)
            return Redirect("/settings?google=failed");

        var result = await ExternalLogins.LinkAsync(db, linkUserId, identity, HttpContext.RequestAborted);

        return Redirect(result switch
        {
            ExternalLogins.LinkResult.Linked => "/settings?saved=google-linked",
            ExternalLogins.LinkResult.SubjectTaken => "/settings?google=taken",
            _ => "/settings?google=already",
        });
    }

    private static bool fixedTimeEquals(string a, string b)
        => System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(a), System.Text.Encoding.UTF8.GetBytes(b));
}
