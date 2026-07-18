namespace Typebeat.Web.Email;

/// <summary>
/// Transactional email delivery. Two implementations: <see cref="ResendEmailSender"/> (real,
/// active when TYPEBEAT_RESEND_API_KEY is configured) and <see cref="LogEmailSender"/> (writes
/// the whole message — code included — to the log, so dev/tests and a not-yet-configured prod
/// box still surface verification codes). Program.cs picks which one at startup.
///
/// Implementations throw on failure; callers that must not fail on a send error (the game-API
/// registration side effect) wrap the call.
/// </summary>
public interface IEmailSender
{
    Task SendAsync(string toEmail, string subject, string htmlBody, string textBody, CancellationToken ct = default);
}
