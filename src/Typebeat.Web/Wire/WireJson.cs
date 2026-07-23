using System.Text;
using Newtonsoft.Json;

namespace Typebeat.Web.Wire;

/// <summary>
/// Wire-format conventions for everything the game client consumes.
///
/// The client deserializes with Newtonsoft.Json attribute semantics ([JsonProperty],
/// MemberSerialization.OptIn, EnumMember snake_case, and property-ORDER sensitivity in
/// SoloScoreInfo), so every API response is serialized here with Newtonsoft, never with
/// System.Text.Json. Response models declare explicit [JsonProperty] names and their C#
/// property declaration order is meaningful.
/// </summary>
public static class WireJson
{
    public static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
    {
        // Explicit [JsonProperty] on every serialized member; nothing implicit.
        NullValueHandling = NullValueHandling.Include,
    };

    public static IResult Ok(object payload, int statusCode = StatusCodes.Status200OK)
        => new NewtonsoftJsonResult(payload, statusCode);

    /// <summary>
    /// The osu-web error envelope: <c>{"error":"message"}</c>. Score-submit failure messages
    /// are special-cased by the client by exact string; do not reword them at call sites.
    /// </summary>
    public static IResult Error(int statusCode, string message)
        => new NewtonsoftJsonResult(new { error = message }, statusCode);

    private sealed class NewtonsoftJsonResult(object payload, int statusCode) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = statusCode;
            httpContext.Response.ContentType = "application/json; charset=utf-8";

            string json = JsonConvert.SerializeObject(payload, Settings);
            await httpContext.Response.WriteAsync(json, Encoding.UTF8, httpContext.RequestAborted);
        }
    }
}
