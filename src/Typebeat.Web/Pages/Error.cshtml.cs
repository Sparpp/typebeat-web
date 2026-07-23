namespace Typebeat.Web.Pages;

/// <summary>
/// The styled error page for HTML routes, rendered two ways (see Program.cs):
///
///  - UseStatusCodePagesWithReExecute("/error/{0}") re-executes bodyless 4xx/5xx responses
///    (a page handler's bare NotFound(), an unmatched route) through this page; the original
///    status code is preserved on the response; the route {code} only drives the copy.
///  - UseExceptionHandler("/error/500") lands unhandled page exceptions here in production.
///
/// Both are scoped to NON-wire routes only: /api, /bss, /oauth, /ws, /health,
/// /menu-content.json, /debug and the registration POST /users keep their exact wire
/// envelopes and empty-body semantics (the game client string-matches some of them).
///
/// Direct GETs of /error/{code} by the curious set the status honestly, so the page is never
/// an OK-masquerading error (and crawlers won't index it).
/// </summary>
public sealed class ErrorModel : TypebeatPageModel
{
    public int Code { get; private set; }

    public string Headline => Code switch
    {
        404 => "This page skipped a beat.",
        403 => "That verse isn't yours to sing.",
        >= 500 => "We dropped the mic, something broke on our side.",
        _ => "That request fell out of rhythm.",
    };

    public string Detail => Code switch
    {
        404 => "The page may have been removed, renamed, or never existed at all.",
        >= 500 => "It's been noted. Try again in a moment.",
        _ => "Head back and try another route.",
    };

    public void OnGet(int code)
    {
        Code = code is >= 400 and < 600 ? code : 500;

        // Re-executed requests already carry the original status; direct visits get it here.
        HttpContext.Response.StatusCode = Code;

        ViewData["Title"] = $"{Code}";
        ViewData["MetaDescription"] = "type!beat error page.";
    }
}
