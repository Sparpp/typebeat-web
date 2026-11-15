using System.Net;
using Dapper;
using Newtonsoft.Json.Linq;
using Npgsql;

namespace Typebeat.Web.Tests.Website;

/// <summary>
/// Backlog 472: a set may declare several song languages (beatmapsets.languages, 057_languages.sql), and
/// both language filters match a set when ANY of its languages is the one asked for: the client listing's
/// Language filter row (<c>l=</c>, osu's language ids) and the search box's <c>language:</c> operator, on the
/// API listing and on the /beatmapsets page alike. Two <c>language:</c> operators find the sets that declare
/// both, as song select's <c>lang:</c> does.
/// </summary>
[TestFixture]
[NonParallelizable]
public class LanguageListSearchTest
{
    private const string tag = "languagelistsearch";

    private static NpgsqlDataSource dataSource = null!;

    private static long bilingualSetId;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        await PublicSiteSeed.EnsureSeededAsync();

        dataSource = NpgsqlDataSource.Create(WebsiteFixture.ConnectionString);
        await using var conn = await dataSource.OpenConnectionAsync();

        bilingualSetId = await insertSetAsync(conn, "Bilingual", "japanese", ["english", "japanese"]);
        await insertSetAsync(conn, "Japanese Only", "japanese", ["japanese"]);
        await insertSetAsync(conn, "English Only", "english", ["english"]);
        await insertSetAsync(conn, "Korean And English", "korean", ["korean", "english"]);
        await insertSetAsync(conn, "Unclassified", "", []);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (dataSource != null)
            await dataSource.DisposeAsync();
    }

    [TestCase("l=2", new[] { "Bilingual", "English Only", "Korean And English" })] // osu's English
    [TestCase("l=3", new[] { "Bilingual", "Japanese Only" })] // osu's Japanese
    [TestCase("l=6", new[] { "Korean And English" })] // osu's Korean
    [TestCase("l=1", new[] { "Unclassified" })] // osu's Unspecified: the sets with no language at all
    [TestCase("l=4", new string[0])] // osu's Chinese
    public async Task TheListingFilterRowMatchesAnyLanguage(string filter, string[] expected)
        => Assert.That(await titlesAsync($"title%3D{tag}&{filter}"), Is.EquivalentTo(expected));

    [TestCase("language:english", new[] { "Bilingual", "English Only", "Korean And English" })]
    [TestCase("lang:jp", new[] { "Bilingual", "Japanese Only" })]
    [TestCase("lang:en lang:jp", new[] { "Bilingual" })]
    [TestCase("lang:ko lang:jp", new string[0])]
    public async Task TheLanguageOperatorMatchesAnyLanguage(string query, string[] expected)
        => Assert.That(await titlesAsync(Uri.EscapeDataString($"title={tag} {query}")), Is.EquivalentTo(expected));

    [Test]
    public async Task TheListingPageMatchesAnyLanguageToo()
    {
        string html = await getBodyAsync($"/beatmapsets?q={Uri.EscapeDataString($"{tag} language:english")}");

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain($"{tag} Bilingual"));
            Assert.That(html, Does.Contain($"{tag} Korean And English"));
            Assert.That(html, Does.Not.Contain($"{tag} Japanese Only"));
        });
    }

    [Test]
    public async Task TheSetPageShowsAChipPerLanguage()
    {
        string html = await getBodyAsync($"/beatmapsets/{bilingualSetId}");

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("href=\"/beatmapsets?q=language%3Aenglish\""));
            Assert.That(html, Does.Contain("href=\"/beatmapsets?q=language%3Ajapanese\""));
            Assert.That(html.IndexOf("language%3Aenglish", StringComparison.Ordinal),
                Is.LessThan(html.IndexOf("language%3Ajapanese", StringComparison.Ordinal)), "in the mapper's order");
        });
    }

    // ---- helpers ----

    /// <summary>
    /// The titles (tag stripped) the API listing returns for <paramref name="query"/>, the already-escaped rest of the
    /// URL after <c>q=</c>, which always confines the search to this fixture's sets by title.
    /// </summary>
    private static async Task<List<string>> titlesAsync(string query)
    {
        var result = JObject.Parse(await getBodyAsync($"/api/v2/beatmapsets/search?q={query}"));
        return result["beatmapsets"]!.Select(s => ((string)s["title"]!)[(tag.Length + 1)..]).ToList();
    }

    private static async Task<string> getBodyAsync(string url)
    {
        using var response = await WebsiteFixture.Client.GetAsync(url);
        string body = await response.Content.ReadAsStringAsync();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), $"{url}: {body}");
        return body;
    }

    private static async Task<long> insertSetAsync(NpgsqlConnection conn, string title, string language, string[] languages)
    {
        long setId = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmapsets (owner_id, title, artist, language, languages, status, submitted_at, updated_at)
            VALUES (@ownerId, @title, 'Language List Artist', @language, @languages, 'ranked', now() - interval '3 days', now() - interval '3 days')
            RETURNING id
            """,
            new { ownerId = PublicSiteSeed.MapperId, title = $"{tag} {title}", language, languages });

        await conn.ExecuteAsync(
            """
            INSERT INTO beatmaps (set_id, version_name, checksum_md5, total_length_s, drain_length_s, difficulty_rating, filename, lyrics)
            VALUES (@setId, 'only', @checksum, 60, 0, 3.0, 'only.osu', 'some lyrics here')
            """,
            new { setId, checksum = Guid.NewGuid().ToString("N") });

        return setId;
    }
}
