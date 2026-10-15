using Newtonsoft.Json;
using typebeat.Game.Online.API.Requests.Responses;
using typebeat.Game.Online.Leaderboards;
using BeatmapLeaderboard = Typebeat.Web.Scoring.BeatmapLeaderboard;
using BeatmapsetEndpoints = Typebeat.Web.Endpoints.BeatmapsetEndpoints;

namespace Typebeat.WireCompat;

/// <summary>
/// Cross-repo pin for WHICH BOARD a beatmap has. The server decides it from the set's stored status
/// (<see cref="BeatmapLeaderboard.BoardFor"/>); the game decides it from the status it was sent
/// (<see cref="GlobalLeaderboardAvailability.Resolve"/>), to choose between the ranked board, the
/// unranked board with its cue, and the "not available" message. A status the two sides read
/// differently would show a board the server never fills, or hide one it does.
///
/// <para>
/// Every stored status goes through the real wire path: <see cref="BeatmapsetEndpoints.StatusString"/>
/// on the server, then the client's own <see cref="APIBeatmap.Status"/> binding.
/// </para>
/// </summary>
[TestFixture]
public class LeaderboardAvailabilityParityTest
{
    /// <summary>Every value <c>beatmapsets.status</c> may hold (the CHECK in 047_loved_status.sql).</summary>
    private static readonly string[] stored_statuses = ["hidden", "pending", "unranked", "ranked", "loved", "removed"];

    [Test]
    public void ServerAndClientPickTheSameBoardForEveryStoredStatus()
    {
        Assert.Multiple(() =>
        {
            foreach (string stored in stored_statuses)
            {
                string onTheWire = BeatmapsetEndpoints.StatusString(stored);
                var beatmap = JsonConvert.DeserializeObject<APIBeatmap>($$"""{"id":1,"status":"{{onTheWire}}"}""")!;

                GlobalLeaderboardKind server = BeatmapLeaderboard.BoardFor(stored) switch
                {
                    true => GlobalLeaderboardKind.Ranked,
                    false => GlobalLeaderboardKind.Unranked,
                    null => GlobalLeaderboardKind.None,
                };

                Assert.That(GlobalLeaderboardAvailability.Resolve(beatmap.OnlineID, beatmap.Status), Is.EqualTo(server),
                    $"stored '{stored}', sent as '{onTheWire}' ({beatmap.Status})");
            }
        });
    }
}
