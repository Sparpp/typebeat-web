using Newtonsoft.Json;

namespace Typebeat.Web.Wire;

/// <summary>
/// Wire DTOs for the game client's PROFILE SECTIONS (Overlays/Profile/Sections), served by
/// <c>Endpoints/ProfileScoreEndpoints</c>. Serialized with Newtonsoft via <see cref="WireJson"/>,
/// like every other client-facing payload.
///
/// <para>
/// Two shapes, both list elements: <see cref="ProfileScoreWire"/> is the client's
/// <c>SoloScoreInfo</c> (Ranks and Historical's score subsections), and
/// <see cref="MostPlayedBeatmapWire"/> is its <c>APIUserMostPlayedBeatmap</c>.
/// </para>
/// </summary>
public sealed class ProfileScoreWire
{
    // ---- Declaration order mirrors SoloScoreInfo's own, per the WireJson convention. ----

    [JsonProperty("id")]
    public long Id { get; init; }

    [JsonProperty("beatmap_id")]
    public long BeatmapId { get; init; }

    [JsonProperty("ruleset_id")]
    public int RulesetId { get; init; }

    [JsonProperty("passed")]
    public bool Passed { get; init; }

    [JsonProperty("total_score")]
    public long TotalScore { get; init; }

    [JsonProperty("accuracy")]
    public double Accuracy { get; init; }

    [JsonProperty("user_id")]
    public long UserId { get; init; }

    [JsonProperty("max_combo")]
    public int MaxCombo { get; init; }

    [JsonProperty("rank")]
    public string Rank { get; init; } = "D";

    [JsonProperty("ended_at")]
    public DateTimeOffset EndedAt { get; init; }

    [JsonProperty("mods")]
    public object[] Mods { get; init; } = Array.Empty<object>();

    [JsonProperty("statistics")]
    public IDictionary<string, int> Statistics { get; init; } = new Dictionary<string, int>();

    [JsonProperty("maximum_statistics")]
    public IDictionary<string, int> MaximumStatistics { get; init; } = new Dictionary<string, int>();

    /// <summary>
    /// The score's own user. Nothing the profile sections RENDER reads it (every row on a profile
    /// belongs to the profile's owner, who is already named above the list), but
    /// <c>SoloScoreInfo</c> exposes it through <c>IScoreInfo.User</c> with a null-forgiving
    /// dereference, so it is sent rather than left as a trap for the next consumer.
    /// </summary>
    [JsonProperty("user")]
    public ScoreUserWire? User { get; init; }

    /// <summary>
    /// The map this score is on, WITH its set nested inside it. Required, not decorative: the
    /// client's <c>DrawableProfileScore</c> does <c>Score.Beatmap.AsNonNull()</c> for the title
    /// line and reads <c>Score.Beatmap.Status</c> to decide whether a pp figure may be shown at
    /// all, so a row without it is a crash rather than a blank.
    ///
    /// <para>
    /// The set is nested INSIDE the beatmap deliberately, instead of being sent as a sibling
    /// <c>beatmapset</c> key. <c>SoloScoreInfo.BeatmapSet</c> is a set-only property that THROWS
    /// ("Beatmap set metadata arrived before beatmap metadata") when the sibling key is
    /// deserialized before <c>beatmap</c>, which makes the sibling form quietly order-dependent.
    /// Nesting has no such ordering rule, and <c>APIBeatmap.BeatmapSet</c> binds it directly.
    /// </para>
    /// </summary>
    [JsonProperty("beatmap")]
    public APIBeatmapResponse? Beatmap { get; init; }

    /// <summary>
    /// Performance points for this play, or NULL when it earns none.
    ///
    /// <para>
    /// Null is not "unknown" here, it is "no pp", and the client renders it as a dash with a "no
    /// pp" tooltip precisely because <see cref="Processed"/> is true beside it. The rule is
    /// <see cref="Scoring.PpRanking"/>'s, not a second opinion: that board counts a play only when
    /// its stored pp is above zero, because a custom-rate play is pp-ineligible and a not-yet-priced
    /// row is stored 0, so both mean the same thing. Sending the stored 0 through as a number would
    /// print a confident "0pp" beside a play the pp board never looked at.
    /// </para>
    /// </summary>
    [JsonProperty("pp")]
    public double? Pp { get; init; }

    [JsonProperty("has_replay")]
    public bool HasReplay { get; init; }

    [JsonProperty("ranked")]
    public bool Ranked { get; init; }

    /// <summary>
    /// osu's "this score is kept forever" flag. Always true here: this server prunes nothing, so
    /// every row it serves is preserved by definition. It is on the wire because the client gates
    /// its pp display on it (<c>!Score.Preserve</c> renders a dash), and the field defaults to
    /// false, so omitting it would suppress pp on every profile row.
    /// </summary>
    [JsonProperty("preserve")]
    public bool Preserve { get; init; }

    /// <summary>
    /// Whether the scoring pipeline has finished with this play. Always true here: pp is priced
    /// inside the submission transaction, so a stored row is never mid-flight. Sending false would
    /// make the client render a spinner beside every pp-less play, promising a number that is never
    /// coming.
    /// </summary>
    [JsonProperty("processed")]
    public bool Processed { get; init; }
}

/// <summary>
/// APIUserMostPlayedBeatmap: one row of the profile's "Most played" subsection.
///
/// <para>
/// Both <see cref="Beatmap"/> and <see cref="BeatmapSet"/> are required, and here the set really
/// does have to be a SIBLING rather than nested: the client's <c>BeatmapInfo</c> getter assigns
/// <c>beatmap.BeatmapSet = BeatmapSet</c> every time it is read, so a payload that only nested the
/// set would have it overwritten with null on first access. (Unlike <c>SoloScoreInfo</c> this is a
/// plain assignment in a getter, not an order-sensitive setter, so no ordering rule applies.)
/// </para>
/// </summary>
public sealed class MostPlayedBeatmapWire
{
    [JsonProperty("beatmap_id")]
    public long BeatmapId { get; init; }

    /// <summary>How many times this user has played the map; every play counts, passed or not.</summary>
    [JsonProperty("count")]
    public long PlayCount { get; init; }

    [JsonProperty("beatmap")]
    public APIBeatmapResponse? Beatmap { get; init; }

    [JsonProperty("beatmapset")]
    public APIBeatmapSetResponse? BeatmapSet { get; init; }
}

/// <summary>
/// APIUserHistoryCount: one month of a profile history graph (<c>monthly_playcounts</c>,
/// <c>replays_watched_counts</c> on the user payload). <c>start_date</c> binds to a
/// <c>DateTime</c> client-side, so it is sent as a plain date with no offset.
/// </summary>
public sealed class UserHistoryCountWire
{
    [JsonProperty("start_date")]
    public string StartDate { get; init; } = string.Empty;

    [JsonProperty("count")]
    public long Count { get; init; }
}
