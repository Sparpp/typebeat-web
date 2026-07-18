using Typebeat.Web.Auth;

namespace Typebeat.Web.Wire;

/// <summary>
/// Shared builder for the user JSON the client's APIUser/APIMe consume (Online/API/Requests/
/// Responses/APIUser.cs). MemberSerialization.OptIn: only [JsonProperty]-named fields are read,
/// and every one absent from the payload falls back to its C# default — so we emit only the
/// handful the login/profile paths actually dereference. Deliberately OMITTED:
/// <c>session_verification_method</c> (its absence leaves APIMe.SessionVerificationMethod null,
/// which keeps APIAccess on the Online path instead of RequiresSecondFactorAuth).
/// </summary>
public static class UserWire
{
    /// <summary>The single ruleset short name; matches the client's typebeat ruleset ShortName.</summary>
    public const string PlayMode = "typebeat";

    /// <summary>
    /// The APIMe payload for GET /api/v2/me/. <paramref name="scheme"/>/<paramref name="host"/>
    /// come from the incoming request so the avatar URL is served back from this same host.
    /// </summary>
    public static object Me(AuthedUser user, string scheme, string host) => new
    {
        id = user.Id,
        username = user.Username,
        country_code = user.CountryCode,
        avatar_url = AvatarUrl(scheme, host, user.AvatarKey),
        is_supporter = false,
        is_admin = user.IsAdmin,
        is_bot = false,
        is_active = true,
        playmode = PlayMode,
        statistics = ZeroedStatistics(),
        // APIMe reads this (defaults to empty string); no score-processing pipeline in M1.
        score_processing_notice_url = "",
    };

    /// <summary>
    /// Absolute avatar URL: the user's uploaded avatar (<paramref name="avatarKey"/>, a served
    /// store key) when set, else the self-hosted default served by StubEndpoints. Never null and
    /// always absolute — the client's APIUser falls back to a ppy CDN URL for a null/relative
    /// avatar_url, so every payload emits a full URL on THIS host.
    /// </summary>
    public static string AvatarUrl(string scheme, string host, string? avatarKey = null)
        => avatarKey is null
            ? $"{scheme}://{host}/img/default-avatar.png"
            : $"{scheme}://{host}/{avatarKey}";

    /// <summary>
    /// A zeroed UserStatistics (Users/UserStatistics.cs). global_rank stays null so the client
    /// renders the local user as unranked rather than rank #0.
    /// </summary>
    public static object ZeroedStatistics() => new
    {
        level = new { current = 1, progress = 0 },
        pp = 0,
        global_rank = (int?)null,
        ranked_score = 0L,
        hit_accuracy = 0.0,
        play_count = 0,
        total_score = 0L,
        is_ranked = false,
    };
}
