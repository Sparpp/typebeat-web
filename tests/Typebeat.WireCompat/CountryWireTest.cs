using Newtonsoft.Json;
using osu.Framework.Extensions;
using typebeat.Game.Online.API.Requests.Responses;
using typebeat.Game.Users;
using Countries = Typebeat.Web.Countries;

namespace Typebeat.WireCompat;

/// <summary>
/// Cross-repo pin for country flags (backlog 333). <c>country_code</c> has travelled on every user
/// and score since the first wire endpoint; what changed is that the server now STORES real codes
/// (detected at sign-up, backfilled once, or chosen in Settings). The client reads the field with
/// <c>Enum.TryParse</c> into its <see cref="CountryCode"/> enum, so the server's list of storable
/// codes (<see cref="Countries"/>) has to be exactly the enum's real countries: a code the enum
/// lacks would silently show as the unknown flag in game while the website shows a real one.
/// </summary>
[TestFixture]
public class CountryWireTest
{
    /// <summary>The enum members that are not ISO 3166-1 countries, and so are never stored.</summary>
    private static readonly HashSet<CountryCode> non_countries =
    [
        CountryCode.Unknown,
        CountryCode.A1, CountryCode.A2, CountryCode.AP, CountryCode.O1,
        CountryCode.EU, CountryCode.AN, CountryCode.FX,
    ];

    [Test]
    public void ServerListMirrorsTheClientEnum_BothWays_WithTheSameNames()
    {
        var clientCountries = Enum.GetValues<CountryCode>().Where(c => !non_countries.Contains(c)).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(Countries.Codes, Is.EquivalentTo(clientCountries.Select(c => c.ToString())),
                "Typebeat.Web.Countries must list exactly the client enum's real countries");

            foreach (var country in clientCountries)
                Assert.That(Countries.NameOf(country.ToString()), Is.EqualTo(country.GetDescription()), country.ToString());
        });
    }

    [Test]
    public void EveryCodeTheServerCanStore_ParsesThroughTheClientsOwnProperty()
    {
        // APIUser.CountryCode is what every flag in the game reads; drive it from JSON exactly as a
        // response body reaches it, rather than calling Enum.TryParse here and hoping it matches.
        foreach (string code in Countries.Codes)
        {
            var user = JsonConvert.DeserializeObject<APIUser>($$"""{"id":1,"username":"x","country_code":"{{code}}"}""")!;
            Assert.That(user.CountryCode.ToString(), Is.EqualTo(code), code);
            Assert.That(user.CountryCode, Is.Not.EqualTo(CountryCode.Unknown), code);
        }

        var none = JsonConvert.DeserializeObject<APIUser>("""{"id":1,"username":"x","country_code":"XX"}""")!;
        Assert.That(none.CountryCode, Is.EqualTo(CountryCode.Unknown), "the stored 'no country' reads as Unknown, which hides the flag");
    }

    [Test]
    public async Task AUserWithARealCountry_RoundTripsToTheClientEnum()
    {
        long id;
        await using (var db = new Npgsql.NpgsqlConnection(ServerFixture.ConnectionString))
        {
            await db.OpenAsync();
            id = await Dapper.SqlMapper.ExecuteScalarAsync<long>(db,
                """
                INSERT INTO users (username, email, password_hash, country_code, country_chosen)
                VALUES ('wc_japan', 'wc_japan@example.com', 'x', 'JP', true)
                RETURNING id
                """);
            await Dapper.SqlMapper.ExecuteAsync(db, "INSERT INTO user_stats (user_id) VALUES (@id)", new { id });
        }

        using var req = ServerFixture.Authed(HttpMethod.Get, $"/api/v2/users/{id}?key=id");
        using var resp = await ServerFixture.Client.SendAsync(req);
        string body = await resp.Content.ReadAsStringAsync();
        Assert.That(resp.IsSuccessStatusCode, Is.True, $"users status {(int)resp.StatusCode}: {body}");

        var user = JsonConvert.DeserializeObject<APIUser>(body)!;
        Assert.That(user.CountryCode, Is.EqualTo(CountryCode.JP));
    }
}
