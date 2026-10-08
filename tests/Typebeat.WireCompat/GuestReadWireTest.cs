using System.Net;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using typebeat.Game.Online.API.Requests.Responses;

namespace Typebeat.WireCompat;

/// <summary>
/// Backlog 406 on the game's wire: a guest client (no bearer at all, which is what APIRequest sends
/// when it holds no token) reads the map lookup, the beatmap board, profiles and their sections,
/// and each answer parses with the client's own DTO and is BYTE-IDENTICAL to what a signed-in
/// caller gets, so opening these reads exposed nothing the signed-in wire did not already carry.
/// The one exception is the board's <c>user_score</c>, the caller's own row, which a guest does
/// not have.
/// </summary>
[TestFixture]
public class GuestReadWireTest
{
    private static HttpClient client => ServerFixture.Client;

    [Test]
    public async Task Lookup_AnswersAGuest_AsItAnswersAPlayer()
    {
        string path = $"/api/v2/beatmaps/lookup?checksum={ServerFixture.SeedChecksum}";
        string body = await sameForGuestAsync(path);

        var beatmap = JsonConvert.DeserializeObject<APIBeatmap>(body)!;

        Assert.Multiple(() =>
        {
            Assert.That(beatmap.OnlineID, Is.EqualTo((int)ServerFixture.SeededBeatmapId));
            Assert.That(beatmap.Status, Is.EqualTo(typebeat.Game.Beatmaps.BeatmapOnlineStatus.Ranked));
            Assert.That(beatmap.BeatmapSet, Is.Not.Null);
        });
    }

    [Test]
    public async Task Board_AnswersAGuest_TheGlobalBoard_WithNoUserScore()
    {
        // The pending map: its seeded play is there from the fixture on, whatever order the
        // suite runs in (the ranked map's plays come from WireCompatTests' score loop).
        string path = $"/api/v2/beatmaps/{ServerFixture.PendingBeatmapId}/scores?type=global&mode=typebeat&limit=50";

        string guest = await getAsync(path, bearer: false);
        string player = await getAsync(path, bearer: true);

        var guestBoard = JsonConvert.DeserializeObject<APIScoresCollection>(guest)!;

        var playerJson = JObject.Parse(player);
        playerJson["user_score"] = null;

        Assert.Multiple(() =>
        {
            Assert.That(guestBoard.Scores, Is.Not.Empty, "the pending map's seeded play is on its board");
            Assert.That(guestBoard.UserScore, Is.Null, "a guest has no row of its own");
            Assert.That(JToken.DeepEquals(JObject.Parse(guest), playerJson), Is.True,
                "everything but user_score is the board a player reads");
        });
    }

    [TestCase("country")]
    [TestCase("friend")]
    [TestCase("team")]
    public async Task Board_PersonalScope_IsAnEmptyBoardForAGuest_AndUnchangedForAPlayer(string type)
    {
        string board = $"/api/v2/beatmaps/{ServerFixture.PendingBeatmapId}/scores";
        string path = $"{board}?type={type}&mode=typebeat&limit=50";

        var guest = JsonConvert.DeserializeObject<APIScoresCollection>(await getAsync(path, bearer: false))!;
        string player = await getAsync(path, bearer: true);
        string playerGlobal = await getAsync($"{board}?type=global&mode=typebeat&limit=50", bearer: true);

        Assert.Multiple(() =>
        {
            Assert.That(guest.Scores, Is.Empty);
            Assert.That(guest.ScoresCount, Is.Zero);
            Assert.That(guest.UserScore, Is.Null);

            // A signed-in caller's scope is still not filtered by this server (every type answers
            // the global board), which backlog 406 deliberately leaves alone.
            Assert.That(player, Is.EqualTo(playerGlobal));
            Assert.That(JsonConvert.DeserializeObject<APIScoresCollection>(player)!.Scores, Is.Not.Empty);
        });
    }

    [TestCase("")]
    [TestCase("/typebeat")]
    public async Task Profile_AnswersAGuest_AsItAnswersAPlayer(string suffix)
    {
        string body = await sameForGuestAsync($"/api/v2/users/{ServerFixture.PlayerUserId}{suffix}?key=id");

        var user = JsonConvert.DeserializeObject<APIUser>(body)!;
        Assert.That(user.Username, Is.EqualTo(ServerFixture.PlayerUsername));
    }

    [TestCase("best")]
    [TestCase("firsts")]
    [TestCase("recent")]
    [TestCase("pinned")]
    public async Task ProfileScoreSections_AnswerAGuest_AsTheyAnswerAPlayer(string type)
    {
        string body = await sameForGuestAsync($"/api/v2/users/{ServerFixture.PlayerUserId}/scores/{type}?offset=0&limit=51&mode=typebeat");
        Assert.That(JsonConvert.DeserializeObject<List<SoloScoreInfo>>(body), Is.Not.Null);
    }

    [Test]
    public async Task ProfileMostPlayed_AnswersAGuest_AsItAnswersAPlayer()
    {
        string body = await sameForGuestAsync($"/api/v2/users/{ServerFixture.PlayerUserId}/beatmapsets/most_played?offset=0&limit=51");
        Assert.That(JsonConvert.DeserializeObject<List<APIUserMostPlayedBeatmap>>(body), Is.Not.Null);
    }

    [TestCase("ranked")]
    [TestCase("pending")]
    [TestCase("favourite")]
    public async Task ProfileBeatmapsets_AnswerAGuest_AsTheyAnswerAPlayer(string type)
    {
        // The owner's sections, read by the player, whose favourites are not on them: the viewer
        // decides only each card's own heart, so the two answers must match byte for byte.
        string body = await sameForGuestAsync($"/api/v2/users/{ServerFixture.OwnerUserId}/beatmapsets/{type}?offset=0&limit=51");
        Assert.That(JsonConvert.DeserializeObject<List<APIBeatmapSet>>(body), Is.Not.Null);
    }

    [Test]
    public async Task SeasonalBackgrounds_AnswerAGuest()
    {
        string body = await sameForGuestAsync("/api/v2/seasonal-backgrounds");
        var backgrounds = JsonConvert.DeserializeObject<APISeasonalBackgrounds>(body)!;

        Assert.That(backgrounds.Backgrounds, Is.Empty);
    }

    // ---- helpers ----

    /// <summary>GETs <paramref name="path"/> as a guest and as the seeded player; both 200, same bytes.</summary>
    private static async Task<string> sameForGuestAsync(string path)
    {
        string guest = await getAsync(path, bearer: false);
        string player = await getAsync(path, bearer: true);

        Assert.That(guest, Is.EqualTo(player), $"{path}: a guest reads exactly what a player does");
        return guest;
    }

    private static async Task<string> getAsync(string path, bool bearer)
    {
        using var req = bearer ? ServerFixture.Authed(HttpMethod.Get, path) : new HttpRequestMessage(HttpMethod.Get, path);
        using var resp = await client.SendAsync(req);
        string body = await resp.Content.ReadAsStringAsync();

        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK), $"{path} ({(bearer ? "player" : "guest")}): {body}");
        return body;
    }
}
