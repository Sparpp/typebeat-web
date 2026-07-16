using System.Text.RegularExpressions;
using Dapper;
using Newtonsoft.Json;
using Npgsql;
using Typebeat.Web.Auth;
using Typebeat.Web.Data;
using Typebeat.Web.Wire;

namespace Typebeat.Web.Endpoints;

/// <summary>
/// POST /users — in-client account registration (the AccountCreationOverlay flow).
///
/// Wire contract is dictated by the client's <c>RegistrationRequest</c> /
/// <c>APIAccess.CreateAccount</c>:
///  - request is FORM-encoded with fields user[username] / user[user_email] / user[password];
///  - SUCCESS is any 2xx (the client's WebRequest.Perform() does not throw; CreateAccount
///    returns null and the body is ignored) — we return a minimal user object anyway;
///  - FAILURE must be a non-2xx so Perform() throws, and the body must carry a
///    <c>form_error</c> token: <c>{"form_error":{"user":{"username":[...],"user_email":[...],"password":[...]}}}</c>.
///    CreateAccount does <c>JObject.Parse(body).SelectToken("form_error", true).ToObject&lt;RegistrationRequestErrors&gt;()</c>,
///    and ScreenEntry surfaces User.Username / User.Email / User.Password arrays under the
///    matching fields. osu-web uses HTTP 422 for this, which we mirror.
///
/// The other error path the client understands is a top-level <c>{"error":"..."}</c> (or
/// <c>{"url":"..."}</c> redirect); we use the standard {"error":...} envelope for the
/// non-validation rejections (bad User-Agent, rate limit, malformed request).
/// </summary>
public static class RegistrationEndpoints
{
    /// <summary>
    /// The fork's fixed User-Agent (OsuWebRequest.UserAgent == "type!beat"). Gating on it is a
    /// speed bump against drive-by/browser registration only — it is trivially spoofable and is
    /// NOT a security control. Wrong UA -> 403 {"error":...}.
    /// </summary>
    private const string client_user_agent = "type!beat";

    // Permissive superset of osu's username charset: letters, digits, space, and _ - [ ].
    // Deliberately does NOT enforce osu's finer rules (no mixed space/underscore, no
    // leading/trailing space) — kept permissive for M1; tighten later if abuse shows up.
    private static readonly Regex username_charset = new(@"^[A-Za-z0-9 _\-\[\]]+$", RegexOptions.Compiled);

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/users", async (HttpContext ctx, Db db, PasswordService passwords) =>
        {
            // UA gate (speed bump only — see client_user_agent).
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

            var usernameErrors = new List<string>(ValidateUsername(username));
            var emailErrors = new List<string>(ValidateEmail(email));
            var passwordErrors = new List<string>(ValidatePassword(password, username));

            await using var conn = await db.OpenAsync();

            // Uniqueness is checked per-field only when the field is otherwise well-formed, so
            // "is already taken" never stacks on top of a "malformed" error. citext columns make
            // both comparisons case-insensitive.
            if (usernameErrors.Count == 0 &&
                await conn.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM users WHERE username = @username)", new { username }))
                usernameErrors.Add("Username is already taken.");

            if (emailErrors.Count == 0 &&
                await conn.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM users WHERE email = @email)", new { email }))
                emailErrors.Add("Email address is already in use.");

            if (usernameErrors.Count > 0 || emailErrors.Count > 0 || passwordErrors.Count > 0)
                return WireJson.Ok(BuildFormError(usernameErrors, emailErrors, passwordErrors), StatusCodes.Status422UnprocessableEntity);

            long id;
            try
            {
                // verified_at stays NULL (email verification is a later milestone);
                // country_code defaults to 'XX' until GeoIP lands.
                id = await conn.ExecuteScalarAsync<long>(
                    """
                    INSERT INTO users (username, email, password_hash)
                    VALUES (@username, @email, @hash)
                    RETURNING id
                    """,
                    new { username, email, hash = passwords.Hash(password) });
            }
            catch (PostgresException pg) when (pg.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                // Lost a race between the EXISTS check and the INSERT — map the violated
                // constraint back to its field and re-emit as a form_error.
                if (pg.ConstraintName is not null && pg.ConstraintName.Contains("email"))
                    emailErrors.Add("Email address is already in use.");
                else
                    usernameErrors.Add("Username is already taken.");

                return WireJson.Ok(BuildFormError(usernameErrors, emailErrors, passwordErrors), StatusCodes.Status422UnprocessableEntity);
            }

            await conn.ExecuteAsync("INSERT INTO user_stats (user_id) VALUES (@id)", new { id });

            // The client ignores the success body (CreateAccount returns null on any 2xx), but
            // we return a minimal, sane user object rather than an empty 200.
            return WireJson.Ok(new CreatedUser { Id = id, Username = username }, StatusCodes.Status200OK);
        });
    }

    // ---- Pure validation (static + DB-free so RegistrationValidationTest can exercise them) ----

    /// <summary>Username: 3–15 chars, restricted charset. Returns human-readable errors (empty = valid).</summary>
    public static IReadOnlyList<string> ValidateUsername(string username)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(username))
        {
            errors.Add("Username is required.");
            return errors;
        }

        if (username.Length < 3 || username.Length > 15)
            errors.Add("Username must be between 3 and 15 characters.");

        if (!username_charset.IsMatch(username))
            errors.Add("Username may only contain letters, digits, spaces, and the characters _ - [ ].");

        return errors;
    }

    /// <summary>Email: syntactically valid (single @, dotted domain, no spaces). Returns errors (empty = valid).</summary>
    public static IReadOnlyList<string> ValidateEmail(string email)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(email))
        {
            errors.Add("Email address is required.");
            return errors;
        }

        if (!LooksLikeEmail(email))
            errors.Add("Please enter a valid email address.");

        return errors;
    }

    /// <summary>Password: >= 8 chars and not equal (case-insensitively) to the username. Returns errors (empty = valid).</summary>
    public static IReadOnlyList<string> ValidatePassword(string password, string username)
    {
        var errors = new List<string>();

        if (string.IsNullOrEmpty(password))
        {
            errors.Add("Password is required.");
            return errors;
        }

        if (password.Length < 8)
            errors.Add("Password must be at least 8 characters.");

        if (!string.IsNullOrEmpty(username) && string.Equals(password, username, StringComparison.OrdinalIgnoreCase))
            errors.Add("Password must not match your username.");

        return errors;
    }

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

    private static bool LooksLikeEmail(string email)
    {
        if (email.Contains(' '))
            return false;

        int at = email.IndexOf('@');
        if (at <= 0 || at != email.LastIndexOf('@'))
            return false;

        string domain = email[(at + 1)..];
        if (domain.Length == 0 || domain.StartsWith('.') || domain.EndsWith('.') || !domain.Contains('.'))
            return false;

        try
        {
            // MailAddress is lenient; require it to round-trip to the exact input.
            var parsed = new System.Net.Mail.MailAddress(email);
            return parsed.Address == email;
        }
        catch (FormatException)
        {
            return false;
        }
    }

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
    /// the single-node M1 deploy) and unbounded in the number of distinct IPs it tracks — a real
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
