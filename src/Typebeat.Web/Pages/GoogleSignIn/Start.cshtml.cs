using Microsoft.AspNetCore.Mvc;
using Typebeat.Web.Auth;

namespace Typebeat.Web.Pages.GoogleSignIn;

/// <summary>
/// GET /auth/google: the "Continue with Google" button on /login and /register. Starts the flow in
/// sign-in mode (<see cref="GoogleOidc.Begin"/>) and redirects to Google's account chooser. A GET
/// is safe here: all it can do is send a browser to Google as whoever that browser already is, and
/// the state bound into this browser's flow cookie is what stops a code from anyone else landing.
/// Linking an account to a signed-in user starts from Settings instead, as an antiforgery-checked
/// POST. 404 while Google sign-in is not configured.
/// </summary>
public sealed class StartModel(GoogleOidc google, GoogleCookies cookies) : TypebeatPageModel
{
    public IActionResult OnGet()
    {
        if (!google.Enabled)
            return NotFound();

        if (CurrentUser is not null)
            return Redirect("/");

        return Redirect(google.Begin(HttpContext, cookies, linkUserId: null));
    }
}
