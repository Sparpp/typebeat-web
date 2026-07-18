using System.Net;

namespace Typebeat.Web.Email;

/// <summary>
/// Builds the one transactional message this milestone sends: a 6-digit code, plus a plain and a
/// minimal-HTML body. Deliberately plain and un-osu — no branding trade dress, just the code, the
/// expiry, and an "ignore this if it wasn't you" line.
/// </summary>
public static class VerificationEmail
{
    public sealed record Content(string Subject, string HtmlBody, string TextBody);

    public static Content Build(string purpose, string code, int expiryMinutes)
    {
        string action = purpose == "login" ? "finish signing in" : "confirm your email";
        string subject = $"Your type!beat code: {code}";

        string text =
            $"""
            Your type!beat verification code is {code}.

            Enter it on the website to {action}. The code expires in {expiryMinutes} minutes.

            If this wasn't you, you can ignore this email — nothing will change.
            """;

        string safeCode = WebUtility.HtmlEncode(code);
        string safeAction = WebUtility.HtmlEncode(action);
        string html =
            $"""
            <div style="font-family:ui-monospace,SFMono-Regular,Menlo,Consolas,monospace;color:#141519;max-width:420px;margin:0 auto">
              <p style="font-size:15px;line-height:1.5">Your type!beat verification code is:</p>
              <p style="font-size:32px;font-weight:700;letter-spacing:6px;margin:16px 0">{safeCode}</p>
              <p style="font-size:14px;line-height:1.5;color:#555">Enter it on the website to {safeAction}. The code expires in {expiryMinutes} minutes.</p>
              <p style="font-size:12px;line-height:1.5;color:#888">If this wasn't you, you can ignore this email — nothing will change.</p>
            </div>
            """;

        return new Content(subject, html, text);
    }
}
