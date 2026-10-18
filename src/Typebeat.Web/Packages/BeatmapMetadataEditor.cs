using Dapper;
using Newtonsoft.Json;
using Typebeat.Web.Data;

namespace Typebeat.Web.Packages;

/// <summary>Database display metadata only. Never rewrites packages, hashes, gameplay or scores.</summary>
public static class BeatmapMetadataEditor
{
    public enum Result { Saved, NotFound, Invalid, Conflict }

    public static async Task<Result> SaveAsync(Db db, long setId, long actorId, long metadataRevision,
        int packageRevision, string? title, string? artist, string? titleUnicode, string? artistUnicode,
        string? source, string? tags, Dictionary<long, string>? difficultyNames, CancellationToken ct)
    {
        var fields = new { title = title?.Trim() ?? "", artist = artist?.Trim() ?? "",
            titleUnicode = titleUnicode?.Trim() ?? "", artistUnicode = artistUnicode?.Trim() ?? "",
            source = source?.Trim() ?? "", tags = tags?.Trim() ?? "" };
        if (!valid(fields.title, 512, true) || !valid(fields.artist, 512, true)
            || !valid(fields.titleUnicode, 512) || !valid(fields.artistUnicode, 512)
            || !valid(fields.source, 512) || !valid(fields.tags, 4096)
            || difficultyNames is null || difficultyNames.Count == 0
            || difficultyNames.Any(d => !valid(d.Value?.Trim(), 512, true)))
            return Result.Invalid;

        await using var conn = await db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        // Join the upload/stem critical section before reading revisions. A row lock alone
        // would allow an already-running upload to read old metadata before our correction.
        await conn.ExecuteAsync(
            "SELECT pg_advisory_xact_lock(hashtextextended('bss-set-ingest:' || @setId::text, 0))",
            new { setId }, tx);
        var before = await conn.QuerySingleOrDefaultAsync<MetadataRow>(
            """
            SELECT title, artist, title_unicode AS titleUnicode, artist_unicode AS artistUnicode,
                   source, tags, metadata_revision AS metadataRevision, current_version AS packageRevision
            FROM beatmapsets WHERE id=@setId AND status IN ('pending','unranked','ranked','loved') FOR UPDATE
            """, new { setId }, tx);
        if (before is null) return Result.NotFound;
        if (before.MetadataRevision != metadataRevision || before.PackageRevision != packageRevision)
            return Result.Conflict;

        var oldNames = (await conn.QueryAsync<(long Id, string Name)>(
            "SELECT id, version_name FROM beatmaps WHERE set_id=@setId AND filename IS NOT NULL ORDER BY id FOR UPDATE",
            new { setId }, tx)).ToDictionary(d => d.Id, d => d.Name);
        if (!oldNames.Keys.Order().SequenceEqual(difficultyNames.Keys.Order())) return Result.Invalid;
        var names = difficultyNames.ToDictionary(d => d.Key, d => d.Value.Trim());
        bool changed = before.Title != fields.title || before.Artist != fields.artist
            || (before.TitleUnicode ?? "") != fields.titleUnicode || (before.ArtistUnicode ?? "") != fields.artistUnicode
            || before.Source != fields.source || before.Tags != fields.tags || names.Any(d => oldNames[d.Key] != d.Value);
        if (!changed) return Result.Saved;

        await conn.ExecuteAsync(
            """
            UPDATE beatmapsets SET title=@title, artist=@artist, title_unicode=@titleUnicode,
                artist_unicode=@artistUnicode, source=@source, tags=@tags, metadata_revision=metadata_revision+1
            WHERE id=@setId
            """, new { setId, fields.title, fields.artist, fields.titleUnicode, fields.artistUnicode, fields.source, fields.tags }, tx);
        foreach (var name in names)
            await conn.ExecuteAsync("UPDATE beatmaps SET version_name=@name WHERE id=@id AND set_id=@setId",
                new { setId, id = name.Key, name = name.Value }, tx);
        await conn.ExecuteAsync($"UPDATE beatmapsets s SET search = {PackageIngest.SearchVectorSql} FROM users u WHERE s.id=@setId AND u.id=s.owner_id", new { setId }, tx);
        await conn.ExecuteAsync(
            "INSERT INTO moderation_actions(actor_id,set_id,action,note) VALUES(@actorId,@setId,'metadata_edit',@note)",
            new { actorId, setId, note = JsonConvert.SerializeObject(new { before, after = fields, oldDifficultyNames = oldNames, difficultyNames = names }) }, tx);
        await tx.CommitAsync(ct);
        return Result.Saved;
    }

    private static bool valid(string? value, int limit, bool required = false)
        => value is not null && value.Length <= limit && (!required || value.Length > 0) && !value.Any(char.IsControl);

    private sealed record MetadataRow(string Title, string Artist, string? TitleUnicode, string? ArtistUnicode,
        string Source, string Tags, long MetadataRevision, int PackageRevision);
}
