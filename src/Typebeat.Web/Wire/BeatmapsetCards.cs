using Dapper;
using Npgsql;
using Typebeat.Web.Endpoints;

namespace Typebeat.Web.Wire;

/// <summary>
/// The APIBeatmapSet "card" payload (a set plus its live difficulties, no typing stats) for every
/// client surface that lists sets rather than showing one: the beatmap listing overlay's search
/// (<see cref="BeatmapsetSearchEndpoints"/>) and the profile's Beatmaps section
/// (<see cref="ProfileBeatmapsetEndpoints"/>). One builder, so a set reads the same in both.
/// </summary>
public static class BeatmapsetCards
{
    /// <summary>
    /// Loads the cards for the sets <paramref name="fromWhereOrder"/> selects, in its order.
    /// </summary>
    /// <param name="fromWhereOrder">
    /// Everything after the SELECT list: a FROM that aliases the set as <c>s</c> and its owner as
    /// <c>u</c>, then any WHERE, ORDER BY and LIMIT/OFFSET. Built only from C# literals.
    /// </param>
    /// <param name="param">The query's parameters; must bind <c>@viewerId</c> (0 when anonymous) for has_favourited.</param>
    public static async Task<List<APIBeatmapSetResponse>> LoadAsync(
        NpgsqlConnection conn, string fromWhereOrder, object param, string urlBase, CancellationToken ct = default)
    {
        var rows = (await conn.QueryAsync<SetRow>(new CommandDefinition(
            $"""
             SELECT s.id AS id, s.owner_id AS ownerId, s.title AS title, s.title_unicode AS titleUnicode,
                    s.artist AS artist, s.artist_unicode AS artistUnicode, s.source AS source, s.tags AS tags,
                    s.status AS status, s.explicit AS explicit, s.has_video AS hasVideo, s.cover_key AS coverKey,
                    s.preview_key AS previewKey, s.bpm AS bpm, s.play_count AS playCount,
                    s.favourite_count AS favouriteCount, s.submitted_at AS submittedAt, s.updated_at AS updatedAt,
                    u.username::text AS creator, s.language AS language,
                    EXISTS (SELECT 1 FROM favourites f WHERE f.set_id = s.id AND f.user_id = @viewerId) AS hasFavourited,
                    EXISTS (SELECT 1 FROM set_versions v WHERE v.set_id = s.id AND v.package_key IS NOT NULL) AS hasPackage
             {fromWhereOrder}
             """, param, cancellationToken: ct))).ToList();

        var beatmaps = rows.Count == 0
            ? new Dictionary<long, List<BeatmapRow>>()
            : (await conn.QueryAsync<BeatmapRow>(new CommandDefinition(
                """
                SELECT set_id AS setId, id AS id, ruleset_id AS rulesetId, checksum_md5 AS checksum,
                       total_length_s AS totalLengthSeconds, drain_length_s AS drainLengthSeconds,
                       difficulty_rating AS difficultyRating, version_name AS version, play_count AS playCount
                FROM beatmaps
                WHERE set_id = ANY(@ids) AND filename IS NOT NULL
                ORDER BY difficulty_rating, id
                """, new { ids = rows.Select(r => r.Id).ToArray() }, cancellationToken: ct)))
                .GroupBy(b => b.SetId).ToDictionary(g => g.Key, g => g.ToList());

        return rows.Select(set =>
        {
            string status = BeatmapsetEndpoints.StatusString(set.Status);
            return new APIBeatmapSetResponse
            {
                Id = (int)set.Id,
                Title = set.Title,
                Artist = set.Artist,
                Status = status,
                Creator = set.Creator,
                UserId = (int)set.OwnerId,
                Covers = BeatmapCovers.FromCoverKey(urlBase, set.CoverKey),
                SubmittedDate = set.SubmittedAt,
                RankedDate = set.Status == "ranked" ? set.UpdatedAt : null,
                LastUpdated = set.UpdatedAt,
                TitleUnicode = set.TitleUnicode,
                ArtistUnicode = set.ArtistUnicode,
                Source = set.Source,
                Tags = set.Tags,
                PreviewUrl = set.PreviewKey is { } previewKey ? $"{urlBase}/{previewKey}" : string.Empty,
                HasFavourited = set.HasFavourited,
                PlayCount = set.PlayCount,
                FavouriteCount = set.FavouriteCount,
                Bpm = set.Bpm is { } bpm ? (double)bpm : 0,
                HasVideo = set.HasVideo,
                Explicit = set.Explicit,
                SongLanguage = set.Language,
                HasPackage = set.HasPackage,
                Beatmaps = (beatmaps.GetValueOrDefault(set.Id) ?? []).Select(b => new APIBeatmapResponse
                {
                    Id = (int)b.Id,
                    BeatmapsetId = (int)set.Id,
                    ModeInt = b.RulesetId,
                    Status = status,
                    Checksum = b.Checksum,
                    UserId = (int)set.OwnerId,
                    DifficultyRating = b.DifficultyRating,
                    TotalLength = b.TotalLengthSeconds,
                    HitLength = b.DrainLengthSeconds,
                    Version = b.Version,
                    LastUpdated = set.UpdatedAt,
                    PlayCount = b.PlayCount,
                }).ToList(),
            };
        }).ToList();
    }

    private sealed record SetRow(
        long Id,
        long OwnerId,
        string Title,
        string TitleUnicode,
        string Artist,
        string ArtistUnicode,
        string Source,
        string Tags,
        string Status,
        bool Explicit,
        bool HasVideo,
        string? CoverKey,
        string? PreviewKey,
        decimal? Bpm,
        int PlayCount,
        int FavouriteCount,
        DateTime SubmittedAt,
        DateTime UpdatedAt,
        string Creator,
        string Language,
        bool HasFavourited,
        bool HasPackage);

    private sealed record BeatmapRow(
        long SetId,
        long Id,
        short RulesetId,
        string Checksum,
        double TotalLengthSeconds,
        double DrainLengthSeconds,
        double DifficultyRating,
        string Version,
        int PlayCount);
}
