using Newtonsoft.Json;

namespace Typebeat.Web.Wire;

/// <summary>
/// BSS (beatmap submission) request/response DTOs, mirroring osu-server-beatmap-submission's
/// wire contract per the M3 recon (result.bss.endpoint_sequence): the client's
/// PutBeatmapSetRequest body and PutBeatmapSetResponse. Parsed and serialized with Newtonsoft
/// (WireJson) like every other wire shape in this repo.
/// </summary>
public sealed class BssPutRequest
{
    /// <summary>Null = create a brand-new set; non-null = update an existing owned set.</summary>
    [JsonProperty("beatmapset_id")]
    public long? BeatmapsetId { get; init; }

    /// <summary>How many new (blank) beatmap ids to allocate.</summary>
    [JsonProperty("beatmaps_to_create")]
    public int BeatmapsToCreate { get; init; }

    /// <summary>Existing beatmap ids that stay in the set; everything else is dropped.</summary>
    [JsonProperty("beatmaps_to_keep")]
    public long[] BeatmapsToKeep { get; init; } = [];

    // "WIP" | "Pending" on the wire. typebeat has no ranking pipeline — sets are simply
    // hidden until first upload, then public — so the target is accepted and ignored.
    [JsonProperty("target")]
    public string Target { get; init; } = "WIP";

    // Accepted and ignored: there is no discussion system (recon: safely no-op-able).
    [JsonProperty("notify_on_discussion_replies")]
    public bool NotifyOnDiscussionReplies { get; init; }
}

/// <summary>
/// 200 body of PUT /bss/beatmapsets. <see cref="Files"/> MUST be the latest version's manifest
/// and MUST be an empty array for a fresh set — the client branches replace-vs-patch on
/// <c>Files.Count == 0</c> (recon result.bss.endpoint_sequence step 3).
/// </summary>
public sealed class BssPutBeatmapSetResponse
{
    [JsonProperty("beatmapset_id")]
    public required long BeatmapsetId { get; init; }

    /// <summary>The newly allocated beatmap ids (only the created ones, not the kept ones).</summary>
    [JsonProperty("beatmap_ids")]
    public required IReadOnlyList<long> BeatmapIds { get; init; }

    [JsonProperty("files")]
    public required IReadOnlyList<BssFileWire> Files { get; init; }
}

/// <summary>One latest-version manifest entry; the client hash-diffs its export against these.</summary>
public sealed class BssFileWire
{
    [JsonProperty("filename")]
    public required string Filename { get; init; }

    /// <summary>Lowercase hex SHA-256 of the file bytes.</summary>
    [JsonProperty("sha2_hash")]
    public required string Sha2Hash { get; init; }
}
