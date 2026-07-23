using Newtonsoft.Json;

namespace Typebeat.Web.Wire;

/// <summary>
/// APIBeatmap / APIBeatmapSet-shaped response DTOs, mirroring the client's
/// typebeat.Game.Online.API.Requests.Responses.APIBeatmap and APIBeatmapSet.
///
/// The client deserializes these with Newtonsoft default settings (OsuJsonWebRequest), so
/// only the [JsonProperty] names are load-bearing; APIBeatmap/APIBeatmapSet are plain
/// classes (no MemberSerialization.OptIn, no property-order sensitivity). We still declare a
/// [JsonProperty] on every member so nothing leaks under an implicit name.
///
/// Status is emitted as the string "ranked": BeatmapOnlineStatus carries no [EnumMember]
/// values, so Newtonsoft matches enum members by NAME (case-insensitively); "ranked" binds to
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

    // playcount (no underscore; osu-web's historical name, mirrored by APIBeatmap.PlayCount).
    [JsonProperty("playcount")]
    public int PlayCount { get; init; }

    // Nullable: beatmaps nested inside a beatmapset response omit the back-reference, KEY AND
    // ALL (osu-web's shape; NullValueHandling.Ignore overrides the settings-level Include so
    // null never reaches the wire). The lookup endpoint always sets it.
    [JsonProperty("beatmapset", NullValueHandling = NullValueHandling.Ignore)]
    public APIBeatmapSetResponse? Beatmapset { get; init; }
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

    // ---- Fields below were added for GET /api/v2/beatmapsets/{id} (M3). They are optional
    //      with client-default values so the import-lookup path above keeps emitting them
    //      harmlessly (APIBeatmapSet binds by name; absent/default values are inert). ----

    [JsonProperty("title_unicode")]
    public string TitleUnicode { get; init; } = string.Empty;

    [JsonProperty("artist_unicode")]
    public string ArtistUnicode { get; init; } = string.Empty;

    [JsonProperty("source")]
    public string Source { get; init; } = string.Empty;

    [JsonProperty("tags")]
    public string Tags { get; init; } = string.Empty;

    // Empty string (never null) when no preview exists: the client's APIBeatmapSet.Preview
    // defaults to string.Empty and its consumers use IsNullOrEmpty-style checks.
    [JsonProperty("preview_url")]
    public string PreviewUrl { get; init; } = string.Empty;

    [JsonProperty("has_favourited")]
    public bool HasFavourited { get; init; }

    [JsonProperty("play_count")]
    public int PlayCount { get; init; }

    [JsonProperty("favourite_count")]
    public int FavouriteCount { get; init; }

    // The client's APIBeatmapSet.BPM is a plain double; 0 = unknown (fresh set, no package yet).
    [JsonProperty("bpm")]
    public double Bpm { get; init; }

    [JsonProperty("video")]
    public bool HasVideo { get; init; }

    // Null on the nested-inside-a-beatmap variant (lookup), where the key must be OMITTED:
    // the client's APIBeatmapSet.Beatmaps defaults to an empty array and an explicit JSON null
    // would overwrite it with null under Newtonsoft. The set GET emits the real list.
    [JsonProperty("beatmaps", NullValueHandling = NullValueHandling.Ignore)]
    public IReadOnlyList<APIBeatmapResponse>? Beatmaps { get; init; }
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

    /// <summary>The self-hosted fallback image (served by MediaEndpoints, generated in-process).</summary>
    public const string DefaultCoverPath = "/img/default-cover.jpg";

    /// <summary>
    /// Builds the covers block for a set. <paramref name="coverKey"/> is the PREFIX stored in
    /// <c>beatmapsets.cover_key</c> (<c>covers/{setId}/{versionNo}</c>); each variant appends
    /// <c>/{name}.jpg</c>, matching the objects CoverGenerator wrote and the MediaEndpoints
    /// route that serves them. Null (no cover generated) falls back to the placeholder.
    /// </summary>
    public static BeatmapCovers FromCoverKey(string urlBase, string? coverKey)
    {
        if (string.IsNullOrEmpty(coverKey))
            return Placeholder(urlBase + DefaultCoverPath);

        return new BeatmapCovers
        {
            Cover = $"{urlBase}/{coverKey}/cover.jpg",
            Cover2x = $"{urlBase}/{coverKey}/cover@2x.jpg",
            Card = $"{urlBase}/{coverKey}/card.jpg",
            Card2x = $"{urlBase}/{coverKey}/card@2x.jpg",
            List = $"{urlBase}/{coverKey}/list.jpg",
            List2x = $"{urlBase}/{coverKey}/list@2x.jpg",
        };
    }
}
