// typebeat-web seed tool.
//
// Takes a lyriclab-produced .osz (a zip of one or more "type!beat file format v1" .osu files
// plus audio and an optional background), registers the set + its beatmaps in the typebeat-web
// database, injects the assigned online IDs into each .osu's [Metadata] section, hashes the
// FINAL injected bytes, and writes a finalized .osz. Importing that .osz in the game client
// yields beatmaps whose MD5Hash equals the DB row's checksum_md5 — the beatmap_hash identity
// contract that score submission validates against.
//
// The client computes each beatmap's MD5Hash over the exact .osu file bytes on import
// (BeatmapImporter.createBeatmapDifficulties -> memoryStream.ComputeMD5Hash(), lowercase hex),
// and reads OnlineID from the [Metadata] "BeatmapID"/"BeatmapSetID" lines
// (LegacyBeatmapDecoder). So the hash MUST be computed over the bytes AFTER id injection, and
// those exact bytes must land in the finalized .osz.
//
// Usage:
//   dotnet run --project tools/seed -- <path-to.osz> [--out <dir>] [--owner <userId>] [--db <conn>]
//
//   --out    directory for the finalized .osz (default: alongside the input as <name>.online.osz)
//   --owner  owning user id (default 1)
//   --db     Npgsql connection string (default env TYPEBEAT_DB, else the standard localhost string)

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Npgsql;

try
{
    return await Seed.RunAsync(args);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"seed: {ex.Message}");
    return 1;
}

/// <summary>The seed command, kept in one place so top-level statements stay a thin entry point.</summary>
static class Seed
{
    public static async Task<int> RunAsync(string[] args)
    {
        var opts = SeedOptions.Parse(args);
        if (opts is null)
            return 1;

        if (!File.Exists(opts.InputPath))
        {
            Console.Error.WriteLine($"seed: input .osz not found: {opts.InputPath}");
            return 1;
        }

        // ---- Read the archive and parse every .osu difficulty --------------------------------
        var osu = new List<OsuEntry>();

        using (var src = ZipFile.OpenRead(opts.InputPath))
        {
            foreach (var entry in src.Entries)
            {
                if (!entry.FullName.EndsWith(".osu", StringComparison.OrdinalIgnoreCase))
                    continue;

                // stable (and the client's createBeatmapDifficulties) only honour top-level .osu
                // files; nested ones are ignored on import, so we skip them here too.
                if (entry.FullName.Contains('/') || entry.FullName.Contains('\\'))
                    continue;

                byte[] bytes;
                using (var s = entry.Open())
                using (var ms = new MemoryStream())
                {
                    s.CopyTo(ms);
                    bytes = ms.ToArray();
                }

                osu.Add(OsuEntry.Parse(entry.FullName, bytes));
            }
        }

        if (osu.Count == 0)
        {
            Console.Error.WriteLine("seed: no top-level .osu files found in the archive.");
            return 1;
        }

        // Set-level metadata comes from the first difficulty (all difficulties in a set share it).
        string title = osu[0].Title;
        string artist = osu[0].Artist;

        Console.WriteLine($"Parsed {osu.Count} .osu difficult{(osu.Count == 1 ? "y" : "ies")}: \"{artist} - {title}\"");

        // ---- Register in the database --------------------------------------------------------
        await using var conn = new NpgsqlConnection(opts.ConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        long ownerId = await EnsureOwnerAsync(conn, tx, opts.OwnerId);

        // Re-seed heuristic: because the injected ids differ every run the finalized hashes also
        // differ, so we cannot dedupe a set by checksum. Warn on a title/artist/owner match and
        // let the operator decide — this run still creates a fresh set.
        var dupSets = (await conn.QueryAsync<long>(
            "SELECT id FROM beatmapsets WHERE owner_id = @ownerId AND title = @title AND artist = @artist",
            new { ownerId, title, artist }, tx)).ToList();

        if (dupSets.Count > 0)
        {
            Console.WriteLine(
                $"WARNING: {dupSets.Count} existing set(s) with the same title/artist/owner already exist " +
                $"(id(s) {string.Join(", ", dupSets)}). Re-seeding creates a NEW set with new ids — dedupe manually if unintended.");
        }

        long setId = await conn.QuerySingleAsync<long>(
            """
            INSERT INTO beatmapsets (owner_id, title, artist, status)
            VALUES (@ownerId, @title, @artist, 'public')
            RETURNING id
            """,
            new { ownerId, title, artist }, tx);

        var injected = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        foreach (var e in osu)
        {
            // Placeholder insert first: beatmaps.checksum_md5 is NOT NULL UNIQUE, so we seed a
            // throwaway unique value to obtain the id, then inject that id and rewrite the row
            // with the real MD5 of the final bytes.
            string placeholder = Guid.NewGuid().ToString("N"); // 32 hex chars, guaranteed unique
            long beatmapId = await conn.QuerySingleAsync<long>(
                "INSERT INTO beatmaps (set_id, checksum_md5) VALUES (@setId, @placeholder) RETURNING id",
                new { setId, placeholder }, tx);

            byte[] finalBytes = InjectIds(e.Bytes, beatmapId, setId);
            string checksum = Convert.ToHexString(MD5.HashData(finalBytes)).ToLowerInvariant();

            // Guards the (astronomically unlikely for fresh ids) case where the identical finalized
            // bytes were already seeded; the checksum UNIQUE index would otherwise throw on UPDATE.
            var existing = await conn.QuerySingleOrDefaultAsync<BeatmapRow>(
                "SELECT id, set_id AS SetId FROM beatmaps WHERE checksum_md5 = @checksum AND id <> @beatmapId",
                new { checksum, beatmapId }, tx);

            if (existing is not null)
            {
                Console.Error.WriteLine(
                    $"seed: a beatmap with checksum {checksum} already exists (id {existing.Id}, set {existing.SetId}); " +
                    "skipping to avoid a duplicate. No changes committed.");
                await tx.RollbackAsync();
                return 1;
            }

            await conn.ExecuteAsync(
                """
                UPDATE beatmaps
                SET checksum_md5   = @checksum,
                    version_name   = @version,
                    total_length_s = @length,
                    drain_length_s = @length
                WHERE id = @beatmapId
                """,
                new { checksum, version = e.Version, length = e.LengthSeconds, beatmapId }, tx);

            e.OnlineId = beatmapId;
            e.Checksum = checksum;
            injected[e.FileName] = finalBytes;
        }

        await tx.CommitAsync();

        // ---- Write the finalized .osz --------------------------------------------------------
        string outPath = ResolveOutputPath(opts);
        WriteFinalizedOsz(opts.InputPath, outPath, injected);

        // ---- Summary -------------------------------------------------------------------------
        Console.WriteLine();
        Console.WriteLine($"Seeded set {setId}  (owner {ownerId})  \"{artist} - {title}\"");
        foreach (var e in osu)
            Console.WriteLine($"  beatmap {e.OnlineId,-6} [{e.Version}]  {e.Checksum}  ({e.LengthSeconds:0.###}s)  <- {e.FileName}");
        Console.WriteLine($"Finalized .osz: {outPath}");

        return 0;
    }

    /// <summary>
    /// Ensures the owning user exists. When the users table is empty, bootstraps a "typebeat"
    /// system user with an intentionally unusable password hash and returns its id. When the
    /// table is non-empty but the requested owner is missing, this is an operator error.
    /// </summary>
    static async Task<long> EnsureOwnerAsync(NpgsqlConnection conn, System.Data.Common.DbTransaction tx, long ownerId)
    {
        bool exists = await conn.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM users WHERE id = @ownerId)", new { ownerId }, tx);

        if (exists)
            return ownerId;

        long userCount = await conn.ExecuteScalarAsync<long>("SELECT count(*) FROM users", transaction: tx);

        if (userCount != 0)
        {
            throw new InvalidOperationException(
                $"owner user id {ownerId} not found and the users table is not empty; pass --owner <existing user id>.");
        }

        // Unusable password hash: valid base64 (so the hasher's Convert.FromBase64String never
        // throws) whose first byte is an unknown format marker (0xFF, not the 0x00/0x01 the
        // ASP.NET Identity PasswordHasher recognises). Verify therefore returns Failed cleanly —
        // the system user can never authenticate, it only owns seeded content.
        byte[] raw = RandomNumberGenerator.GetBytes(33);
        raw[0] = 0xFF;
        string unusableHash = Convert.ToBase64String(raw);

        long id = await conn.QuerySingleAsync<long>(
            """
            INSERT INTO users (username, email, password_hash)
            VALUES ('typebeat', 'system@typebeat.invalid', @unusableHash)
            RETURNING id
            """,
            new { unusableHash }, tx);

        Console.WriteLine($"Bootstrapped system user 'typebeat' (id {id}) as the content owner.");
        return id;
    }

    /// <summary>
    /// Injects "BeatmapID"/"BeatmapSetID" lines into the .osu's [Metadata] section, preserving
    /// every other byte, the original newline style, and any UTF-8 BOM exactly. LyricOsuFormat
    /// does not emit these lines, so on the source .osz there is nothing to replace; any that do
    /// exist (a re-run on an already-finalized file) are stripped first so ids never duplicate.
    /// </summary>
    static byte[] InjectIds(byte[] original, long beatmapId, long setId)
    {
        bool bom = original.Length >= 3 && original[0] == 0xEF && original[1] == 0xBB && original[2] == 0xBF;
        int start = bom ? 3 : 0;

        string text = Encoding.UTF8.GetString(original, start, original.Length - start);

        // The format is written with a single consistent terminator; detect and reuse it so we
        // never re-encode existing line endings.
        string nl = text.Contains("\r\n") ? "\r\n" : "\n";

        var segs = new List<string>(text.Split(nl));

        int meta = segs.FindIndex(s => s.Trim() == "[Metadata]");
        if (meta < 0)
            throw new InvalidOperationException("no [Metadata] section found in .osu");

        // Walk the section body: the section ends at the first blank line, the next "[Section]"
        // header, or EOF. Strip any pre-existing id lines along the way; the survivor position is
        // where the fresh ids go — before the blank line that closes the section.
        int insert = meta + 1;
        while (insert < segs.Count)
        {
            string t = segs[insert];
            if (t.Length == 0 || t.StartsWith("[", StringComparison.Ordinal))
                break;

            if (t.StartsWith("BeatmapID:", StringComparison.Ordinal)
                || t.StartsWith("BeatmapSetID:", StringComparison.Ordinal))
            {
                segs.RemoveAt(insert);
                continue;
            }

            insert++;
        }

        // Insert both at the same index; the second insert pushes the first down, yielding the
        // conventional "BeatmapID" then "BeatmapSetID" order the decoder reads independently.
        segs.Insert(insert, $"BeatmapSetID:{setId}");
        segs.Insert(insert, $"BeatmapID:{beatmapId}");

        byte[] body = Encoding.UTF8.GetBytes(string.Join(nl, segs)); // Encoding.UTF8.GetBytes never emits a BOM
        if (!bom)
            return body;

        var withBom = new byte[body.Length + 3];
        withBom[0] = 0xEF;
        withBom[1] = 0xBB;
        withBom[2] = 0xBF;
        Array.Copy(body, 0, withBom, 3, body.Length);
        return withBom;
    }

    /// <summary>
    /// Copies the source archive into the output, substituting the injected bytes for each .osu
    /// entry and leaving every other entry's content byte-identical.
    /// </summary>
    static void WriteFinalizedOsz(string inputPath, string outPath, IReadOnlyDictionary<string, byte[]> injected)
    {
        string? dir = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using var src = ZipFile.OpenRead(inputPath);
        using var outStream = File.Create(outPath);
        using var dest = new ZipArchive(outStream, ZipArchiveMode.Create);

        foreach (var entry in src.Entries)
        {
            var outEntry = dest.CreateEntry(entry.FullName, CompressionLevel.Optimal);
            outEntry.LastWriteTime = entry.LastWriteTime;

            using var os = outEntry.Open();

            if (injected.TryGetValue(entry.FullName, out byte[]? bytes))
            {
                os.Write(bytes, 0, bytes.Length);
            }
            else
            {
                using var es = entry.Open();
                es.CopyTo(os);
            }
        }
    }

    static string ResolveOutputPath(SeedOptions opts)
    {
        string baseName = Path.GetFileNameWithoutExtension(opts.InputPath) + ".online.osz";
        string dir = opts.OutDir ?? Path.GetDirectoryName(Path.GetFullPath(opts.InputPath)) ?? ".";
        return Path.Combine(dir, baseName);
    }
}

/// <summary>Parsed command-line options.</summary>
sealed class SeedOptions
{
    public required string InputPath { get; init; }
    public string? OutDir { get; init; }
    public long OwnerId { get; init; }
    public required string ConnectionString { get; init; }

    // Mirror of Db.ResolveConnectionString's default so the tool and server agree out of the box.
    const string DEFAULT_CONNECTION = "Host=localhost;Port=5432;Database=typebeat;Username=typebeat;Password=typebeat";

    public static SeedOptions? Parse(string[] args)
    {
        string? input = null;
        string? outDir = null;
        long owner = 1;
        string? db = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out":
                    outDir = RequireValue(args, ref i, "--out");
                    break;

                case "--owner":
                    string ownerRaw = RequireValue(args, ref i, "--owner");
                    if (!long.TryParse(ownerRaw, out owner))
                    {
                        Console.Error.WriteLine($"seed: --owner must be an integer (got '{ownerRaw}').");
                        return null;
                    }
                    break;

                case "--db":
                    db = RequireValue(args, ref i, "--db");
                    break;

                default:
                    if (args[i].StartsWith("--", StringComparison.Ordinal))
                    {
                        Console.Error.WriteLine($"seed: unknown option '{args[i]}'.");
                        return null;
                    }
                    if (input is not null)
                    {
                        Console.Error.WriteLine("seed: only one input .osz may be given.");
                        return null;
                    }
                    input = args[i];
                    break;
            }
        }

        if (input is null)
        {
            Console.Error.WriteLine("usage: seed <path-to.osz> [--out <dir>] [--owner <userId>] [--db <connection string>]");
            return null;
        }

        string connection = db
                            ?? Environment.GetEnvironmentVariable("TYPEBEAT_DB")
                            ?? DEFAULT_CONNECTION;

        return new SeedOptions
        {
            InputPath = input,
            OutDir = outDir,
            OwnerId = owner,
            ConnectionString = connection,
        };
    }

    static string RequireValue(string[] args, ref int i, string flag)
    {
        if (i + 1 >= args.Length)
            throw new ArgumentException($"seed: {flag} requires a value.");
        return args[++i];
    }
}

/// <summary>A single parsed .osu difficulty plus the ids/checksum assigned during seeding.</summary>
sealed class OsuEntry
{
    public required string FileName { get; init; }
    public required byte[] Bytes { get; init; }
    public required string Title { get; init; }
    public required string Artist { get; init; }
    public required string Version { get; init; }
    public required string AudioFilename { get; init; }
    public required double LengthSeconds { get; init; }

    // Assigned during the DB pass.
    public long OnlineId { get; set; }
    public string Checksum { get; set; } = "";

    /// <summary>
    /// Parses the sections we need: [General] AudioFilename, [Metadata] Title/Artist/Version, and
    /// the [Lyrics] header's song_end_ms (falling back to the max end_ms across the lyric lines)
    /// which yields total/drain length in seconds. Byte content is retained verbatim for hashing.
    /// </summary>
    public static OsuEntry Parse(string fileName, byte[] bytes)
    {
        int start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        string text = Encoding.UTF8.GetString(bytes, start, bytes.Length - start);
        string nl = text.Contains("\r\n") ? "\r\n" : "\n";

        string title = "", artist = "", version = "type!beat", audio = "";
        double? songEndMs = null;
        double maxEndMs = 0;

        string section = "";
        bool lyricsHeaderSeen = false;

        foreach (string rawLine in text.Split(nl))
        {
            string line = rawLine;
            if (line.Length == 0)
                continue;

            if (line.StartsWith("[", StringComparison.Ordinal) && line.TrimEnd().EndsWith("]", StringComparison.Ordinal))
            {
                section = line.Trim();
                continue;
            }

            switch (section)
            {
                case "[General]":
                    if (TryKey(line, "AudioFilename", out string audioVal))
                        audio = audioVal;
                    break;

                case "[Metadata]":
                    if (TryKey(line, "Title", out string t) && title.Length == 0) title = t;
                    else if (TryKey(line, "Artist", out string a) && artist.Length == 0) artist = a;
                    else if (TryKey(line, "Version", out string v)) version = v;
                    break;

                case "[Lyrics]":
                    // Each [Lyrics] line is a compact JSON object: the first (no "text" key) is the
                    // header carrying song_end_ms; the rest are per-line timing objects.
                    ScanLyricLine(line, ref lyricsHeaderSeen, ref songEndMs, ref maxEndMs);
                    break;
            }
        }

        double lengthMs = songEndMs ?? maxEndMs;

        return new OsuEntry
        {
            FileName = fileName,
            Bytes = bytes,
            Title = title,
            Artist = artist,
            Version = version.Length == 0 ? "type!beat" : version,
            AudioFilename = audio,
            LengthSeconds = lengthMs / 1000.0,
        };
    }

    static void ScanLyricLine(string line, ref bool headerSeen, ref double? songEndMs, ref double maxEndMs)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return;

            bool isHeader = !root.TryGetProperty("text", out _);

            if (isHeader && !headerSeen)
            {
                headerSeen = true;
                if (root.TryGetProperty("song_end_ms", out var se) && se.ValueKind == JsonValueKind.Number)
                    songEndMs = se.GetDouble();
                return;
            }

            // Line object: track max end for the fallback length, at line and word granularity.
            if (root.TryGetProperty("end_ms", out var end) && end.ValueKind == JsonValueKind.Number)
                maxEndMs = Math.Max(maxEndMs, end.GetDouble());

            if (root.TryGetProperty("words", out var words) && words.ValueKind == JsonValueKind.Array)
            {
                foreach (var w in words.EnumerateArray())
                {
                    if (w.ValueKind == JsonValueKind.Object
                        && w.TryGetProperty("end_ms", out var we) && we.ValueKind == JsonValueKind.Number)
                        maxEndMs = Math.Max(maxEndMs, we.GetDouble());
                }
            }
        }
        catch (JsonException)
        {
            // A malformed [Lyrics] line is non-fatal for seeding; the client's decoder tolerates
            // it too, and it only affects the length fallback.
        }
    }

    /// <summary>Matches "Key:value" / "Key: value" (the legacy .osu key/value shape) case-sensitively.</summary>
    static bool TryKey(string line, string key, out string value)
    {
        if (line.StartsWith(key, StringComparison.Ordinal) && line.Length > key.Length && line[key.Length] == ':')
        {
            value = line[(key.Length + 1)..].Trim();
            return true;
        }

        value = "";
        return false;
    }
}

/// <summary>Row shape for the checksum-collision guard.</summary>
sealed record BeatmapRow(long Id, long SetId);
