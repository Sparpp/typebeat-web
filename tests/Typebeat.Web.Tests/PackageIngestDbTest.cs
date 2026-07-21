using System.Reflection;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Typebeat.Web.Data;
using Typebeat.Web.Packages;
using Typebeat.Web.Packages.Lyrics;
using Typebeat.Web.Storage;

namespace Typebeat.Web.Tests;

/// <summary>
/// Version-pipeline tests against a real local Postgres (the repo's WireCompat fixture pattern:
/// a DEDICATED drop+recreated database, superuser postgres/postgres, dev db never touched).
///
/// The fixture also proves migration 002 applies cleanly to a database that already ran 001:
/// 001 is applied by hand and recorded in schema_migrations, then <see cref="Db.MigrateAsync"/>
/// runs and must apply exactly 002 on top.
/// </summary>
[TestFixture]
[NonParallelizable]
public class PackageIngestDbTest
{
    // NOTE: "typebeat_pkgtests" — must stay distinct from the website tests' "typebeat_webtests"
    // and WireCompat's "typebeat_wirecompat"; each fixture force-drops its own database.
    private const string database_name = "typebeat_pkgtests";

    private const string connection_string =
        "Host=localhost;Port=5432;Database=" + database_name + ";Username=postgres;Password=postgres";

    private const string admin_connection_string =
        "Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=postgres";

    private NpgsqlDataSource dataSource = null!;
    private Db db = null!;
    private LocalFileStore fileStore = null!;
    private PackageIngest ingest = null!;
    private string fileRoot = null!;

    private long uploaderId;
    private long setId;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        await using (var conn = new NpgsqlConnection(admin_connection_string))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync($"DROP DATABASE IF EXISTS {database_name} WITH (FORCE)");
            await conn.ExecuteAsync($"CREATE DATABASE {database_name}");
        }

        await Db.EnsureExtensionsAsync(connection_string);

        // Simulate a production database that already ran 001: apply it manually and record it,
        // so MigrateAsync below exercises the "002 lands on an existing 001 schema" path.
        await using (var conn = new NpgsqlConnection(connection_string))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync(readEmbeddedMigration("001_init.sql"));
            await conn.ExecuteAsync(
                """
                CREATE TABLE schema_migrations (name text PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now());
                INSERT INTO schema_migrations (name) VALUES ('001_init.sql');
                """);
        }

        dataSource = NpgsqlDataSource.Create(connection_string);
        db = new Db(dataSource);
        await db.MigrateAsync(NullLogger.Instance);

        fileRoot = Path.Combine(Path.GetTempPath(), "typebeat-webtests-" + Guid.NewGuid().ToString("N"));
        fileStore = new LocalFileStore(fileRoot);
        ingest = new PackageIngest(db, fileStore, new CoverGenerator(), new PreviewGenerator(), NullLogger<PackageIngest>.Instance);

        await using (var conn = await db.OpenAsync())
        {
            uploaderId = await conn.ExecuteScalarAsync<long>(
                """
                INSERT INTO users (username, email, password_hash, country_code)
                VALUES ('uploader', 'uploader@example.com', 'x', 'US')
                RETURNING id
                """);

            setId = await conn.ExecuteScalarAsync<long>(
                "INSERT INTO beatmapsets (owner_id) VALUES (@uploaderId) RETURNING id",
                new { uploaderId });
        }
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (dataSource != null)
            await dataSource.DisposeAsync();

        if (fileRoot != null && Directory.Exists(fileRoot))
            Directory.Delete(fileRoot, recursive: true);
    }

    [Test]
    public async Task Migration002_AppliedOnTopOf001_AndBackfillRuns()
    {
        await using var conn = await db.OpenAsync();

        // Later milestones append migrations (003+); this test's contract is that 001 and 002
        // applied in order, not that they are the only ones.
        var applied = (await conn.QueryAsync<string>("SELECT name FROM schema_migrations ORDER BY name")).ToList();
        Assert.That(applied.Take(2), Is.EqualTo(new[] { "001_init.sql", "002_website_uploads.sql" }));

        // Spot-check every new column exists (a bad ALTER would have failed MigrateAsync anyway).
        await conn.ExecuteAsync(
            """
            SELECT u.last_visit,
                   s.title_unicode, s.artist_unicode, s.description, s.bpm, s.download_count,
                   b.filename, b.word_count, b.char_count, b.wpm,
                   f.created_at,
                   v.uploader_id
            FROM users u
            LEFT JOIN beatmapsets s ON false
            LEFT JOIN beatmaps b ON false
            LEFT JOIN favourites f ON false
            LEFT JOIN set_versions v ON false
            """);
    }

    // The pipeline tests share one set and run as a sequence (fresh -> no-op -> new version).
    // NUnit orders [Order]ed tests deterministically within the fixture.

    private (string Name, byte[] Content)[] packageV1() =>
    [
        ("map.osu", SyntheticPackage.Utf8(SyntheticPackage.OsuText(beatmapId: 1001, beatmapSetId: setId))),
        ("audio.mp3", SyntheticPackage.Utf8("fake audio bytes")),
        ("bg.jpg", SyntheticPackage.TinyPng()),
    ];

    private async Task<PackageIngest.IngestResult> ingestAsync((string Name, byte[] Content)[] entries)
    {
        using var zip = SyntheticPackage.Zip(entries);
        var parsed = BeatmapPackageParser.Parse(zip);

        PackageValidator.Validate(parsed, setId, [1001L, 1002L], "uploader");

        await using var scope = await ingest.BeginSetScopeAsync(setId);
        return await ingest.IngestAsync(scope, zip, parsed, setId, uploaderId);
    }

    [Test]
    [Order(1)]
    public async Task FreshIngest_CutsVersionOne()
    {
        var result = await ingestAsync(packageV1());

        Assert.Multiple(() =>
        {
            Assert.That(result.CutNewVersion, Is.True);
            Assert.That(result.VersionNo, Is.EqualTo(1));
            Assert.That(result.Files, Has.Count.EqualTo(3));
            Assert.That(result.CoverStatus, Is.EqualTo("generated"));
            Assert.That(result.PreviewStatus, Is.AnyOf("generated", "ffmpeg_missing", "failed"));
        });

        await using var conn = await db.OpenAsync();

        // Version + manifest.
        var version = await conn.QuerySingleAsync<(long Id, int VersionNo, long? UploaderId, string PackageKey)>(
            "SELECT id, version_no AS VersionNo, uploader_id AS UploaderId, package_key AS PackageKey FROM set_versions WHERE set_id = @setId",
            new { setId });

        Assert.Multiple(() =>
        {
            Assert.That(version.VersionNo, Is.EqualTo(1));
            Assert.That(version.UploaderId, Is.EqualTo(uploaderId));
            Assert.That(version.PackageKey, Is.EqualTo(StoreKeys.Package(setId, 1)));
        });

        int manifestCount = await conn.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM version_files WHERE version_id = @id", new { version.Id });
        Assert.That(manifestCount, Is.EqualTo(3));

        // Set metadata came from the package; search vector is populated and queryable.
        var set = await conn.QuerySingleAsync<(string Title, string Artist, string TitleUnicode, decimal? Bpm, int CurrentVersion, string? CoverKey)>(
            """
            SELECT title AS Title, artist AS Artist, title_unicode AS TitleUnicode, bpm AS Bpm,
                   current_version AS CurrentVersion, cover_key AS CoverKey
            FROM beatmapsets WHERE id = @setId
            """,
            new { setId });

        Assert.Multiple(() =>
        {
            Assert.That(set.Title, Is.EqualTo("Neon Nights"));
            Assert.That(set.Artist, Is.EqualTo("Synth Rider"));
            Assert.That(set.TitleUnicode, Is.EqualTo("Neon Nights"));
            Assert.That((double)set.Bpm!.Value, Is.EqualTo(120).Within(1e-6));
            Assert.That(set.CurrentVersion, Is.EqualTo(1));
            Assert.That(set.CoverKey, Is.EqualTo(StoreKeys.CoverPrefix(setId, 1)));
        });

        bool searchHits = await conn.ExecuteScalarAsync<bool>(
            "SELECT search @@ plainto_tsquery('simple', 'neon') FROM beatmapsets WHERE id = @setId",
            new { setId });
        Assert.That(searchHits, Is.True, "search vector should match the title");

        // Beatmap row upserted by allocated id with the pace-derived stats.
        var beatmap = await conn.QuerySingleAsync<(string Filename, string Checksum, int WordCount, int CharCount, decimal Wpm, double Difficulty)>(
            """
            SELECT filename AS Filename, checksum_md5 AS Checksum, word_count AS WordCount,
                   char_count AS CharCount, wpm AS Wpm, difficulty_rating AS Difficulty
            FROM beatmaps WHERE id = 1001
            """);

        Assert.Multiple(() =>
        {
            Assert.That(beatmap.Filename, Is.EqualTo("map.osu"));
            Assert.That(beatmap.Checksum, Has.Length.EqualTo(32));
            Assert.That(beatmap.WordCount, Is.EqualTo(2));
            Assert.That(beatmap.CharCount, Is.EqualTo(5));
            Assert.That((double)beatmap.Wpm, Is.EqualTo(40).Within(1e-6));
            Assert.That(beatmap.Difficulty, Is.EqualTo(0.19).Within(0.01)); // strain-based stars
        });

        // Blobs + assembled package + covers exist in the store.
        foreach (var file in result.Files)
            Assert.That(await fileStore.BlobExistsAsync(file.Sha256), Is.True, $"blob for {file.Filename}");

        Assert.That(await fileStore.ObjectExistsAsync(StoreKeys.Package(setId, 1)), Is.True);

        foreach (var (name, _, _) in CoverGenerator.Sizes)
        {
            Assert.That(await fileStore.ObjectExistsAsync(StoreKeys.Cover(setId, 1, name)), Is.True, name);
            Assert.That(await fileStore.ObjectExistsAsync(StoreKeys.Cover(setId, 1, name + "@2x")), Is.True, name + "@2x");
        }
    }

    [Test]
    [Order(2)]
    public async Task IdenticalReupload_DoesNotCutAVersion()
    {
        await using (var conn = await db.OpenAsync())
        {
            // Backdate updated_at so the touch is observable.
            await conn.ExecuteAsync("UPDATE beatmapsets SET updated_at = now() - interval '1 hour' WHERE id = @setId", new { setId });
        }

        var result = await ingestAsync(packageV1());

        Assert.Multiple(() =>
        {
            Assert.That(result.CutNewVersion, Is.False);
            Assert.That(result.VersionNo, Is.EqualTo(1));
        });

        await using (var conn = await db.OpenAsync())
        {
            int versions = await conn.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM set_versions WHERE set_id = @setId", new { setId });
            Assert.That(versions, Is.EqualTo(1));

            double ageSeconds = await conn.ExecuteScalarAsync<double>(
                "SELECT extract(epoch FROM (now() - updated_at)) FROM beatmapsets WHERE id = @setId", new { setId });
            Assert.That(ageSeconds, Is.LessThan(60), "updated_at must be touched on a no-op re-upload");
        }
    }

    [Test]
    [Order(3)]
    public async Task ChangedPackage_CutsVersionTwo_AndDedupsUnchangedFiles()
    {
        long filesBefore;

        await using (var conn = await db.OpenAsync())
            filesBefore = await conn.ExecuteScalarAsync<long>("SELECT count(*) FROM files");

        // Same audio + background, one extra file and a re-titled .osu -> new version; the two
        // unchanged blobs must NOT produce new files rows.
        var entries = new (string, byte[])[]
        {
            ("map.osu", SyntheticPackage.Utf8(SyntheticPackage.OsuText(title: "Neon Nights", titleUnicode: "Neon Nights", beatmapId: 1001, beatmapSetId: setId, previewTime: 1500))),
            ("audio.mp3", SyntheticPackage.Utf8("fake audio bytes")),
            ("bg.jpg", SyntheticPackage.TinyPng()),
            ("storyboard/extra.txt", SyntheticPackage.Utf8("new file")),
        };

        var result = await ingestAsync(entries);

        Assert.Multiple(() =>
        {
            Assert.That(result.CutNewVersion, Is.True);
            Assert.That(result.VersionNo, Is.EqualTo(2));
        });

        await using (var conn = await db.OpenAsync())
        {
            long filesAfter = await conn.ExecuteScalarAsync<long>("SELECT count(*) FROM files");

            // v1 had 3 files; v2 re-uses audio + bg (same bytes; bg.jpg TinyPng is deterministic)
            // and adds a changed .osu + one brand-new file -> exactly 2 new rows.
            Assert.That(filesAfter - filesBefore, Is.EqualTo(2));

            int currentVersion = await conn.ExecuteScalarAsync<int>(
                "SELECT current_version FROM beatmapsets WHERE id = @setId", new { setId });
            Assert.That(currentVersion, Is.EqualTo(2));
        }

        var manifest = await ingest.GetLatestVersionFilesAsync(setId);

        Assert.Multiple(() =>
        {
            Assert.That(manifest, Has.Count.EqualTo(4));
            Assert.That(manifest.Select(f => f.Filename), Does.Contain("storyboard/extra.txt"));
        });

        Assert.That(await fileStore.ObjectExistsAsync(StoreKeys.Package(setId, 2)), Is.True);
    }

    [Test]
    [Order(4)]
    public async Task AssembledPackage_RoundTripsThroughParser()
    {
        // The downloadable zip for the latest version must itself be a valid package with the
        // same content-addressed identity (blobs round-trip bit-exact).
        await using var packageStream = await fileStore.OpenObjectReadAsync(StoreKeys.Package(setId, 2));
        Assert.That(packageStream, Is.Not.Null);

        using var buffer = new MemoryStream();
        await packageStream!.CopyToAsync(buffer);

        var reparsed = BeatmapPackageParser.Parse(buffer);
        var stored = await ingest.GetLatestVersionFilesAsync(setId);

        var reparsedSet = reparsed.Files.Select(f => (f.Sha256Hex, f.Size, f.Filename)).ToHashSet();
        var storedSet = stored.Select(f => (f.Sha256Hex, f.Size, f.Filename)).ToHashSet();

        Assert.That(reparsedSet.SetEquals(storedSet), Is.True);
    }

    [Test]
    [Order(5)]
    public async Task ThirdVersion_PrunesTheAssembledPackageBeyondLatestTwo()
    {
        // Superseded assembled packages are pure derivatives of the content-addressed blobs;
        // only the latest two survive a version cut (75 GB prod disk, see ingest comment).
        var entries = new (string, byte[])[]
        {
            ("map.osu", SyntheticPackage.Utf8(SyntheticPackage.OsuText(title: "Neon Nights", titleUnicode: "Neon Nights", beatmapId: 1001, beatmapSetId: setId, previewTime: 2500))),
            ("audio.mp3", SyntheticPackage.Utf8("fake audio bytes")),
        };

        var result = await ingestAsync(entries);

        Assert.That(result.VersionNo, Is.EqualTo(3));

        bool latest = await fileStore.ObjectExistsAsync(StoreKeys.Package(setId, 3));
        bool previous = await fileStore.ObjectExistsAsync(StoreKeys.Package(setId, 2));
        bool oldest = await fileStore.ObjectExistsAsync(StoreKeys.Package(setId, 1));

        Assert.Multiple(() =>
        {
            Assert.That(latest, Is.True, "latest package must exist (assembled before commit)");
            Assert.That(previous, Is.True, "latest-1 is the safety margin and survives");
            Assert.That(oldest, Is.False, "latest-2 is pruned after the commit");
        });

        // The blobs behind v1 are untouched — content-addressed storage is never pruned here.
        foreach (var file in result.Files)
            Assert.That(await fileStore.BlobExistsAsync(file.Sha256), Is.True, file.Filename);
    }

    [Test]
    [Order(6)]
    public async Task PaceBackfill_RecomputesStaleRowsFromStoredBlob()
    {
        // Simulate a row written under the old (v1, perfect-play cells/5) arithmetic: scribble
        // wrong numbers and downgrade the stamp — exactly the state prod is in when a LyricPace
        // version bump deploys.
        await using var conn = await db.OpenAsync();

        await conn.ExecuteAsync(
            """
            UPDATE beatmaps
            SET wpm = 999, difficulty_rating = 9.9, word_count = 1, char_count = 1, pace_version = 1
            WHERE id = 1001
            """);

        await PaceBackfill.RunAsync(db, fileStore, NullLogger.Instance);

        var row = await conn.QuerySingleAsync<(decimal Wpm, double Difficulty, int WordCount, int CharCount, int PaceVersion)>(
            """
            SELECT wpm AS Wpm, difficulty_rating AS Difficulty, word_count AS WordCount,
                   char_count AS CharCount, pace_version AS PaceVersion
            FROM beatmaps WHERE id = 1001
            """);

        Assert.Multiple(() =>
        {
            // The regression package: "ab cd" over a 3000 ms boundary window.
            Assert.That((double)row.Wpm, Is.EqualTo(40).Within(1e-6));
            Assert.That(row.Difficulty, Is.EqualTo(0.19).Within(0.01)); // strain-based stars
            Assert.That(row.WordCount, Is.EqualTo(2));
            Assert.That(row.CharCount, Is.EqualTo(5));
            Assert.That(row.PaceVersion, Is.EqualTo(LyricPace.VERSION));
        });

        // Second run: nothing stale, nothing changes (idempotent no-op).
        await PaceBackfill.RunAsync(db, fileStore, NullLogger.Instance);

        int stale = await conn.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM beatmaps WHERE pace_version < @v", new { v = LyricPace.VERSION });
        Assert.That(stale, Is.Zero);
    }

    private static string readEmbeddedMigration(string name)
    {
        var assembly = typeof(Db).GetTypeInfo().Assembly;
        string resource = assembly.GetManifestResourceNames().Single(n => n.EndsWith(name, StringComparison.Ordinal));

        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
