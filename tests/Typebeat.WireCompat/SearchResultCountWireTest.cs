using System.Net;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using typebeat.Game.Online.API.Requests;

namespace Typebeat.WireCompat;

/// <summary>
/// Backlog 474 on the game's wire: the listing overlay shows "1,234 results" from the search
/// response's <c>total</c>. The live answer parses with the client's own
/// <see cref="SearchBeatmapSetsResponse"/> and carries the count the overlay shows, and an answer
/// without the field (a server from before it) reads as unknown, which the overlay hides, never as
/// a wrong 0.
/// </summary>
[TestFixture]
public class SearchResultCountWireTest
{
    [Test]
    public async Task Search_CarriesTheTotal_TheClientReads()
    {
        using var req = ServerFixture.Authed(HttpMethod.Get, "/api/v2/beatmapsets/search");
        using var resp = await ServerFixture.Client.SendAsync(req);
        string body = await resp.Content.ReadAsStringAsync();
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK), body);

        var response = JsonConvert.DeserializeObject<SearchBeatmapSetsResponse>(body)!;

        Assert.Multiple(() =>
        {
            Assert.That(response.Total, Is.Not.Null);
            Assert.That(response.Total, Is.EqualTo((int)JObject.Parse(body)["total"]!));
            Assert.That(response.Total, Is.GreaterThan(0), "the fixture's published sets match the empty search");
            Assert.That(response.Total, Is.GreaterThanOrEqualTo(response.BeatmapSets.Count()));
        });
    }

    [Test]
    public void AnAnswerWithoutTheTotal_ReadsAsUnknown()
    {
        var response = JsonConvert.DeserializeObject<SearchBeatmapSetsResponse>("""{"beatmapsets": [], "cursor": null}""")!;

        Assert.That(response.Total, Is.Null);
    }
}
