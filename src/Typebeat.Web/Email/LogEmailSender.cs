namespace Typebeat.Web.Email;

/// <summary>
/// The fallback sender: logs the full message (subject + text body, which contains the code) at
/// Information level instead of delivering it. Active whenever TYPEBEAT_RESEND_API_KEY is unset,
/// so:
///  - local dev and the website tests can read the emitted code straight from the logs;
///  - a freshly deployed prod box that has not been given a Resend key yet still lets the
///    operator complete verification by reading `docker logs typebeat-web-app-1`.
/// Never throws.
/// </summary>
public sealed class LogEmailSender(ILogger<LogEmailSender> logger) : IEmailSender
{
    public Task SendAsync(string toEmail, string subject, string htmlBody, string textBody, CancellationToken ct = default)
    {
        logger.LogInformation(
            "[email:log] to={ToEmail} subject={Subject}\n{TextBody}",
            toEmail, subject, textBody);

        return Task.CompletedTask;
    }
}
