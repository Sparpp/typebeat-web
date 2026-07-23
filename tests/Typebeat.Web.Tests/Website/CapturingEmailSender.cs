using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Typebeat.Web.Email;

namespace Typebeat.Web.Tests.Website;

/// <summary>
/// Test double for <see cref="IEmailSender"/>: records every message instead of sending it, and
/// pulls the 6-digit code out of the body so tests can drive the /verify flow. Registered into
/// the website test host in place of the real sender (see <see cref="WebsiteFixture"/>).
///
/// <see cref="ThrowOnSend"/> flips it into a failing sender for the regression that POST /users
/// must still succeed when email delivery throws; tests run serially, so a toggle on the shared
/// instance (set/reset in a finally) is safe.
/// </summary>
public sealed class CapturingEmailSender : IEmailSender
{
    public sealed record Sent(string ToEmail, string Subject, string TextBody, string? Code);

    private static readonly Regex code_pattern = new(@"\b(\d{6})\b", RegexOptions.Compiled);

    private readonly ConcurrentQueue<Sent> sent = new();

    public bool ThrowOnSend { get; set; }

    public Task SendAsync(string toEmail, string subject, string htmlBody, string textBody, CancellationToken ct = default)
    {
        if (ThrowOnSend)
            throw new InvalidOperationException("simulated email delivery failure");

        string? code = code_pattern.Match(textBody) is { Success: true } m ? m.Groups[1].Value : null;
        sent.Enqueue(new Sent(toEmail, subject, textBody, code));
        return Task.CompletedTask;
    }

    /// <summary>
    /// The most recent code emailed to anyone. Safe for the serial website suite: each flow reads
    /// it immediately after the single send it triggered.
    /// </summary>
    public string? LastCode
        => sent.Where(s => s.Code is not null).Select(s => s.Code).LastOrDefault();

    /// <summary>The most recent code emailed to <paramref name="email"/>, or null if none.</summary>
    public string? LastCodeFor(string email)
        => sent.Where(s => string.Equals(s.ToEmail, email, StringComparison.OrdinalIgnoreCase))
               .Select(s => s.Code)
               .LastOrDefault();

    /// <summary>How many messages were sent to <paramref name="email"/>.</summary>
    public int CountFor(string email)
        => sent.Count(s => string.Equals(s.ToEmail, email, StringComparison.OrdinalIgnoreCase));
}
