using Newtonsoft.Json;

namespace Typebeat.Web.Wire;

/// <summary>
/// APIBeatmap / APIBeatmapSet-shaped response DTOs, mirroring the client's
/// typebeat.Game.Online.API.Requests.Responses.APIBeatmap and APIBeatmapSet.
///
/// The client deserializes these with Newtonsoft default settings (OsuJsonWebRequest), so
/// only the [JsonProperty] names are load-bearing — APIBeatmap/APIBeatmapSet are plain
/// classes (no MemberSerialization.OptIn, no property-order sensitivity). We still declare a
/// [JsonProperty] on every member so nothing leaks under an implicit name.
///
/// Status is emitted as the string "ranked": BeatmapOnlineStatus carries no [EnumMember]
/// values, so Newtonsoft matches enum members by NAME (case-insensitively) — "ranked" binds to
/// BeatmapOnlineStatus.Ranked (=1). A ranked-family status is REQUIRED for anything to work:
///  - MatchesOnlineVersion must hold (checksum echoed back == local MD5) before the client
///    stamps beatmapInfo.Status = res.Status (BeatmapUpdaterMetadataLookup), and
///  - leaderboards only fetch when Beatmap.OnlineID > 0 AND Status > Pending
///    (LeaderboardManager). Locally-imported public maps therefore report "ranked".
/// </summary>
public sealed class APIBeatmapResponse
{
    [JsonProperty("id")]
    public required int Id { get; init; }

    [JsonProperty("beatmapset_id")]
    public required int BeatmapsetId { get; init; }

    // RulesetID on the client is read from mode_int (there is no "mode" property on APIBeatmap).
    [JsonProperty("mode_int")]
    public required int ModeInt { get; init; }

    [JsonProperty("status")]
    public required string Status { get; init; }

    // APIBeatmap.MD5Hash == Checksum; the identity the client compares against its local hash.
    [JsonProperty("checksum")]
    public required string Checksum { get; init; }

    [JsonProperty("user_id")]
    public required int UserId { get; init; }

    [JsonProperty("difficulty_rating")]
    public required double DifficultyRating { get; init; }

    // total_length / hit_length are consumed as SECONDS (the client converts to ms on read).
    [JsonProperty("total_length")]
    public required double TotalLength { get; init; }

    [JsonProperty("hit_length")]
    public required double HitLength { get; init; }

    [JsonProperty("version")]
    public required string Version { get; init; }

    [JsonProperty("last_updated")]
    public required DateTimeOffset LastUpdated { get; init; }

    [JsonProperty("beatmapset")]
    public required APIBeatmapSetResponse Beatmapset { get; init; }
}

/// <summary>
/// The nested beatmapset object. The import-time lookup (APIBeatmapMetadataSource) reads
/// Status, Ranked (ranked_date) and Submitted (submitted_date) off this; the rest is UI-facing.
/// Covers is a value-type struct client-side (never null), but osu-web always sends the object,
/// so we emit it with placeholder URLs.
/// </summary>
public sealed class APIBeatmapSetResponse
{
    [JsonProperty("id")]
    public required int Id { get; init; }

    [JsonProperty("title")]
    public required string Title { get; init; }

    [JsonProperty("artist")]
    public required string Artist { get; init; }

    [JsonProperty("status")]
    public required string Status { get; init; }

    // AuthorString on the client; AuthorID is user_id.
    [JsonProperty("creator")]
    public required string Creator { get; init; }

    [JsonProperty("user_id")]
    public required int UserId { get; init; }

    [JsonProperty("covers")]
    public required BeatmapCovers Covers { get; init; }

    [JsonProperty("submitted_date")]
    public required DateTimeOffset SubmittedDate { get; init; }

    [JsonProperty("ranked_date")]
    public required DateTimeOffset? RankedDate { get; init; }

    [JsonProperty("last_updated")]
    public required DateTimeOffset LastUpdated { get; init; }
}

/// <summary>
/// BeatmapSetOnlineCovers shape. The client only actually binds the "@2x" variants by name;
/// the remaining keys are emitted to match osu-web's contract. All point at a self-hosted
/// placeholder so no request ever leaves for ppy CDN hosts.
/// </summary>
public sealed class BeatmapCovers
{
    [JsonProperty("cover")]
    public required string Cover { get; init; }

    [JsonProperty("cover@2x")]
    public required string Cover2x { get; init; }

    [JsonProperty("card")]
    public required string Card { get; init; }

    [JsonProperty("card@2x")]
    public required string Card2x { get; init; }

    [JsonProperty("list")]
    public required string List { get; init; }

    [JsonProperty("list@2x")]
    public required string List2x { get; init; }

    /// <summary>Builds a covers block whose every field points at the same placeholder image.</summary>
    public static BeatmapCovers Placeholder(string url) => new()
    {
        Cover = url,
        Cover2x = url,
        Card = url,
        Card2x = url,
        List = url,
        List2x = url,
    };
}
