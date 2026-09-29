using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Dapper;
using Newtonsoft.Json.Linq;
using Npgsql;
using Typebeat.Web.Auth;
using Typebeat.Web.Data;
using Typebeat.Web.Scoring;

namespace Typebeat.Web.Tests.Website;

/// <summary>
/// Country flags, site and client wide (backlog 333), through the real host. The fixture runs with
/// production's TYPEBEAT_BEHIND_PROXY, so CF-IPCountry is honoured exactly as it is live; the
/// direct-connection arm (never trusted) is the pure <c>CountryResolverTest</c>.
///
/// <para>Every test seeds its own users rather than sharing any: EmailCodeService allows six codes
/// per user and purpose an hour, and the backfill is a one-shot per account.</para>
/// </summary>
public class CountryTest
{
    private const string password = "countrypass123";

    private long rankedSwede;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        await PublicSiteSeed.EnsureSeededAsync();
        rankedSwede = await seedRankedSwedeAsync();
    }

    // ---- detection at account creation, all three paths ----

    [Test]
    public async Task GamePath_PostUsers_StoresTheProxyCountry()
    {
        string name = uniqueName("gjp");
        using var response = await postUsersAsync(name, "JP");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var row = await countryRowAsync(name);
        Assert.Multiple(() =>
        {
            Assert.That(row.Code, Is.EqualTo("JP"));
            Assert.That(row.Chosen, Is.False, "a detected country is not a chosen one");
        });
    }

    [TestCase("T1")]
    [TestCase("XX")]
    [TestCase("ZZ")]
    public async Task GamePath_PostUsers_ANonCountryHeader_StoresNoCountry(string header)
    {
        string name = uniqueName("gnc");
        using var response = await postUsersAsync(name, header);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        Assert.That((await countryRowAsync(name)).Code, Is.EqualTo(Countries.Unknown));
    }

    [Test]
    public async Task WebsiteRegister_StoresTheProxyCountry()
    {
        var (client, _) = WebsiteFixture.CreateBrowser();
        using var __ = client;
        client.DefaultRequestHeaders.Add(CountryResolver.HeaderName, "DE");

        string name = uniqueName("wde");
        string token = await WebsiteFixture.GetAntiforgeryTokenAsync(client, "/register");
        using (var register = await client.PostAsync("/register", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["Username"] = name,
            ["Email"] = name + "@example.com",
            ["Password"] = password,
        })))
        {
            Assert.That(register.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }

        var row = await countryRowAsync(name);
        Assert.Multiple(() =>
        {
            Assert.That(row.Code, Is.EqualTo("DE"));
            Assert.That(row.Chosen, Is.False);
        });
    }

    [Test]
    public async Task GoogleSignUp_StoresTheResolvedCountry_AndNeverANonCountry()
    {
        // The Google username page 404s while Google sign-in is unconfigured (it is in the test
        // host), so the page's one line, CountryResolver.Resolve(HttpContext) into
        // CreateExternalAsync, is covered by the resolver tests and this drives the creation itself.
        await using var ds = NpgsqlDataSource.Create(WebsiteFixture.ConnectionString);
        var db = new Db(ds);

        string fr = uniqueName("gfr");
        string bad = uniqueName("gbd");
        var made = await AccountCreation.CreateExternalAsync(db, fr, identity(fr), "FR");
        var madeBad = await AccountCreation.CreateExternalAsync(db, bad, identity(bad), "T1");

        Assert.That(made.Succeeded && madeBad.Succeeded, Is.True);
        var frRow = await countryRowAsync(fr);
        var badRow = await countryRowAsync(bad);
        Assert.Multiple(() =>
        {
            Assert.That(frRow, Is.EqualTo(("FR", false)));
            Assert.That(badRow.Code, Is.EqualTo(Countries.Unknown), "CreateExternalAsync stores only a storable country");
        });
    }

    // ---- the one-shot backfill for accounts made before detection ----

    [Test]
    public async Task Backfill_ASignedInXXAccount_GetsTheRequestCountry_Once_AndItIsNeverOverwritten()
    {
        long id = await insertUserAsync(uniqueName("bbr"), Countries.Unknown, chosen: false);

        using (var br = await sessionClientAsync(id, "BR"))
        using (var page = await br.GetAsync("/"))
            page.EnsureSuccessStatusCode();

        Assert.That((await countryRowAsync(id)).Code, Is.EqualTo("BR"), "the first signed-in request fills it");

        using (var ca = await sessionClientAsync(id, "CA"))
        using (var page = await ca.GetAsync("/"))
            page.EnsureSuccessStatusCode();

        var row = await countryRowAsync(id);
        Assert.Multiple(() =>
        {
            Assert.That(row.Code, Is.EqualTo("BR"), "a later request from somewhere else never re-detects");
            Assert.That(row.Chosen, Is.False, "the backfill detects; it does not choose");
        });
    }

    [Test]
    public async Task Backfill_TheGameBearerPath_FillsItToo_AndMeAlreadyCarriesIt()
    {
        long id = await insertUserAsync(uniqueName("bar"), Countries.Unknown, chosen: false);

        await using var ds = NpgsqlDataSource.Create(WebsiteFixture.ConnectionString);
        var pair = await new TokenService(new Db(ds)).IssueAsync(id);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v2/me/");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pair.AccessToken);
        request.Headers.Add(CountryResolver.HeaderName, "AR");
        using var response = await WebsiteFixture.Client.SendAsync(request);

        var me = JObject.Parse(await response.Content.ReadAsStringAsync());
        var row = await countryRowAsync(id);
        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That((string?)me["country_code"], Is.EqualTo("AR"), "the same request already answers with the stored country");
            Assert.That(row.Code, Is.EqualTo("AR"));
        });
    }

    [Test]
    public async Task Backfill_NeverTouchesADeliberateNoCountry_OrAnAccountThatHasOne()
    {
        long none = await insertUserAsync(uniqueName("bnn"), Countries.Unknown, chosen: true);
        long us = await insertUserAsync(uniqueName("bus"), "US", chosen: false);

        foreach (long id in new[] { none, us })
        {
            using var client = await sessionClientAsync(id, "NO");
            using var page = await client.GetAsync("/");
            page.EnsureSuccessStatusCode();
        }

        var noneRow = await countryRowAsync(none);
        var usRow = await countryRowAsync(us);
        Assert.Multiple(() =>
        {
            Assert.That(noneRow.Code, Is.EqualTo(Countries.Unknown), "a chosen 'No country' is never re-detected");
            Assert.That(usRow.Code, Is.EqualTo("US"));
        });
    }

    [Test]
    public async Task Backfill_ARequestWithNoCountry_WritesNothing()
    {
        long id = await insertUserAsync(uniqueName("bxx"), Countries.Unknown, chosen: false);

        using (var client = await sessionClientAsync(id, null))
        using (var page = await client.GetAsync("/"))
            page.EnsureSuccessStatusCode();

        using (var tor = await sessionClientAsync(id, "T1"))
        using (var page = await tor.GetAsync("/"))
            page.EnsureSuccessStatusCode();

        var row = await countryRowAsync(id);
        Assert.Multiple(() =>
        {
            Assert.That(row.Code, Is.EqualTo(Countries.Unknown));
            Assert.That(row.Chosen, Is.False, "still eligible: a later request from a real country fills it");
        });
    }

    // ---- the Settings control ----

    [Test]
    public async Task Settings_Country_RoundTripsDetectedChosenAndNone()
    {
        long id = await insertUserAsync(uniqueName("set"), Countries.Unknown, chosen: false);
        using var client = await sessionClientAsync(id, "NO");

        // 1. Detected: the first signed-in request backfills NO, and the control shows it as detected.
        string html = await getSettingsAsync(client);
        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("<option value=\"NO\" selected=\"selected\">Norway</option>"));
            Assert.That(html, Does.Contain("Detected from your connection"));
            Assert.That(html, Does.Contain("<img class=\"flag\" src=\"/flags/NO.png\" alt=\"Norway\""));
        });

        // 2. Chosen: a real country.
        using (var save = await postCountryAsync(client, "US"))
        {
            Assert.That(save.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(await save.Content.ReadAsStringAsync(), Does.Contain("Country saved."), "PRG lands on the notice");
        }

        Assert.That(await countryRowAsync(id), Is.EqualTo(("US", true)));
        html = await getSettingsAsync(client);
        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("<option value=\"US\" selected=\"selected\">United States</option>"));
            Assert.That(html, Does.Contain("You chose this."));
        });

        // 3. None: stored as XX, chosen, rendered with no flag, and NOT re-detected afterwards even
        //    though this client keeps arriving from Norway.
        using (var save = await postCountryAsync(client, Countries.Unknown))
            Assert.That(save.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        html = await getSettingsAsync(client);
        var noneState = await countryRowAsync(id);
        Assert.Multiple(() =>
        {
            Assert.That(noneState, Is.EqualTo((Countries.Unknown, true)));
            Assert.That(html, Does.Contain("<option value=\"XX\" selected=\"selected\">No country</option>"));
            Assert.That(html, Does.Not.Contain("class=\"flag\""));
        });

        // A code that is not on the list is refused and changes nothing.
        using (var bad = await postCountryAsync(client, "ZZ"))
        {
            string body = await bad.Content.ReadAsStringAsync();
            Assert.That(body, Does.Contain("Choose a country from the list."));
        }

        Assert.That(await countryRowAsync(id), Is.EqualTo((Countries.Unknown, true)));
    }

    [Test]
    public async Task Erasure_ClearsTheCountry_AndMarksItChosen()
    {
        string name = uniqueName("del");
        long id = await insertUserAsync(name, "US", chosen: false);
        using var client = await sessionClientAsync(id, null);

        string token = await WebsiteFixture.GetAntiforgeryTokenAsync(client, "/settings");
        using (var delete = await client.PostAsync("/settings?handler=Delete", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["ConfirmUsername"] = name,
        })))
        {
            delete.EnsureSuccessStatusCode();
        }

        Assert.That(await countryRowAsync(id), Is.EqualTo((Countries.Unknown, true)));
    }

    // ---- rendering: the shared _Flag partial ----

    [Test]
    public async Task Profile_ShowsTheFlagAfterTheUsername_AndNothingForNoCountry()
    {
        string withName = uniqueName("pjp");
        long with = await insertUserAsync(withName, "JP", chosen: false);
        long without = await insertUserAsync(uniqueName("pxx"), Countries.Unknown, chosen: false);

        string withHtml = await getAsync($"/users/{with}");
        string withoutHtml = await getAsync($"/users/{without}");

        Assert.Multiple(() =>
        {
            Assert.That(withHtml, Does.Match(
                $"profile-header__name\">{Regex.Escape(withName)}</h1>\\s*<img class=\"flag\" src=\"/flags/JP.png\" alt=\"Japan\" title=\"Japan\""));
            Assert.That(withoutHtml, Does.Not.Contain("class=\"flag\""), "XX renders nothing: no image, no '?' tile");
            Assert.That(withoutHtml, Does.Not.Contain("src=\"/flags/"));
        });
    }

    [TestCase("/rankings?board=performance")]
    [TestCase("/rankings?board=score")]
    [TestCase("/rankings?board=plays")]
    public async Task Rankings_EveryBoard_PutsTheFlagBetweenRankAndName(string url)
    {
        long player = rankedSwede;

        // Its one small ranked play sorts it near the bottom of boards other fixtures also fill, so
        // walk the pages to its row rather than assume a rank.
        string? html = null;
        for (int page = 1; page <= 20 && html is null; page++)
        {
            string candidate = await getAsync($"{url}&page={page}");
            if (candidate.Contains($"href=\"/users/{player}\"", StringComparison.Ordinal))
                html = candidate;
        }

        Assert.That(html, Is.Not.Null, $"player {player} not found on {url}");
        Assert.That(html, Does.Match(
            "<td class=\"num\">#\\d+</td>\\s*<td class=\"player-cell\"><span class=\"player-cell__who\">\\s*<img class=\"flag\" src=\"/flags/SE.png\" alt=\"Sweden\" title=\"Sweden\"[^>]*/>\\s*<a href=\"/users/"
            + player + "\">"));
    }

    /// <summary>One SE player with one ranked, pp-carrying play, so all three boards list them.</summary>
    private static async Task<long> seedRankedSwedeAsync()
    {
        long id = await insertUserAsync(uniqueName("rse"), "SE", chosen: false);

        await using var conn = new NpgsqlConnection(WebsiteFixture.ConnectionString);
        await conn.OpenAsync();

        long setId = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmapsets (owner_id, title, artist, status, submitted_at, updated_at)
            VALUES (@owner, 'Flag Board Tune', 'The Flags', 'ranked', now(), now())
            RETURNING id
            """,
            new { owner = PublicSiteSeed.MapperId });

        long beatmapId = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmaps
                (set_id, version_name, checksum_md5, total_length_s, drain_length_s,
                 difficulty_rating, filename, word_count, char_count, wpm, ratings)
            VALUES (@setId, 'type!beat', @checksum, 90, 80, 2.0, 'map.osu', 100, 500, 75, @ratings::jsonb)
            RETURNING id
            """,
            new { setId, checksum = Guid.NewGuid().ToString("N"), ratings = TestRatings.Json(2.0, null, null) });

        await conn.ExecuteAsync(
            """
            INSERT INTO scores
                (user_id, beatmap_id, total_score, accuracy, completion, max_combo, rank, passed, ranked,
                 mods, statistics, maximum_statistics, pp, pp_version)
            VALUES
                (@id, @beatmapId, 1000, 0.95, 0.97, 50, 'A', true, true,
                 '[]'::jsonb, '{"great":100}'::jsonb, '{"great":103}'::jsonb, 0.5, @ppVersion)
            """,
            new { id, beatmapId, ppVersion = PerformancePoints.VERSION });

        return id;
    }

    [Test]
    public async Task SetLeaderboard_RowsAndPodium_CarryTheFlag()
    {
        string html = await getAsync($"/beatmapsets/{PublicSiteSeed.LeaderboardSetId}");

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Match(
                "<td class=\"num\">#1</td>(?:\\s*<td[^>]*>.*?</td>){4}\\s*<td class=\"player-cell\"><span class=\"player-cell__who\">\\s*<img class=\"flag\" src=\"/flags/US.png\" alt=\"United States\""));
            Assert.That(html, Does.Match("podium__nameline\">\\s*<img class=\"flag\" src=\"/flags/US.png\""));
        });
    }

    [Test]
    public async Task Comments_AndFollowerLists_FlagOnlyTheAccountsThatHaveACountry()
    {
        long host = await insertUserAsync(uniqueName("hst"), "GB", chosen: false);
        long fromKe = await insertUserAsync(uniqueName("cke"), "KE", chosen: false);
        long fromNone = await insertUserAsync(uniqueName("cxx"), Countries.Unknown, chosen: true);

        long setId;
        long keComment, noneComment;
        await using (var conn = new NpgsqlConnection(WebsiteFixture.ConnectionString))
        {
            await conn.OpenAsync();
            setId = await conn.ExecuteScalarAsync<long>(
                "INSERT INTO beatmapsets (owner_id, title, artist, status) VALUES (@host, 'Flag Thread Tune', 'The Flags', 'ranked') RETURNING id",
                new { host });
            keComment = await PublicSiteSeed.SeedCommentAsync(conn, setId, fromKe, "from Kenya");
            noneComment = await PublicSiteSeed.SeedCommentAsync(conn, setId, fromNone, "from nowhere");

            await conn.ExecuteAsync(
                "INSERT INTO user_follows (follower_id, followee_id, kind) VALUES (@fromKe, @host, 'user'), (@fromNone, @host, 'user')",
                new { fromKe, fromNone, host });
        }

        string set = await getAsync($"/beatmapsets/{setId}");
        string followers = await getAsync($"/users/{host}/followers");

        Assert.Multiple(() =>
        {
            Assert.That(commentBlock(set, keComment), Does.Contain("<img class=\"flag\" src=\"/flags/KE.png\" alt=\"Kenya\""));
            Assert.That(commentBlock(set, noneComment), Does.Not.Contain("class=\"flag\""));

            Assert.That(Regex.Matches(followers, "<img class=\"flag\"").Count, Is.EqualTo(1), "one follower has a country, the other has none");
            Assert.That(followers, Does.Contain("<img class=\"flag\" src=\"/flags/KE.png\" alt=\"Kenya\""));
        });
    }

    // ---- helpers ----

    private static string uniqueName(string prefix) => prefix + Guid.NewGuid().ToString("N")[..10];

    private static GoogleIdentity identity(string name) => new("sub-" + name, name + "@gmail.example", null);

    private static string commentBlock(string html, long commentId)
    {
        int start = html.IndexOf($"id=\"comment-{commentId}\"", StringComparison.Ordinal);
        Assert.That(start, Is.GreaterThanOrEqualTo(0), $"comment {commentId} not rendered");

        int end = html.IndexOf("comment-row__body", start, StringComparison.Ordinal);
        return html[start..end];
    }

    private static async Task<long> insertUserAsync(string username, string country, bool chosen)
    {
        await using var conn = new NpgsqlConnection(WebsiteFixture.ConnectionString);
        await conn.OpenAsync();

        long id = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO users (username, email, password_hash, country_code, country_chosen, verified_at)
            VALUES (@username, @email, @hash, @country, @chosen, now())
            RETURNING id
            """,
            new { username, email = username + "@example.com", hash = new PasswordService().Hash(password), country, chosen });

        await conn.ExecuteAsync("INSERT INTO user_stats (user_id) VALUES (@id)", new { id });
        return id;
    }

    /// <summary>
    /// A browser signed in as <paramref name="userId"/> by a minted session token (no email code
    /// spent), whose every request arrives with <paramref name="cfCountry"/> as Cloudflare's
    /// CF-IPCountry, or with none.
    /// </summary>
    private static async Task<HttpClient> sessionClientAsync(long userId, string? cfCountry)
    {
        await using var ds = NpgsqlDataSource.Create(WebsiteFixture.ConnectionString);
        var pair = await new TokenService(new Db(ds)).IssueAsync(userId);

        var (client, cookies) = WebsiteFixture.CreateBrowser();
        cookies.Add(WebsiteFixture.BaseAddress, new Cookie(SessionCookieAuth.CookieName, pair.AccessToken));

        if (cfCountry is not null)
            client.DefaultRequestHeaders.Add(CountryResolver.HeaderName, cfCountry);

        return client;
    }

    private static async Task<HttpResponseMessage> postUsersAsync(string username, string cfCountry)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/users")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["user[username]"] = username,
                ["user[user_email]"] = username + "@example.com",
                ["user[password]"] = password,
            }),
        };
        request.Headers.Add("User-Agent", "type!beat");
        request.Headers.Add("CF-Connecting-IP", WebsiteFixture.NextClientIp());
        request.Headers.Add(CountryResolver.HeaderName, cfCountry);

        return await WebsiteFixture.Client.SendAsync(request);
    }

    private static async Task<string> getSettingsAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/settings");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    private static async Task<HttpResponseMessage> postCountryAsync(HttpClient client, string code)
    {
        string token = await WebsiteFixture.GetAntiforgeryTokenAsync(client, "/settings");
        return await client.PostAsync("/settings?handler=Country", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["Country"] = code,
        }));
    }

    private static async Task<string> getAsync(string url)
    {
        using var response = await WebsiteFixture.Client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    private static async Task<(string Code, bool Chosen)> countryRowAsync(string username)
    {
        await using var conn = new NpgsqlConnection(WebsiteFixture.ConnectionString);
        await conn.OpenAsync();
        return await conn.QuerySingleAsync<(string, bool)>(
            "SELECT country_code::text, country_chosen FROM users WHERE username = @username::citext", new { username });
    }

    private static async Task<(string Code, bool Chosen)> countryRowAsync(long id)
    {
        await using var conn = new NpgsqlConnection(WebsiteFixture.ConnectionString);
        await conn.OpenAsync();
        return await conn.QuerySingleAsync<(string, bool)>(
            "SELECT country_code::text, country_chosen FROM users WHERE id = @id", new { id });
    }
}
