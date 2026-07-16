using Microsoft.AspNetCore.Mvc.RazorPages;
using Typebeat.Web.Auth;

namespace Typebeat.Web.Pages;

/// <summary>
/// Base class for all website pages: exposes the cookie-session user resolved by
/// <see cref="SessionCookieAuth"/>. Views can equivalently read <c>Context.SessionUser()</c>
/// (the layout does, so it needs no model type).
/// </summary>
public abstract class TypebeatPageModel : PageModel
{
    public AuthedUser? CurrentUser => HttpContext.SessionUser();
}
