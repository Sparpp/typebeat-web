using System.Net;
using System.Reflection;
using Newtonsoft.Json;
using typebeat.Game.Online.API;
using typebeat.Game.Online.API.Requests;
using typebeat.Game.Users;

namespace Typebeat.WireCompat;

/// <summary>
/// Backlog 400 on the game's wire: the batch user lookup behind the client's UserLookupCache. The path is
/// the one the game's own <see cref="LookupUsersRequest"/> builds (its trailing slash included), the body
/// parses with the client's own <see cref="GetUsersResponse"/>, and the cache reads the users off it by id,
/// so an unknown id is simply absent. A guest reads exactly what a player does.
/// </summary>
[TestFixture]
public class UserLookupWireTest
{
    private static HttpClient client => ServerFixture.Client;

    [Test]
    public async Task Lookup_OnTheClientsOwnTarget_ParsesThroughTheClientsOwnResponse()
    {
        const int unknown_id = int.MaxValue;
        var request = new LookupUsersRequest([(int)ServerFixture.PlayerUserId, (int)ServerFixture.OwnerUserId, unknown_id]);
        string path = "/api/v2/" + targetOf(request);

        Assert.That(path, Does.StartWith("/api/v2/users/lookup/?ids[]="), "the route this server maps is the one the client calls");

        string guest = await getAsync(path, bearer: false);
        string player = await getAsync(path, bearer: true);
        Assert.That(guest, Is.EqualTo(player), "a guest reads exactly what a player does");

        var response = JsonConvert.DeserializeObject<GetUsersResponse>(guest)!;
        var byId = response.Users.ToDictionary(u => u.Id);

        Assert.Multiple(() =>
        {
            Assert.That(byId.Keys, Is.EquivalentTo(new[] { (int)ServerFixture.PlayerUserId, (int)ServerFixture.OwnerUserId }),
                "the cache maps an absent id to null, so the unknown one must simply be missing");

            var playerUser = byId[(int)ServerFixture.PlayerUserId];
            Assert.That(playerUser.Username, Is.EqualTo(ServerFixture.PlayerUsername));
            Assert.That(playerUser.CountryCode, Is.EqualTo(CountryCode.US));
            Assert.That(playerUser.AvatarUrl, Does.StartWith("http"), "absolute, so the client never falls back to a ppy CDN avatar");
            Assert.That(playerUser.Team, Is.Null);
        });
    }

    [Test]
    public async Task Lookup_OfNobody_IsAnEmptyListTheCacheCanRead()
    {
        string path = "/api/v2/" + targetOf(new LookupUsersRequest([int.MaxValue]));

        var response = JsonConvert.DeserializeObject<GetUsersResponse>(await getAsync(path, bearer: false))!;
        Assert.That(response.Users, Is.Empty);
    }

    /// <summary>The request's own (protected) target, so this test calls whatever the client calls.</summary>
    private static string targetOf(APIRequest request)
        => (string)typeof(APIRequest).GetProperty("Target", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(request)!;

    private static async Task<string> getAsync(string path, bool bearer)
    {
        using var req = bearer ? ServerFixture.Authed(HttpMethod.Get, path) : new HttpRequestMessage(HttpMethod.Get, path);
        using var resp = await client.SendAsync(req);
        string body = await resp.Content.ReadAsStringAsync();

        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK), $"{path} ({(bearer ? "player" : "guest")}): {body}");
        return body;
    }
}
