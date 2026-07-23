using Newtonsoft.Json;

namespace Typebeat.Web.Wire;

/// <summary>
/// Wire DTOs for the three solo-score endpoints. Serialized with Newtonsoft via <see cref="WireJson"/>.
///
/// The shapes mirror the client's deserialisation targets exactly:
///  - the leaderboard scores + <c>user_score.score</c> are <c>SoloScoreInfo</c> (its property-ORDER
///    trap, <c>beatmap</c> must precede <c>beatmapset</c>, is avoided by emitting neither);
///  - the score-submit PUT response is <c>MultiplayerScore</c> (SubmitScoreRequest is
///    <c>APIRequest&lt;MultiplayerScore&gt;</c>);
///  - the leaderboard GET response is <c>APIScoresCollection</c>.
///
/// Every member carries an explicit <see cref="JsonProperty"/> and the C# declaration order matches
/// the client class's order. <c>statistics</c> / <c>maximum_statistics</c> keys are the HitResult
/// EnumMember snake_case names (great/ok/meh/miss…), which the client parses back into
/// <c>Dictionary&lt;HitResult,int&gt;</c>.
/// </summary>
public sealed class ScoreUserWire
{
    // Minimal APIUser shape the leaderboard/results need (APIUser is MemberSerialization.OptIn, so
    // omitted fields default cleanly). Private to this module; reconciled with the me-stubs UserWire
    // at integration.
    [JsonProperty("id")]
    public long Id { get; init; }

    [JsonProperty("username")]
    public string Username { get; init; } = string.Empty;

    [JsonProperty("country_code")]
    public string CountryCode { get; init; } = "XX";

    [JsonProperty("avatar_url")]
    public string? AvatarUrl { get; init; }
}

/// <summary>SoloScoreInfo-shaped row (leaderboard entry and <c>user_score.score</c>).</summary>
public sealed class SoloScoreWire
{
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

    [JsonProperty("ranked")]
    public bool Ranked { get; init; }

    // Emitted last, after all scalar fields; never alongside "beatmap"/"beatmapset".
    [JsonProperty("user")]
    public ScoreUserWire? User { get; init; }
}

/// <summary>MultiplayerScore-shaped response for the score-submit PUT.</summary>
public sealed class MultiplayerScoreWire
{
    [JsonProperty("id")]
    public long Id { get; init; }

    [JsonProperty("user")]
    public ScoreUserWire? User { get; init; }

    [JsonProperty("rank")]
    public string Rank { get; init; } = "D";

    [JsonProperty("total_score")]
    public long TotalScore { get; init; }

    [JsonProperty("accuracy")]
    public double Accuracy { get; init; }

    [JsonProperty("max_combo")]
    public int MaxCombo { get; init; }

    [JsonProperty("mods")]
    public object[] Mods { get; init; } = Array.Empty<object>();

    [JsonProperty("statistics")]
    public IDictionary<string, int> Statistics { get; init; } = new Dictionary<string, int>();

    [JsonProperty("maximum_statistics")]
    public IDictionary<string, int> MaximumStatistics { get; init; } = new Dictionary<string, int>();

    [JsonProperty("passed")]
    public bool Passed { get; init; }

    [JsonProperty("ended_at")]
    public DateTimeOffset EndedAt { get; init; }

    // The leaderboard position of this score, starting at 1 (null when the score is unranked).
    [JsonProperty("position")]
    public int? Position { get; init; }

    [JsonProperty("pp")]
    public double? Pp { get; init; }

    [JsonProperty("has_replay")]
    public bool HasReplay { get; init; }

    [JsonProperty("ranked")]
    public bool Ranked { get; init; }

    [JsonProperty("ruleset_id")]
    public int RulesetId { get; init; }

    [JsonProperty("beatmap_id")]
    public long BeatmapId { get; init; }
}

/// <summary>APIScoreWithPosition: the caller's own score plus its leaderboard position.</summary>
public sealed class ScoreWithPositionWire
{
    [JsonProperty("position")]
    public int? Position { get; init; }

    [JsonProperty("score")]
    public SoloScoreWire Score { get; init; } = default!;
}

/// <summary>APIScoresCollection: the leaderboard GET response.</summary>
public sealed class ScoresCollectionWire
{
    [JsonProperty("score_count")]
    public int ScoreCount { get; init; }

    [JsonProperty("scores")]
    public IReadOnlyList<SoloScoreWire> Scores { get; init; } = Array.Empty<SoloScoreWire>();

    // Null when the caller has no ranked score on this beatmap.
    [JsonProperty("user_score")]
    public ScoreWithPositionWire? UserScore { get; init; }
}
