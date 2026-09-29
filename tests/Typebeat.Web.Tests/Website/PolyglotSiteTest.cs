using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Dapper;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Npgsql;
using Typebeat.Web.Auth;
using Typebeat.Web.Data;
using Typebeat.Web.Packages;

namespace Typebeat.Web.Tests.Website;

/// <summary>
/// The site half of Polyglot (backlog 332). The desktop client's PG mod types a lyric in its
/// ORIGINAL script and is LOCAL ONLY, so:
/// <list type="bullet">
/// <item>the game API REFUSES a score token and a submission carrying PG, and stores nothing
/// (the stock client never sends either, so only a modified one could);</item>
/// <item>the set page shows a difficulty's originals beside the romanisation, ENCODED, each line
/// declaring its language, led by whichever the viewer's "prefer original metadata" asks for, and
/// marks the difficulty "Polyglot available";</item>
/// <item>the listing card marks its difficulty chip the same way;</item>
/// <item>search finds the map in either script: its original title, its romanised title, and a
/// word of its original lyrics.</item>
/// </list>
/// </summary>
[TestFixture]
[NonParallelizable]
public class PolyglotSiteTest
{
    // A clean 10/10 play: accuracy 1, fully judged, total inside the no-mod ceiling.
    private const long clean_total = 400_000;

    // Script-like ORIGINAL text on the first line: the page must print it as text, never as markup.
    private const string xss_original = "<script>alert('pg')</script> Звезда";

    private const string romanised_lyrics = "Zvezda po imeni Solntse\nGruppa krovi na rukave\nand one english line";

    // Aligned line for line; the third line has no original.
    private static readonly string original_lyrics = xss_original + "\nГруппа крови на рукаве\n";

    private const string preferring_username = "polyglot prefers original";
    private const string preferring_password = "hunter2hunter2";

    private static long setId;
    private static long polyglotBeatmapId;
    private static long plainBeatmapId;
    private static long plainSetId;
    private static string polyglotChecksum = null!;
    private static long typistId;
    private static string bearer = null!;

    private static NpgsqlDataSource dataSource = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        await PublicSiteSeed.EnsureSeededAsync();

        dataSource = NpgsqlDataSource.Create(WebsiteFixture.ConnectionString);

        await using var conn = await dataSource.OpenConnectionAsync();

        // A dedicated typist: the refusal tests count THIS user's rows, so nothing else writes them.
        typistId = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO users (username, email, password_hash, country_code)
            VALUES ('polyglot typist', 'polyglot.typist@example.com', 'x', 'US')
            RETURNING id
            """);

        bearer = (await new TokenService(new Db(dataSource)).IssueAsync(typistId)).AccessToken;

        // The set: Russian, a romanised title and its Cyrillic original, and two difficulties of
        // which only the harder carries originals.
        setId = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmapsets (owner_id, title, artist, title_unicode, artist_unicode, language, status, submitted_at, updated_at)
            VALUES (@ownerId, 'Zvezda Polyglotnaya', 'Kinoshki', 'Звезда Полиглотная', 'Киношки', 'russian', 'ranked',
                    now() - interval '3 days', now() - interval '3 days')
            RETURNING id
            """,
            new { ownerId = PublicSiteSeed.MapperId });

        await RefreshSearchVectorAsync(conn, setId);

        polyglotChecksum = Guid.NewGuid().ToString("N");
        polyglotBeatmapId = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmaps (set_id, version_name, checksum_md5, total_length_s, drain_length_s, difficulty_rating,
                                  filename, lyrics, lyrics_original)
            VALUES (@setId, 'polyglot hard', @checksum, 60, 0, 4.0, 'hard.osu', @lyrics, @originals)
            RETURNING id
            """,
            new { setId, checksum = polyglotChecksum, lyrics = romanised_lyrics, originals = original_lyrics });

        plainBeatmapId = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmaps (set_id, version_name, checksum_md5, total_length_s, drain_length_s, difficulty_rating,
                                  filename, lyrics)
            VALUES (@setId, 'romanised easy', @checksum, 60, 0, 2.0, 'easy.osu', 'Zvezda po imeni Solntse')
            RETURNING id
            """,
            new { setId, checksum = Guid.NewGuid().ToString("N") });

        // A control set with no originals anywhere, whose card must carry no marker.
        plainSetId = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmapsets (owner_id, title, artist, language, status, submitted_at, updated_at)
            VALUES (@ownerId, 'Monoglot Control', 'Kinoshki', 'english', 'ranked', now() - interval '3 days', now() - interval '3 days')
            RETURNING id
            """,
            new { ownerId = PublicSiteSeed.MapperId });

        await RefreshSearchVectorAsync(conn, plainSetId);

        await conn.ExecuteAsync(
            """
            INSERT INTO beatmaps (set_id, version_name, checksum_md5, total_length_s, drain_length_s, difficulty_rating, filename, lyrics)
            VALUES (@plainSetId, 'only', @checksum, 60, 0, 3.0, 'only.osu', 'no originals here')
            """,
            new { plainSetId, checksum = Guid.NewGuid().ToString("N") });

        long preferringId = await WebsiteFixture.SeedUserAsync(preferring_username, "polyglot.prefers@example.com", preferring_password, verified: true);
        await conn.ExecuteAsync("UPDATE users SET prefer_original_metadata = true WHERE id = @preferringId", new { preferringId });
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (dataSource != null)
            await dataSource.DisposeAsync();
    }

    // ---- local only: the game API refuses PG and stores nothing ----

    [TestCase("PG")]
    [TestCase("pg")]
    [TestCase("""["HD","PG"]""")]
    [TestCase("""[{"acronym":"PG"}]""")]
    [TestCase("DT,PG")]
    public async Task ScoreToken_CarryingPolyglot_IsRefused_AndNoTokenIsStored(string mods)
    {
        long tokensBefore = await CountAsync("SELECT count(*) FROM score_tokens WHERE user_id = @typistId");

        using var response = await RequestTokenAsync(new Dictionary<string, string> { ["mods"] = mods });
        string body = await response.Content.ReadAsStringAsync();
        long tokensAfter = await CountAsync("SELECT count(*) FROM score_tokens WHERE user_id = @typistId");

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.UnprocessableEntity), body);
            Assert.That((string?)JObject.Parse(body)["error"], Does.Contain("Polyglot"), "the client reads the error field of any non-2xx body");
            Assert.That(tokensAfter, Is.EqualTo(tokensBefore),
                "a refused token request writes no token row");
        });
    }

    [Test]
    public async Task ScoreToken_WithAnOrdinaryModsField_IsStillIssued()
    {
        // The refusal must not catch anything but a local-only mod.
        using var response = await RequestTokenAsync(new Dictionary<string, string> { ["mods[]"] = "HD" });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task Submission_CarryingPolyglot_IsRefused_AndNothingIsStored()
    {
        long tokenId = await IssueTokenAsync();
        long scoresBefore = await CountAsync("SELECT count(*) FROM scores WHERE user_id = @typistId");

        using var response = await SubmitAsync(tokenId, [new { acronym = "PG" }]);
        string body = await response.Content.ReadAsStringAsync();
        long scoresAfter = await CountAsync("SELECT count(*) FROM scores WHERE user_id = @typistId");
        long consumed = await CountAsync("SELECT count(*) FROM score_tokens WHERE id = " + tokenId + " AND score_id IS NOT NULL");

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.UnprocessableEntity), body);
            Assert.That((string?)JObject.Parse(body)["error"], Does.Contain("Polyglot"));
            Assert.That(scoresAfter, Is.EqualTo(scoresBefore),
                "no score row, so no pp, no rating cell and no board");
            Assert.That(consumed, Is.Zero,
                "the token is not even consumed");
        });
    }

    [Test]
    public async Task Submission_WithPolyglotInsideAStack_IsRefused_ButTheSameStackWithoutItIsStored()
    {
        using (var refused = await SubmitAsync(await IssueTokenAsync(), [new { acronym = "HD" }, new { acronym = "pg" }]))
            Assert.That(refused.StatusCode, Is.EqualTo(HttpStatusCode.UnprocessableEntity), "case-insensitive, anywhere in the stack");

        long scoresBefore = await CountAsync("SELECT count(*) FROM scores WHERE user_id = @typistId");

        using var accepted = await SubmitAsync(await IssueTokenAsync(), [new { acronym = "HD" }]);
        long scoresAfter = await CountAsync("SELECT count(*) FROM scores WHERE user_id = @typistId");

        Assert.Multiple(() =>
        {
            Assert.That(accepted.StatusCode, Is.EqualTo(HttpStatusCode.OK), "the gate is PG, not mods in general");
            Assert.That(scoresAfter, Is.EqualTo(scoresBefore + 1));
        });
    }

    // ---- the set page ----

    [Test]
    public async Task SetPage_RendersOriginalsEncoded_WithLangAttributes_AndTheMarker()
    {
        string html = await GetHtmlAsync(WebsiteFixture.Client, $"/beatmapsets/{setId}?diff={polyglotBeatmapId}");
        string lyrics = LyricsSection(html);

        Assert.Multiple(() =>
        {
            // Encoded, never live: the script-like original arrives as text.
            Assert.That(html, Does.Not.Contain("<script>alert('pg')</script>"));
            Assert.That(lyrics, Does.Contain("&lt;script&gt;alert(&#x27;pg&#x27;)&lt;/script&gt; Звезда").Or.Contain("&lt;script&gt;alert(&#x27;pg&#x27;)&lt;/script&gt; &#x417;"),
                "the original is printed as text");

            // Each half declares its language: the original in Russian, the romanisation in Russian
            // written in Latin script, so a screen reader keeps the voice and a browser the font.
            Assert.That(lyrics, Does.Contain("lang=\"ru\""));
            Assert.That(lyrics, Does.Contain("lang=\"ru-Latn\">Zvezda po imeni Solntse<"));
            Assert.That(Regex.Matches(lyrics, "<li class=\"set-lyrics__line\">").Count, Is.EqualTo(3), "one item per stored lyric line");

            // Anonymous viewers do not prefer originals: the romanisation leads, the script beneath.
            Assert.That(lyrics.IndexOf("Zvezda po imeni Solntse", StringComparison.Ordinal),
                Is.LessThan(lyrics.IndexOf("&lt;script&gt;", StringComparison.Ordinal)));
            Assert.That(lyrics, Does.Contain("set-lyrics__beneath set-lyrics__original\" lang=\"ru\""));

            // A line with no original shows the romanisation alone.
            Assert.That(lyrics, Does.Contain("<span class=\"set-lyrics__lead\" lang=\"ru-Latn\">and one english line</span>\n                    </li>").Or
                .Contain("<span class=\"set-lyrics__lead\" lang=\"ru-Latn\">and one english line</span>\r\n                    </li>"));

            // The marker, in the lyrics section and in the stats box.
            Assert.That(lyrics, Does.Contain("Polyglot available"));
            Assert.That(html, Does.Contain("stat-row--polyglot"));
        });
    }

    [Test]
    public async Task SetPage_ForAViewerWhoPrefersOriginals_LeadsWithTheScript()
    {
        var (client, _) = WebsiteFixture.CreateBrowser();
        using (await WebsiteFixture.LoginAndVerifyAsync(client, preferring_username, preferring_password)) { }

        string lyrics = LyricsSection(await GetHtmlAsync(client, $"/beatmapsets/{setId}?diff={polyglotBeatmapId}"));

        Assert.Multiple(() =>
        {
            Assert.That(lyrics, Does.Contain("set-lyrics__lead set-lyrics__original\" lang=\"ru\""));
            Assert.That(lyrics.IndexOf("&lt;script&gt;", StringComparison.Ordinal),
                Is.LessThan(lyrics.IndexOf("Zvezda po imeni Solntse", StringComparison.Ordinal)), "the script leads, the romanisation beneath");
            Assert.That(lyrics, Does.Contain("set-lyrics__beneath\" lang=\"ru-Latn\">Zvezda po imeni Solntse<"));
            Assert.That(lyrics, Does.Not.Contain("<script>"));
        });
    }

    [Test]
    public async Task SetPage_ADifficultyWithoutOriginals_KeepsThePlainLyricsAndNoMarker()
    {
        string html = await GetHtmlAsync(WebsiteFixture.Client, $"/beatmapsets/{setId}?diff={plainBeatmapId}");
        string lyrics = LyricsSection(html);

        Assert.Multiple(() =>
        {
            Assert.That(lyrics, Does.Contain("<div class=\"set-lyrics__text\" lang=\"ru-Latn\">Zvezda po imeni Solntse</div>"));
            Assert.That(lyrics, Does.Not.Contain("Polyglot"));
            Assert.That(html, Does.Not.Contain("stat-row--polyglot"));
        });
    }

    // ---- the card ----

    [Test]
    public async Task Card_MarksTheDifficultyChip_OnlyWhenADifficultyHasOriginals()
    {
        string polyglotCard = Card(await GetHtmlAsync(WebsiteFixture.Client, "/beatmapsets?q=Polyglotnaya"), setId);
        string plainCard = Card(await GetHtmlAsync(WebsiteFixture.Client, "/beatmapsets?q=" + Uri.EscapeDataString("Monoglot Control")), plainSetId);

        var button = Regex.Match(polyglotCard, "<button type=\"button\" class=\"chip chip--diffs\"[^>]*>").Value;

        Assert.Multiple(() =>
        {
            Assert.That(polyglotCard, Does.Contain("data-polyglot-available"));
            Assert.That(polyglotCard, Does.Contain("Polyglot available on 1 of 2 difficulties"));

            // Per difficulty, in the label the stack swaps as it cycles: the hard one says it, the
            // easy one does not.
            Assert.That(WebUtility.HtmlDecode(Regex.Match(button, "aria-label=\"([^\"]*)\"").Groups[1].Value),
                Does.StartWith("polyglot hard: 4.0 stars, Polyglot available."));
            Assert.That(WebUtility.HtmlDecode(Regex.Match(button, "data-diffs=\"([^\"]*)\"").Groups[1].Value),
                Does.Contain("romanised easy: 2.0 stars. 2 difficulties"));

            Assert.That(plainCard, Does.Not.Contain("data-polyglot-available"));
            Assert.That(plainCard, Does.Not.Contain("Polyglot"));
        });
    }

    // ---- search, in either script ----

    [TestCase("Звезда Полиглотная", TestName = "Search_FindsTheMap_ByItsCyrillicTitle")]
    [TestCase("Zvezda Polyglotnaya", TestName = "Search_FindsTheMap_ByItsRomanisedTitle")]
    [TestCase("title:Полиглотная", TestName = "Search_FindsTheMap_ByTheTitleOperatorInCyrillic")]
    [TestCase("artist:Киношки", TestName = "Search_FindsTheMap_ByTheArtistOperatorInCyrillic")]
    [TestCase("lyrics:рукаве", TestName = "Search_FindsTheMap_ByItsOriginalLyricText")]
    [TestCase("lyrics:rukave", TestName = "Search_FindsTheMap_ByItsRomanisedLyricText")]
    public async Task Search_FindsTheMapInEitherScript(string query)
    {
        string html = await GetHtmlAsync(WebsiteFixture.Client, "/beatmapsets?q=" + Uri.EscapeDataString(query));

        Assert.That(html, Does.Contain($"data-set-id=\"{setId}\""), query);
    }

    [Test]
    public async Task Search_ByOriginalLyricText_DoesNotFindAMapWithoutIt()
    {
        string html = await GetHtmlAsync(WebsiteFixture.Client, "/beatmapsets?q=" + Uri.EscapeDataString("lyrics:рукаве"));

        Assert.That(html, Does.Not.Contain($"data-set-id=\"{plainSetId}\""));
    }

    // ---- helpers ----

    private static async Task RefreshSearchVectorAsync(NpgsqlConnection conn, long id)
        => await conn.ExecuteAsync(
            $"""
             UPDATE beatmapsets s
             SET search = {PackageIngest.SearchVectorSql}
             FROM users u
             WHERE u.id = s.owner_id AND s.id = @id
             """,
            new { id });

    private static async Task<long> CountAsync(string sql)
    {
        await using var conn = await dataSource.OpenConnectionAsync();
        return await conn.ExecuteScalarAsync<long>(sql, new { typistId });
    }

    private static async Task<HttpResponseMessage> RequestTokenAsync(Dictionary<string, string>? extra = null)
    {
        var form = new Dictionary<string, string>
        {
            ["version_hash"] = "polyglot-test-build",
            ["beatmap_hash"] = polyglotChecksum,
            ["ruleset_id"] = "0",
        };

        foreach (var (key, value) in extra ?? [])
            form[key] = value;

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v2/beatmaps/{polyglotBeatmapId}/solo/scores");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Content = new FormUrlEncodedContent(form);

        return await WebsiteFixture.Client.SendAsync(request);
    }

    private static async Task<long> IssueTokenAsync()
    {
        using var response = await RequestTokenAsync();
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), "score token");

        return (long)JObject.Parse(await response.Content.ReadAsStringAsync())["id"]!;
    }

    private static async Task<HttpResponseMessage> SubmitAsync(long tokenId, object[] mods)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v2/beatmaps/{polyglotBeatmapId}/solo/scores/{tokenId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Content = new StringContent(JsonConvert.SerializeObject(new
        {
            passed = true,
            total_score = clean_total,
            total_score_without_mods = clean_total,
            accuracy = 1.0,
            max_combo = 10,
            ruleset_id = 0,
            rank = "X",
            statistics = new Dictionary<string, int> { ["great"] = 10 },
            maximum_statistics = new Dictionary<string, int> { ["great"] = 10 },
            mods,
        }), System.Text.Encoding.UTF8, "application/json");

        return await WebsiteFixture.Client.SendAsync(request);
    }

    private static async Task<string> GetHtmlAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), url);

        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>The page's lyrics section, from its opening tag to the next section.</summary>
    private static string LyricsSection(string html)
    {
        int start = html.IndexOf("<section class=\"set-lyrics\">", StringComparison.Ordinal);
        Assert.That(start, Is.GreaterThanOrEqualTo(0), "the lyrics section renders");

        int end = html.IndexOf("</section>", start, StringComparison.Ordinal);
        return html[start..end];
    }

    /// <summary>One card's markup out of a listing page.</summary>
    private static string Card(string html, long id)
    {
        int start = html.IndexOf($"data-set-id=\"{id}\"", StringComparison.Ordinal);
        Assert.That(start, Is.GreaterThanOrEqualTo(0), $"card {id} is listed");

        int end = html.IndexOf("</article>", start, StringComparison.Ordinal);
        return html[start..end];
    }
}
