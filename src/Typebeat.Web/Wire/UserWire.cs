using Typebeat.Web.Auth;

namespace Typebeat.Web.Wire;

/// <summary>
/// Shared builder for the user JSON the client's APIUser/APIMe consume (Online/API/Requests/
/// Responses/APIUser.cs). MemberSerialization.OptIn: only [JsonProperty]-named fields are read,
/// and every one absent from the payload falls back to its C# default; so we emit only the
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
    /// The APIUser payload for GET /api/v2/users/{lookup} (GetUserRequest): the profile overlay's
    /// fetch. A superset of <see cref="Me"/> with the header fields the profile page reads
    /// (join_date, cover, last_visit) and real <see cref="ProfileStatistics"/>.
    /// </summary>
    public static object User(UserProfile p, string scheme, string host) => new
    {
        id = p.Id,
        username = p.Username,
        country_code = p.CountryCode,
        avatar_url = AvatarUrl(scheme, host, p.AvatarKey),
        cover_url = "",
        cover = new { url = (string?)null, custom_url = (string?)null, id = (string?)null },
        is_supporter = false,
        is_admin = p.IsAdmin,
        is_bot = false,
        is_active = true,
        is_online = false,
        is_deleted = false,
        pm_friends_only = false,
        join_date = p.JoinedAt.ToString("o"),
        last_visit = p.LastVisit?.ToString("o"),
        playmode = PlayMode,
        // The profile header reads these list fields; absent → null default is a NRE risk in some
        // header components, so emit empty arrays defensively.
        badges = Array.Empty<object>(),
        groups = Array.Empty<object>(),
        statistics = p.Statistics,
    };

    /// <summary>Everything the user endpoint needs to render a full profile payload.
    /// JoinedAt/LastVisit are UTC DateTimes (timestamptz); "o" formatting appends the Z the
    /// client parses as an offset.</summary>
    public sealed record UserProfile(
        long Id,
        string Username,
        string CountryCode,
        string? AvatarKey,
        bool IsAdmin,
        DateTime JoinedAt,
        DateTime? LastVisit,
        object Statistics);

    /// <summary>
    /// A populated UserStatistics (Users/UserStatistics.cs). global_rank/ranked_score come from the
    /// shared cumulative metric; grade_counts only carries ss/s/a (the client DTO has no b/c/d);
    /// hit_accuracy is a 0–100 percentage (the client divides by 100 for display).
    /// </summary>
    public static object ProfileStatistics(
        long? globalRank, long rankedScore, long totalScore, int playCount, long playTimeS,
        double accuracyPercent, int ss, int s, int a) => new
    {
        level = new { current = 1, progress = 0 },
        pp = 0,
        global_rank = globalRank is { } r ? (int?)(int)r : null,
        ranked_score = rankedScore,
        hit_accuracy = accuracyPercent,
        play_count = playCount,
        play_time = (int?)playTimeS,
        total_score = totalScore,
        is_ranked = globalRank is not null,
        grade_counts = new { ss, s, a },
    };

    /// <summary>
    /// Absolute avatar URL: the user's uploaded avatar (<paramref name="avatarKey"/>, a served
    /// store key) when set, else the self-hosted default served by StubEndpoints. Never null and
    /// always absolute; the client's APIUser falls back to a ppy CDN URL for a null/relative
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
