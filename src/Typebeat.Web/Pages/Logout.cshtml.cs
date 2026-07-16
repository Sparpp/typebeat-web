using Microsoft.AspNetCore.Mvc;
using Typebeat.Web.Auth;

namespace Typebeat.Web.Pages;

/// <summary>
/// POST-only sign-out (the layout's "Sign out" form): revokes the session's token row and
/// deletes the cookie. Antiforgery is validated by the Razor Pages pipeline like every other
/// POST. A stray GET just goes home.
/// </summary>
public sealed class LogoutModel(TokenService tokens) : TypebeatPageModel
{
    public IActionResult OnGet() => Redirect("/");

    public async Task<IActionResult> OnPostAsync()
    {
        await SessionCookieAuth.SignOutAsync(HttpContext, tokens);
        return Redirect("/");
    }
}
