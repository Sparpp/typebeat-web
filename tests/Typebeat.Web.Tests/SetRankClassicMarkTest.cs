using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Typebeat.Web.Data;
using Typebeat.Web.Packages;
using Typebeat.Web.Scoring;
using Typebeat.Web.Storage;

namespace Typebeat.Web.Tests;

/// <summary>
/// <see cref="SetRankClassicMark"/> (backlog 398), the DOWNWARD half of the version rule as a
/// Classic mark, against real ingested versions: a set ranked at v1, re-uploaded with different
/// gameplay to v2 and re-ranked must MARK the v1 score with "CL" (keeping it ranked, 0.95x score and
/// pp) and leave the v2 score alone; the strict arm must mark each unprovable shape by its own
/// reason; an identical-gameplay re-upload survives unmarked; an already-current-version play is not
/// marked; a second sweep is a no-op; and the dry run changes nothing. Same harness shape (and the
/// same real-blob version rule) as <see cref="SetRankRefundVersionTest"/>, so the two directions are
/// proven against the same machinery rather than a mock.
/// </summary>
[TestFixture]
[NonParallelizable]
public class SetRankClassicMarkTest
{
    private const string database_name = "typebeat_setrankclassictests";

    private const string connection_string =
        "Host=localhost;Port=5432;Database=" + database_name + ";Username=postgres;Password=postgres";

    private const string admin_connection_string =
        "Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=postgres";

    /// <summary>The rated fixture lyric, retimed: same words, every time moved. A gameplay change.</summary>
    private const string retimed_lyrics =
        """
        {"version":2,"song_end_ms":4000,"granularity":"Word"}
        {"text":"neon lights are calling","start_ms":1100,"end_ms":3100,"words":[{"text":"neon","start_ms":1100,"end_ms":1600,"score":1},{"text":"lights","start_ms":1600,"end_ms":2100,"score":1},{"text":"are","start_ms":2100,"end_ms":2500,"score":1},{"text":"calling","start_ms":2500,"end_ms":3100,"score":1}]}
        """;

    private NpgsqlDataSource dataSource = null!;
    private Db db = null!;
    private LocalFileStore fileStore = null!;
    private PackageIngest ingest = null!;
    private string fileRoot = null!;

    private long uploaderId;
    private long buildId;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        await using (var admin = new NpgsqlConnection(admin_connection_string))
        {
            await admin.OpenAsync();
            await admin.ExecuteAsync($"DROP DATABASE IF EXISTS {database_name} WITH (FORCE)");
            await admin.ExecuteAsync($"CREATE DATABASE {database_name}");
        }

        await Db.EnsureExtensionsAsync(connection_string);

        dataSource = NpgsqlDataSource.Create(connection_string);
        db = new Db(dataSource);
        await db.MigrateAsync(NullLogger.Instance);

        fileRoot = Path.Combine(Path.GetTempPath(), "typebeat-setrankclassic-" + Guid.NewGuid().ToString("N"));
        fileStore = new LocalFileStore(fileRoot);
        ingest = new PackageIngest(db, fileStore, new CoverGenerator(), new PreviewGenerator(), NullLogger<PackageIngest>.Instance);

        await using var conn = await db.OpenAsync();

        uploaderId = await conn.ExecuteScalarAsync<long>(
            "INSERT INTO users (username, email, password_hash, country_code) VALUES ('classic-uploader', 'classic.uploader@example.com', 'x', 'US') RETURNING id");
        buildId = await conn.ExecuteScalarAsync<long>(
            "INSERT INTO builds (version_hash, blocked) VALUES ('classic-build', false) RETURNING id");
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (dataSource != null)
            await dataSource.DisposeAsync();

        if (fileRoot != null && Directory.Exists(fileRoot))
            Directory.Delete(fileRoot, recursive: true);
    }

    /// <summary>
    /// THE HEADLINE CASE: a play made on v1, then a gameplay-changing re-upload to v2, then a
    /// re-rank. The v1 play gains CL and keeps ranked, its total_score is 0.95x, and the v2 play is
    /// not marked.
    /// </summary>
    [Test]
    public async Task APlayBeforeAGameplayChangingReupload_IsMarkedClassic_TheCurrentVersionIsNot()
    {
        var (setId, mapId) = await newRankedSetAsync();
        long before = await playAsync(mapId, "v1 score", totalScore: 200_000, pp: 210);

        // Same words, every time shifted: the edit the fingerprint exists to see. The re-upload
        // demotes the set to pending (030), so re-rank it before the transition runs.
        await uploadAsync(setId, mapId, lyrics: retimed_lyrics);
        await rankAsync(setId);
        long after = await playAsync(mapId, "v2 score", totalScore: 300_000, pp: 190);

        var outcome = await SetRankClassicMark.RunForSetAsync(db, fileStore, NullLogger.Instance, setId);

        var marked = await rowAsync(before);
        var current = await rowAsync(after);

        Assert.Multiple(() =>
        {
            Assert.That(HasClassic(marked.ModsJson), Is.True, "played on timing that is not the timing that was ranked");
            Assert.That(marked.Ranked, Is.True, "the mark is NOT an unranking");
            Assert.That(marked.TotalScore, Is.EqualTo(Round(200_000 * 0.95)), "the score carries the 0.95x penalty");
            Assert.That(marked.PpVersion, Is.Zero, "stamped for PpBackfill to reprice at 0.95x");

            Assert.That(HasClassic(current.ModsJson), Is.False, "a play on the ranked version is not marked");
            Assert.That(current.TotalScore, Is.EqualTo(300_000));
            Assert.That(current.Ranked, Is.True);

            Assert.That(outcome.Marked, Is.EqualTo(1));
            Assert.That(outcome.Report.ChangedGameplay, Is.EqualTo(1));
        });
    }

    /// <summary>The mark extends the stored mods rather than replacing them, so a modded play keeps its badges.</summary>
    [Test]
    public async Task Marking_AStackedScore_PreservesTheExistingMods()
    {
        var (setId, mapId) = await newRankedSetAsync();
        long stacked = await playAsync(mapId, "stacked score", totalScore: 100_000, pp: 100, modsJson: """[{"acronym":"DT","settings":{"speed_change":1.5}}]""");

        await uploadAsync(setId, mapId, lyrics: retimed_lyrics);
        await rankAsync(setId);

        await SetRankClassicMark.RunForSetAsync(db, fileStore, NullLogger.Instance, setId);

        var mods = ScoreMods.Parse((await rowAsync(stacked)).ModsJson);

        Assert.Multiple(() =>
        {
            Assert.That(mods.Select(m => m.Acronym), Is.EquivalentTo(new[] { "DT", "CL" }));
            Assert.That(mods.Single(m => m.Acronym == "DT").Rate, Is.EqualTo(1.5));
        });
    }

    /// <summary>A title / artist / tag edit moves the .osu hash but not the gameplay: the v1 play survives unmarked.</summary>
    [Test]
    public async Task APlayBeforeAMetadataOnlyReupload_IsNotMarked()
    {
        var (setId, mapId) = await newRankedSetAsync();
        long before = await playAsync(mapId, "metadata v1", totalScore: 200_000, pp: 205);

        await uploadAsync(setId, mapId, title: "Neon Nights (Remastered)", artist: "Synth Rider and Friends", version: "insane", tags: "renamed");
        await rankAsync(setId);

        // Precondition: the .osu really did change, so only the fingerprint can keep this unmarked.
        string? played, current;

        await using (var conn = await db.OpenAsync())
        {
            played = await conn.ExecuteScalarAsync<string>("SELECT beatmap_hash FROM score_tokens WHERE score_id = @before", new { before });
            current = await conn.ExecuteScalarAsync<string>("SELECT checksum_md5 FROM beatmaps WHERE id = @mapId", new { mapId });
        }

        var outcome = await SetRankClassicMark.RunForSetAsync(db, fileStore, NullLogger.Instance, setId);
        var row = await rowAsync(before);

        Assert.Multiple(() =>
        {
            Assert.That(played, Is.Not.EqualTo(current), "precondition: the play really was on an earlier .osu");
            Assert.That(outcome.Marked, Is.Zero);
            Assert.That(outcome.Report.Kept, Is.EqualTo(1), "a cosmetic re-upload must not mark the plays made before it");
            Assert.That(HasClassic(row.ModsJson), Is.False);
            Assert.That(row.TotalScore, Is.EqualTo(200_000));
        });
    }

    /// <summary>Each unprovable shape is marked by its own reason: no token, a hash no version produces, a changed version.</summary>
    [Test]
    public async Task TheStrictArm_MarksEachReason()
    {
        var (setId, mapId) = await newRankedSetAsync();

        // 1) no token: nothing names the version the play was on.
        long noToken = await insertScoreAsync(mapId, "no token typist", withToken: false);

        // 2) a hash no stored version's .osu produces (a manifest edited outside the ingest).
        long orphan = await insertScoreAsync(mapId, "orphan typist", beatmapHash: "0123456789abcdef0123456789abcdef");

        // 3) a version whose fingerprint differs.
        long stale = await playAsync(mapId, "stale typist");
        await uploadAsync(setId, mapId, lyrics: retimed_lyrics);
        await rankAsync(setId);

        var outcome = await SetRankClassicMark.RunForSetAsync(db, fileStore, NullLogger.Instance, setId);

        bool noTokenMarked = HasClassic((await rowAsync(noToken)).ModsJson);
        bool orphanMarked = HasClassic((await rowAsync(orphan)).ModsJson);
        bool staleMarked = HasClassic((await rowAsync(stale)).ModsJson);

        Assert.Multiple(() =>
        {
            Assert.That(noTokenMarked, Is.True);
            Assert.That(orphanMarked, Is.True);
            Assert.That(staleMarked, Is.True);
            Assert.That(outcome.Report.MissingToken, Is.EqualTo(1));
            Assert.That(outcome.Report.NoMatchingVersion, Is.EqualTo(1));
            Assert.That(outcome.Report.ChangedGameplay, Is.EqualTo(1));
            Assert.That(outcome.Marked, Is.EqualTo(3));
        });
    }

    /// <summary>
    /// The mark also reaches UNRANKED rows (owner scope): an unranked play on a stale version is
    /// marked too, so it carries the mark and the price wherever it is later read.
    /// </summary>
    [Test]
    public async Task TheSweepAlsoMarksUnrankedRows()
    {
        var (setId, mapId) = await newRankedSetAsync();
        long unrankedPlay = await insertScoreAsync(mapId, "unranked typist", ranked: false, beatmapHash: "0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f");

        var outcome = await SetRankClassicMark.RunForSetAsync(db, fileStore, NullLogger.Instance, setId);
        bool marked = HasClassic((await rowAsync(unrankedPlay)).ModsJson);

        Assert.Multiple(() =>
        {
            Assert.That(marked, Is.True);
            Assert.That(outcome.Marked, Is.EqualTo(1));
        });
    }

    /// <summary>pp falls to 0.95x once the mark is present: PpBackfill reprices the stamped row under the CL price.</summary>
    [Test]
    public async Task AMarkedRow_RepricesToPointNintyFivePp()
    {
        var (setId, mapId) = await newRankedSetAsync();

        // Two plays on the same map, same statistics. The FIRST is on v1; the SECOND is inserted
        // AFTER the re-upload, so its token names the current v2 .osu. Only the v1 play is stale and
        // gets the mark, so the two differ by exactly the mark's factor.
        long stale = await playAsync(mapId, "pp stale typist", totalScore: 100_000, pp: 0);

        await uploadAsync(setId, mapId, lyrics: retimed_lyrics);

        long survivor = await playAsync(mapId, "pp survivor typist", totalScore: 100_000, pp: 0);

        // Both stamped stale, so PpBackfill prices both on the run below. The mark is the only
        // difference between them: the stale row's mods carry CL, the survivor's do not.
        await using (var stamp = await db.OpenAsync())
            await stamp.ExecuteAsync(
                "UPDATE scores SET pp_version = 0 WHERE id = ANY(@ids)", new { ids = new[] { stale, survivor } });

        await rankAsync(setId);

        await SetRankClassicMark.RunForSetAsync(db, fileStore, NullLogger.Instance, setId);
        await PpBackfill.RunAsync(db, NullLogger.Instance, setId: setId);

        var staleRow = await rowAsync(stale);
        var survivorRow = await rowAsync(survivor);

        Assert.Multiple(() =>
        {
            Assert.That(HasClassic(staleRow.ModsJson), Is.True);
            Assert.That(HasClassic(survivorRow.ModsJson), Is.False, "the v2 play is not marked");
            Assert.That(survivorRow.Pp, Is.GreaterThan(0), "an unmarked, current-version play is still priced");
            Assert.That(staleRow.Pp, Is.GreaterThan(0), "the marked play is still priceable, just penalised");
            // Both sit on the same map with the same statistics, so pp is identical up to the mod
            // factor, and the mark is the only difference: 0.95x exactly.
            Assert.That(staleRow.Pp, Is.EqualTo(survivorRow.Pp * 0.95).Within(survivorRow.Pp * 1e-6));
        });
    }

    /// <summary>A set with no re-upload is untouched, and the sweep is a no-op on a second run (unmarked rows stay candidates).</summary>
    [Test]
    public async Task ASetWithNoReupload_IsUntouched_AndTheSweepIsIdempotent()
    {
        var (setId, mapId) = await newRankedSetAsync();
        long play = await playAsync(mapId, "settled typist", totalScore: 200_000, pp: 180);

        var first = await SetRankClassicMark.RunForSetAsync(db, fileStore, NullLogger.Instance, setId);
        var second = await SetRankClassicMark.RunForSetAsync(db, fileStore, NullLogger.Instance, setId);
        var row = await rowAsync(play);

        Assert.Multiple(() =>
        {
            Assert.That(first.Marked, Is.Zero);
            Assert.That(first.Report.Kept, Is.EqualTo(1));
            Assert.That(second.Marked, Is.Zero);
            Assert.That(second.Report.Examined, Is.EqualTo(1), "an unmarked row is still a candidate; nothing moves");
            Assert.That(HasClassic(row.ModsJson), Is.False);
            Assert.That(row.TotalScore, Is.EqualTo(200_000));
        });
    }

    /// <summary>Idempotence when a row WAS marked: the mark in mods removes it from the candidate set, so a rerun moves nothing.</summary>
    [Test]
    public async Task AMarkedRow_IsNotACandidateAgain_AndItsPenaltyIsAppliedOnce()
    {
        var (setId, mapId) = await newRankedSetAsync();
        long stale = await playAsync(mapId, "idempotent typist", totalScore: 200_000, pp: 99);

        await uploadAsync(setId, mapId, lyrics: retimed_lyrics);
        await rankAsync(setId);

        var first = await SetRankClassicMark.RunForSetAsync(db, fileStore, NullLogger.Instance, setId);
        long afterFirst = (await rowAsync(stale)).TotalScore;

        var second = await SetRankClassicMark.RunForSetAsync(db, fileStore, NullLogger.Instance, setId);
        long afterSecond = (await rowAsync(stale)).TotalScore;

        Assert.Multiple(() =>
        {
            Assert.That(first.Marked, Is.EqualTo(1));
            Assert.That(second.Marked, Is.Zero);
            Assert.That(second.Report.Examined, Is.Zero, "the mark in mods removes the row from the candidate set");
            Assert.That(afterSecond, Is.EqualTo(afterFirst), "the 0.95x is applied exactly once");
            Assert.That(afterFirst, Is.EqualTo(Round(200_000 * 0.95)));
        });
    }

    /// <summary>
    /// The DRY RUN counts each reason and CHANGES NOTHING. This is what lets the owner size the prod
    /// impact before the backfill (the deploy IS the backfill).
    /// </summary>
    [Test]
    public async Task TheDryRun_CountsEachReasonAndChangesNothing()
    {
        var (setId, mapId) = await newRankedSetAsync();

        long noToken = await insertScoreAsync(mapId, "report no token", withToken: false);
        long orphan = await insertScoreAsync(mapId, "report orphan", beatmapHash: "00112233445566778899aabbccddeeff");
        long keep = await playAsync(mapId, "report keep", pp: 120);

        var report = await SetRankClassicMark.ReportAsync(db, fileStore, NullLogger.Instance, setId);

        bool noneMoved = !HasClassic((await rowAsync(noToken)).ModsJson)
                         && !HasClassic((await rowAsync(orphan)).ModsJson)
                         && !HasClassic((await rowAsync(keep)).ModsJson);

        Assert.Multiple(() =>
        {
            Assert.That(report.MissingToken, Is.EqualTo(1));
            Assert.That(report.NoMatchingVersion, Is.EqualTo(1));
            Assert.That(report.ChangedGameplay, Is.Zero);
            Assert.That(report.Kept, Is.EqualTo(1));
            Assert.That(report.Marked, Is.EqualTo(2));
            Assert.That(report.Examined, Is.EqualTo(3));
            Assert.That(noneMoved, Is.True, "a dry run writes nothing");
        });
    }

    /// <summary>
    /// Non-vacuity witness: <see cref="PlayedVersionRule.IsCarried"/> is the SINGLE keep test the
    /// upward and downward sweeps share. Asserted here so a future edit that grows a second,
    /// divergent predicate fails this rather than passing silently.
    /// </summary>
    [Test]
    public void TheKeepTest_IsTheSharedPredicate()
    {
        Assert.Multiple(() =>
        {
            Assert.That(PlayedVersionRule.IsCarried(PlayedVersionRule.Verdict.SameBytes), Is.True);
            Assert.That(PlayedVersionRule.IsCarried(PlayedVersionRule.Verdict.SameGameplay), Is.True);
            Assert.That(PlayedVersionRule.IsCarried(PlayedVersionRule.Verdict.Changed), Is.False);
            Assert.That(PlayedVersionRule.IsCarried(PlayedVersionRule.Verdict.Unknown), Is.False);
        });
    }

    // ---- helpers ----

    private static long Round(double value) => (long)Math.Round(value, MidpointRounding.AwayFromZero);

    private static bool HasClassic(string? modsJson)
        => ScoreMods.Parse(modsJson).Any(m => m.Acronym == "CL");

    private async Task<(bool Ranked, long TotalScore, double Pp, int PpVersion, string? ModsJson)> rowAsync(long id)
    {
        await using var conn = await db.OpenAsync();
        return await conn.QuerySingleAsync<(bool, long, double, int, string?)>(
            "SELECT ranked, total_score, pp, pp_version, mods::text FROM scores WHERE id = @id", new { id });
    }

    /// <summary>A fresh hidden set carrying one live difficulty, ingested at v1 and set ranked.</summary>
    private async Task<(long SetId, long MapId)> newRankedSetAsync()
    {
        long setId, mapId;

        await using (var conn = await db.OpenAsync())
        {
            setId = await conn.ExecuteScalarAsync<long>(
                "INSERT INTO beatmapsets (owner_id, status, intended_status) VALUES (@uploaderId, 'hidden', 'pending') RETURNING id",
                new { uploaderId });
            mapId = await conn.ExecuteScalarAsync<long>(
                """
                INSERT INTO beatmaps (set_id, checksum_md5, difficulty_rating, ratings)
                VALUES (@setId, md5(random()::text), 2.0, @ratings::jsonb)
                RETURNING id
                """,
                new { setId, ratings = TestRatings.Json(2.0) });
        }

        await uploadAsync(setId, mapId);

        await using (var conn = await db.OpenAsync())
            await conn.ExecuteAsync("UPDATE beatmapsets SET status = 'ranked' WHERE id = @setId", new { setId });

        return (setId, mapId);
    }

    private async Task uploadAsync(
        long setId,
        long mapId,
        string lyrics = SyntheticPackage.RatedLyrics,
        string title = "Neon Nights",
        string artist = "Synth Rider",
        string version = "type!beat",
        string tags = "typebeat lyrics typing",
        string audio = "fake audio bytes")
    {
        (string Name, byte[] Content)[] entries =
        [
            ("a.osu", SyntheticPackage.Utf8(SyntheticPackage.OsuText(
                title: title, titleUnicode: title, artist: artist, artistUnicode: artist, creator: "classic-uploader",
                version: version, tags: tags, beatmapId: mapId, beatmapSetId: setId, lyrics: lyrics))),
            ("audio.mp3", SyntheticPackage.Utf8(audio)),
            ("bg.jpg", SyntheticPackage.TinyPng()),
        ];

        using var zip = SyntheticPackage.Zip(entries);
        var parsed = BeatmapPackageParser.Parse(zip);

        PackageValidator.Validate(parsed, setId, [mapId], "classic-uploader");

        await using var scope = await ingest.BeginSetScopeAsync(setId);
        await ingest.IngestAsync(scope, zip, parsed, setId, uploaderId);
    }

    private async Task rankAsync(long setId)
    {
        await using var conn = await db.OpenAsync();
        await conn.ExecuteAsync("UPDATE beatmapsets SET status = 'ranked' WHERE id = @setId", new { setId });
    }

    /// <summary>A RANKED play whose token names the difficulty's .osu as it is NOW, priced at a nonzero pp.</summary>
    private Task<long> playAsync(long mapId, string username, long totalScore = 200_000, double pp = 150, string modsJson = "[]")
        => insertScoreAsync(mapId, username, totalScore: totalScore, pp: pp, modsJson: modsJson, beatmapHash: null);

    private async Task<long> insertScoreAsync(
        long mapId, string username, long totalScore = 200_000, double pp = 150, string modsJson = "[]",
        string? beatmapHash = null, bool withToken = true, bool ranked = true, int ppVersion = -1)
    {
        await using var conn = await db.OpenAsync();

        long userId = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO users (username, email, password_hash, country_code)
            VALUES (@username, @email, 'x', 'US')
            RETURNING id
            """,
            new { username, email = username.Replace(' ', '.') + "@example.com" });

        long id = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO scores
                (user_id, beatmap_id, total_score, accuracy, completion, max_combo, rank, passed, ranked,
                 mods, statistics, maximum_statistics, build_id, started_at, ended_at, pp, pp_version)
            VALUES
                (@userId, @mapId, @totalScore, 0.97, 1.0, 10, 'X', true, @ranked,
                 CAST(@modsJson AS jsonb), '{"great": 10}'::jsonb, '{"great": 10}'::jsonb, @buildId,
                 now() - interval '95 seconds', now(), @pp, @ppVersion)
            RETURNING id
            """,
            new { userId, mapId, totalScore, modsJson, buildId, pp, ppVersion = ppVersion < 0 ? PerformancePoints.VERSION : ppVersion, ranked });

        if (withToken)
            await SetRankRefundTest.insertTokenAsync(conn, userId, mapId, buildId, id, beatmapHash);

        return id;
    }
}
