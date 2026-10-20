using System.Net;
using Dapper;
using Newtonsoft.Json.Linq;
using Npgsql;
using Typebeat.Web.Endpoints;

namespace Typebeat.Web.Tests.Website;

/// <summary>
/// Backlog 400: GET /api/v2/users/lookup?ids[]=.., the game's LookupUsersRequest (its UserLookupCache). The
/// visible users come back as compact users, restricted and deleted ones are absent, the id list is capped at
/// the client's own 50, and a request with no ids at all stays the profile fetch of a user NAMED "lookup",
/// which the literal route would otherwise shadow.
/// </summary>
[TestFixture]
[NonParallelizable]
public class UserLookupTest
{
    private static long visibleId, otherVisibleId, restrictedId, deletedId, namedLookupId;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        await PublicSiteSeed.EnsureSeededAsync();

        await using var conn = new NpgsqlConnection(WebsiteFixture.ConnectionString);
        await conn.OpenAsync();

        visibleId = await insertUserAsync(conn, "lookup visible", "DE", restricted: false, deleted: false);
        otherVisibleId = await insertUserAsync(conn, "lookup other", "JP", restricted: false, deleted: false);
        restrictedId = await insertUserAsync(conn, "lookup restricted", "US", restricted: true, deleted: false);
        deletedId = await insertUserAsync(conn, "lookup deleted", "US", restricted: false, deleted: true);
        namedLookupId = await insertUserAsync(conn, "lookup", "FR", restricted: false, deleted: false);
    }

    [Test]
    public async Task VisibleUsers_ComeBackCompact_AndHiddenOrUnknownIdsAreAbsent()
    {
        // The client's own target shape: a trailing slash before the query.
        var json = await getAsync($"/api/v2/users/lookup/?ids[]={visibleId}&ids[]={otherVisibleId}&ids[]={restrictedId}&ids[]={deletedId}&ids[]=999999999&ids[]=junk");
        var users = (JArray)json["users"]!;

        var byId = users.ToDictionary(u => (long)u["id"]!);

        Assert.Multiple(() =>
        {
            Assert.That(byId.Keys, Is.EquivalentTo(new[] { visibleId, otherVisibleId }));
            Assert.That((string?)byId[visibleId]["username"], Is.EqualTo("lookup visible"));
            Assert.That((string?)byId[visibleId]["country_code"], Is.EqualTo("DE"));
            Assert.That((string?)byId[visibleId]["avatar_url"], Does.StartWith("https://localhost/"), "always absolute, on this host");
        });
    }

    [Test]
    public async Task NoValidIds_IsAnEmptyList_NotAnError()
    {
        var json = await getAsync("/api/v2/users/lookup?ids[]=&ids[]=0&ids[]=-3");
        Assert.That((JArray)json["users"]!, Is.Empty);
    }

    [Test]
    public async Task TheIdList_IsCappedAtTheClientsOwnLimit()
    {
        // The visible user's id sits PAST the cap, behind fifty well-formed ids that name nobody.
        var filler = Enumerable.Range(0, UserEndpoints.MaxLookupIds).Select(i => 900_000_000L + i);
        string query = string.Join("&", filler.Append(visibleId).Select(id => $"ids[]={id}"));

        var json = await getAsync($"/api/v2/users/lookup?{query}");
        Assert.That((JArray)json["users"]!, Is.Empty, "an id past the cap is not looked up");

        var within = await getAsync($"/api/v2/users/lookup?ids[]={visibleId}&{string.Join("&", filler.Take(UserEndpoints.MaxLookupIds - 1).Select(id => $"ids[]={id}"))}");
        Assert.That(((JArray)within["users"]!).Select(u => (long)u["id"]!), Is.EqualTo(new[] { visibleId }));
    }

    [TestCase("/api/v2/users/lookup?key=username")]
    [TestCase("/api/v2/users/lookup/?key=username")]
    public async Task WithNoIds_ItIsStillTheProfileOfAUserNamedLookup(string path)
    {
        var json = await getAsync(path);

        Assert.Multiple(() =>
        {
            Assert.That((long)json["id"]!, Is.EqualTo(namedLookupId));
            Assert.That((string?)json["username"], Is.EqualTo("lookup"));
        });
    }

    private static async Task<JObject> getAsync(string path)
    {
        using var response = await WebsiteFixture.Client.GetAsync(path);
        string body = await response.Content.ReadAsStringAsync();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), $"{path}: {body}");
        return JObject.Parse(body);
    }

    private static async Task<long> insertUserAsync(NpgsqlConnection conn, string username, string country, bool restricted, bool deleted)
        => await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO users (username, email, password_hash, country_code, restricted, deleted_at)
            VALUES (@username, @email, 'not-a-real-hash', @country, @restricted, CASE WHEN @deleted THEN now() END)
            RETURNING id
            """,
            new { username, email = username.Replace(' ', '.') + "@example.com", country, restricted, deleted });
}
