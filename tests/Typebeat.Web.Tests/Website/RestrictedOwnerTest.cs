using System.Net;
using Dapper;
using Npgsql;

namespace Typebeat.Web.Tests.Website;

/// <summary>
/// Restricting a mapper (users.restricted = true, the existing moderation lever) must delist
/// their sets everywhere their profile already 404s: landing strip, /beatmapsets listing and
/// search, favourite walls, and the set page itself; otherwise every "mapped by" link
/// site-wide points at a 404.
/// </summary>
public class RestrictedOwnerTest
{
    private static long restrictedMapperId;
    private static long restrictedSetId;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        await PublicSiteSeed.EnsureSeededAsync();

        await using var conn = new NpgsqlConnection(WebsiteFixture.ConnectionString);
        await conn.OpenAsync();

        restrictedMapperId = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO users (username, email, password_hash, country_code, restricted)
            VALUES ('banned mapper', 'banned.mapper@example.com', 'x', 'US', true)
            RETURNING id
            """);

        // Submitted "in the future" (the seed's hidden-set trick): it would top the landing
        // strip and the default listing sort if the restriction filter leaked.
        restrictedSetId = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmapsets (owner_id, title, artist, status, submitted_at, updated_at)
            VALUES (@ownerId, 'Restricted Banger', 'Banned Beats', 'ranked', now() + interval '3 hours', now())
            RETURNING id
            """,
            new { ownerId = restrictedMapperId });

        await conn.ExecuteAsync(
            """
            INSERT INTO beatmaps (set_id, version_name, checksum_md5, total_length_s, drain_length_s, difficulty_rating, filename)
            VALUES (@setId, 'type!beat', @checksum, 90, 80, 3.0, 'map.osu')
            """,
            new { setId = restrictedSetId, checksum = Guid.NewGuid().ToString("N") });

        // The set even has a version/package and a favourite from the fixture user; none of
        // which may resurface it.
        await conn.ExecuteAsync(
            "INSERT INTO set_versions (set_id, version_no, package_key) VALUES (@setId, 1, 'packages/' || @setId || '/1.typb')",
            new { setId = restrictedSetId });

        await conn.ExecuteAsync(
            "INSERT INTO favourites (user_id, set_id) VALUES (@userId, @setId) ON CONFLICT DO NOTHING",
            new { userId = WebsiteFixture.SeededUserId, setId = restrictedSetId });

        // Same search-vector expression the seed uses, scoped to the new set.
        await conn.ExecuteAsync(
            $"UPDATE beatmapsets s SET search = {Typebeat.Web.Packages.PackageIngest.SearchVectorSql} " +
            "FROM users u WHERE u.id = s.owner_id AND s.id = @setId",
            new { setId = restrictedSetId });
    }

    [Test]
    public async Task Landing_OmitsRestrictedMappersSets()
    {
        using var response = await WebsiteFixture.Client.GetAsync("/");
        string html = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(html, Does.Not.Contain("Restricted Banger"));
            Assert.That(html, Does.Not.Contain($"data-set-id=\"{restrictedSetId}\""));
        });
    }

    [Test]
    public async Task Listing_And_Search_OmitRestrictedMappersSets()
    {
        using var listing = await WebsiteFixture.Client.GetAsync("/beatmapsets");
        using var byTitle = await WebsiteFixture.Client.GetAsync("/beatmapsets?q=Restricted%20Banger");
        using var byMapper = await WebsiteFixture.Client.GetAsync("/beatmapsets?q=banned%20mapper");

        string listingHtml = await listing.Content.ReadAsStringAsync();
        string byTitleHtml = await byTitle.Content.ReadAsStringAsync();
        string byMapperHtml = await byMapper.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(listingHtml, Does.Not.Contain("Restricted Banger"),
                "future submitted_at means it would lead the default sort if leaked");
            Assert.That(byTitleHtml, Does.Not.Contain($"data-set-id=\"{restrictedSetId}\""));
            Assert.That(byMapperHtml, Does.Not.Contain($"data-set-id=\"{restrictedSetId}\""));
        });
    }

    [Test]
    public async Task SetPage_404s_LikeTheMappersProfile()
    {
        using var setPage = await WebsiteFixture.Client.GetAsync($"/beatmapsets/{restrictedSetId}");
        using var profile = await WebsiteFixture.Client.GetAsync($"/users/{restrictedMapperId}");

        Assert.Multiple(() =>
        {
            Assert.That(setPage.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(profile.StatusCode, Is.EqualTo(HttpStatusCode.NotFound), "profiles already 404, the pages must agree");
        });
    }

    [Test]
    public async Task FavouriteWalls_OmitRestrictedMappersSets()
    {
        using var response = await WebsiteFixture.Client.GetAsync($"/users/{WebsiteFixture.SeededUserId}");
        string html = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(html, Does.Not.Contain("Restricted Banger"));
        });
    }
}
