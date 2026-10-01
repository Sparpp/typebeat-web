using System.Reflection;
using System.Security.Cryptography;
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

    /// <summary>
    /// THE SHARED SET'S LYRIC, <see cref="SyntheticPackage.RatedLyrics"/>: every upload of beatmap
    /// 1001 below carries it. It is not <see cref="SyntheticPackage.PaceRegressionLyrics"/> any more
    /// because that map rates exactly zero since LyricPace v22 (five cells is under the chunked
    /// axis's 16-character floor), which made every star-dependent claim in this fixture vacuous:
    /// sr_dt equal to sr_ht equal to the base rating, a DT play priced the same as a no-mod one
    /// because both are 0.
    /// </summary>
    private const string fixture_lyrics = SyntheticPackage.RatedLyrics;

    private (string Name, byte[] Content)[] packageV1() =>
    [
        ("map.osu", SyntheticPackage.Utf8(SyntheticPackage.OsuText(beatmapId: 1001, beatmapSetId: setId, lyrics: fixture_lyrics))),
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
        var beatmap = await conn.QuerySingleAsync<(string Filename, string Checksum, int WordCount, int CharCount, decimal Wpm, double Difficulty, string Lyrics, double? TargetWpm, int PaceVersion, string Ratings)>(
            """
            SELECT filename AS Filename, checksum_md5 AS Checksum, word_count AS WordCount,
                   char_count AS CharCount, wpm AS Wpm, difficulty_rating AS Difficulty,
                   lyrics AS Lyrics, target_wpm AS TargetWpm, pace_version AS PaceVersion,
                   ratings::text AS Ratings
            FROM beatmaps WHERE id = 1001
            """);

        Assert.Multiple(() =>
        {
            Assert.That(beatmap.Filename, Is.EqualTo("map.osu"));
            Assert.That(beatmap.Checksum, Has.Length.EqualTo(32));
            Assert.That(beatmap.WordCount, Is.EqualTo(4));
            Assert.That(beatmap.CharCount, Is.EqualTo(23));
            // 23 cells over the 2000 ms of word spans = 690 CPM, stored WPM = 690/5 = 138. The two
            // counts above are what the stored figure is derived from. It read 92 (3000 ms, the
            // spans plus the 1000 ms tail to the line boundary) until LyricPace v23 stopped charging
            // anything after the final vocal end.
            Assert.That((double)beatmap.Wpm, Is.EqualTo(138).Within(1e-6));
            Assert.That(beatmap.Difficulty, Is.EqualTo(starsAtRate(1)).Within(1e-12), "the model's own reading of the stored blob");
            Assert.That(beatmap.Difficulty, Is.EqualTo(3.6210541182715925).Within(1e-9)); // chunked-axis stars, LyricPace v24 (3.6309 at v22, before an unsubdivided word became one segment)
            Assert.That(beatmap.Lyrics, Is.EqualTo("neon lights are calling")); // the lyrics: search haystack

            // 033_target_wpm.sql: written by the same upsert. Since LyricPace v21 it is the map's
            // hardest window by raw speed, read off the difficulty model and floored at the whole-map
            // average, so it is a number on a map far too short for the 16-cell rolling window: that
            // is why this is a number where peak_wpm is NULL. The pace_version stamp is what makes the startup sweep skip this
            // row, so it has to be the CURRENT version or the new column would be filled twice over.
            // The model's own window reads 97.99, which cleared the old 92 WPM average and sits
            // under the v23 one, so the floor is what the column holds now.
            Assert.That(beatmap.TargetWpm, Is.EqualTo(138).Within(1e-9), "below the 138 WPM average, so the floor");
            Assert.That(beatmap.PaceVersion, Is.EqualTo(LyricPace.VERSION));

            // 034_ratings_matrix.sql: the eighteen readings pp prices a play from, written by the
            // same upsert. Its arm-none rate-1.0 stars ARE difficulty_rating, which is what keeps
            // the matrix and the legacy columns one computation rather than two.
            Assert.That(beatmap.Ratings, Is.Not.Null, "the matrix is written at ingest, not by a later sweep");

            var ingested = BeatmapRatings.Parse(beatmap.Ratings)!;

            Assert.That(ingested.Count, Is.EqualTo(18));
            Assert.That(ingested.TryGet(LyricDifficulty.JudgementArm.None, false, 1.0)!.Value.Stars,
                Is.EqualTo(beatmap.Difficulty).Within(1e-12));
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
            ("map.osu", SyntheticPackage.Utf8(SyntheticPackage.OsuText(title: "Neon Nights", titleUnicode: "Neon Nights", beatmapId: 1001, beatmapSetId: setId, previewTime: 1500, lyrics: fixture_lyrics))),
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
            ("map.osu", SyntheticPackage.Utf8(SyntheticPackage.OsuText(title: "Neon Nights", titleUnicode: "Neon Nights", beatmapId: 1001, beatmapSetId: setId, previewTime: 2500, lyrics: fixture_lyrics))),
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
            // The fixture lyric: 23 cells over 2000 ms of word spans (LyricPace v23).
            Assert.That((double)row.Wpm, Is.EqualTo(138).Within(1e-6));
            Assert.That(row.Difficulty, Is.EqualTo(starsAtRate(1)).Within(1e-12)); // chunked-axis stars
            Assert.That(row.WordCount, Is.EqualTo(4));
            Assert.That(row.CharCount, Is.EqualTo(23));
            Assert.That(row.Lyrics, Is.EqualTo("neon lights are calling")); // v8 fills the lyrics: haystack
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
                language: "Japanese", previewTime: 3500, lyrics: fixture_lyrics))),
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
                previewTime: 4500, lyrics: fixture_lyrics))),
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

        var row = await conn.QuerySingleAsync<(double Base, double? Dt, double? Ht, double? Lt, double? LtDt, double? LtHt)>(
            """
            SELECT difficulty_rating AS Base, sr_dt AS Dt, sr_ht AS Ht,
                   sr_literate AS Lt, sr_literate_dt AS LtDt, sr_literate_ht AS LtHt
            FROM beatmaps WHERE id = 1001
            """);

        Assert.Multiple(() =>
        {
            Assert.That(row.Dt, Is.Not.Null, "sr_dt is written at ingest, never derived at query time");
            Assert.That(row.Ht, Is.Not.Null);

            // 029_literate_stars.sql: the same three for the map the client's Literate mod converts
            // this one into, written at ingest for the same reason. THIS FIXTURE'S LYRIC IS
            // "neon lights are calling", which carries no mark and no capital, so its converted stream is the same string and
            // all three equal their plain counterparts. That is the correct value, not a missing
            // one, and it is what every map authored before punctuation existed stores; the
            // WireCompat suite is where a genuinely punctuated map pins the difference.
            Assert.That(row.Lt, Is.Not.Null, "sr_literate is written at ingest too");
            Assert.That(row.LtDt, Is.Not.Null);
            Assert.That(row.LtHt, Is.Not.Null);

            Assert.That(row.Lt!.Value, Is.EqualTo(literateStarsAtRate(1)).Within(1e-9));
            Assert.That(row.LtDt!.Value, Is.EqualTo(literateStarsAtRate(RateMods.DoubleTimeBaseRate)).Within(1e-9));
            Assert.That(row.LtHt!.Value, Is.EqualTo(literateStarsAtRate(RateMods.HalfTimeBaseRate)).Within(1e-9));

            // Exactly LyricDifficulty at the two BASE rates, the only two ratings pp ever needs.
            Assert.That(row.Dt!.Value, Is.EqualTo(starsAtRate(RateMods.DoubleTimeBaseRate)).Within(1e-9));
            Assert.That(row.Ht!.Value, Is.EqualTo(starsAtRate(RateMods.HalfTimeBaseRate)).Within(1e-9));

            // All three differ, i.e. the rate genuinely moves the rating rather than the columns
            // being copies of the base one. (No DIRECTION is asserted here even though this
            // fixture does now order them HT < base < DT: the ordering is LyricDifficulty's
            // business and is pinned there, and on a map short enough to fall out of the longer
            // scheduled windows at 1.50x it can legitimately invert. The pp-side contract, "a
            // harder rating is worth more pp", is pinned in PerformancePointsTest where the
            // ratings are inputs.)
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
        //
        // SPOTLESS SINCE PerformancePoints v22, and the reason is worth stating: the miss penalty is
        // judged against the map's DIFFICULT CHARACTERS now, and this fixture's note count (120) is
        // a fiction against a 23-cell map, so its eight misses read as several times the whole
        // map's difficulty and price every arm to exactly zero. That made the DT/no-mod comparison
        // below 0 against 0, which is the same way this fixture went degenerate at the backlog-97
        // cliff. It is a PLUMBING test (does the DT row read sr_dt), so the flubs are dropped
        // rather than retuned: any priced play proves the plumbing.
        long noMod = await insertPlayAsync(conn, playerId, "[]");
        long baseRateDt = await insertPlayAsync(conn, playerId, """[{"acronym":"DT","settings":{"speed_change":1.5}}]""");
        long customRateDt = await insertPlayAsync(conn, playerId, """[{"acronym":"DT","settings":{"speed_change":1.75}}]""");
        long unranked = await insertPlayAsync(conn, playerId, "[]", ranked: false);

        Assert.That(await ppOf(conn, noMod), Is.Zero, "nothing is priced before the sweep runs");

        await PpBackfill.RunAsync(db, NullLogger.Instance);

        var beatmap = await conn.QuerySingleAsync<(double Base, double Dt, string Ratings)>(
            "SELECT difficulty_rating AS Base, sr_dt AS Dt, ratings::text AS Ratings FROM beatmaps WHERE id = 1001");

        // The matrix the ingest wrote (034_ratings_matrix.sql), which is what the pricing reads: its
        // arm-none stars ARE the two columns above, and it carries the difficult characters as well,
        // which no column does.
        var ratings = BeatmapRatings.Parse(beatmap.Ratings)!;
        var noModCell = ratings.TryGet(LyricDifficulty.JudgementArm.None, false, 1.0)!.Value;
        var dtCell = ratings.TryGet(LyricDifficulty.JudgementArm.None, false, RateMods.DoubleTimeBaseRate)!.Value;

        // 120 great = 120 notes; ignore_hit is not a note (see insertPlayAsync). NO MISSES, for the
        // reason given where the plays are inserted: this fixture's note count is unrelated to its
        // 23-cell map, so any miss count at all prices every arm to zero under v22.
        double expectedNoMod = PerformancePoints.Compute(noModCell.Stars, 120, noModCell.DifficultCharacters, 0, 0.9, 100, []);
        double expectedDt = PerformancePoints.Compute(dtCell.Stars, 120, dtCell.DifficultCharacters, 0, 0.9, 100, []);

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
        //
        // THE VERSION ARM IS WHAT IS PINNED HERE, on its own, because that is the arm a star-model
        // change travels on: LyricPace.VERSION bumps (v9, v10, v12, v14, v17, v19 for the envelope
        // model and v20 for its 12.0 anchor), the sweep re-rates every row one generation behind,
        // and the pp_version = 0
        // stamp on that row's scores is what hands them to PpBackfill in the same startup. The two
        // rate columns are deliberately left FILLED, so the second (IS NULL) staleness arm cannot
        // be what selects the row and this cannot pass for the wrong reason.
        await conn.ExecuteAsync(
            "UPDATE beatmaps SET difficulty_rating = 9.9, pace_version = @previous WHERE id = 1001",
            new { previous = LyricPace.VERSION - 1 });

        Assert.That(await conn.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM beatmaps WHERE id = 1001 AND (sr_dt IS NULL OR sr_ht IS NULL)"),
            Is.Zero, "only the version arm may select this row");

        await PaceBackfill.RunAsync(db, fileStore, NullLogger.Instance);

        Assert.Multiple(async () =>
        {
            Assert.That(await conn.ExecuteScalarAsync<int>(
                    "SELECT count(*) FROM scores WHERE beatmap_id = 1001 AND pp_version = 0"),
                Is.GreaterThan(0), "rewriting a map's stars hands its scores back to the pp sweep");

            Assert.That(await conn.ExecuteScalarAsync<int>(
                    "SELECT pace_version FROM beatmaps WHERE id = 1001"),
                Is.EqualTo(LyricPace.VERSION), "and the row is stamped at the current arithmetic");
        });

        await PpBackfill.RunAsync(db, NullLogger.Instance);

        Assert.That(await ppOf(conn, noMod), Is.EqualTo(before).Within(1e-9),
            "recomputed against the same reparsed blob, so the value comes back identical");
    }


    /// <summary>
    /// 029_literate_stars.sql, the Literate half of the pair above. Two things have to hold and
    /// they pull in opposite directions:
    ///
    /// <list type="number">
    /// <item>A Literate play whose converted rating is NOT stored must be written UNPRICED and
    /// retried, never priced off the unconverted map's rating. That is the existing rule for a
    /// Double Time play on a map without <c>sr_dt</c>, followed rather than reinvented.</item>
    /// <item>The startup sweep must actually fill the columns for every row that predates them,
    /// which is what the <c>LyricPace.VERSION</c> bump to 13 is for. Unlike 020, which added a
    /// second arm to the staleness predicate instead, the version arm is used here: the deferral
    /// that made 020 avoid a bump was spent at v9.</item>
    /// </list>
    /// </summary>
    [Test]
    [Order(13)]
    public async Task ALiteratePlayIsUnpricedUntilThePaceSweepFillsTheConvertedRatings()
    {
        await using var conn = await db.OpenAsync();

        long playerId = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO users (username, email, password_hash, country_code)
            VALUES ('literate typist', 'literate.typist@example.com', 'x', 'US')
            RETURNING id
            """);

        long literatePlay = await insertPlayAsync(conn, playerId, """[{"acronym":"LT"}]""");

        // The state every existing row is in the moment 029 deploys: the three new columns NULL and
        // the pace stamp one generation behind. The RATING MATRIX goes with them (034): it is what a
        // price reads now, so leaving it behind is what makes the play below pending at all.
        await conn.ExecuteAsync(
            """
            UPDATE beatmaps
            SET sr_literate = NULL, sr_literate_dt = NULL, sr_literate_ht = NULL, ratings = NULL, pace_version = 12
            WHERE id = 1001
            """);

        await conn.ExecuteAsync("UPDATE scores SET pp = 0, pp_version = 0 WHERE id = @id", new { id = literatePlay });

        await PpBackfill.RunAsync(db, NullLogger.Instance);

        int pendingVersion = await ppVersionOf(conn, literatePlay);
        double pendingPp = await ppOf(conn, literatePlay);

        Assert.Multiple(() =>
        {
            Assert.That(pendingVersion, Is.Zero,
                "left stale for the next boot, not stamped at a price the sweep would disagree with");
            Assert.That(pendingPp, Is.Zero);
        });

        // Now the pace sweep reaches the map, on the VERSION arm alone.
        await PaceBackfill.RunAsync(db, fileStore, NullLogger.Instance);

        var beatmap = await conn.QuerySingleAsync<(double? Lt, int PaceVersion, string Ratings)>(
            "SELECT sr_literate AS Lt, pace_version AS PaceVersion, ratings::text AS Ratings FROM beatmaps WHERE id = 1001");

        var literateCell = BeatmapRatings.Parse(beatmap.Ratings)!.TryGet(LyricDifficulty.JudgementArm.None, true, 1.0)!.Value;

        await PpBackfill.RunAsync(db, NullLogger.Instance);

        double priced = await ppOf(conn, literatePlay);
        int stamped = await ppVersionOf(conn, literatePlay);

        Assert.Multiple(() =>
        {
            Assert.That(beatmap.PaceVersion, Is.EqualTo(LyricPace.VERSION));
            Assert.That(beatmap.Lt, Is.EqualTo(literateStarsAtRate(1)).Within(1e-9));

            Assert.That(stamped, Is.EqualTo(PerformancePoints.VERSION), "and now it settles");

            // Priced off sr_literate, with NO mod multiplier of its own: since backlog 144 Literate
            // contributes nothing to modMult, so on this mark-free fixture (whose converted rating
            // equals its plain one) the play is worth exactly what the no-mod play is.
            Assert.That(priced, Is.EqualTo(PerformancePoints.Compute(
                literateCell.Stars, 120, literateCell.DifficultCharacters, 0, 0.9, 100, [])).Within(1e-9));
        });
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
                 '{"great":120,"ignore_hit":8}'::jsonb,
                 '{"great":120,"ignore_hit":8}'::jsonb)
            RETURNING id
            """,
            new { userId, modsJson, ranked });

    private static async Task<double> ppOf(NpgsqlConnection conn, long scoreId)
        => await conn.ExecuteScalarAsync<double>("SELECT pp FROM scores WHERE id = @scoreId", new { scoreId });

    private static async Task<int> ppVersionOf(NpgsqlConnection conn, long scoreId)
        => await conn.ExecuteScalarAsync<int>("SELECT pp_version FROM scores WHERE id = @scoreId", new { scoreId });

    /// <summary>
    /// The seeded difficulty's stars at a given clock rate, straight from the model. Every upload of
    /// the shared set carries <see cref="fixture_lyrics"/>, so this is the same input the ingested
    /// blob carries.
    /// </summary>
    private static double starsAtRate(double rate)
    {
        var parsed = BeatmapPackageParser.ParseDifficulty("map.osu", SyntheticPackage.Utf8(SyntheticPackage.OsuText(lyrics: fixture_lyrics)));

        return LyricDifficulty.Compute(parsed.Lines, rate);
    }

    /// <summary>The same, on the stream the client's Literate mod converts the map to.</summary>
    private static double literateStarsAtRate(double rate)
    {
        var parsed = BeatmapPackageParser.ParseDifficulty("map.osu", SyntheticPackage.Utf8(SyntheticPackage.OsuText(lyrics: fixture_lyrics)));

        return LyricDifficulty.Compute(parsed.Lines, rate, literate: true);
    }


    // ---------------------------------------------------------------------------------------------
    // Gameplay fingerprint + automatic ranked -> pending demotion (030_gameplay_fingerprint.sql,
    // backlog 173). Each of these owns a FRESH set so the shared fixture set above is untouched.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The fixture lyric, retimed: identical words, every time shifted. This is the edit
    /// beatmaps.lyrics (the search haystack) is structurally blind to, and the whole reason the
    /// fingerprint could not just reuse that column.
    /// </summary>
    private const string retimed_lyrics =
        """
        {"version":2,"song_end_ms":4000,"granularity":"Word"}
        {"text":"ab cd","start_ms":1200,"end_ms":3400,"words":[{"text":"ab","start_ms":1200,"end_ms":2100,"score":1},{"text":"cd","start_ms":2100,"end_ms":3400,"score":1}]}
        """;

    /// <summary>The fixture lyric with only the menu-only beatdrop added to the header.</summary>
    private const string beatdrop_lyrics =
        """
        {"version":2,"song_end_ms":4000,"beatdrop_ms":1750,"granularity":"Word"}
        {"text":"ab cd","start_ms":1000,"end_ms":3000,"words":[{"text":"ab","start_ms":1000,"end_ms":2000,"score":1},{"text":"cd","start_ms":2000,"end_ms":3000,"score":1}]}
        """;

    /// <summary>A second difficulty's lyric, so an added diff carries real content.</summary>
    private const string second_diff_lyrics =
        """
        {"version":2,"song_end_ms":9000,"granularity":"Line"}
        {"text":"we type at night","start_ms":1000,"end_ms":8000}
        """;

    private long rankedSetId;
    private long rankedDiffA;
    private long rankedDiffB;

    /// <summary>
    /// Creates a set with two allocated difficulty rows, ingests a first version carrying only
    /// difficulty A, and forces it to 'ranked' exactly as a reviewer would.
    /// </summary>
    private async Task<(long SetId, long DiffA, long DiffB)> newRankedSetAsync()
    {
        await using var conn = await db.OpenAsync();

        long id = await conn.ExecuteScalarAsync<long>(
            "INSERT INTO beatmapsets (owner_id, status, intended_status) VALUES (@uploaderId, 'hidden', 'pending') RETURNING id",
            new { uploaderId });

        long a = await conn.ExecuteScalarAsync<long>(
            "INSERT INTO beatmaps (set_id, checksum_md5) VALUES (@id, md5(random()::text)) RETURNING id", new { id });
        long b = await conn.ExecuteScalarAsync<long>(
            "INSERT INTO beatmaps (set_id, checksum_md5) VALUES (@id, md5(random()::text)) RETURNING id", new { id });

        rankedSetId = id;
        rankedDiffA = a;
        rankedDiffB = b;

        await ingestRankedAsync(
            ("a.osu", SyntheticPackage.Utf8(SyntheticPackage.OsuText(beatmapId: a, beatmapSetId: id))),
            ("audio.mp3", SyntheticPackage.Utf8("fake audio bytes")),
            ("bg.jpg", SyntheticPackage.TinyPng()));

        await conn.ExecuteAsync("UPDATE beatmapsets SET status = 'ranked' WHERE id = @id", new { id });

        return (id, a, b);
    }

    /// <summary>Ingests into the current ranked-demotion set with both of its allocated ids permitted.</summary>
    private async Task ingestRankedAsync(params (string Name, byte[] Content)[] entries)
    {
        using var zip = SyntheticPackage.Zip(entries);
        var parsed = BeatmapPackageParser.Parse(zip);

        PackageValidator.Validate(parsed, rankedSetId, [rankedDiffA, rankedDiffB], "uploader");

        await using var scope = await ingest.BeginSetScopeAsync(rankedSetId);
        await ingest.IngestAsync(scope, zip, parsed, rankedSetId, uploaderId);
    }

    private async Task<string?> statusOfAsync(long id)
    {
        await using var conn = await db.OpenAsync();
        return await conn.ExecuteScalarAsync<string?>("SELECT status FROM beatmapsets WHERE id = @id", new { id });
    }

    private async Task<List<(string Action, long ActorId, string Note)>> auditOfAsync(long id)
    {
        await using var conn = await db.OpenAsync();

        return (await conn.QueryAsync<(string Action, long ActorId, string Note)>(
            "SELECT action AS Action, actor_id AS ActorId, note AS Note FROM moderation_actions WHERE set_id = @id ORDER BY id",
            new { id })).ToList();
    }

    [Test]
    [Order(14)]
    public async Task GameplayChange_DemotesARankedSet_AndAuditsItAsAutoUnrank()
    {
        var (id, a, _) = await newRankedSetAsync();

        string? before;
        await using (var conn = await db.OpenAsync())
            before = await conn.ExecuteScalarAsync<string?>("SELECT gameplay_fingerprint FROM beatmaps WHERE id = @a", new { a });

        Assert.That(before, Does.StartWith($"v{GameplayFingerprint.VERSION}:"), "ingest stamps the fingerprint with its recipe version");

        // Identical words, shifted times: invisible to beatmaps.lyrics, invisible to a metadata
        // comparison, and the single most gameplay-affecting edit a mapper can make.
        await ingestRankedAsync(
            ("a.osu", SyntheticPackage.Utf8(SyntheticPackage.OsuText(beatmapId: a, beatmapSetId: id, lyrics: retimed_lyrics))),
            ("audio.mp3", SyntheticPackage.Utf8("fake audio bytes")),
            ("bg.jpg", SyntheticPackage.TinyPng()));

        Assert.That(await statusOfAsync(id), Is.EqualTo("pending"), "a retimed ranked map goes back for re-review");

        await using (var conn = await db.OpenAsync())
        {
            string? after = await conn.ExecuteScalarAsync<string?>("SELECT gameplay_fingerprint FROM beatmaps WHERE id = @a", new { a });
            Assert.That(after, Is.Not.EqualTo(before), "the stored fingerprint moves with the timing");

            // The lyrics haystack did NOT move, which is exactly why it could not have caught this.
            Assert.That(await conn.ExecuteScalarAsync<string?>("SELECT lyrics FROM beatmaps WHERE id = @a", new { a }),
                Is.EqualTo("ab cd"));
        }

        var audit = await auditOfAsync(id);

        Assert.Multiple(() =>
        {
            Assert.That(audit, Has.Count.EqualTo(1));
            Assert.That(audit[0].Action, Is.EqualTo("auto_unrank"), "distinct from the reviewer's manual 'unrank'");
            Assert.That(audit[0].ActorId, Is.EqualTo(uploaderId), "actor_id is NOT NULL, so the uploading mapper owns the row");
            Assert.That(audit[0].Note, Does.Contain("1 changed"));
        });
    }

    [Test]
    [Order(15)]
    public async Task MetadataOnlyChange_KeepsTheRank()
    {
        var (id, a, _) = await newRankedSetAsync();

        // Everything the item listed as must-not-demote, in one upload: title, both unicode
        // variants, artist, difficulty name, source, tags, language, background, video, preview
        // time. Plus a brand-new unrelated file, so a new version really is cut.
        await ingestRankedAsync(
            ("a.osu", SyntheticPackage.Utf8(SyntheticPackage.OsuText(
                title: "Neon Nights (Remastered)", titleUnicode: "Neon Nights R",
                artist: "Synth Rider and Friends", artistUnicode: "Synth Rider F",
                version: "insane", source: "Some Album", tags: "retimed nothing at all",
                language: "Japanese", background: "bg2.jpg", video: "clip.mp4", previewTime: 7500,
                beatmapId: a, beatmapSetId: id))),
            ("audio.mp3", SyntheticPackage.Utf8("fake audio bytes")),
            ("bg2.jpg", SyntheticPackage.TinyPng()),
            ("clip.mp4", SyntheticPackage.Utf8("not really a video")),
            ("extra.txt", SyntheticPackage.Utf8("liner notes")));

        Assert.That(await statusOfAsync(id), Is.EqualTo("ranked"), "a cosmetic re-upload must not cost the mapper their rank");
        Assert.That(await auditOfAsync(id), Is.Empty);

        await using var conn = await db.OpenAsync();

        // The metadata really did land, so this is a "no demotion" pin and not a "nothing
        // happened" one.
        Assert.That(await conn.ExecuteScalarAsync<string?>("SELECT title FROM beatmapsets WHERE id = @id", new { id }),
            Is.EqualTo("Neon Nights (Remastered)"));
        Assert.That(await conn.ExecuteScalarAsync<int>("SELECT current_version FROM beatmapsets WHERE id = @id", new { id }),
            Is.EqualTo(2), "a new version WAS cut; the fingerprint simply did not move");
    }

    [Test]
    [Order(16)]
    public async Task BeatdropOnlyChange_KeepsTheRank()
    {
        var (id, a, _) = await newRankedSetAsync();

        // The game normalises beatdrop_ms out of its own ranked-status comparison
        // (TypeBeatRuleset.NativeEncodingsEquivalentForStatus) because the beatdrop only
        // soundtracks the main-menu intro. The server draws the line in the same place.
        await ingestRankedAsync(
            ("a.osu", SyntheticPackage.Utf8(SyntheticPackage.OsuText(beatmapId: a, beatmapSetId: id, lyrics: beatdrop_lyrics))),
            ("audio.mp3", SyntheticPackage.Utf8("fake audio bytes")),
            ("bg.jpg", SyntheticPackage.TinyPng()));

        Assert.That(await statusOfAsync(id), Is.EqualTo("ranked"));
        Assert.That(await auditOfAsync(id), Is.Empty);
    }

    [Test]
    [Order(17)]
    public async Task AudioSwapWithIdenticalLyrics_Demotes()
    {
        var (id, a, _) = await newRankedSetAsync();

        byte[] unchangedOsu = SyntheticPackage.Utf8(SyntheticPackage.OsuText(beatmapId: a, beatmapSetId: id));

        // Backlog 171 made this reachable: the .osu is byte-identical (same filename, same every
        // timing), only the recording behind audio.mp3 changed. A fingerprint over lyrics alone
        // would keep the rank on timings that no longer match the song.
        await ingestRankedAsync(
            ("a.osu", unchangedOsu),
            ("audio.mp3", SyntheticPackage.Utf8("a completely different recording")),
            ("bg.jpg", SyntheticPackage.TinyPng()));

        await using (var conn = await db.OpenAsync())
        {
            Assert.That(await conn.ExecuteScalarAsync<string?>("SELECT checksum_md5 FROM beatmaps WHERE id = @a", new { a }),
                Is.EqualTo(Convert.ToHexStringLower(MD5.HashData(unchangedOsu))),
                "the difficulty file itself did not change at all");
        }

        Assert.That(await statusOfAsync(id), Is.EqualTo("pending"));
        Assert.That((await auditOfAsync(id))[0].Action, Is.EqualTo("auto_unrank"));
    }

    [Test]
    [Order(18)]
    public async Task RenamingTheAudioWithoutChangingItsBytes_KeepsTheRank()
    {
        var (id, a, _) = await newRankedSetAsync();

        // The audio is fingerprinted by BYTES, not by name, so pointing the same recording at a
        // new filename is the cosmetic edit it looks like.
        await ingestRankedAsync(
            ("a.osu", SyntheticPackage.Utf8(SyntheticPackage.OsuText(beatmapId: a, beatmapSetId: id, audioFilename: "song.mp3"))),
            ("song.mp3", SyntheticPackage.Utf8("fake audio bytes")),
            ("bg.jpg", SyntheticPackage.TinyPng()));

        Assert.That(await statusOfAsync(id), Is.EqualTo("ranked"));
        Assert.That(await auditOfAsync(id), Is.Empty);
    }

    [Test]
    [Order(19)]
    public async Task AddingADifficultyToARankedSet_Demotes()
    {
        var (id, a, b) = await newRankedSetAsync();

        await ingestRankedAsync(
            ("a.osu", SyntheticPackage.Utf8(SyntheticPackage.OsuText(beatmapId: a, beatmapSetId: id))),
            ("b.osu", SyntheticPackage.Utf8(SyntheticPackage.OsuText(version: "hard", beatmapId: b, beatmapSetId: id, lyrics: second_diff_lyrics))),
            ("audio.mp3", SyntheticPackage.Utf8("fake audio bytes")),
            ("bg.jpg", SyntheticPackage.TinyPng()));

        Assert.That(await statusOfAsync(id), Is.EqualTo("pending"),
            "a new difficulty arrives pre-ranked with a live board no reviewer has seen");
        Assert.That((await auditOfAsync(id))[0].Note, Does.Contain("1 added"));
    }

    [Test]
    [Order(20)]
    public async Task RemovingADifficultyFromARankedSet_Demotes()
    {
        var (id, a, b) = await newRankedSetAsync();

        // Grow to two diffs, then re-rank, so the removal below starts from a ranked two-diff set.
        await ingestRankedAsync(
            ("a.osu", SyntheticPackage.Utf8(SyntheticPackage.OsuText(beatmapId: a, beatmapSetId: id))),
            ("b.osu", SyntheticPackage.Utf8(SyntheticPackage.OsuText(version: "hard", beatmapId: b, beatmapSetId: id, lyrics: second_diff_lyrics))),
            ("audio.mp3", SyntheticPackage.Utf8("fake audio bytes")),
            ("bg.jpg", SyntheticPackage.TinyPng()));

        await using (var conn = await db.OpenAsync())
        {
            await conn.ExecuteAsync("UPDATE beatmapsets SET status = 'ranked' WHERE id = @id", new { id });
            await conn.ExecuteAsync("DELETE FROM moderation_actions WHERE set_id = @id", new { id });

            // What the BSS metadata PUT does in the request BEFORE the upload: it drops the diff by
            // nulling filename. The fingerprint is deliberately NOT cleared there, which is the
            // only reason the ingest below can still tell that b used to be part of the set.
            await conn.ExecuteAsync("UPDATE beatmaps SET filename = NULL WHERE id = @b", new { b });
        }

        await ingestRankedAsync(
            ("a.osu", SyntheticPackage.Utf8(SyntheticPackage.OsuText(beatmapId: a, beatmapSetId: id))),
            ("audio.mp3", SyntheticPackage.Utf8("fake audio bytes")),
            ("bg.jpg", SyntheticPackage.TinyPng()));

        Assert.That(await statusOfAsync(id), Is.EqualTo("pending"), "a removed difficulty takes a ranked leaderboard offline");
        Assert.That((await auditOfAsync(id))[0].Note, Does.Contain("1 removed"));

        await using (var conn = await db.OpenAsync())
        {
            Assert.That(await conn.ExecuteScalarAsync<string?>("SELECT gameplay_fingerprint FROM beatmaps WHERE id = @b", new { b }),
                Is.Null, "a diff that stopped being live loses its fingerprint with its filename");
        }
    }

    [Test]
    [Order(21)]
    public async Task PendingAndUnrankedSets_AreUnaffected()
    {
        foreach (string status in new[] { "pending", "unranked" })
        {
            var (id, a, _) = await newRankedSetAsync();

            await using (var conn = await db.OpenAsync())
                await conn.ExecuteAsync("UPDATE beatmapsets SET status = @status WHERE id = @id", new { id, status });

            await ingestRankedAsync(
                ("a.osu", SyntheticPackage.Utf8(SyntheticPackage.OsuText(beatmapId: a, beatmapSetId: id, lyrics: retimed_lyrics))),
                ("audio.mp3", SyntheticPackage.Utf8("fake audio bytes")),
                ("bg.jpg", SyntheticPackage.TinyPng()));

            Assert.That(await statusOfAsync(id), Is.EqualTo(status), $"a '{status}' set has no rank to lose");
            Assert.That(await auditOfAsync(id), Is.Empty, status);
        }
    }

    [Test]
    [Order(22)]
    public async Task Demotion_LeavesExistingScoresAttachedAndRanked()
    {
        var (id, a, _) = await newRankedSetAsync();

        await using (var conn = await db.OpenAsync())
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO scores (user_id, beatmap_id, total_score, accuracy, max_combo, rank, passed, ranked)
                VALUES (@uploaderId, @a, 500000, 0.97, 40, 'A', true, true)
                """,
                new { uploaderId, a });
        }

        await ingestRankedAsync(
            ("a.osu", SyntheticPackage.Utf8(SyntheticPackage.OsuText(beatmapId: a, beatmapSetId: id, lyrics: retimed_lyrics))),
            ("audio.mp3", SyntheticPackage.Utf8("fake audio bytes")),
            ("bg.jpg", SyntheticPackage.TinyPng()));

        Assert.That(await statusOfAsync(id), Is.EqualTo("pending"));

        await using (var conn = await db.OpenAsync())
        {
            var score = await conn.QuerySingleAsync<(long BeatmapId, bool Ranked)>(
                "SELECT beatmap_id AS BeatmapId, ranked AS Ranked FROM scores WHERE beatmap_id = @a", new { a });

            Assert.Multiple(() =>
            {
                // Deliberate, and a known limitation: scores.beatmap_id has no version link, so the
                // schema cannot tell a play on the old content from a play on the new. Nothing is
                // wiped; the rows come back with the set when a reviewer re-ranks it.
                Assert.That(score.BeatmapId, Is.EqualTo(a));
                Assert.That(score.Ranked, Is.True, "a demotion never rewrites scores.ranked");
            });
        }
    }

    [Test]
    [Order(23)]
    public async Task GameplayFingerprintBackfill_RefillsLiveRows_AndLeavesDeadOnesNull()
    {
        var (id, a, b) = await newRankedSetAsync();

        string? expected;

        await using (var conn = await db.OpenAsync())
        {
            expected = await conn.ExecuteScalarAsync<string?>("SELECT gameplay_fingerprint FROM beatmaps WHERE id = @a", new { a });

            // Exactly the state prod is in the moment 030 applies: the column exists, every ranked
            // map predates it, nothing has a value. Row b is an allocated-but-never-uploaded shell,
            // i.e. the not-live case.
            await conn.ExecuteAsync("UPDATE beatmaps SET gameplay_fingerprint = NULL WHERE set_id = @id", new { id });
        }

        await GameplayFingerprintBackfill.RunAsync(db, fileStore, NullLogger.Instance);

        await using (var conn = await db.OpenAsync())
        {
            Assert.That(await conn.ExecuteScalarAsync<string?>("SELECT gameplay_fingerprint FROM beatmaps WHERE id = @a", new { a }),
                Is.EqualTo(expected), "recomputing from the stored blobs reproduces what ingest wrote");
            Assert.That(await conn.ExecuteScalarAsync<string?>("SELECT gameplay_fingerprint FROM beatmaps WHERE id = @b", new { b }),
                Is.Null, "a row that is not live in the current version has no fingerprint to fill");
        }

        // With the column filled, the very next upload behaves normally: a metadata-only edit on a
        // still-ranked map keeps its rank. This is the deploy-day case the sweep exists for.
        Assert.That(await statusOfAsync(id), Is.EqualTo("ranked"));

        await ingestRankedAsync(
            ("a.osu", SyntheticPackage.Utf8(SyntheticPackage.OsuText(title: "Backfilled", beatmapId: a, beatmapSetId: id))),
            ("audio.mp3", SyntheticPackage.Utf8("fake audio bytes")),
            ("bg.jpg", SyntheticPackage.TinyPng()));

        Assert.That(await statusOfAsync(id), Is.EqualTo("ranked"));

        // Idempotent: a second sweep finds nothing stale and changes nothing.
        await GameplayFingerprintBackfill.RunAsync(db, fileStore, NullLogger.Instance);

        await using (var conn = await db.OpenAsync())
        {
            // Scoped to this set on purpose. The fixture also holds hand-seeded beatmap rows whose
            // set has no set_versions at all (the LanguageBackfill cases above write beatmaps.lyrics
            // directly), and those are the documented unreachable case: no current version means no
            // manifest and no blob to recompute from. They are not producible through the real
            // upload path, where a filename is only ever written by an ingest that cut a version.
            int stale = await conn.ExecuteScalarAsync<int>(
                """
                SELECT count(*) FROM beatmaps
                WHERE set_id = @id AND filename IS NOT NULL
                  AND (gameplay_fingerprint IS NULL OR gameplay_fingerprint NOT LIKE @current)
                """,
                new { id, current = GameplayFingerprint.CurrentVersionLikePattern });
            Assert.That(stale, Is.Zero);
        }
    }

    [Test]
    [Order(24)]
    public void CanonicalForm_ExcludesMetadataAndTheBeatdrop_ButNotTheTiming()
    {
        var files = new List<PackageFileEntry>
        {
            new PackageFileEntry(SHA256.HashData(SyntheticPackage.Utf8("audio")), 5, "audio.mp3"),
        };

        string form(string osu) =>
            GameplayFingerprint.CanonicalForm(BeatmapPackageParser.ParseDifficulty("m.osu", SyntheticPackage.Utf8(osu)), files);

        string baseline = form(SyntheticPackage.OsuText());

        Assert.Multiple(() =>
        {
            // Nothing cosmetic reaches the hashed bytes at all, which is a stronger statement than
            // "the two hashes happened to collide".
            Assert.That(form(SyntheticPackage.OsuText(title: "X", titleUnicode: "Y", artist: "Z", artistUnicode: "W",
                    creator: "someone else", version: "another diff", source: "Album", tags: "a b c",
                    language: "Japanese", background: "other.png", video: "v.mp4", previewTime: 9999)),
                Is.EqualTo(baseline));

            Assert.That(form(SyntheticPackage.OsuText(lyrics: beatdrop_lyrics)), Is.EqualTo(baseline),
                "beatdrop_ms is normalised out, mirroring TypeBeatRuleset.NativeEncodingsEquivalentForStatus");

            Assert.That(form(SyntheticPackage.OsuText(lyrics: retimed_lyrics)), Is.Not.EqualTo(baseline));

            // The audio arm, driven purely by the resolved blob hash.
            Assert.That(baseline, Does.Contain("audio:" + Convert.ToHexStringLower(SHA256.HashData(SyntheticPackage.Utf8("audio")))));
        });
    }

    /// <summary>A flagged freestyle line: three '&amp;' slots the parser keeps as cells.</summary>
    private const string freestyle_lyrics =
        """
        {"version":2,"song_end_ms":9000,"granularity":"Line"}
        {"text":"take me &&& with you tonight","start_ms":1000,"end_ms":2600,"freestyle":true}
        """;

    [Test]
    [Order(25)]
    public async Task Ingest_StoresTheFreestyleCellCount_AndTheSweepRefillsIt()
    {
        // 031_freestyle_cell_count.sql. The column is a SUBSET of char_count (markers have counted
        // towards the pace since v6), and it is written on every difficulty, 0 included: NULL means
        // one thing only, that the v16 sweep has not reached the row.
        await using var conn = await db.OpenAsync();

        long id = await conn.ExecuteScalarAsync<long>(
            "INSERT INTO beatmapsets (owner_id, status, intended_status) VALUES (@uploaderId, 'hidden', 'pending') RETURNING id",
            new { uploaderId });

        long plain = await conn.ExecuteScalarAsync<long>(
            "INSERT INTO beatmaps (set_id, checksum_md5) VALUES (@id, md5(random()::text)) RETURNING id", new { id });
        long free = await conn.ExecuteScalarAsync<long>(
            "INSERT INTO beatmaps (set_id, checksum_md5) VALUES (@id, md5(random()::text)) RETURNING id", new { id });

        (string Name, byte[] Content)[] entries =
        [
            ("plain.osu", SyntheticPackage.Utf8(SyntheticPackage.OsuText(beatmapId: plain, beatmapSetId: id))),
            ("free.osu", SyntheticPackage.Utf8(SyntheticPackage.OsuText(version: "freestyle", beatmapId: free, beatmapSetId: id, lyrics: freestyle_lyrics))),
            ("audio.mp3", SyntheticPackage.Utf8("fake audio bytes")),
            ("bg.jpg", SyntheticPackage.TinyPng()),
        ];

        using (var zip = SyntheticPackage.Zip(entries))
        {
            var parsed = BeatmapPackageParser.Parse(zip);
            PackageValidator.Validate(parsed, id, [plain, free], "uploader");

            await using var scope = await ingest.BeginSetScopeAsync(id);
            await ingest.IngestAsync(scope, zip, parsed, id, uploaderId);
        }

        async Task<(int? Freestyle, int Chars, double Rating)> rowOf(long beatmapId) =>
            await conn.QuerySingleAsync<(int? Freestyle, int Chars, double Rating)>(
                """
                SELECT freestyle_cell_count AS Freestyle, char_count AS Chars, difficulty_rating AS Rating
                FROM beatmaps WHERE id = @beatmapId
                """,
                new { beatmapId });

        var plainRow = await rowOf(plain);
        var freeRow = await rowOf(free);

        Assert.Multiple(() =>
        {
            Assert.That(plainRow.Freestyle, Is.Zero, "a map with no flagged line stores 0, not NULL");
            // "take me &&& with you tonight": 20 letters + 5 inter-word spaces = 25 typed cells, plus
            // 3 any-key slots. (It was "me &&& you" until LyricPace v22, whose 7 cells rate exactly
            // zero under the 16-character floor, which made the rating check below vacuous.)
            // The two counts are DISJOINT since LyricPace v21, which reverses what v6 decided: a
            // slot takes any key, so no map can ask for a particular speed in one and the pace does
            // not count it. It is still a cell of the map with a deadline, priced at a quarter by
            // the star rating, which is what freestyle_cell_count is for.
            Assert.That(freeRow.Freestyle, Is.EqualTo(3));
            Assert.That(freeRow.Chars, Is.EqualTo(25), "the slots are counted beside char_count, not inside it");
            Assert.That(freeRow.Rating, Is.GreaterThan(0));
        });

        // And the v16 sweep fills it: NULL the column and roll the row back, exactly the state prod
        // is in on the deploy that ships this migration.
        await conn.ExecuteAsync(
            "UPDATE beatmaps SET freestyle_cell_count = NULL, pace_version = 15 WHERE id = @free", new { free });

        await PaceBackfill.RunAsync(db, fileStore, NullLogger.Instance);

        var refilled = await rowOf(free);

        Assert.Multiple(() =>
        {
            Assert.That(refilled.Freestyle, Is.EqualTo(3), "the pace sweep fills the new column from the stored blob");
            Assert.That(refilled.Rating, Is.EqualTo(freeRow.Rating), "and rewrites the rating byte-identically");
        });
    }

    [Test]
    [Order(26)]
    public async Task Ingest_StoresTheLyricFont_TheSweepRefillsIt_AndAnUploadWithoutOneClearsIt()
    {
        // 037_lyric_font.sql: the mapper-chosen font family off [General] LyricFont, stored per
        // difficulty like the rest of the parsed metadata. The bundled file itself is an ordinary
        // set file (admitted by the validator's font rules) and gets no column.
        await using var conn = await db.OpenAsync();

        long id = await conn.ExecuteScalarAsync<long>(
            "INSERT INTO beatmapsets (owner_id, status, intended_status) VALUES (@uploaderId, 'hidden', 'pending') RETURNING id",
            new { uploaderId });

        long mapId = await conn.ExecuteScalarAsync<long>(
            "INSERT INTO beatmaps (set_id, checksum_md5) VALUES (@id, md5(random()::text)) RETURNING id", new { id });

        async Task upload(params (string Name, byte[] Content)[] entries)
        {
            using var zip = SyntheticPackage.Zip(entries);
            var parsed = BeatmapPackageParser.Parse(zip);
            PackageValidator.Validate(parsed, id, [mapId], "uploader");

            await using var scope = await ingest.BeginSetScopeAsync(id);
            await ingest.IngestAsync(scope, zip, parsed, id, uploaderId);
        }

        async Task<string?> fontOf() => await conn.ExecuteScalarAsync<string?>(
            "SELECT lyric_font FROM beatmaps WHERE id = @mapId", new { mapId });

        await upload(
            ("map.osu", SyntheticPackage.Utf8(SyntheticPackage.OsuText(
                beatmapId: mapId, beatmapSetId: id, lyricFont: "Blocky Pixels", lyricFontFile: "lyricfont.ttf"))),
            ("audio.mp3", SyntheticPackage.Utf8("fake audio bytes")),
            ("lyricfont.ttf", SyntheticPackage.Utf8("fake font bytes")));

        Assert.That(await fontOf(), Is.EqualTo("Blocky Pixels"), "ingest stores the family the .osu names");

        // The pace sweep rewrites it from the stored blob beside everything else it reparses.
        await conn.ExecuteAsync(
            "UPDATE beatmaps SET lyric_font = NULL, pace_version = 15 WHERE id = @mapId", new { mapId });

        await PaceBackfill.RunAsync(db, fileStore, NullLogger.Instance);

        Assert.That(await fontOf(), Is.EqualTo("Blocky Pixels"), "the sweep refills the column from the blob");

        // Per-difficulty parsed metadata, rewritten each upload: an upload whose .osu carries no
        // LyricFont key means the mapper removed the font, so the stored value goes with it. This
        // is deliberately NOT the language's coalesce rule; the encoder always writes the key when
        // a font is set, so absence is a statement rather than an old client's silence.
        await upload(
            ("map.osu", SyntheticPackage.Utf8(SyntheticPackage.OsuText(beatmapId: mapId, beatmapSetId: id))),
            ("audio.mp3", SyntheticPackage.Utf8("fake audio bytes")));

        Assert.That(await fontOf(), Is.Null, "an upload without the key clears the stored font");
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
