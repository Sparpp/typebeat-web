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

    // Passed plays (scores.passed), against playcount's every submitted play: the set overlay's success
    // rate. Every submission inserts a scores row and bumps playcount, so the two are on the same footing.
    // Set GET only, like the typing stats below; absent elsewhere.
    [JsonProperty("passcount", NullValueHandling = NullValueHandling.Ignore)]
    public int? PassCount { get; init; }

    // ---- Typing stats for the client's set overlay (additive; absent on every route but the set GET). ----
    // Null on any row the pace backfills have not reached, and the key is then omitted entirely.

    [JsonProperty("word_count", NullValueHandling = NullValueHandling.Ignore)]
    public int? WordCount { get; init; }

    [JsonProperty("char_count", NullValueHandling = NullValueHandling.Ignore)]
    public int? CharCount { get; init; }

    // Average words per minute (five keystrokes to the word), the site's "Average WPM".
    [JsonProperty("wpm", NullValueHandling = NullValueHandling.Ignore)]
    public double? Wpm { get; init; }

    [JsonProperty("target_wpm", NullValueHandling = NullValueHandling.Ignore)]
    public double? TargetWpm { get; init; }

    [JsonProperty("peak_wpm", NullValueHandling = NullValueHandling.Ignore)]
    public double? PeakWpm { get; init; }

    // The pace graph's samples, first word to last.
    [JsonProperty("wpm_curve", NullValueHandling = NullValueHandling.Ignore)]
    public float[]? WpmCurve { get; init; }

    [JsonProperty("lyric_font", NullValueHandling = NullValueHandling.Ignore)]
    public string? LyricFont { get; init; }

    [JsonProperty("lyrics", NullValueHandling = NullValueHandling.Ignore)]
    public string? Lyrics { get; init; }

    [JsonProperty("lyrics_original", NullValueHandling = NullValueHandling.Ignore)]
    public string? LyricsOriginal { get; init; }

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
///
/// The set's song language travels as <c>song_language</c>, not under its column's name
/// (<c>beatmapsets.language</c>): the client's APIBeatmapSet already binds <c>language</c> to
/// osu's <c>{id, name}</c> object, so a bare string there would fail to deserialize the whole
/// response. The metadata lookup reads it to fill a map whose .osu predates the Language: key.
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

    /// <summary>
    /// The creator as a compact user (id, username, country, avatar_url), which the client's
    /// APIBeatmapSet reads into <c>Author</c> over the bare creator/user_id pair. Without it the set
    /// overlay's "mapped by" avatar has no avatar_url, and the client never builds one of its own
    /// (it refuses ppy's CDN), so it falls back to the guest picture. Omitted, key and all, on the
    /// routes that do not load it (the beatmap lookup's nested set, profile score rows).
    /// </summary>
    [JsonProperty("user", NullValueHandling = NullValueHandling.Ignore)]
    public ScoreUserWire? User { get; init; }

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

    /// <summary>
    /// The creator's explicit-content marker (beatmapsets.explicit), echoed so the submission
    /// wizard can preselect its toggle on a re-submission. ADDITIVE: clients that predate the
    /// toggle deserialize with Newtonsoft defaults (unknown members ignored) and never see it.
    /// </summary>
    [JsonProperty("explicit")]
    public bool Explicit { get; init; }

    /// <summary>
    /// The set's song language (beatmapsets.language): a canonical BeatmapLanguages name, or
    /// empty when nothing has determined one. Always emitted, never null, so the client can tell
    /// "the server knows none" from an older server that sends no key. See the class remarks for
    /// why it is not called <c>language</c>.
    /// </summary>
    [JsonProperty("song_language")]
    public string SongLanguage { get; init; } = string.Empty;

    /// <summary>
    /// Whether the set's CURRENT online version carries an isolated vocals stem file
    /// (<c>vocals.ogg</c> / <c>vocals.wav</c>, backlog 392/393). Set-level because a stem lives beside
    /// the song, not per difficulty, and every difficulty of a set downloads the same package.
    ///
    /// <para>
    /// The client's UPDATE offer keys on this (backlog 396): a stem-only version cut leaves every
    /// <c>.osu</c> MD5 unchanged, so neither the MD5 path nor the version-time path can see it, and a
    /// client that already holds the audio never re-fetches a version that only added a file. The
    /// client reads this against the vocals stem its LOCAL copy carries
    /// (<c>VocalsStem.FilenameIn</c>): online-has-a-stem AND local-has-none means the local copy is
    /// missing a file the online version offers, so the update must be offered.
    /// </para>
    ///
    /// <para>
    /// Defaults to false: a server that predates this field (or a route that does not compute it)
    /// deserialises to "no stem", which reads as "nothing to offer" rather than manufacturing an
    /// offer for every map. Emitted on both the lookup and the beatmapset GET.
    /// </para>
    /// </summary>
    [JsonProperty("has_vocals_stem")]
    public bool HasVocalsStem { get; init; }

    // The creator's plain-text description (empty when none); the set overlay shows it under the header.
    [JsonProperty("description")]
    public string Description { get; init; } = string.Empty;

    [JsonProperty("download_count")]
    public int DownloadCount { get; init; }

    // False for pre-M3 sets with no uploaded package: the overlay's Download button must not offer a dead download.
    [JsonProperty("has_package")]
    public bool HasPackage { get; init; } = true;

    /// <summary>
    /// osu's availability object, which the client's set header and listing cards read to withhold the download
    /// button (and say why) for a set that cannot be downloaded. Derived from <see cref="HasPackage"/>.
    /// </summary>
    [JsonProperty("availability")]
    public BeatmapsetAvailability Availability => new() { DownloadDisabled = !HasPackage };

    // Null on the nested-inside-a-beatmap variant (lookup), where the key must be OMITTED:
    // the client's APIBeatmapSet.Beatmaps defaults to an empty array and an explicit JSON null
    // would overwrite it with null under Newtonsoft. The set GET emits the real list.
    [JsonProperty("beatmaps", NullValueHandling = NullValueHandling.Ignore)]
    public IReadOnlyList<APIBeatmapResponse>? Beatmaps { get; init; }
}

/// <summary>BeatmapSetOnlineAvailability: whether the set can be downloaded.</summary>
public sealed class BeatmapsetAvailability
{
    [JsonProperty("download_disabled")]
    public bool DownloadDisabled { get; init; }

    [JsonProperty("more_information")]
    public string? MoreInformation { get; init; }
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
