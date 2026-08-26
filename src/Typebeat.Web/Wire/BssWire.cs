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

    // "WIP" | "Pending" | "Unranked" on the wire. WIP/Pending both publish to 'pending' (the map
    // still gets reviewed for ranking); "Unranked" is the creator opting out of ranking entirely,
    // publishing to 'unranked' (browsable + playable, never leaderboard-eligible). See BssEndpoints.
    [JsonProperty("target")]
    public string Target { get; init; } = "WIP";

    /// <summary>
    /// The creator's explicit-content marker, chosen in the same wizard step as
    /// <see cref="Target"/>. OPTIONAL: absent (every pre-toggle client) deserializes to false,
    /// which is the "not explicit" answer. Stored on beatmapsets.explicit and re-applied on
    /// every submission, so the toggle can be flipped either way by re-submitting.
    /// </summary>
    [JsonProperty("explicit")]
    public bool Explicit { get; init; }

    // Accepted and ignored: there is no discussion system (recon: safely no-op-able).
    [JsonProperty("notify_on_discussion_replies")]
    public bool NotifyOnDiscussionReplies { get; init; }
}

/// <summary>
/// 200 body of PUT /bss/beatmapsets. <see cref="Files"/> MUST be the latest version's manifest
/// and MUST be an empty array for a fresh set; the client branches replace-vs-patch on
/// <c>Files.Count == 0</c> (recon result.bss.endpoint_sequence step 3).
/// </summary>
public sealed class BssPutBeatmapSetResponse
{
    [JsonProperty("beatmapset_id")]
    public required long BeatmapsetId { get; init; }

    /// <summary>
    /// ALL beatmap ids in the submitted set: the newly created ones followed by the kept ones
    /// (osu-server-beatmap-submission appends <c>beatmaps_to_keep</c> before returning). The
    /// client's exporter resolves every kept diff's online id by membership in this list and
    /// assigns the leftovers to new diffs, so kept ids MUST be present.
    /// </summary>
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

/// <summary>
/// Body of POST /bss/beatmapsets/{id}/upload-sessions: the client declaring the payload it is
/// about to send in chunks. The payload is the VERBATIM multipart body it would otherwise have
/// PUT/PATCHed in one request, so <see cref="ContentType"/> is that body's own Content-Type
/// header (boundary included) and <see cref="Sha256"/> covers the whole of it.
/// </summary>
public sealed class BssUploadSessionRequest
{
    /// <summary>"full" (the PUT route's body) or "patch" (the PATCH route's body).</summary>
    [JsonProperty("kind")]
    public string? Kind { get; init; }

    [JsonProperty("content_type")]
    public string? ContentType { get; init; }

    [JsonProperty("total_bytes")]
    public long TotalBytes { get; init; }

    /// <summary>Hex SHA-256 of the whole payload, verified server-side at complete.</summary>
    [JsonProperty("sha256")]
    public string? Sha256 { get; init; }
}

/// <summary>
/// 200 body of the session create and status routes. <see cref="Received"/> is what the server
/// already holds, so a client resuming an interrupted upload sends only the gaps.
/// </summary>
public sealed class BssUploadSessionResponse
{
    [JsonProperty("session_id")]
    public required string SessionId { get; init; }

    [JsonProperty("chunk_bytes")]
    public required int ChunkBytes { get; init; }

    [JsonProperty("total_chunks")]
    public required int TotalChunks { get; init; }

    /// <summary>Stored chunk indexes, ascending.</summary>
    [JsonProperty("received")]
    public required IReadOnlyList<int> Received { get; init; }

    /// <summary>ISO 8601 UTC instant after which the session is swept.</summary>
    [JsonProperty("expires_at")]
    public required string ExpiresAt { get; init; }
}
