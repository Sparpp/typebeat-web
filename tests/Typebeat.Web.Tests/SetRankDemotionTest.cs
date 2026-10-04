using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Typebeat.Web.Data;
using Typebeat.Web.Packages;
using Typebeat.Web.Scoring;
using Typebeat.Web.Storage;

namespace Typebeat.Web.Tests;

/// <summary>
/// <see cref="SetRankDemotion"/> (backlog 398), the DOWNWARD half of the version rule, against real
/// ingested versions: a set ranked at v1, re-uploaded with different gameplay to v2 and re-ranked
/// must drop the v1 score and keep the v2 one, the strict arm must drop each unprovable shape by its
/// own reason, pp must fall to 0 with the flag, an untouched set must not move, and a second run
/// must be a no-op. Same harness shape (and the same real-blob version rule) as
/// <see cref="SetRankRefundVersionTest"/>, so the two directions are proven against the same
/// machinery rather than a mock.
/// </summary>
[TestFixture]
[NonParallelizable]
public class SetRankDemotionTest
{
    private const string database_name = "typebeat_setrankdemotiontests";

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

        fileRoot = Path.Combine(Path.GetTempPath(), "typebeat-setrankdemotion-" + Guid.NewGuid().ToString("N"));
        fileStore = new LocalFileStore(fileRoot);
        ingest = new PackageIngest(db, fileStore, new CoverGenerator(), new PreviewGenerator(), NullLogger<PackageIngest>.Instance);

        await using var conn = await db.OpenAsync();

        uploaderId = await conn.ExecuteScalarAsync<long>(
            "INSERT INTO users (username, email, password_hash, country_code) VALUES ('demote-uploader', 'demote.uploader@example.com', 'x', 'US') RETURNING id");
        buildId = await conn.ExecuteScalarAsync<long>(
            "INSERT INTO builds (version_hash, blocked) VALUES ('demote-build', false) RETURNING id");
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
    /// re-rank. The v1 play drops (its version's fingerprint differs), the v2 play stays.
    /// </summary>
    [Test]
    public async Task APlayBeforeAGameplayChangingReupload_IsDemoted_TheCurrentVersionKept()
    {
        var (setId, mapId) = await newRankedSetAsync();
        long before = await rankedPlayAsync(mapId, "v1 score", pp: 210);

        // Same words, every time shifted: the edit the fingerprint exists to see. The re-upload
        // demotes the set to pending (030), so re-rank it before the transition runs.
        await uploadAsync(setId, mapId, lyrics: retimed_lyrics);
        await rankAsync(setId);
        long after = await rankedPlayAsync(mapId, "v2 score", pp: 190);

        var outcome = await SetRankDemotion.RunForSetAsync(db, fileStore, NullLogger.Instance, setId);

        bool beforeRanked = await rankedOf(before);
        bool afterRanked = await rankedOf(after);

        Assert.Multiple(() =>
        {
            Assert.That(beforeRanked, Is.False, "played on timing that is not the timing that was ranked");
            Assert.That(afterRanked, Is.True, "the rule is not a blanket wipe: a play on the ranked version stays");
            Assert.That(outcome.Demoted, Is.EqualTo(1));
            Assert.That(outcome.Report.ChangedGameplay, Is.EqualTo(1));
        });
    }

    /// <summary>A title / artist / tag edit moves the .osu hash but not the gameplay: the v1 play survives.</summary>
    [Test]
    public async Task APlayBeforeAMetadataOnlyReupload_Survives()
    {
        var (setId, mapId) = await newRankedSetAsync();
        long before = await rankedPlayAsync(mapId, "metadata v1", pp: 205);

        await uploadAsync(setId, mapId, title: "Neon Nights (Remastered)", artist: "Synth Rider and Friends", version: "insane", tags: "renamed");
        await rankAsync(setId);

        // Precondition: the .osu really did change, so only the fingerprint can keep this.
        string? played, current;

        await using (var conn = await db.OpenAsync())
        {
            played = await conn.ExecuteScalarAsync<string>("SELECT beatmap_hash FROM score_tokens WHERE score_id = @before", new { before });
            current = await conn.ExecuteScalarAsync<string>("SELECT checksum_md5 FROM beatmaps WHERE id = @mapId", new { mapId });
        }

        var outcome = await SetRankDemotion.RunForSetAsync(db, fileStore, NullLogger.Instance, setId);
        bool beforeRanked = await rankedOf(before);

        Assert.Multiple(() =>
        {
            Assert.That(played, Is.Not.EqualTo(current), "precondition: the play really was on an earlier .osu");
            Assert.That(outcome.Demoted, Is.Zero);
            Assert.That(outcome.Report.Kept, Is.EqualTo(1), "a cosmetic re-upload must not strand the plays made before it");
            Assert.That(beforeRanked, Is.True);
        });
    }

    /// <summary>Each unprovable shape drops by its own reason: no token, a hash no version produces, a changed version.</summary>
    [Test]
    public async Task TheStrictArm_DropsEachReason()
    {
        var (setId, mapId) = await newRankedSetAsync();

        // 1) no token: nothing names the version the play was on.
        long noToken = await insertRankedScoreAsync(mapId, "no token typist", withToken: false);

        // 2) a hash no stored version's .osu produces (a manifest edited outside the ingest).
        long orphan = await insertRankedScoreAsync(mapId, "orphan typist", beatmapHash: "0123456789abcdef0123456789abcdef");

        // 3) a version whose fingerprint differs.
        long stale = await rankedPlayAsync(mapId, "stale typist");
        await uploadAsync(setId, mapId, lyrics: retimed_lyrics);
        await rankAsync(setId);

        var outcome = await SetRankDemotion.RunForSetAsync(db, fileStore, NullLogger.Instance, setId);

        bool noTokenRanked = await rankedOf(noToken);
        bool orphanRanked = await rankedOf(orphan);
        bool staleRanked = await rankedOf(stale);

        Assert.Multiple(() =>
        {
            Assert.That(noTokenRanked, Is.False);
            Assert.That(orphanRanked, Is.False);
            Assert.That(staleRanked, Is.False);
            Assert.That(outcome.Report.MissingToken, Is.EqualTo(1));
            Assert.That(outcome.Report.NoMatchingVersion, Is.EqualTo(1));
            Assert.That(outcome.Report.ChangedGameplay, Is.EqualTo(1));
            Assert.That(outcome.Demoted, Is.EqualTo(3));
        });
    }

    /// <summary>pp falls to 0 with the flag, so the demoted play drops off every pp surface at once.</summary>
    [Test]
    public async Task ADemotedScore_LosesItsPpAndItsAuditRowIsWritten()
    {
        var (setId, mapId) = await newRankedSetAsync();
        long stale = await rankedPlayAsync(mapId, "pp typist", pp: 315);

        await uploadAsync(setId, mapId, lyrics: retimed_lyrics);
        await rankAsync(setId);

        await SetRankDemotion.RunForSetAsync(db, fileStore, NullLogger.Instance, setId);

        await using var conn = await db.OpenAsync();

        double pp = await conn.ExecuteScalarAsync<double>("SELECT pp FROM scores WHERE id = @stale", new { stale });
        string? reason = await conn.ExecuteScalarAsync<string>("SELECT reason FROM score_demotions WHERE score_id = @stale", new { stale });
        long? auditedSet = await conn.ExecuteScalarAsync<long?>("SELECT set_id FROM score_demotions WHERE score_id = @stale", new { stale });

        Assert.Multiple(() =>
        {
            Assert.That(pp, Is.Zero, "a demoted play earns nothing");
            Assert.That(reason, Is.EqualTo("changed_gameplay"));
            Assert.That(auditedSet, Is.EqualTo(setId));
        });
    }

    /// <summary>A set with no re-upload is untouched, and the sweep is a no-op on a second run.</summary>
    [Test]
    public async Task ASetWithNoReupload_IsUntouched_AndTheSweepIsIdempotent()
    {
        var (setId, mapId) = await newRankedSetAsync();
        long play = await rankedPlayAsync(mapId, "settled typist", pp: 180);

        var first = await SetRankDemotion.RunForSetAsync(db, fileStore, NullLogger.Instance, setId);
        var second = await SetRankDemotion.RunForSetAsync(db, fileStore, NullLogger.Instance, setId);
        bool playRanked = await rankedOf(play);

        Assert.Multiple(() =>
        {
            Assert.That(first.Demoted, Is.Zero);
            Assert.That(first.Report.Kept, Is.EqualTo(1));
            Assert.That(second.Demoted, Is.Zero, "a row it keeps is still a candidate, but nothing moves");
            Assert.That(second.Report.Examined, Is.EqualTo(1));
            Assert.That(playRanked, Is.True);
        });
    }

    /// <summary>Idempotence when a row WAS dropped: a demoted row is no longer a candidate, so a rerun moves nothing.</summary>
    [Test]
    public async Task ADroppedRow_IsNotACandidateAgain()
    {
        var (setId, mapId) = await newRankedSetAsync();
        long stale = await rankedPlayAsync(mapId, "idempotent typist", pp: 99);

        await uploadAsync(setId, mapId, lyrics: retimed_lyrics);
        await rankAsync(setId);

        var first = await SetRankDemotion.RunForSetAsync(db, fileStore, NullLogger.Instance, setId);
        var second = await SetRankDemotion.RunForSetAsync(db, fileStore, NullLogger.Instance, setId);
        bool staleRanked = await rankedOf(stale);

        Assert.Multiple(() =>
        {
            Assert.That(first.Demoted, Is.EqualTo(1));
            Assert.That(second.Demoted, Is.Zero);
            Assert.That(second.Report.Examined, Is.Zero, "the dropped row is ranked=false and no longer examined");
            Assert.That(staleRanked, Is.False);
        });
    }

    /// <summary>
    /// THE BACKFILL over a mixed board: the standing boot sweep (the pass that IS the deploy) leaves
    /// only current-gameplay rows ranked. Asserted on this set's own rows so a neighbour test's set
    /// cannot make it flaky.
    /// </summary>
    [Test]
    public async Task TheBootSweep_LeavesOnlyCurrentGameplayRowsRanked()
    {
        var (setId, mapId) = await newRankedSetAsync();
        long v1 = await rankedPlayAsync(mapId, "mixed v1", pp: 220);

        await uploadAsync(setId, mapId, lyrics: retimed_lyrics);
        await rankAsync(setId);

        // A current-version play (keep) mixed with a hand-inserted token for bytes no version ever
        // shipped (drop): the two arms on one board.
        long v2Current = await rankedPlayAsync(mapId, "mixed v2 current", pp: 200);
        long orphan = await insertRankedScoreAsync(mapId, "mixed orphan", beatmapHash: "fedcba9876543210fedcba9876543210");

        await SetRankDemotion.RunAsync(db, fileStore, NullLogger.Instance);

        bool v1Ranked = await rankedOf(v1);
        bool currentRanked = await rankedOf(v2Current);
        bool orphanRanked = await rankedOf(orphan);

        Assert.Multiple(() =>
        {
            Assert.That(v1Ranked, Is.False, "a v1 play on a v2 board");
            Assert.That(currentRanked, Is.True, "the current version");
            Assert.That(orphanRanked, Is.False, "an unprovable version");
        });
    }

    /// <summary>
    /// The DRY RUN (<see cref="SetRankDemotion.ReportAsync"/>, the ops endpoint's engine) counts
    /// each drop reason and CHANGES NOTHING: the same board, still ranked, after it runs. This is
    /// what lets the owner size the prod impact before the backfill (the deploy IS the backfill).
    /// </summary>
    [Test]
    public async Task TheDryRun_CountsEachReasonAndChangesNothing()
    {
        var (setId, mapId) = await newRankedSetAsync();

        long noToken = await insertRankedScoreAsync(mapId, "report no token", withToken: false);
        long orphan = await insertRankedScoreAsync(mapId, "report orphan", beatmapHash: "00112233445566778899aabbccddeeff");
        long keep = await rankedPlayAsync(mapId, "report keep", pp: 120);

        var report = await SetRankDemotion.ReportAsync(db, fileStore, NullLogger.Instance, setId);

        bool noneMoved = await rankedOf(noToken) && await rankedOf(orphan) && await rankedOf(keep);

        Assert.Multiple(() =>
        {
            Assert.That(report.MissingToken, Is.EqualTo(1));
            Assert.That(report.NoMatchingVersion, Is.EqualTo(1));
            Assert.That(report.ChangedGameplay, Is.Zero);
            Assert.That(report.Kept, Is.EqualTo(1));
            Assert.That(report.Dropped, Is.EqualTo(2));
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

    private async Task<bool> rankedOf(long id)
    {
        await using var conn = await db.OpenAsync();
        return await conn.ExecuteScalarAsync<bool>("SELECT ranked FROM scores WHERE id = @id", new { id });
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
                "INSERT INTO beatmaps (set_id, checksum_md5) VALUES (@setId, md5(random()::text)) RETURNING id", new { setId });
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
                title: title, titleUnicode: title, artist: artist, artistUnicode: artist, creator: "demote-uploader",
                version: version, tags: tags, beatmapId: mapId, beatmapSetId: setId, lyrics: lyrics))),
            ("audio.mp3", SyntheticPackage.Utf8(audio)),
            ("bg.jpg", SyntheticPackage.TinyPng()),
        ];

        using var zip = SyntheticPackage.Zip(entries);
        var parsed = BeatmapPackageParser.Parse(zip);

        PackageValidator.Validate(parsed, setId, [mapId], "demote-uploader");

        await using var scope = await ingest.BeginSetScopeAsync(setId);
        await ingest.IngestAsync(scope, zip, parsed, setId, uploaderId);
    }

    private async Task rankAsync(long setId)
    {
        await using var conn = await db.OpenAsync();
        await conn.ExecuteAsync("UPDATE beatmapsets SET status = 'ranked' WHERE id = @setId", new { setId });
    }

    /// <summary>A RANKED play whose token names the difficulty's .osu as it is NOW, priced at a nonzero pp.</summary>
    private Task<long> rankedPlayAsync(long mapId, string username, double pp = 150)
        => insertRankedScoreAsync(mapId, username, pp: pp, beatmapHash: null);

    private async Task<long> insertRankedScoreAsync(
        long mapId, string username, double pp = 150, string? beatmapHash = null, bool withToken = true)
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
                (@userId, @mapId, 400000, 0.97, 1.0, 10, 'X', true, true,
                 '[]'::jsonb, '{"great": 10}'::jsonb, '{"great": 10}'::jsonb, @buildId,
                 now() - interval '95 seconds', now(), @pp, @ppVersion)
            RETURNING id
            """,
            new { userId, mapId, buildId, pp, ppVersion = PerformancePoints.VERSION });

        if (withToken)
            await SetRankRefundTest.insertTokenAsync(conn, userId, mapId, buildId, id, beatmapHash);

        return id;
    }
}
