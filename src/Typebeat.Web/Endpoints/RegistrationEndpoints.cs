using Newtonsoft.Json;
using Typebeat.Web.Auth;
using Typebeat.Web.Data;
using Typebeat.Web.Email;
using Typebeat.Web.Wire;

namespace Typebeat.Web.Endpoints;

/// <summary>
/// POST /users: in-client account registration (the AccountCreationOverlay flow).
///
/// Wire contract is dictated by the client's <c>RegistrationRequest</c> /
/// <c>APIAccess.CreateAccount</c>:
///  - request is FORM-encoded with fields user[username] / user[user_email] / user[password];
///  - SUCCESS is any 2xx (the client's WebRequest.Perform() does not throw; CreateAccount
///    returns null and the body is ignored); we return a minimal user object anyway;
///  - FAILURE must be a non-2xx so Perform() throws, and the body must carry a
///    <c>form_error</c> token: <c>{"form_error":{"user":{"username":[...],"user_email":[...],"password":[...]}}}</c>.
///    CreateAccount does <c>JObject.Parse(body).SelectToken("form_error", true).ToObject&lt;RegistrationRequestErrors&gt;()</c>,
///    and ScreenEntry surfaces User.Username / User.Email / User.Password arrays under the
///    matching fields. osu-web uses HTTP 422 for this, which we mirror.
///
/// The other error path the client understands is a top-level <c>{"error":"..."}</c> (or
/// <c>{"url":"..."}</c> redirect); we use the standard {"error":...} envelope for the
/// non-validation rejections (bad User-Agent, rate limit, malformed request).
///
/// Validation and creation logic live in <see cref="AccountValidation"/> /
/// <see cref="AccountCreation"/>, shared with the website's /register form; only the wire
/// envelope is owned here.
/// </summary>
public static class RegistrationEndpoints
{
    /// <summary>
    /// The fork's fixed User-Agent (OsuWebRequest.UserAgent == "type!beat"). Gating on it is a
    /// speed bump against drive-by/browser registration only; it is trivially spoofable and is
    /// NOT a security control. Wrong UA -> 403 {"error":...}.
    /// </summary>
    private const string client_user_agent = "type!beat";

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/users", async (HttpContext ctx, Db db, PasswordService passwords, EmailCodeService codes, IEmailSender emailSender, ILoggerFactory loggerFactory) =>
        {
            // UA gate (speed bump only, see client_user_agent).
            if (ctx.Request.Headers.UserAgent.ToString() != client_user_agent)
                return WireJson.Error(StatusCodes.Status403Forbidden, "forbidden");

            // Per-IP rate limit: every POST counts, so this also throttles enumeration probes.
            string ip = ctx.GetClientIp();
            if (!RegistrationRateLimiter.TryRegister(ip))
                return WireJson.Error(StatusCodes.Status429TooManyRequests, "too many registration attempts, please try again later");

            if (!ctx.Request.HasFormContentType)
                return WireJson.Error(StatusCodes.Status415UnsupportedMediaType, "expected a form-encoded body");

            var form = await ctx.Request.ReadFormAsync();
            string username = form["user[username]"].ToString().Trim();
            string email = form["user[user_email]"].ToString().Trim();
            string password = form["user[password]"].ToString();

            var result = await AccountCreation.CreateAsync(db, passwords, username, email, password);

            if (!result.Succeeded)
                return WireJson.Ok(BuildFormError(result.UsernameErrors, result.EmailErrors, result.PasswordErrors), StatusCodes.Status422UnprocessableEntity);

            // Side effect only: issue + email a 'verify' code so an in-game registrant has one to
            // enter on the WEBSITE (the game client can't do interactive codes). This must never
            // change the wire response; a send failure is swallowed and logged, and the account
            // still returns 200 with the exact success body. The user can request a fresh code any
            // time by signing in on the website.
            try
            {
                await EmailCodeFlow.IssueAndSendAsync(codes, emailSender, email, result.UserId!.Value, "verify", ctx.RequestAborted);
            }
            catch (Exception ex)
            {
                loggerFactory.CreateLogger("Registration").LogError(ex, "Failed to send verify code to new user {UserId}", result.UserId);
            }

            // The client ignores the success body (CreateAccount returns null on any 2xx), but
            // we return a minimal, sane user object rather than an empty 200.
            return WireJson.Ok(new CreatedUser { Id = result.UserId!.Value, Username = username }, StatusCodes.Status200OK);
        });
    }

    // ---- Validation forwards (rules live in AccountValidation, shared with the website) ----

    /// <summary>Username: 3–15 chars, restricted charset. Returns human-readable errors (empty = valid).</summary>
    public static IReadOnlyList<string> ValidateUsername(string username) => AccountValidation.ValidateUsername(username);

    /// <summary>Email: syntactically valid (single @, dotted domain, no spaces). Returns errors (empty = valid).</summary>
    public static IReadOnlyList<string> ValidateEmail(string email) => AccountValidation.ValidateEmail(email);

    /// <summary>Password: >= 8 chars and not equal (case-insensitively) to the username. Returns errors (empty = valid).</summary>
    public static IReadOnlyList<string> ValidatePassword(string password, string username) => AccountValidation.ValidatePassword(password, username);

    /// <summary>
    /// Wraps per-field error lists in the exact envelope the client deserializes:
    /// <c>{"form_error":{"user":{"username":[...],"user_email":[...],"password":[...]}}}</c>.
    /// Public so the wire shape can be asserted directly in tests.
    /// </summary>
    public static object BuildFormError(
        IReadOnlyList<string> usernameErrors,
        IReadOnlyList<string> emailErrors,
        IReadOnlyList<string> passwordErrors)
        => new FormErrorEnvelope
        {
            FormError = new FormErrorBody
            {
                User = new UserErrors
                {
                    Username = usernameErrors.ToArray(),
                    Email = emailErrors.ToArray(),
                    Password = passwordErrors.ToArray(),
                }
            }
        };

    // ---- Wire DTOs (explicit [JsonProperty] on every member; Newtonsoft via WireJson) ----

    private sealed class FormErrorEnvelope
    {
        [JsonProperty("form_error")]
        public FormErrorBody FormError { get; init; } = new();
    }

    private sealed class FormErrorBody
    {
        [JsonProperty("user")]
        public UserErrors User { get; init; } = new();
    }

    private sealed class UserErrors
    {
        [JsonProperty("username")]
        public string[] Username { get; init; } = [];

        [JsonProperty("user_email")]
        public string[] Email { get; init; } = [];

        [JsonProperty("password")]
        public string[] Password { get; init; } = [];
    }

    private sealed class CreatedUser
    {
        [JsonProperty("id")]
        public long Id { get; init; }

        [JsonProperty("username")]
        public string Username { get; init; } = string.Empty;
    }

    /// <summary>
    /// In-memory sliding-window limiter: 3 registrations per IP per hour. Process-local (fine for
    /// the single-node M1 deploy) and unbounded in the number of distinct IPs it tracks; a real
    /// deploy behind a shared limiter would replace this. Documented as a speed bump.
    /// </summary>
    private static class RegistrationRateLimiter
    {
        private const int max_per_window = 3;
        private static readonly TimeSpan window = TimeSpan.FromHours(1);
        private static readonly Dictionary<string, List<DateTime>> hits = new();
        private static readonly object gate = new();

        public static bool TryRegister(string ip)
        {
            var now = DateTime.UtcNow;

            lock (gate)
            {
                if (!hits.TryGetValue(ip, out var list))
                    hits[ip] = list = new List<DateTime>();

                list.RemoveAll(t => now - t > window);

                if (list.Count >= max_per_window)
                    return false;

                list.Add(now);
                return true;
            }
        }
    }
}
