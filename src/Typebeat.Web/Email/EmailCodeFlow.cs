using Typebeat.Web.Auth;

namespace Typebeat.Web.Email;

/// <summary>
/// Glue between <see cref="EmailCodeService"/> (issue a code) and <see cref="IEmailSender"/>
/// (deliver it), the one place both the website flows and the game-API registration side effect
/// turn "issue a code for this user" into an actual email. Send failures propagate; callers that
/// must not fail on a bad send (POST /users) wrap the call.
/// </summary>
public static class EmailCodeFlow
{
    public static async Task<EmailCodeService.IssueResult> IssueAndSendAsync(
        EmailCodeService codes, IEmailSender email, string toEmail, long userId, string purpose, CancellationToken ct = default)
    {
        var result = await codes.IssueAsync(userId, purpose, ct);

        if (result is { Status: EmailCodeService.IssueStatus.Sent, Code: { } code })
        {
            var content = VerificationEmail.Build(purpose, code, result.ExpiryMinutes);
            await email.SendAsync(toEmail, content.Subject, content.HtmlBody, content.TextBody, ct);
        }

        return result;
    }

    /// <summary>Masks an email for display: "alice@example.com" → "a•••@example.com".</summary>
    public static string Mask(string email)
    {
        int at = email.IndexOf('@');
        if (at <= 0)
            return "•••";

        string local = email[..at];
        string domain = email[at..];
        string head = local.Length > 0 ? local[..1] : string.Empty;

        return $"{head}•••{domain}";
    }
}
