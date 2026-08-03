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
    /// <paramref name="statistics"/> is the SAME <see cref="ProfileStatistics"/> object the profile
    /// fetch serves (built by <see cref="UserStatisticsWire.ForUserAsync"/>), not a zeroed stand-in:
    /// the login response lands in the client's <c>api.LocalUser</c> and is read straight back out
    /// by anything holding an APIUser for the local player, so a placeholder there is simply a
    /// wrong number waiting to be printed.
    /// </summary>
    public static object Me(AuthedUser user, string scheme, string host, object statistics) => new
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
        statistics,
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
    /// A populated UserStatistics (Users/UserStatistics.cs). grade_counts only carries ss/s/a (the
    /// client DTO has no b/c/d); hit_accuracy is a 0–100 percentage (the client divides by 100 for
    /// display).
    ///
    /// <para>
    /// <c>global_rank</c> IS THE pp RANK (<see cref="Scoring.PpRanking"/>), not the cumulative-score
    /// rank. The client has exactly one rank slot and every surface that reads it pairs it with pp:
    /// the profile header prints "Global Ranking" beside a "pp" value, the results screen's Overall
    /// Ranking panel puts the rank change next to the pp change, and the toolbar shows the two
    /// deltas side by side. Filling that slot with the score rank would make a play read "+37pp,
    /// rank unchanged" (or worse, moved for an unrelated reason). It also matches osu, where this
    /// field has always been the pp rank, and the website, which has led with pp since task 61. The
    /// cumulative-score metric is NOT lost from the client: its VALUE still ships as
    /// <c>ranked_score</c> (and the results screen has a row for it); only its rank has no slot,
    /// and that board lives on the website.
    /// </para>
    ///
    /// <para>
    /// <c>pp</c> is ALWAYS a number, 0 for a player with no pp-earning play, because a weighted sum
    /// over no plays genuinely is 0; it is not unknown. Null would be actively harmful here: the
    /// toolbar's delta display falls back to <c>Before.PP ?? After.PP</c>, so a null "before" would
    /// render a player's FIRST pp ever as a gain of nothing. <c>global_rank</c> stays null when
    /// unranked, which is the client's existing convention for "no rank" (a dash in
    /// GlobalRankDisplay, a hidden counter in the toolbar), and <c>is_ranked</c> tracks it so the
    /// payload cannot claim to be ranked and rankless at once.
    /// </para>
    /// </summary>
    public static object ProfileStatistics(
        double totalPp, long? ppRank, long rankedScore, long totalScore, int playCount, long playTimeS,
        double accuracyPercent, int ss, int s, int a) => new
    {
        level = new { current = 1, progress = 0 },
        // 2dp, as osu serves it: the client rounds to whole pp for display, and pinning the wire to
        // a stable number keeps the login and profile payloads byte-identical for the same user.
        pp = Math.Round(totalPp, 2, MidpointRounding.AwayFromZero),
        global_rank = ppRank is { } r ? (int?)(int)r : null,
        ranked_score = rankedScore,
        hit_accuracy = accuracyPercent,
        play_count = playCount,
        play_time = (int?)playTimeS,
        total_score = totalScore,
        is_ranked = ppRank is not null,
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
}
