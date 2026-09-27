using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Typebeat.Web.Packages;
using Typebeat.Web.Storage;

namespace Typebeat.Web.Tests.Website;

/// <summary>
/// Deterministic content seed for the public-site page tests (landing / listing / set page):
/// a mapper, 60 filler sets for paging, named specials for search/sort/status assertions, and a
/// small leaderboard. Idempotent via a gate so every test class can call
/// <see cref="EnsureSeededAsync"/> from its OneTimeSetUp regardless of execution order.
/// Inserted directly with SQL (the upload pipeline has its own tests), which means the search
/// vector must be populated here the same way the ingest write path does it.
/// </summary>
public static class PublicSiteSeed
{
    private static readonly SemaphoreSlim gate = new(1, 1);
    private static bool seeded;

    public static long MapperId { get; private set; }
    public static long TypistOneId { get; private set; }
    public static long TypistTwoId { get; private set; }
    public static long TypistThreeId { get; private set; }
    public static long CheatSuspectId { get; private set; }

    /// <summary>"Bohemian Keyboard Rhapsody" by "Queen of Types": search assertions.</summary>
    public static long SearchSetId { get; private set; }

    /// <summary>"Hammered Keys", play_count 999999: most-played sort.</summary>
    public static long MostPlayedId { get; private set; }

    /// <summary>"Favourite Fingers", favourite_count 999999: most-favourited sort.</summary>
    public static long MostFavedId { get; private set; }

    /// <summary>"Fresh Drop", newest published set site-wide.</summary>
    public static long FreshId { get; private set; }

    /// <summary>"Waiting Room", the one seeded 'pending' set, browsable but not ranked, so
    /// listing filters, profile sections and the set page's locked leaderboard cover both
    /// published statuses.</summary>
    public static long PendingId { get; private set; }

    /// <summary>Hidden set, submitted "in the future" so it would top every list if leaked.</summary>
    public static long HiddenId { get; private set; }

    public static long RemovedId { get; private set; }

    /// <summary>"Leaderboard Anthem": set page + leaderboard + report/favourite tests.</summary>
    public static long LeaderboardSetId { get; private set; }

    public static long LeaderboardBeatmapId { get; private set; }

    /// <summary>Set with cover_key/preview_key populated: cover img + preview button markup.</summary>
    public static long CoveredSetId { get; private set; }

    /// <summary>"Parental Advisory Anthem", the one set flagged explicit: the EXPLICIT badge and
    /// the <c>explicit:</c> search operator assert against it (every other seeded set is clean).</summary>
    public static long ExplicitSetId { get; private set; }

    /// <summary>"Radio Edit Anthem", the non-explicit twin sharing the "advisoryset" tag.</summary>
    public static long CleanTwinSetId { get; private set; }

    /// <summary>
    /// Public set with live diffs but NO set_versions row; the pre-M3 shape migration 004
    /// backfills. Its pages must hide the Download actions ("available in-game only").
    /// </summary>
    public static long PackagelessId { get; private set; }

    /// <summary>"Off The Record", the one seeded 'unranked' set (published, never rankable). The
    /// third published status, so the webplay rail and the /play picker can be shown to admit all
    /// three rather than just the two that happened to be seeded.</summary>
    public static long UnrankedSetId { get; private set; }

    /// <summary>
    /// A second difficulty of <see cref="UnrankedSetId"/> whose archive filename deliberately
    /// COLLIDES with "Twin Peaks Typing"'s hard difficulty ("hard.osu"). It is what makes the media
    /// routes' set-membership bound testable: without it, naming this beatmap under Twin Peaks would
    /// resolve a filename that really is in Twin Peaks' manifest and serve that difficulty's bytes.
    /// </summary>
    public static long UnrankedCollidingDiffId { get; private set; }

    /// <summary>"Twin Peaks Typing": the only seeded set with more than one difficulty, and the
    /// only one whose .osu and audio are really stored, so the /play media routes can be driven end
    /// to end per difficulty. Its two diffs point at DIFFERENT audio files on purpose.</summary>
    public static long MultiDiffSetId { get; private set; }

    /// <summary>"twin easy", the lower-rated difficulty of <see cref="MultiDiffSetId"/>
    /// (easy.osu, audio easy.mp3). Lowest id, so it is also the set's primary.</summary>
    public static long MultiDiffEasyId { get; private set; }

    /// <summary>"twin hard", the higher-rated difficulty (hard.osu, audio hard.mp3).</summary>
    public static long MultiDiffHardId { get; private set; }

    /// <summary>A dropped difficulty of <see cref="MultiDiffSetId"/>: a beatmaps row kept for the
    /// scores FK with filename NULL, which nothing on the /play path may resolve.</summary>
    public static long MultiDiffDroppedId { get; private set; }

    public const string MultiDiffEasyAudio = "easy.mp3";
    public const string MultiDiffHardAudio = "hard.mp3";

    /// <summary>The hard difficulty's mapper-chosen lyric font (backlog 291): the family stored in
    /// beatmaps.lyric_font AND written into hard.osu's [General] LyricFont line, so the set page
    /// row and the /play font route are fed by one fixture. The easy diff carries none, covering
    /// the hidden-row / 404 arm.</summary>
    public const string MultiDiffFontFamily = "Blocky Pixels";

    /// <summary>The bundled font file hard.osu names (LyricFontFile) and the manifest stores.</summary>
    public const string MultiDiffFontFile = "lyricfont.ttf";

    /// <summary>
    /// "Video Clip Anthem": the one seeded set with has_video true and a real video entry in its
    /// manifest (an .osu naming <see cref="VideoClipFile"/> in [Events], the clip itself, and a
    /// separate audio file). Everything about the card's two-option download and the
    /// download-sizes endpoint keys on it; every other seeded set is has_video false, which is what
    /// makes "no video, no expand" testable against a neighbour rather than against nothing.
    /// </summary>
    public static long VideoSetId { get; private set; }

    /// <summary>
    /// "Mp4 Single Anthem": the pre-234 shape, a map whose AudioFilename IS its VideoFilename
    /// (imported from an mp4 alone, with no standalone mp3 to fall back on). An audio-only package
    /// of it would be silent, so the server withdraws the variant for it.
    /// </summary>
    public static long Mp4AsAudioSetId { get; private set; }

    public const string VideoClipFile = "clip.mp4";
    public const string VideoSetAudio = "audio.mp3";

    /// <summary>The one file of <see cref="Mp4AsAudioSetId"/>, its audio AND its video.</summary>
    public const string Mp4AsAudioFile = "song.mp4";

    // Two fixed-fingerprint sets for the typed search-operator tests. Both tagged "operatorset"
    // so a test can scope free text to just this pair, then narrow with an operator. Fixed past
    // submit dates make the date: assertions deterministic.
    //
    // Alpha: submitted 2024-03-15, stars 4.5, wpm 100, length 90s (1:30), cpm 100*5 = 500. Its
    // seeded counts happen to average exactly 5 cells per word (500/100), so it is the ONE fixture
    // on which the pre-v15 cpm derivation and the current one agree; Bravo is the one that tells
    // them apart.
    /// <summary>"Operator Alpha Synthwave": the low-stat operator fixture.</summary>
    public static long OpAlphaId { get; private set; }

    // Bravo: submitted 2022-11-01, stars 7.0, wpm 200, length 240s (4:00), cpm 200*5 = 1000. Its
    // average word is 700/100 = 7 cells, so the pre-v15 derivation would have called this 1400.
    /// <summary>"Operator Bravo Ballad": the high-stat operator fixture.</summary>
    public static long OpBravoId { get; private set; }

    public static async Task EnsureSeededAsync()
    {
        await gate.WaitAsync();

        try
        {
            if (seeded)
                return;

            await using var conn = new NpgsqlConnection(WebsiteFixture.ConnectionString);
            await conn.OpenAsync();

            MapperId = await InsertUserAsync(conn, "neon mapper");
            TypistOneId = await InsertUserAsync(conn, "typist one");
            TypistTwoId = await InsertUserAsync(conn, "typist two");
            TypistThreeId = await InsertUserAsync(conn, "typist three");
            CheatSuspectId = await InsertUserAsync(conn, "cheat suspect");

            // 60 filler sets: paging (50/page) needs more than one page of public sets.
            for (int i = 0; i < 60; i++)
            {
                await InsertSetAsync(conn,
                    title: $"Filler Song {i:00}", artist: "Filler Artist",
                    submittedOffset: TimeSpan.FromHours(-(i + 1)),
                    playCount: i, favouriteCount: i % 7);
            }

            SearchSetId = await InsertSetAsync(conn,
                title: "Bohemian Keyboard Rhapsody", artist: "Queen of Types",
                submittedOffset: TimeSpan.FromMinutes(-30),
                tags: "classic rock typing");

            MostPlayedId = await InsertSetAsync(conn,
                title: "Hammered Keys", artist: "The Plays",
                submittedOffset: TimeSpan.FromDays(-2), playCount: 999_999);

            MostFavedId = await InsertSetAsync(conn,
                title: "Favourite Fingers", artist: "The Hearts",
                submittedOffset: TimeSpan.FromDays(-2), favouriteCount: 999_999);

            FreshId = await InsertSetAsync(conn,
                title: "Fresh Drop", artist: "The Newest",
                submittedOffset: TimeSpan.FromHours(1));

            PendingId = await InsertSetAsync(conn,
                title: "Waiting Room", artist: "The Unreviewed",
                submittedOffset: TimeSpan.FromMinutes(-20), status: "pending");

            await InsertBeatmapAsync(conn, PendingId,
                totalLengthS: 80, stars: 4.2, wpm: 100, wordCount: 130, charCount: 640);

            HiddenId = await InsertSetAsync(conn,
                title: "Hidden Gem Nobody", artist: "Should Not Appear",
                submittedOffset: TimeSpan.FromHours(2), status: "hidden");

            RemovedId = await InsertSetAsync(conn,
                title: "Removed For Reasons", artist: "Gone",
                submittedOffset: TimeSpan.FromHours(2), status: "removed");

            LeaderboardSetId = await InsertSetAsync(conn,
                title: "Leaderboard Anthem", artist: "The Score Settlers",
                submittedOffset: TimeSpan.FromMinutes(-40),
                tags: "anthem leaderboard", source: "Type Hero", bpm: 128,
                description: "Line one\nLine two <script>alert(1)</script>");

            // Mixed-case multi-line lyrics + a markup probe: the set page's lyrics section must
            // render one stored line per line, casing intact, always encoded (a real ingested
            // haystack can't contain '<', Typeability.Normalize strips it, but the page must
            // not care).
            // Also the one seeded difficulty carrying a pace curve (028_wpm_curve.sql): a peak in
            // the middle and one empty bucket, so the set page's WPM tab has a real bar, a peak bar
            // and a baseline stub to render.
            LeaderboardBeatmapId = await InsertBeatmapAsync(conn, LeaderboardSetId,
                totalLengthS: 95.5, stars: 3.2, wpm: 80, wordCount: 120, charCount: 600,
                lyrics: "Neon LIGHTS are calling\nWe TYPE through the storm\n<i>stage whisper</i>",
                peakWpm: 143, peakCpm: 702, wpmCurve: [60, 95, 143, 0, 88], targetWpm: 118);

            CoveredSetId = await InsertSetAsync(conn,
                title: "Covered In Neon", artist: "The Artwork",
                submittedOffset: TimeSpan.FromMinutes(30));

            await InsertBeatmapAsync(conn, CoveredSetId,
                totalLengthS: 120, stars: 5.0, wpm: 125, wordCount: 200, charCount: 950);

            // The only explicit-flagged set. Tagged "advisoryset" so a search can scope to it (and
            // to its clean twin below) without dragging in the filler wall.
            ExplicitSetId = await InsertSetAsync(conn,
                title: "Parental Advisory Anthem", artist: "The Unfiltered",
                submittedOffset: TimeSpan.FromMinutes(-25), tags: "advisoryset",
                isExplicit: true);

            await InsertBeatmapAsync(conn, ExplicitSetId,
                totalLengthS: 110, stars: 4.0, wpm: 110, wordCount: 150, charCount: 700);

            // Clean twin of the above, same tag: proves the badge and the explicit: operator
            // discriminate rather than matching everything in scope.
            CleanTwinSetId = await InsertSetAsync(conn,
                title: "Radio Edit Anthem", artist: "The Bleeped",
                submittedOffset: TimeSpan.FromMinutes(-24), tags: "advisoryset");

            await InsertBeatmapAsync(conn, CleanTwinSetId,
                totalLengthS: 110, stars: 4.0, wpm: 110, wordCount: 150, charCount: 700);

            await conn.ExecuteAsync(
                """
                UPDATE beatmapsets
                SET cover_key = 'covers/' || id || '/1', preview_key = 'previews/' || id || '.mp3'
                WHERE id = @id
                """,
                new { id = CoveredSetId });

            // Leaderboard: typist one twice (only the 900k best may surface, DISTINCT ON),
            // the others once, plus an unranked row that must never appear.
            await InsertScoreAsync(conn, TypistOneId, LeaderboardBeatmapId, 900_000, 0.9846, 87, "S");
            await InsertScoreAsync(conn, TypistOneId, LeaderboardBeatmapId, 600_000, 0.9012, 40, "A");
            await InsertScoreAsync(conn, TypistTwoId, LeaderboardBeatmapId, 700_000, 0.9311, 61, "A");
            await InsertScoreAsync(conn, TypistThreeId, LeaderboardBeatmapId, 500_000, 0.8523, 30, "B");
            await InsertScoreAsync(conn, CheatSuspectId, LeaderboardBeatmapId, 999_999_999, 1, 110, "X", ranked: false);

            PackagelessId = await InsertSetAsync(conn,
                title: "Editor Era Classic", artist: "The Backfilled",
                submittedOffset: TimeSpan.FromDays(-1));

            await InsertBeatmapAsync(conn, PackagelessId,
                totalLengthS: 100, stars: 2.5, wpm: 60, wordCount: 90, charCount: 420);

            // The third published status. Everything else about it matches "Waiting Room": a live
            // difficulty and (via the bulk insert below) a package, so only the status differs.
            UnrankedSetId = await InsertSetAsync(conn,
                title: "Off The Record", artist: "The Unrankable",
                submittedOffset: TimeSpan.FromMinutes(-18), status: "unranked");

            await InsertBeatmapAsync(conn, UnrankedSetId,
                totalLengthS: 70, stars: 3.1, wpm: 90, wordCount: 110, charCount: 520);

            UnrankedCollidingDiffId = await InsertBeatmapAsync(conn, UnrankedSetId,
                totalLengthS: 70, stars: 5.2, wpm: 140, wordCount: 160, charCount: 780,
                versionName: "off the record hard", filename: "hard.osu");

            // Two live difficulties plus one dropped row, with REAL stored files (below), so the
            // per-difficulty media routes have something to serve and something to refuse.
            MultiDiffSetId = await InsertSetAsync(conn,
                title: "Twin Peaks Typing", artist: "The Two Ways",
                submittedOffset: TimeSpan.FromMinutes(-15));

            MultiDiffEasyId = await InsertBeatmapAsync(conn, MultiDiffSetId,
                totalLengthS: 100, stars: 2.0, wpm: 60, wordCount: 90, charCount: 420,
                versionName: "twin easy", filename: "easy.osu");

            // The hard diff carries a target_wpm and the easy one does not, so /play/map/{id}/diffs
            // proves both arms of its coalesce in one response: the pill reads the target where
            // there is one, and falls back to the stored average where the v18 backfill has not
            // been. The target is deliberately BELOW its own average, which no real map can be (a
            // mean over the fastest fifth of the lines cannot sit under the mean over all of them),
            // so a pill still reading 180 would be reading the wrong column rather than a close
            // number. These are stored values written straight into the column, not computed ones,
            // which is what lets the fixture take a shape the arithmetic never produces.
            MultiDiffHardId = await InsertBeatmapAsync(conn, MultiDiffSetId,
                totalLengthS: 100, stars: 6.0, wpm: 180, wordCount: 260, charCount: 1300,
                targetWpm: 165, versionName: "twin hard", filename: "hard.osu",
                lyricFont: MultiDiffFontFamily);

            MultiDiffDroppedId = await InsertBeatmapAsync(conn, MultiDiffSetId,
                totalLengthS: 100, stars: 4.0, wpm: 120, wordCount: 150, charCount: 700,
                versionName: "twin dropped", filename: null);

            // The two video fixtures (their manifests are stored below).
            VideoSetId = await InsertSetAsync(conn,
                title: "Video Clip Anthem", artist: "The Cinematics",
                submittedOffset: TimeSpan.FromMinutes(-12), hasVideo: true);

            // A live difficulty as well, so this set reaches the /play picker: that is where the
            // card renders in PLAY mode, and the download expand must not appear there.
            await InsertBeatmapAsync(conn, VideoSetId,
                totalLengthS: 105, stars: 4.4, wpm: 115, wordCount: 140, charCount: 680,
                versionName: "cinematic", filename: "video.osu");

            // No difficulty row for this one: it exists for the mp4-as-audio guard, which is read
            // off the manifest, and it has nothing to say to the picker.
            Mp4AsAudioSetId = await InsertSetAsync(conn,
                title: "Mp4 Single Anthem", artist: "The Undivided",
                submittedOffset: TimeSpan.FromMinutes(-11), hasVideo: true);

            OpAlphaId = await InsertSetAtAsync(conn,
                title: "Operator Alpha Synthwave", artist: "Synth Operator",
                tags: "operatorset", submittedAt: new DateTime(2024, 3, 15, 0, 0, 0, DateTimeKind.Utc));
            // Disjoint lyric haystacks (only "night" shared) so the lyrics: tests can prove
            // single-word narrowing and multi-word AND semantics within the pair.
            // Alpha's target (180) is ABOVE Bravo's (130) while its average WPM is below: the
            // target: operator has to invert the wpm: ordering on this pair, which is what proves
            // it reads its own column instead of the one next to it. Bravo's pairing (target 130
            // under an average of 200) is one the arithmetic cannot produce, and that is on
            // purpose: these are values written straight into the column rather than computed.
            await InsertBeatmapAsync(conn, OpAlphaId,
                totalLengthS: 90, stars: 4.5, wpm: 100, wordCount: 100, charCount: 500,
                lyrics: "neon skyline glowing all night", targetWpm: 180);

            OpBravoId = await InsertSetAtAsync(conn,
                title: "Operator Bravo Ballad", artist: "Piano Operator",
                tags: "operatorset", submittedAt: new DateTime(2022, 11, 1, 0, 0, 0, DateTimeKind.Utc));
            await InsertBeatmapAsync(conn, OpBravoId,
                totalLengthS: 240, stars: 7.0, wpm: 200, wordCount: 100, charCount: 700,
                lyrics: "quiet rain falls on the piano at night", targetWpm: 130);

            // Every seeded set EXCEPT the packageless one gets a version row, mirroring sets
            // that went through the upload pipeline: the set page / card Download actions key
            // on package existence (the object itself is never streamed by these tests).
            await conn.ExecuteAsync(
                """
                INSERT INTO set_versions (set_id, version_no, package_key)
                SELECT id, 1, 'packages/' || id || '/1.typb'
                FROM beatmapsets
                WHERE owner_id = @mapperId AND id <> @packagelessId
                """,
                new { mapperId = MapperId, packagelessId = PackagelessId });

            await StoreTwinPeaksFilesAsync(conn);
            await StoreVideoFilesAsync(conn);

            // Same expression the upload write path uses (and migration 002's backfill).
            await conn.ExecuteAsync(
                $"UPDATE beatmapsets s SET search = {PackageIngest.SearchVectorSql} FROM users u WHERE u.id = s.owner_id");

            seeded = true;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Inserts one set comment directly (036_beatmapset_comments.sql), bypassing the page
    /// handler: for fixtures that need volume (pagination) or authors who could never post
    /// through the UI (a restricted account). Returns the comment's id.
    /// </summary>
    public static async Task<long> SeedCommentAsync(NpgsqlConnection conn, long setId, long userId, string body)
        => await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmapset_comments (set_id, user_id, body)
            VALUES (@setId, @userId, @body)
            RETURNING id
            """,
            new { setId, userId, body });

    private static async Task<long> InsertUserAsync(NpgsqlConnection conn, string username)
        => await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO users (username, email, password_hash, country_code)
            VALUES (@username, @email, 'not-a-real-hash', 'US')
            RETURNING id
            """,
            new { username, email = username.Replace(' ', '.') + "@example.com" });

    private static async Task<long> InsertSetAsync(NpgsqlConnection conn,
        string title, string artist, TimeSpan submittedOffset,
        string status = "ranked", string tags = "", string source = "", string description = "",
        int playCount = 0, int favouriteCount = 0, double? bpm = null, bool isExplicit = false,
        bool hasVideo = false)
        => await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmapsets
                (owner_id, title, artist, source, tags, description, status, bpm, explicit, has_video,
                 play_count, favourite_count, submitted_at, updated_at)
            VALUES
                (@ownerId, @title, @artist, @source, @tags, @description, @status, @bpm, @isExplicit, @hasVideo,
                 @playCount, @favouriteCount, now() + @submittedOffset, now() + @submittedOffset)
            RETURNING id
            """,
            new
            {
                ownerId = MapperId, title, artist, source, tags, description, status, bpm, isExplicit, hasVideo,
                playCount, favouriteCount, submittedOffset,
            });

    /// <summary>As <see cref="InsertSetAsync"/> but pins an ABSOLUTE submitted_at (the operator
    /// date: fixtures need stable calendar dates, not now()-relative offsets).</summary>
    private static async Task<long> InsertSetAtAsync(NpgsqlConnection conn,
        string title, string artist, string tags, DateTime submittedAt, string status = "ranked")
        => await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmapsets
                (owner_id, title, artist, tags, status, submitted_at, updated_at)
            VALUES
                (@ownerId, @title, @artist, @tags, @status, @submittedAt, @submittedAt)
            RETURNING id
            """,
            new { ownerId = MapperId, title, artist, tags, status, submittedAt });

    /// <summary>
    /// <paramref name="peakWpm"/> / <paramref name="wpmCurve"/> are the 028_wpm_curve.sql columns
    /// and <paramref name="targetWpm"/> is 033_target_wpm.sql's. All left NULL by default, which is
    /// the state of every row the pace backfill has not reached (and of every map too short to
    /// measure): the set page's WPM tab must degrade to a note for those, and the listing card and
    /// the /play pill must fall back to the stored average rather than going blank. Only some
    /// fixtures carry them, so both paths are covered.
    /// </summary>
    private static async Task<long> InsertBeatmapAsync(NpgsqlConnection conn, long setId,
        double totalLengthS, double stars, double wpm, int wordCount, int charCount,
        string lyrics = "", double? peakWpm = null, double? peakCpm = null, float[]? wpmCurve = null,
        double? targetWpm = null,
        string versionName = "type!beat", string? filename = "map.osu", string? lyricFont = null)
        => await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmaps
                (set_id, version_name, checksum_md5, total_length_s, drain_length_s,
                 difficulty_rating, filename, word_count, char_count, wpm, lyrics,
                 peak_wpm, peak_cpm, wpm_curve, target_wpm, lyric_font)
            VALUES
                (@setId, @versionName, @checksum, @totalLengthS, @drainLengthS,
                 @stars, @filename, @wordCount, @charCount, @wpm, @lyrics,
                 @peakWpm, @peakCpm, @wpmCurve, @targetWpm, @lyricFont)
            RETURNING id
            """,
            new
            {
                setId, versionName, checksum = Guid.NewGuid().ToString("N"), totalLengthS,
                drainLengthS = totalLengthS * 0.9, stars, filename, wordCount, charCount, wpm, lyrics,
                peakWpm, peakCpm, wpmCurve, targetWpm, lyricFont,
            });

    /// <summary>
    /// Puts REAL bytes behind "Twin Peaks Typing": one .osu per live difficulty, each naming its
    /// own AudioFilename, plus the two audio blobs those names resolve to. Everything else in this
    /// seed stops at the beatmaps row, which is enough for pages but not for the /play media routes,
    /// which walk beatmaps.filename -> version_files -> the blob store the way a real upload wrote it.
    /// </summary>
    private static async Task StoreTwinPeaksFilesAsync(NpgsqlConnection conn)
    {
        long versionId = await LatestVersionIdAsync(conn, MultiDiffSetId);

        await StoreFileAsync(conn, versionId, "easy.osu",
            Encoding.UTF8.GetBytes(SyntheticPackage.OsuText(
                title: "Twin Peaks Typing", artist: "The Two Ways", version: "twin easy",
                audioFilename: MultiDiffEasyAudio, background: null, beatmapId: 2001)));

        // The hard diff also names the set's bundled lyric font (backlog 291), so /play's font
        // route has a difficulty that serves one and a difficulty (easy) that 404s.
        await StoreFileAsync(conn, versionId, "hard.osu",
            Encoding.UTF8.GetBytes(SyntheticPackage.OsuText(
                title: "Twin Peaks Typing", artist: "The Two Ways", version: "twin hard",
                audioFilename: MultiDiffHardAudio, background: null, beatmapId: 2002,
                lyricFont: MultiDiffFontFamily, lyricFontFile: MultiDiffFontFile)));

        // Not decodable audio, and deliberately so: these routes stream bytes, they never parse
        // them, and the two blobs only have to be DIFFERENT for a test to tell which one it got.
        await StoreFileAsync(conn, versionId, MultiDiffEasyAudio, Encoding.UTF8.GetBytes("easy-audio-bytes"));
        await StoreFileAsync(conn, versionId, MultiDiffHardAudio, Encoding.UTF8.GetBytes("hard-audio-bytes"));

        // Same rule for the font: the route streams whatever the manifest resolves, so fake bytes
        // are enough to prove which blob (and which content type) came back.
        await StoreFileAsync(conn, versionId, MultiDiffFontFile, Encoding.UTF8.GetBytes("fake-font-bytes"));
    }

    /// <summary>
    /// The manifests behind the two video fixtures, which is what /beatmapsets/{id}/download-sizes
    /// reads (version_files joined to files, plus the .osu blobs it parses to find out which entry
    /// is the video). The clip is deliberately much the biggest entry, so an audio-only total that
    /// failed to drop it would not merely be wrong, it would be indistinguishable from the full one.
    /// </summary>
    private static async Task StoreVideoFilesAsync(NpgsqlConnection conn)
    {
        long videoVersionId = await LatestVersionIdAsync(conn, VideoSetId);

        await StoreFileAsync(conn, videoVersionId, "video.osu",
            Encoding.UTF8.GetBytes(SyntheticPackage.OsuText(
                title: "Video Clip Anthem", artist: "The Cinematics", version: "cinematic",
                audioFilename: VideoSetAudio, background: null, video: VideoClipFile, beatmapId: 3001)));

        await StoreFileAsync(conn, videoVersionId, VideoSetAudio, Encoding.UTF8.GetBytes(new string('a', 2048)));
        await StoreFileAsync(conn, videoVersionId, VideoClipFile, Encoding.UTF8.GetBytes(new string('v', 65536)));

        // An .mp4 that NO difficulty names as its video. The audio-only variant keeps it, which is
        // the difference between reading the .osu's [Events] Video line and guessing from the
        // extension, and it is why the size labels are not a per-extension sum either.
        await StoreFileAsync(conn, videoVersionId, "bonus.mp4", Encoding.UTF8.GetBytes(new string('b', 512)));

        // The pre-234 shape: ONE media file, named as both the audio and the video.
        long mp4VersionId = await LatestVersionIdAsync(conn, Mp4AsAudioSetId);

        await StoreFileAsync(conn, mp4VersionId, "single.osu",
            Encoding.UTF8.GetBytes(SyntheticPackage.OsuText(
                title: "Mp4 Single Anthem", artist: "The Undivided", version: "single",
                audioFilename: Mp4AsAudioFile, background: null, video: Mp4AsAudioFile, beatmapId: 3002)));

        await StoreFileAsync(conn, mp4VersionId, Mp4AsAudioFile, Encoding.UTF8.GetBytes(new string('m', 32768)));
    }

    private static async Task<long> LatestVersionIdAsync(NpgsqlConnection conn, long setId)
        => await conn.ExecuteScalarAsync<long>(
            "SELECT id FROM set_versions WHERE set_id = @setId ORDER BY version_no DESC LIMIT 1",
            new { setId });

    private static async Task StoreFileAsync(NpgsqlConnection conn, long versionId, string filename, byte[] content)
    {
        byte[] sha = SHA256.HashData(content);

        await conn.ExecuteAsync(
            "INSERT INTO files (sha256, size) VALUES (@sha, @size) ON CONFLICT (sha256) DO NOTHING",
            new { sha, size = (long)content.Length });

        await conn.ExecuteAsync(
            """
            INSERT INTO version_files (version_id, sha256, filename)
            VALUES (@versionId, @sha, @filename)
            ON CONFLICT (version_id, filename) DO NOTHING
            """,
            new { versionId, sha, filename });

        var store = WebsiteFixture.Services.GetRequiredService<IFileStore>();
        using var stream = new MemoryStream(content);
        await store.WriteBlobIfAbsentAsync(sha, stream);
    }

    private static async Task InsertScoreAsync(NpgsqlConnection conn, long userId, long beatmapId,
        long totalScore, double accuracy, int maxCombo, string rank, bool ranked = true)
        => await conn.ExecuteAsync(
            """
            INSERT INTO scores
                (user_id, beatmap_id, total_score, accuracy, completion, max_combo, rank, passed, ranked,
                 mods, statistics, maximum_statistics)
            VALUES
                (@userId, @beatmapId, @totalScore, @accuracy, @completion, @maxCombo, @rank, true, @ranked,
                 '[]'::jsonb, '{"great":100,"ok":5,"meh":2,"miss":3}'::jsonb, '{"great":110}'::jsonb)
            """,
            // completion matches the fixed statistics blob: 107 typed of 110 cells. (Ranks stay
            // whatever the caller seeds; these are display fixtures, not grading fixtures.)
            new { userId, beatmapId, totalScore, accuracy, completion = 107.0 / 110.0, maxCombo, rank, ranked });
}
