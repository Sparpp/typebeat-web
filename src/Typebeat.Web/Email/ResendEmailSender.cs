using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Typebeat.Web.Email;

/// <summary>
/// Delivers mail via the Resend HTTP API (https://resend.com). Active when
/// TYPEBEAT_RESEND_API_KEY is configured. The HttpClient comes from IHttpClientFactory (pooled,
/// no socket exhaustion). On any non-2xx the response body is logged and a generic exception is
/// thrown — the caller turns that into a "couldn't send email, try again" for the user; the
/// upstream body (which may name the address/domain) is never surfaced to the browser.
///
/// This external JSON payload uses System.Text.Json deliberately: WireJson/Newtonsoft is reserved
/// for the osu-web API surface the game client consumes, not for our own outbound integrations.
/// </summary>
public sealed class ResendEmailSender(IHttpClientFactory httpClientFactory, IConfiguration config, ILogger<ResendEmailSender> logger)
    : IEmailSender
{
    private const string endpoint = "https://api.resend.com/emails";

    public static string FromAddress(IConfiguration config)
        => config["TYPEBEAT_EMAIL_FROM"] is { Length: > 0 } from ? from : "type!beat <noreply@mingda.sh>";

    public async Task SendAsync(string toEmail, string subject, string htmlBody, string textBody, CancellationToken ct = default)
    {
        string? apiKey = config["TYPEBEAT_RESEND_API_KEY"];
        if (string.IsNullOrEmpty(apiKey))
            throw new InvalidOperationException("ResendEmailSender selected without TYPEBEAT_RESEND_API_KEY");

        var payload = new
        {
            from = FromAddress(config),
            to = new[] { toEmail },
            subject,
            html = htmlBody,
            text = textBody,
        };

        using var client = httpClientFactory.CreateClient(nameof(ResendEmailSender));
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await client.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync(ct);
            logger.LogError("[email:resend] send failed ({Status}) to={ToEmail}: {Body}", (int)response.StatusCode, toEmail, body);
            throw new InvalidOperationException($"Resend returned {(int)response.StatusCode}");
        }
    }
}
