using System.Reflection;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Typebeat.Web.Data;
using Typebeat.Web.Packages;
using Typebeat.Web.Packages.Lyrics;
using Typebeat.Web.Scoring;
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
    // NOTE: "typebeat_pkgtests"; must stay distinct from the website tests' "typebeat_webtests"
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
        var beatmap = await conn.QuerySingleAsync<(string Filename, string Checksum, int WordCount, int CharCount, decimal Wpm, double Difficulty, string Lyrics)>(
            """
            SELECT filename AS Filename, checksum_md5 AS Checksum, word_count AS WordCount,
                   char_count AS CharCount, wpm AS Wpm, difficulty_rating AS Difficulty,
                   lyrics AS Lyrics
            FROM beatmaps WHERE id = 1001
            """);

        Assert.Multiple(() =>
        {
            Assert.That(beatmap.Filename, Is.EqualTo("map.osu"));
            Assert.That(beatmap.Checksum, Has.Length.EqualTo(32));
            Assert.That(beatmap.WordCount, Is.EqualTo(2));
            Assert.That(beatmap.CharCount, Is.EqualTo(5));
            Assert.That((double)beatmap.Wpm, Is.EqualTo(40).Within(1e-6));
            Assert.That(beatmap.Difficulty, Is.EqualTo(0.61).Within(0.01)); // strain-based stars
            Assert.That(beatmap.Lyrics, Is.EqualTo("ab cd")); // the lyrics: search haystack
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
        //
        // The added second difficulty carries two mixed-case [Lyrics] lines: the end-to-end pin
        // that the author's casing and the line structure survive the whole ingest write path
        // into beatmaps.lyrics (the set page renders that column verbatim; search ILIKEs it
        // case-insensitively, so the display-friendly form costs the operator nothing).
        const string mixedCaseLyrics =
            """
            {"version":2,"song_end_ms":9000}
            {"text":"Neon SKYLINE","start_ms":1000,"end_ms":3000}
            {"text":"we Type at Night","start_ms":4000,"end_ms":8000}
            """;

        var entries = new (string, byte[])[]
        {
            ("map.osu", SyntheticPackage.Utf8(SyntheticPackage.OsuText(title: "Neon Nights", titleUnicode: "Neon Nights", beatmapId: 1001, beatmapSetId: setId, previewTime: 2500))),
            ("map2.osu", SyntheticPackage.Utf8(SyntheticPackage.OsuText(title: "Neon Nights", titleUnicode: "Neon Nights", version: "Hard", beatmapId: 1002, beatmapSetId: setId, lyrics: mixedCaseLyrics))),
            ("audio.mp3", SyntheticPackage.Utf8("fake audio bytes")),
        };

        var result = await ingestAsync(entries);

        Assert.That(result.VersionNo, Is.EqualTo(3));

        await using (var conn = await db.OpenAsync())
        {
            string? storedLyrics = await conn.ExecuteScalarAsync<string?>(
                "SELECT lyrics FROM beatmaps WHERE id = 1002");
            Assert.That(storedLyrics, Is.EqualTo("Neon SKYLINE\nwe Type at Night"));
        }

        bool latest = await fileStore.ObjectExistsAsync(StoreKeys.Package(setId, 3));
        bool previous = await fileStore.ObjectExistsAsync(StoreKeys.Package(setId, 2));
        bool oldest = await fileStore.ObjectExistsAsync(StoreKeys.Package(setId, 1));

        Assert.Multiple(() =>
        {
            Assert.That(latest, Is.True, "latest package must exist (assembled before commit)");
            Assert.That(previous, Is.True, "latest-1 is the safety margin and survives");
            Assert.That(oldest, Is.False, "latest-2 is pruned after the commit");
        });

        // The blobs behind v1 are untouched; content-addressed storage is never pruned here.
        foreach (var file in result.Files)
            Assert.That(await fileStore.BlobExistsAsync(file.Sha256), Is.True, file.Filename);
    }

    [Test]
    [Order(6)]
    public async Task PaceBackfill_RecomputesStaleRowsFromStoredBlob()
    {
        // Simulate a row written under the old (v1, perfect-play cells/5) arithmetic: scribble
        // wrong numbers and downgrade the stamp, exactly the state prod is in when a LyricPace
        // version bump deploys. The asserted values below are also the "no-op backfill" pin for
        // version bumps that only change freestyle maps (v6): a map without a flagged line must
        // come back out of the recompute with byte-identical numbers.
        await using var conn = await db.OpenAsync();

        await conn.ExecuteAsync(
            """
            UPDATE beatmaps
            SET wpm = 999, difficulty_rating = 9.9, word_count = 1, char_count = 1, lyrics = '', pace_version = 1
            WHERE id = 1001
            """);

        await PaceBackfill.RunAsync(db, fileStore, NullLogger.Instance);

        var row = await conn.QuerySingleAsync<(decimal Wpm, double Difficulty, int WordCount, int CharCount, string Lyrics, int PaceVersion)>(
            """
            SELECT wpm AS Wpm, difficulty_rating AS Difficulty, word_count AS WordCount,
                   char_count AS CharCount, lyrics AS Lyrics, pace_version AS PaceVersion
            FROM beatmaps WHERE id = 1001
            """);

        Assert.Multiple(() =>
        {
            // The regression package: "ab cd" over a 3000 ms boundary window.
            Assert.That((double)row.Wpm, Is.EqualTo(40).Within(1e-6));
            Assert.That(row.Difficulty, Is.EqualTo(0.61).Within(0.01)); // strain-based stars
            Assert.That(row.WordCount, Is.EqualTo(2));
            Assert.That(row.CharCount, Is.EqualTo(5));
            Assert.That(row.Lyrics, Is.EqualTo("ab cd")); // v8 fills the lyrics: haystack
            Assert.That(row.PaceVersion, Is.EqualTo(LyricPace.VERSION));
        });

        // Second run: nothing stale, nothing changes (idempotent no-op).
        await PaceBackfill.RunAsync(db, fileStore, NullLogger.Instance);

        int stale = await conn.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM beatmaps WHERE pace_version < @v", new { v = LyricPace.VERSION });
        Assert.That(stale, Is.Zero);
    }

    // ---------------------------------------------------------------------------------------------
    // Song language (019_language.sql): ingest writes the mapper's tag, the backfill fills the rest.
    // ---------------------------------------------------------------------------------------------

    [Test]
    [Order(7)]
    public async Task LanguageBackfill_DetectsFromStoredLyrics_AndLeavesUnclassifiableRowsUnset()
    {
        await using var conn = await db.OpenAsync();

        // Nothing so far has stated a language: the ingested packages carry no [Metadata]
        // Language: line, which is exactly the pre-task-58 client shape.
        string before = await conn.ExecuteScalarAsync<string>(
            "SELECT language FROM beatmapsets WHERE id = @setId", new { setId });
        Assert.That(before, Is.Empty, "a package with no Language: line must leave the set unset");

        // Two more sets, written straight into beatmaps.lyrics: the backfill reads that column and
        // nothing else (no blobs, no network). One classifiable, one deliberately not.
        long japaneseSetId = await newSetWithLyricsAsync(conn, "jp.osu",
            "夜空に光る星を数えて\n君の声がまだ聞こえてる\nこの道の先に何があっても");

        long vocaliseSetId = await newSetWithLyricsAsync(conn, "hum.osu",
            "ooh ooh ooh aah aah ooh\nna na na na na ooh aah\nna na na ooh ooh aah aah na");

        await LanguageBackfill.RunAsync(db, NullLogger.Instance);

        Assert.Multiple(async () =>
        {
            Assert.That(await languageOf(conn, japaneseSetId), Is.EqualTo("japanese"));
            Assert.That(await languageOf(conn, vocaliseSetId), Is.Empty,
                "unclassifiable rows stay unset and are retried next boot, never guessed");
            // End-to-end: the set ingested above sings "Neon SKYLINE / we Type at Night" (its
            // second difficulty, added at Order(5)), so the lyrics the INGEST path wrote are what
            // the detector reads, with no extra plumbing in between.
            Assert.That(await languageOf(conn, setId), Is.EqualTo("english"));
        });

        // Idempotent: a second sweep only ever looks at rows that are still unset, so the answers
        // above cannot churn.
        await LanguageBackfill.RunAsync(db, NullLogger.Instance);

        Assert.That(await languageOf(conn, japaneseSetId), Is.EqualTo("japanese"));
    }

    private async Task<long> newSetWithLyricsAsync(NpgsqlConnection conn, string filename, string lyrics)
    {
        long id = await conn.ExecuteScalarAsync<long>(
            "INSERT INTO beatmapsets (owner_id, status, intended_status) VALUES (@uploaderId, 'pending', 'pending') RETURNING id",
            new { uploaderId });

        // checksum_md5 is UNIQUE across the table, so each synthetic difficulty needs its own.
        await conn.ExecuteAsync(
            """
            INSERT INTO beatmaps (set_id, checksum_md5, filename, lyrics)
            VALUES (@id, md5(@filename), @filename, @lyrics)
            """,
            new { id, filename, lyrics });

        return id;
    }

    private static async Task<string> languageOf(NpgsqlConnection conn, long id)
        => await conn.ExecuteScalarAsync<string>("SELECT language FROM beatmapsets WHERE id = @id", new { id });

    [Test]
    [Order(8)]
    public async Task Ingest_StoresTheMappersLanguage_AndAnOldClientCannotClearIt()
    {
        await using var conn = await db.OpenAsync();

        var withLanguage = new (string, byte[])[]
        {
            ("map.osu", SyntheticPackage.Utf8(SyntheticPackage.OsuText(
                title: "Neon Nights", titleUnicode: "Neon Nights", beatmapId: 1001, beatmapSetId: setId,
                language: "Japanese", previewTime: 3500))),
            ("audio.mp3", SyntheticPackage.Utf8("fake audio bytes")),
        };

        await ingestAsync(withLanguage);

        Assert.That(await conn.ExecuteScalarAsync<string>(
                "SELECT language FROM beatmapsets WHERE id = @setId", new { setId }),
            Is.EqualTo("japanese"), "the mapper's tag is folded to canonical case and stored");

        // The regression this guards: a pre-task-58 client re-submitting the same map writes no
        // Language: line at all, and must NOT wipe what is already known.
        var withoutLanguage = new (string, byte[])[]
        {
            ("map.osu", SyntheticPackage.Utf8(SyntheticPackage.OsuText(
                title: "Neon Nights", titleUnicode: "Neon Nights", beatmapId: 1001, beatmapSetId: setId,
                previewTime: 4500))),
            ("audio.mp3", SyntheticPackage.Utf8("fake audio bytes")),
        };

        await ingestAsync(withoutLanguage);

        Assert.That(await conn.ExecuteScalarAsync<string>(
                "SELECT language FROM beatmapsets WHERE id = @setId", new { setId }),
            Is.EqualTo("japanese"), "an upload that states no language must leave the stored one alone");
    }

    [Test]
    [Order(9)]
    public async Task LanguageBackfill_NeverOverwritesAMapperSuppliedLanguage()
    {
        await using var conn = await db.OpenAsync();

        // English lyrics on a set the mapper has tagged 'instrumental': a deliberate disagreement.
        // The mapper wins, always.
        await conn.ExecuteAsync(
            "UPDATE beatmapsets SET language = 'instrumental' WHERE id = @setId", new { setId });
        await conn.ExecuteAsync(
            """
            UPDATE beatmaps
            SET lyrics = 'I know you never wanted me to say the words out loud
            but all the time we had is gone and I am still here'
            WHERE id = 1001
            """);

        await LanguageBackfill.RunAsync(db, NullLogger.Instance);

        Assert.That(await conn.ExecuteScalarAsync<string>(
                "SELECT language FROM beatmapsets WHERE id = @setId", new { setId }),
            Is.EqualTo("instrumental"));
    }

    // ---------------------------------------------------------------------------------------------
    // Performance points (020_performance_points.sql): the rate-adjusted star ratings ingest writes
    // and PaceBackfill fills, and the pp sweep that hangs off them.
    // ---------------------------------------------------------------------------------------------

    [Test]
    [Order(10)]
    public async Task Ingest_StoresTheRateAdjustedStarRatings()
    {
        await using var conn = await db.OpenAsync();

        var row = await conn.QuerySingleAsync<(double Base, double? Dt, double? Ht)>(
            "SELECT difficulty_rating AS Base, sr_dt AS Dt, sr_ht AS Ht FROM beatmaps WHERE id = 1001");

        Assert.Multiple(() =>
        {
            Assert.That(row.Dt, Is.Not.Null, "sr_dt is written at ingest, never derived at query time");
            Assert.That(row.Ht, Is.Not.Null);

            // Exactly LyricDifficulty at the two BASE rates, the only two ratings pp ever needs.
            Assert.That(row.Dt!.Value, Is.EqualTo(starsAtRate(RateMods.DoubleTimeBaseRate)).Within(1e-9));
            Assert.That(row.Ht!.Value, Is.EqualTo(starsAtRate(RateMods.HalfTimeBaseRate)).Within(1e-9));

            // All three differ, i.e. the rate genuinely moves the rating rather than the columns
            // being copies of the base one. (No DIRECTION is asserted: this fixture's map is two
            // words long, and on a map that short the duration weighting in LyricDifficulty's soft
            // maximum outweighs the strain increase, so up-rating it actually rates LOWER. That is
            // the difficulty model's own behaviour on degenerate input, not pp's business; the
            // pp-side contract, "a harder rating is worth more pp", is pinned in
            // PerformancePointsTest where the ratings are inputs.)
            Assert.That(row.Dt.Value, Is.Not.EqualTo(row.Base).Within(1e-6));
            Assert.That(row.Ht.Value, Is.Not.EqualTo(row.Base).Within(1e-6));
            Assert.That(row.Dt.Value, Is.Not.EqualTo(row.Ht.Value).Within(1e-6));
        });
    }

    [Test]
    [Order(11)]
    public async Task PaceBackfill_FillsMissingRateStarRatings_WithoutAPaceVersionBump()
    {
        await using var conn = await db.OpenAsync();

        // The state every existing row is in the moment 020 deploys: pace numbers already current,
        // the two new columns NULL. The sweep must still visit the row, which is precisely what the
        // second staleness arm buys: no LyricPace.VERSION bump was needed (and so no punctuation
        // re-derivation of .osz-conversion blobs rode along, task 59).
        await conn.ExecuteAsync("UPDATE beatmaps SET sr_dt = NULL, sr_ht = NULL WHERE id = 1001");

        int paceVersionBefore = await conn.ExecuteScalarAsync<int>(
            "SELECT pace_version FROM beatmaps WHERE id = 1001");
        Assert.That(paceVersionBefore, Is.EqualTo(LyricPace.VERSION),
            "this row is not stale by pace version; only the new columns are missing");

        await PaceBackfill.RunAsync(db, fileStore, NullLogger.Instance);

        var row = await conn.QuerySingleAsync<(double? Dt, double? Ht, int PaceVersion)>(
            "SELECT sr_dt AS Dt, sr_ht AS Ht, pace_version AS PaceVersion FROM beatmaps WHERE id = 1001");

        Assert.Multiple(() =>
        {
            Assert.That(row.Dt, Is.EqualTo(starsAtRate(RateMods.DoubleTimeBaseRate)).Within(1e-9));
            Assert.That(row.Ht, Is.EqualTo(starsAtRate(RateMods.HalfTimeBaseRate)).Within(1e-9));
            Assert.That(row.PaceVersion, Is.EqualTo(LyricPace.VERSION), "the pace stamp did not have to move");
        });

        // Idempotent: with both arms satisfied this row is no longer a candidate at all.
        await PaceBackfill.RunAsync(db, fileStore, NullLogger.Instance);

        Assert.That(await conn.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM beatmaps WHERE id = 1001 AND (sr_dt IS NULL OR sr_ht IS NULL)"),
            Is.Zero);
    }

    [Test]
    [Order(12)]
    public async Task PpBackfill_ComputesStoredPp_AndPaceBackfillInvalidatesItWhenStarsMove()
    {
        await using var conn = await db.OpenAsync();

        long playerId = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO users (username, email, password_hash, country_code)
            VALUES ('pp typist', 'pp.typist@example.com', 'x', 'US')
            RETURNING id
            """);

        // Three plays on the same map: a no-mod ranked one, a base-rate DT one (priced off sr_dt),
        // and one at a CUSTOM rate, which stays exactly as it is on the score boards and earns 0 pp.
        long noMod = await insertPlayAsync(conn, playerId, "[]");
        long baseRateDt = await insertPlayAsync(conn, playerId, """[{"acronym":"DT","settings":{"speed_change":1.5}}]""");
        long customRateDt = await insertPlayAsync(conn, playerId, """[{"acronym":"DT","settings":{"speed_change":1.75}}]""");
        long unranked = await insertPlayAsync(conn, playerId, "[]", ranked: false);

        Assert.That(await ppOf(conn, noMod), Is.Zero, "nothing is priced before the sweep runs");

        await PpBackfill.RunAsync(db, NullLogger.Instance);

        var beatmap = await conn.QuerySingleAsync<(double Base, double Dt)>(
            "SELECT difficulty_rating AS Base, sr_dt AS Dt FROM beatmaps WHERE id = 1001");

        // 112 great + 8 miss = 120 notes; ignore_hit is not a note (see insertPlayAsync). EIGHT
        // misses, not the twenty this fixture used to carry: the backlog-97 miss cliff on a
        // 120-note map is sqrt(120) = 10.95, so twenty priced the whole play to zero and the
        // no-mod/DT comparison below (a PLUMBING check that the DT row is priced off sr_dt) became
        // 0 against 0.
        double expectedNoMod = PerformancePoints.Compute(beatmap.Base, 120, 8, 0.9, 100, []);
        double expectedDt = PerformancePoints.Compute(beatmap.Dt, 120, 8, 0.9, 100, []);

        double noModPp = await ppOf(conn, noMod);
        double dtPp = await ppOf(conn, baseRateDt);
        double customPp = await ppOf(conn, customRateDt);
        double unrankedPp = await ppOf(conn, unranked);
        int stillStale = await conn.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM scores WHERE pp_version < @v", new { v = PerformancePoints.VERSION });

        Assert.Multiple(() =>
        {
            Assert.That(noModPp, Is.EqualTo(expectedNoMod).Within(1e-9));
            Assert.That(dtPp, Is.EqualTo(expectedDt).Within(1e-9));
            // The point of the pair: the base-rate DT play is priced off sr_dt, NOT off the base
            // rating. (Which way it moves is the difficulty model's business, see the ingest test.)
            Assert.That(dtPp, Is.Not.EqualTo(noModPp).Within(1e-6));
            Assert.That(customPp, Is.Zero, "a custom rate is pp-ineligible");
            Assert.That(unrankedPp, Is.Zero);

            // Everything settled, including the ineligible ones, so nothing is rescanned forever.
            Assert.That(stillStale, Is.Zero);
        });

        // A second sweep has nothing to do and changes nothing.
        double before = await ppOf(conn, noMod);
        await PpBackfill.RunAsync(db, NullLogger.Instance);
        Assert.That(await ppOf(conn, noMod), Is.EqualTo(before));

        // INVALIDATION: when the map's ratings are rewritten, every pp set on it must be recomputed
        // rather than left pointing at a rating that no longer exists.
        await conn.ExecuteAsync(
            "UPDATE beatmaps SET difficulty_rating = 9.9, sr_dt = NULL, sr_ht = NULL, pace_version = 1 WHERE id = 1001");

        await PaceBackfill.RunAsync(db, fileStore, NullLogger.Instance);

        Assert.That(await conn.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM scores WHERE beatmap_id = 1001 AND pp_version = 0"),
            Is.GreaterThan(0), "rewriting a map's stars hands its scores back to the pp sweep");

        await PpBackfill.RunAsync(db, NullLogger.Instance);

        Assert.That(await ppOf(conn, noMod), Is.EqualTo(before).Within(1e-9),
            "recomputed against the same reparsed blob, so the value comes back identical");
    }

    /// <summary>A ranked, passed play: 100 great + 20 miss + 8 ignore_hit, 90% acc, 100 combo.</summary>
    private static async Task<long> insertPlayAsync(NpgsqlConnection conn, long userId, string modsJson, bool ranked = true)
        => await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO scores
                (user_id, beatmap_id, total_score, accuracy, completion, max_combo, rank, passed, ranked,
                 mods, statistics, maximum_statistics)
            VALUES
                (@userId, 1001, 500000, 0.9, 0.83, 100, 'B', true, @ranked,
                 CAST(@modsJson AS jsonb),
                 '{"great":112,"miss":8,"ignore_hit":8}'::jsonb,
                 '{"great":120,"ignore_hit":8}'::jsonb)
            RETURNING id
            """,
            new { userId, modsJson, ranked });

    private static async Task<double> ppOf(NpgsqlConnection conn, long scoreId)
        => await conn.ExecuteScalarAsync<double>("SELECT pp FROM scores WHERE id = @scoreId", new { scoreId });

    /// <summary>
    /// The seeded difficulty's stars at a given clock rate, straight from the model. The synthetic
    /// package's [Lyrics] payload is fixed (SyntheticPackage.PaceRegressionLyrics), so this is the
    /// same input the ingested blob carries.
    /// </summary>
    private static double starsAtRate(double rate)
    {
        var parsed = BeatmapPackageParser.ParseDifficulty("map.osu", SyntheticPackage.Utf8(SyntheticPackage.OsuText()));

        return LyricDifficulty.Compute(parsed.Lines, rate);
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
