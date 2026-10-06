using System.Net;
using System.Net.Http.Headers;
using Dapper;
using Newtonsoft.Json.Linq;
using Npgsql;
using Typebeat.Web.Auth;
using Typebeat.Web.Data;

namespace Typebeat.Web.Tests.Website;

/// <summary>
/// The stored data the game's native (osu!lazer) profile and beatmap overlays read, served on the
/// routes those overlays call: the profile header's cover, follower counts and country rank, the
/// Beatmaps section's lists and counts, the set overlay's leaderboard pp and comments, and the
/// listing overlay's filters and sorts.
/// </summary>
[TestFixture]
[NonParallelizable]
public class LazerOverlayWireTest
{
    private static NpgsqlDataSource dataSource = null!;

    // The profile under test: a mapper with one set of each status, followed and watched.
    private static long mapperId;
    private static long rankedSetId, pendingSetId, unrankedSetId, hiddenSetId, explicitSetId;
    private static long rankedBeatmapId, pendingBeatmapId, hiddenBeatmapId;

    // The viewer: favourites the ranked set, follows + watches the mapper, plays the ranked map.
    private static long viewerId;
    private static string viewerBearer = null!;

    private const string tag = "lazeroverlaywire";

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        await PublicSiteSeed.EnsureSeededAsync();

        dataSource = NpgsqlDataSource.Create(WebsiteFixture.ConnectionString);
        await using var conn = await dataSource.OpenConnectionAsync();

        mapperId = await insertUserAsync(conn, "lazer overlay mapper", "DE", coverKey: "user-covers/1/1.jpg");
        viewerId = await insertUserAsync(conn, "lazer overlay viewer", "DE", coverKey: null);
        long otherFollower = await insertUserAsync(conn, "lazer overlay fan", "US", coverKey: null);
        viewerBearer = (await new TokenService(new Db(dataSource)).IssueAsync(viewerId)).AccessToken;

        (rankedSetId, rankedBeatmapId) = await insertSetAsync(conn, "Ranked Song", "ranked", "japanese", explicitContent: false, stars: 5.0);
        (pendingSetId, pendingBeatmapId) = await insertSetAsync(conn, "Pending Song", "pending", "english", explicitContent: false, stars: 2.0);
        (unrankedSetId, _) = await insertSetAsync(conn, "Unranked Song", "unranked", "english", explicitContent: false, stars: 3.0);
        (hiddenSetId, hiddenBeatmapId) = await insertSetAsync(conn, "Hidden Song", "hidden", "english", explicitContent: false, stars: 1.0);
        (explicitSetId, _) = await insertSetAsync(conn, "Explicit Song", "pending", "english", explicitContent: true, stars: 4.0);

        await conn.ExecuteAsync("INSERT INTO favourites (user_id, set_id) VALUES (@viewerId, @rankedSetId)", new { viewerId, rankedSetId });

        await conn.ExecuteAsync(
            """
            INSERT INTO user_follows (follower_id, followee_id, kind) VALUES
                (@viewerId, @mapperId, 'user'), (@otherFollower, @mapperId, 'user'), (@viewerId, @mapperId, 'mapper')
            """,
            new { viewerId, mapperId, otherFollower });

        // A priced ranked play by the viewer (and so a pp rank, in DE).
        await conn.ExecuteAsync(
            """
            INSERT INTO scores
                (user_id, beatmap_id, total_score, accuracy, completion, max_combo, rank, passed, ranked, pp,
                 mods, statistics, maximum_statistics, ended_at)
            VALUES
                (@viewerId, @rankedBeatmapId, 700000, 0.97, 1.0, 80, 'S', true, true, 123.4,
                 '[]'::jsonb, '{"great":95}'::jsonb, '{"great":100}'::jsonb, now())
            """,
            new { viewerId, rankedBeatmapId });

        await conn.ExecuteAsync(
            """
            INSERT INTO beatmapset_comments (set_id, user_id, body, created_at) VALUES
                (@rankedSetId, @viewerId, 'first!', now() - interval '2 hours'),
                (@rankedSetId, @mapperId, 'thanks for playing', now() - interval '1 hour')
            """,
            new { rankedSetId, viewerId, mapperId });
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (dataSource != null)
            await dataSource.DisposeAsync();
    }

    // ---- profile header ----

    [Test]
    public async Task UserPayload_CarriesCoverFollowersAndBeatmapsetCounts()
    {
        var user = await getObjectAsync($"/api/v2/users/{mapperId}");

        Assert.Multiple(() =>
        {
            Assert.That((string?)user["cover_url"], Does.EndWith("/user-covers/1/1.jpg"));
            Assert.That((string?)user["cover"]!["url"], Is.EqualTo((string?)user["cover_url"]), "the client reads cover over cover_url");
            Assert.That((int)user["follower_count"]!, Is.EqualTo(2));
            Assert.That((int)user["mapping_follower_count"]!, Is.EqualTo(1));
            Assert.That((int)user["ranked_beatmapset_count"]!, Is.EqualTo(1));
            Assert.That((int)user["pending_beatmapset_count"]!, Is.EqualTo(2), "the explicit set is pending too");
            Assert.That((int)user["graveyard_beatmapset_count"]!, Is.EqualTo(1), "unranked is the client's graveyard");
            Assert.That((int)user["favourite_beatmapset_count"]!, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task UserPayload_WithNoCover_SendsNone()
    {
        var user = await getObjectAsync($"/api/v2/users/{viewerId}");

        Assert.Multiple(() =>
        {
            Assert.That((string?)user["cover_url"], Is.Empty);
            Assert.That(user["cover"]!["url"]!.Type, Is.EqualTo(JTokenType.Null));
            Assert.That((int)user["favourite_beatmapset_count"]!, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Statistics_CarryTheCountryRank()
    {
        var ranked = await getObjectAsync($"/api/v2/users/{viewerId}");
        var unranked = await getObjectAsync($"/api/v2/users/{mapperId}");

        Assert.Multiple(() =>
        {
            Assert.That((int?)ranked["statistics"]!["country_rank"], Is.GreaterThanOrEqualTo(1));
            Assert.That((int?)ranked["statistics"]!["country_rank"], Is.LessThanOrEqualTo((int)ranked["statistics"]!["global_rank"]!));
            Assert.That(unranked["statistics"]!["country_rank"]!.Type, Is.EqualTo(JTokenType.Null));
            Assert.That((double?)ranked["statistics"]!["global_rank_percent"], Is.GreaterThan(0).And.LessThanOrEqualTo(1));
            Assert.That(unranked["statistics"]!["global_rank_percent"]!.Type, Is.EqualTo(JTokenType.Null));
        });
    }

    [Test]
    public async Task Statistics_CarryTheExtendedDetails()
    {
        var statistics = (await getObjectAsync($"/api/v2/users/{viewerId}"))["statistics"]!;

        Assert.Multiple(() =>
        {
            Assert.That((int)statistics["maximum_combo"]!, Is.EqualTo(80));
            Assert.That(statistics["total_hits"]!.Type, Is.EqualTo(JTokenType.Integer));
            Assert.That((int)statistics["replays_watched_by_others"]!, Is.EqualTo(0));
        });
    }

    // ---- profile Beatmaps section ----

    [TestCase("ranked", "Ranked Song")]
    [TestCase("graveyard", "Unranked Song")]
    public async Task UserBeatmapsets_ListTheOwnersSetsOfThatStatus(string type, string title)
    {
        var sets = await getArrayAsync($"/api/v2/users/{mapperId}/beatmapsets/{type}");

        Assert.That(sets.Select(s => (string?)s["title"]), Is.EqualTo(new[] { $"{tag} {title}" }));
        Assert.That(sets[0]["beatmaps"]!.Count(), Is.EqualTo(1), "a card carries its difficulties");
    }

    [Test]
    public async Task UserBeatmapsets_PendingNeverLeaksHiddenSets()
    {
        var titles = (await getArrayAsync($"/api/v2/users/{mapperId}/beatmapsets/pending")).Select(s => (string?)s["title"]).ToList();

        Assert.That(titles, Is.EquivalentTo(new[] { $"{tag} Pending Song", $"{tag} Explicit Song" }));
    }

    [Test]
    public async Task UserBeatmapsets_FavouritesListTheFavouritedSets()
    {
        var sets = await getArrayAsync($"/api/v2/users/{viewerId}/beatmapsets/favourite");

        Assert.Multiple(() =>
        {
            Assert.That(sets.Select(s => (long)s["id"]!), Is.EqualTo(new[] { rankedSetId }));
            Assert.That((bool)sets[0]["has_favourited"]!, Is.True);
        });
    }

    [TestCase("loved")]
    [TestCase("guest")]
    [TestCase("nominated")]
    public async Task UserBeatmapsets_TypesWithNoDataHere_404(string type)
    {
        using var response = await sendAsync($"/api/v2/users/{mapperId}/beatmapsets/{type}");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task UserBeatmapsets_MostPlayedStillRoutesToItsOwnEndpoint()
    {
        var rows = await getArrayAsync($"/api/v2/users/{viewerId}/beatmapsets/most_played");
        Assert.That(rows.Select(r => (long)r["beatmap_id"]!), Is.EqualTo(new[] { rankedBeatmapId }));
    }

    // ---- set overlay ----

    [Test]
    public async Task Leaderboard_RowsCarryTheirPp()
    {
        var board = await getObjectAsync($"/api/v2/beatmaps/{rankedBeatmapId}/scores");

        Assert.Multiple(() =>
        {
            Assert.That((double?)board["scores"]![0]!["pp"], Is.EqualTo(123.4).Within(0.001));
            Assert.That((double?)board["user_score"]!["score"]!["pp"], Is.EqualTo(123.4).Within(0.001));
        });
    }

    [Test]
    public async Task SetGet_CarriesPassCountAndAvailability()
    {
        var set = await getObjectAsync($"/api/v2/beatmapsets/{rankedSetId}");

        Assert.Multiple(() =>
        {
            Assert.That((int)set["beatmaps"]![0]!["passcount"]!, Is.EqualTo(1));
            // The fixture's sets have no uploaded package, so a download would 404.
            Assert.That((bool)set["availability"]!["download_disabled"]!, Is.True);
        });
    }

    [Test]
    public async Task SetGet_CarriesTheCreatorWithAnAvatar()
    {
        var set = await getObjectAsync($"/api/v2/beatmapsets/{rankedSetId}");

        Assert.Multiple(() =>
        {
            Assert.That((long)set["user"]!["id"]!, Is.EqualTo(mapperId));
            Assert.That((string?)set["user"]!["username"], Is.EqualTo("lazer overlay mapper"));
            Assert.That((string?)set["user"]!["avatar_url"], Does.StartWith("https://localhost/").And.EndWith("/img/default-avatar.png"),
                "absolute, on this host: the client never builds an avatar URL of its own");
        });
    }

    [Test]
    public async Task Search_CardsCarryTheCreatorWithAnAvatar()
    {
        var result = await getObjectAsync($"/api/v2/beatmapsets/search?q=title%3D{tag}&sort=title_asc");
        var card = result["beatmapsets"]![0]!;

        Assert.Multiple(() =>
        {
            Assert.That((long)card["user"]!["id"]!, Is.EqualTo(mapperId));
            Assert.That((string?)card["user"]!["avatar_url"], Does.EndWith("/img/default-avatar.png"));
        });
    }

    [Test]
    public async Task Lookup_ByBeatmapId_AnswersTheWholeSet()
    {
        // GetBeatmapSetRequest(id, BeatmapSetLookupType.BeatmapId): a card's difficulty link opens the overlay on that diff.
        var set = await getObjectAsync($"/api/v2/beatmapsets/lookup?beatmap_id={pendingBeatmapId}");

        Assert.Multiple(() =>
        {
            Assert.That((long)set["id"]!, Is.EqualTo(pendingSetId));
            Assert.That(set["beatmaps"]!.Select(b => (long)b["id"]!), Does.Contain(pendingBeatmapId));
            Assert.That((long)set["user"]!["id"]!, Is.EqualTo(mapperId));
        });
    }

    [Test]
    public async Task Lookup_OfAHiddenUnknownOrMissingBeatmap_404s()
    {
        using var hidden = await sendAsync($"/api/v2/beatmapsets/lookup?beatmap_id={hiddenBeatmapId}");
        using var unknown = await sendAsync("/api/v2/beatmapsets/lookup?beatmap_id=999999999");
        using var missing = await sendAsync("/api/v2/beatmapsets/lookup");

        Assert.Multiple(() =>
        {
            Assert.That(hidden.StatusCode, Is.EqualTo(HttpStatusCode.NotFound), "a hidden set is the owner's alone");
            Assert.That(unknown.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        });
    }

    [Test]
    public async Task Lookup_OfOnesOwnHiddenSet_AnswersTheOwner()
    {
        var set = await getObjectAsync($"/api/v2/beatmapsets/lookup?beatmap_id={hiddenBeatmapId}", bearer: mapperBearerAsync());
        Assert.That((long)set["id"]!, Is.EqualTo(hiddenSetId));
    }

    [Test]
    public async Task Leaderboard_OfAPendingMap_ServesTheUnrankedBoard()
    {
        // The set overlay shows pending/unranked maps' boards too (as song select does), so the route must answer one.
        // A player of its own: a viewer play here would leak into the listing's played / rank filters.
        long rivalId;
        await using (var conn = await dataSource.OpenConnectionAsync())
        {
            rivalId = await insertUserAsync(conn, "lazer overlay rival", "US", coverKey: null);
            await conn.ExecuteAsync(
                """
                INSERT INTO scores
                    (user_id, beatmap_id, total_score, accuracy, completion, max_combo, rank, passed, ranked, pp,
                     mods, statistics, maximum_statistics, ended_at)
                VALUES
                    (@rivalId, @pendingBeatmapId, 500000, 0.9, 1.0, 40, 'A', true, false, 0,
                     '[]'::jsonb, '{"great":90}'::jsonb, '{"great":100}'::jsonb, now())
                """,
                new { rivalId, pendingBeatmapId });
        }

        var board = await getObjectAsync($"/api/v2/beatmaps/{pendingBeatmapId}/scores");

        Assert.Multiple(() =>
        {
            Assert.That(board["scores"]!.Count(), Is.EqualTo(1));
            Assert.That((long)board["scores"]![0]!["user"]!["id"]!, Is.EqualTo(rivalId));
            Assert.That((string?)board["scores"]![0]!["user"]!["avatar_url"], Does.EndWith("/img/default-avatar.png"));
        });
    }

    [Test]
    public async Task Comments_AnswerACommentBundle()
    {
        string body = await getBodyAsync($"/api/v2/comments?commentable_type=beatmapset&commentable_id={rankedSetId}&page=1&sort=new");
        var bundle = JObject.Parse(body);

        Assert.Multiple(() =>
        {
            Assert.That(bundle["comments"]!.Select(c => (string?)c["message"]), Is.EqualTo(new[] { "thanks for playing", "first!" }));
            Assert.That((int)bundle["total"]!, Is.EqualTo(2));
            Assert.That((bool)bundle["has_more"]!, Is.False);
            Assert.That(bundle["users"]!.Select(u => (long)u["id"]!), Is.EquivalentTo(new[] { mapperId, viewerId }));
            Assert.That((long?)bundle["commentable_meta"]![0]!["owner_id"], Is.EqualTo(mapperId));
            Assert.That(bundle["commentable_meta"]![0]!["current_user_attributes"]!["can_new_comment_reason"]!.Type, Is.EqualTo(JTokenType.Null),
                "posting is open, so the editor shows its input");

            // The client's users/user_votes setters walk these three lists, so they must precede both.
            int usersAt = body.IndexOf("\"users\"", StringComparison.Ordinal);
            int votesAt = body.IndexOf("\"user_votes\"", StringComparison.Ordinal);
            foreach (string list in new[] { "\"comments\"", "\"included_comments\"", "\"pinned_comments\"" })
                Assert.That(body.IndexOf(list, StringComparison.Ordinal), Is.LessThan(Math.Min(usersAt, votesAt)), list);
        });
    }

    [Test]
    public async Task Comments_OldSortReadsOldestFirst_AndRepliesAreEmpty()
    {
        var old = await getObjectAsync($"/api/v2/comments?commentable_type=beatmapset&commentable_id={rankedSetId}&sort=old");
        var replies = await getObjectAsync($"/api/v2/comments?commentable_type=beatmapset&commentable_id={rankedSetId}&parent_id=1");

        Assert.Multiple(() =>
        {
            Assert.That(old["comments"]!.Select(c => (string?)c["message"]), Is.EqualTo(new[] { "first!", "thanks for playing" }));
            Assert.That(replies["comments"]!, Is.Empty);
        });
    }

    [Test]
    public async Task Comments_OnAHiddenSetOrAnotherType_404()
    {
        using var hidden = await sendAsync($"/api/v2/comments?commentable_type=beatmapset&commentable_id={hiddenSetId}");
        using var build = await sendAsync("/api/v2/comments?commentable_type=build&commentable_id=1");

        Assert.Multiple(() =>
        {
            Assert.That(hidden.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(build.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        });
    }

    // ---- writes ----

    [Test]
    public async Task Favourite_SetsTheStatedState_AndKeepsTheCountInStep()
    {
        long setId = unrankedSetId;

        var on = await sendFormAsync(HttpMethod.Post, $"/api/v2/beatmapsets/{setId}/favourites", ("action", "favourite"));
        var again = await sendFormAsync(HttpMethod.Post, $"/api/v2/beatmapsets/{setId}/favourites", ("action", "favourite"));
        bool listed = (await getObjectAsync("/api/v2/me/beatmapset-favourites"))["beatmapset_ids"]!.Select(t => (long)t).Contains(setId);
        var off = await sendFormAsync(HttpMethod.Post, $"/api/v2/beatmapsets/{setId}/favourites", ("action", "unfavourite"));

        Assert.Multiple(() =>
        {
            Assert.That((int)on["favourite_count"]!, Is.EqualTo(1));
            Assert.That((int)again["favourite_count"]!, Is.EqualTo(1), "favouriting a favourite is a no-op, not a second count");
            Assert.That(listed, Is.True);
            Assert.That((int)off["favourite_count"]!, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task Favourite_OfAHiddenSet_404s()
    {
        using var response = await sendRawFormAsync(HttpMethod.Post, $"/api/v2/beatmapsets/{hiddenSetId}/favourites", ("action", "favourite"));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task Friends_AreThePlayerFollows()
    {
        long target;
        await using (var conn = await dataSource.OpenConnectionAsync())
            target = await insertUserAsync(conn, "lazer overlay friend", "FR", coverKey: null);

        using (var add = await sendRawFormAsync(HttpMethod.Post, $"/api/v2/friends?target={target}"))
        {
            var relation = JObject.Parse(await add.Content.ReadAsStringAsync())["user_relation"]!;

            Assert.Multiple(() =>
            {
                Assert.That(add.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That((long)relation["target_id"]!, Is.EqualTo(target));
                Assert.That((string?)relation["relation_type"], Is.EqualTo("friend"));
                Assert.That((bool)relation["mutual"]!, Is.False);
            });
        }

        var friends = await getArrayAsync("/api/v2/friends");
        int followers = (int)(await getObjectAsync($"/api/v2/users/{target}"))["follower_count"]!;

        using var remove = await sendAsync($"/api/v2/friends/{target}", method: HttpMethod.Delete);
        var after = await getArrayAsync("/api/v2/friends");

        Assert.Multiple(() =>
        {
            Assert.That(friends.Select(f => (long)f["target_id"]!), Does.Contain(target).And.Contain(mapperId), "the fixture's follow of the mapper is a friend too");
            Assert.That(followers, Is.EqualTo(1));
            Assert.That(remove.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(after.Select(f => (long)f["target_id"]!), Does.Not.Contain(target));
        });
    }

    [Test]
    public async Task Friends_CannotFollowYourself()
    {
        using var response = await sendRawFormAsync(HttpMethod.Post, $"/api/v2/friends?target={viewerId}");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.UnprocessableEntity));
    }

    [Test]
    public async Task Comments_PostThenDelete()
    {
        var posted = await sendFormAsync(HttpMethod.Post, "/api/v2/comments",
            ("comment[commentable_type]", "beatmapset"), ("comment[commentable_id]", pendingSetId.ToString()),
            ("comment[message]", "  posted from the game  "), ("comment[parent_id]", ""));

        long commentId = (long)posted["comments"]![0]!["id"]!;
        var listed = await getObjectAsync($"/api/v2/comments?commentable_type=beatmapset&commentable_id={pendingSetId}");

        using var deleted = await sendAsync($"/api/v2/comments/{commentId}", method: HttpMethod.Delete);
        var after = await getObjectAsync($"/api/v2/comments?commentable_type=beatmapset&commentable_id={pendingSetId}");

        Assert.Multiple(() =>
        {
            Assert.That((string?)posted["comments"]![0]!["message"], Is.EqualTo("posted from the game"), "trimmed, as on the website");
            Assert.That(listed["comments"]!.Select(c => (long)c["id"]!), Does.Contain(commentId));
            Assert.That(deleted.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(after["comments"]!.Select(c => (long)c["id"]!), Does.Not.Contain(commentId));
        });
    }

    [Test]
    public async Task Comments_ARepliesOrSomebodyElsesDelete_AreRefused()
    {
        using var reply = await sendRawFormAsync(HttpMethod.Post, "/api/v2/comments",
            ("comment[commentable_type]", "beatmapset"), ("comment[commentable_id]", rankedSetId.ToString()),
            ("comment[message]", "a reply"), ("comment[parent_id]", "1"));

        // The mapper's comment on their own set: the viewer is neither its author, the set owner nor a reviewer.
        long mapperComment;
        await using (var conn = await dataSource.OpenConnectionAsync())
            mapperComment = await conn.ExecuteScalarAsync<long>("SELECT id FROM beatmapset_comments WHERE user_id = @mapperId", new { mapperId });
        using var delete = await sendAsync($"/api/v2/comments/{mapperComment}", method: HttpMethod.Delete);

        Assert.Multiple(() =>
        {
            Assert.That(reply.StatusCode, Is.EqualTo(HttpStatusCode.UnprocessableEntity));
            Assert.That(delete.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        });
    }

    // ---- listing overlay ----

    [Test]
    public async Task Search_FavouritesAndMine_AreTheViewersOwn()
    {
        var favourites = await searchTitlesAsync("s=favourites");
        var mine = await searchTitlesAsync("s=mine", bearer: mapperBearerAsync());

        Assert.Multiple(() =>
        {
            Assert.That(favourites, Is.EqualTo(new[] { "Ranked Song" }));
            Assert.That(mine, Is.EquivalentTo(new[] { "Ranked Song", "Pending Song", "Unranked Song", "Hidden Song", "Explicit Song" }),
                "the owner's own maps include the hidden one");
        });
    }

    [Test]
    public async Task Search_FollowsLanguageVideoRanksPlayedAndNsfw()
    {
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(await searchTitlesAsync("c=follows"), Has.Count.EqualTo(4), "every published set by the watched mapper");
            Assert.That(await searchTitlesAsync("l=3"), Is.EqualTo(new[] { "Ranked Song" }), "3 is osu's Japanese");
            Assert.That(await searchTitlesAsync("e=video"), Is.Empty);
            Assert.That(await searchTitlesAsync("r=S"), Is.EqualTo(new[] { "Ranked Song" }));
            Assert.That(await searchTitlesAsync("r=A"), Is.Empty);
            Assert.That(await searchTitlesAsync("played=played"), Is.EqualTo(new[] { "Ranked Song" }));
            Assert.That(await searchTitlesAsync("nsfw=false"), Does.Not.Contain("Explicit Song"));
            Assert.That(await searchTitlesAsync("nsfw=true"), Does.Contain("Explicit Song"));
        });
    }

    [TestCase("title_asc", new[] { "Explicit Song", "Pending Song", "Ranked Song", "Unranked Song" })]
    [TestCase("difficulty_desc", new[] { "Ranked Song", "Explicit Song", "Unranked Song", "Pending Song" })]
    public async Task Search_SortsTheListingOffers(string sort, string[] expected)
    {
        Assert.That(await searchTitlesAsync($"sort={sort}"), Is.EqualTo(expected));
    }

    // ---- helpers ----

    private static string? mapperBearer;

    private static string mapperBearerAsync()
        => mapperBearer ??= new TokenService(new Db(dataSource)).IssueAsync(mapperId).GetAwaiter().GetResult().AccessToken;

    /// <summary>The titles (tag stripped) a listing search returns, confined to this fixture's sets.</summary>
    private static async Task<List<string>> searchTitlesAsync(string query, string? bearer = null)
    {
        var result = await getObjectAsync($"/api/v2/beatmapsets/search?q=title%3D{tag}&{query}", bearer);
        return result["beatmapsets"]!.Select(s => ((string)s["title"]!)[(tag.Length + 1)..]).ToList();
    }

    private static async Task<long> insertUserAsync(NpgsqlConnection conn, string username, string country, string? coverKey)
        => await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO users (username, email, password_hash, country_code, cover_key)
            VALUES (@username, @email, 'x', @country, @coverKey)
            RETURNING id
            """,
            new { username, email = $"{username.Replace(' ', '.')}@example.com", country, coverKey });

    private static async Task<(long SetId, long BeatmapId)> insertSetAsync(
        NpgsqlConnection conn, string title, string status, string language, bool explicitContent, double stars)
    {
        long setId = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmapsets (owner_id, title, artist, language, status, explicit, submitted_at, updated_at)
            VALUES (@mapperId, @title, 'Lazer Overlay Artist', @language, @status, @explicitContent, now() - interval '3 days', now() - interval '3 days')
            RETURNING id
            """,
            new { mapperId, title = $"{tag} {title}", language, status, explicitContent });

        long beatmapId = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmaps (set_id, version_name, checksum_md5, total_length_s, drain_length_s, difficulty_rating, filename, lyrics)
            VALUES (@setId, 'only', @checksum, 60, 0, @stars, 'only.osu', 'some lyrics here')
            RETURNING id
            """,
            new { setId, checksum = Guid.NewGuid().ToString("N"), stars });

        return (setId, beatmapId);
    }

    private static async Task<HttpResponseMessage> sendAsync(string url, string? bearer = null, HttpMethod? method = null, HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method ?? HttpMethod.Get, url) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer ?? viewerBearer);
        return await WebsiteFixture.Client.SendAsync(request);
    }

    /// <summary>A form-encoded request as the viewer, the way the client's APIRequest sends its parameters.</summary>
    private static Task<HttpResponseMessage> sendRawFormAsync(HttpMethod method, string url, params (string Key, string Value)[] fields)
        => sendAsync(url, method: method, content: new FormUrlEncodedContent(fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value))));

    private static async Task<JObject> sendFormAsync(HttpMethod method, string url, params (string Key, string Value)[] fields)
    {
        using var response = await sendRawFormAsync(method, url, fields);
        string body = await response.Content.ReadAsStringAsync();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), $"{url}: {body}");
        return JObject.Parse(body);
    }

    private static async Task<string> getBodyAsync(string url, string? bearer = null)
    {
        using var response = await sendAsync(url, bearer);
        string body = await response.Content.ReadAsStringAsync();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), $"{url}: {body}");
        return body;
    }

    private static async Task<JObject> getObjectAsync(string url, string? bearer = null) => JObject.Parse(await getBodyAsync(url, bearer));

    private static async Task<JArray> getArrayAsync(string url) => JArray.Parse(await getBodyAsync(url));
}
