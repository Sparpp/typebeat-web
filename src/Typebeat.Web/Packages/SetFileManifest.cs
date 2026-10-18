using Dapper;
using Typebeat.Web.Data;

namespace Typebeat.Web.Packages;

/// <summary>
/// The set files beyond the <c>.osu</c> difficulties that the client's UPDATE offer tracks, and the one
/// manifest string a set's tracked files are fingerprinted by. The server half of the game's
/// <c>typebeat.Game/Beatmaps/SetFileManifest.cs</c>, which builds the same string from the LOCAL copy's
/// file list; <c>tests/Typebeat.WireCompat</c> holds the two against each other.
///
/// <para>
/// The manifest names each tracked KIND the set's current version carries (a kind may be accepted under
/// several names: a vocals stem is <c>vocals.ogg</c> or <c>vocals.wav</c>), sorted ordinally and joined by
/// commas; an empty string means none. It is published as <c>file_manifest</c> on the two routes that
/// serve an APIBeatmapSet (the metadata lookup and the beatmapset GET), and the client offers UPDATE when
/// the online manifest names a kind its local copy lacks. That is how a version cut that only adds a file
/// (a stem attached by <see cref="StemBackfill"/>, every <c>.osu</c> MD5 unchanged) becomes visible.
/// </para>
///
/// <para>
/// A new tracked kind is one more row in <c>kinds</c> here and in the game's copy, and needs no new
/// wire field and no client schema change. <c>has_vocals_stem</c> stays on the wire for clients that
/// predate the manifest.
/// </para>
/// </summary>
public static class SetFileManifest
{
    /// <summary>The kind name of an isolated vocals stem (the game's <c>VocalsStem.BASE_NAME</c>).</summary>
    public const string VocalsStem = "vocals";

    // Every tracked kind, with the file names (root of the set, case-insensitive) that count as it.
    private static readonly (string Kind, string[] Filenames)[] kinds =
    [
        (VocalsStem, StemBackfill.StemFilenames),
    ];

    /// <summary>The manifest of a version carrying the files named <paramref name="filenames"/>.</summary>
    public static string Of(IEnumerable<string> filenames)
    {
        var names = filenames.ToArray();

        return string.Join(',', kinds
            .Where(k => names.Any(n => k.Filenames.Contains(n, StringComparer.OrdinalIgnoreCase)))
            .Select(k => k.Kind)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(k => k, StringComparer.Ordinal));
    }

    /// <summary>Whether <paramref name="manifest"/> names <paramref name="kind"/>.</summary>
    public static bool Names(string manifest, string kind)
        => manifest.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains(kind, StringComparer.Ordinal);

    /// <summary>
    /// The manifest of <paramref name="setId"/>'s CURRENT version, read off its <c>version_files</c>. A set
    /// with no current version carries no tracked file.
    /// </summary>
    public static async Task<string> CurrentVersionAsync(Db db, long setId, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);

        var filenames = await conn.QueryAsync<string>(
            """
            SELECT vf.filename
            FROM beatmapsets s
            JOIN set_versions sv ON sv.set_id = s.id AND sv.version_no = s.current_version
            JOIN version_files vf ON vf.version_id = sv.id
            WHERE s.id = @setId
            """,
            new { setId });

        return Of(filenames);
    }
}
