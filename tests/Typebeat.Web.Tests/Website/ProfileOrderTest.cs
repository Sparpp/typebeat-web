using System.Net;
using Dapper;
using Npgsql;
using Typebeat.Web.Pages.Users;

namespace Typebeat.Web.Tests.Website;

/// <summary>
/// Profile section reordering (task 68): the stored order (026_profile_order.sql) driving the
/// render for EVERY visitor, and the owner-only Reorder POST that writes it.
///
/// The ordering assertions only ever use the five sections that always render (Best scores, Recent
/// scores, Most played, Maps, Favourites), so a user seeded with nothing at all is enough to prove
/// the layout, and no test here has to seed scores or maps to keep another test's counters honest.
///
/// Each test seeds its own throwaway user, since the thing under test is a column on that user.
/// NonParallelizable for the same reason as PinnedScoresTest: the two-step login runs through the
/// single-slot capturing email sender.
/// </summary>
[NonParallelizable]
public class ProfileOrderTest
{
    private const string password = "reorder-me-4242";

    /// <summary>Headings of the sections that render on every profile, in default order.</summary>
    private static readonly string[] always_visible =
        ["Best scores", "Recent scores", "Most played", "Maps", "Favourites"];

    // ---- render ----

    [Test]
    public async Task NothingStored_RendersTheDefaultOrder()
    {
        long userId = await SeedUserAsync();

        string html = await GetProfileAsync(WebsiteFixture.Client, userId);

        AssertHeadingOrder(html, always_visible);
        Assert.That(await StoredOrderAsync(userId), Is.Null, "viewing a profile must not write a default");
    }

    [Test]
    public async Task StoredOrder_RendersForTheOwnerAndForASignedOutVisitor()
    {
        var (client, _) = WebsiteFixture.CreateBrowser();
        using var __ = client;
        long userId = await SeedAndLoginAsync(client);

        await StoreOrderAsync(userId, ProfileSections.Default.Reverse().ToArray());

        string ownHtml = await GetProfileAsync(client, userId);
        string anonHtml = await GetProfileAsync(WebsiteFixture.Client, userId);

        // The owner's layout is served to everyone: whose profile it is decides, not who is looking.
        string[] reversed = ["Favourites", "Maps", "Most played", "Recent scores", "Best scores"];
        AssertHeadingOrder(ownHtml, reversed);
        AssertHeadingOrder(anonHtml, reversed);
    }

    [Test]
    public async Task StoredOrder_IgnoresUnknownIds()
    {
        long userId = await SeedUserAsync();

        // 'kudosu' is an osu section this site has never had; the stored array survives it.
        await StoreOrderAsync(userId, ["kudosu", ProfileSections.Maps, "made-up"]);

        string html = await GetProfileAsync(WebsiteFixture.Client, userId);

        AssertHeadingOrder(html, ["Maps", "Best scores", "Recent scores", "Most played", "Favourites"]);
        Assert.That(html, Does.Not.Contain("kudosu"));
        Assert.That(html, Does.Not.Contain("made-up"));
    }

    [Test]
    public async Task SectionsMissingFromTheStoredArray_RenderAfterIt_InDefaultOrder()
    {
        long userId = await SeedUserAsync();

        // What a user who reordered before Maps/Favourites existed would hold.
        await StoreOrderAsync(userId, [ProfileSections.MostPlayed, ProfileSections.RecentScores]);

        string html = await GetProfileAsync(WebsiteFixture.Client, userId);

        AssertHeadingOrder(html, ["Most played", "Recent scores", "Best scores", "Maps", "Favourites"]);
    }

    [Test]
    public async Task EmptyStoredArray_IsTheDefaultOrder()
    {
        long userId = await SeedUserAsync();
        await StoreOrderAsync(userId, []);

        AssertHeadingOrder(await GetProfileAsync(WebsiteFixture.Client, userId), always_visible);
    }

    [Test]
    public async Task ReorderControls_RenderOnlyForTheOwner()
    {
        var (owner, _) = WebsiteFixture.CreateBrowser();
        using var __ = owner;
        long userId = await SeedAndLoginAsync(owner);

        var (visitor, _) = WebsiteFixture.CreateBrowser();
        using var ___ = visitor;
        await SeedAndLoginAsync(visitor);

        string ownHtml = await GetProfileAsync(owner, userId);
        string visitorHtml = await GetProfileAsync(visitor, userId);
        string anonHtml = await GetProfileAsync(WebsiteFixture.Client, userId);

        Assert.Multiple(() =>
        {
            Assert.That(ownHtml, Does.Contain("data-reorder-handle"));
            Assert.That(ownHtml, Does.Contain("handler=Reorder"), "the owner gets the persist form");
            // The whole point of rendering a real form for a request that is only ever made by
            // fetch: it is where the client lifts a valid antiforgery token from.
            Assert.That(ReorderForm(ownHtml), Does.Contain("__RequestVerificationToken"));
            Assert.That(ownHtml, Does.Contain("js/profile-reorder.js"));
            Assert.That(ownHtml, Does.Contain("aria-label=\"Move Maps up\""), "and keyboard controls");
            Assert.That(ownHtml, Does.Contain("aria-label=\"Move Maps down\""));

            foreach (string html in new[] { visitorHtml, anonHtml })
            {
                Assert.That(html, Does.Not.Contain("data-reorder-handle"));
                Assert.That(html, Does.Not.Contain("data-reorder-move"));
                Assert.That(html, Does.Not.Contain("handler=Reorder"));
                Assert.That(html, Does.Not.Contain("js/profile-reorder.js"));
            }

            // The sections themselves are identical markup either way, handles aside: the layout
            // is a property of the profile, not of who is looking at it.
            Assert.That(visitorHtml, Does.Contain("data-section-id=\"maps\""));
            Assert.That(anonHtml, Does.Contain("data-section-id=\"maps\""));
        });
    }

    [Test]
    public async Task EmptySections_LeaveAHiddenSlotForTheOwnerOnly()
    {
        var (owner, _) = WebsiteFixture.CreateBrowser();
        using var __ = owner;
        long userId = await SeedAndLoginAsync(owner);

        string ownHtml = await GetProfileAsync(owner, userId);
        string anonHtml = await GetProfileAsync(WebsiteFixture.Client, userId);

        Assert.Multiple(() =>
        {
            // A fresh user has no first places, so the section renders nothing. The owner still
            // gets a zero-height slot carrying the id, so the section keeps its place in the array
            // the client submits and does not silently fall to the bottom.
            Assert.That(ownHtml, Does.Contain("data-section-id=\"first-places\""));
            Assert.That(ownHtml, Does.Contain("profile-section-slot"));
            Assert.That(ownHtml, Does.Not.Contain(">First places</h2>"));

            Assert.That(anonHtml, Does.Not.Contain("data-section-id=\"first-places\""),
                "a visitor has nothing to submit, so they get no slots either");
        });
    }

    // ---- write ----

    [Test]
    public async Task Reorder_StoresTheWholeArray_AndTheNextRenderUsesIt()
    {
        var (client, _) = WebsiteFixture.CreateBrowser();
        using var __ = client;
        long userId = await SeedAndLoginAsync(client);

        string[] submitted = ProfileSections.Default.Reverse().ToArray();

        using (var response = await PostOrderAsync(client, userId, submitted))
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        Assert.That(await StoredOrderAsync(userId), Is.EqualTo(submitted));

        AssertHeadingOrder(await GetProfileAsync(WebsiteFixture.Client, userId),
            ["Favourites", "Maps", "Most played", "Recent scores", "Best scores"]);
    }

    [Test]
    public async Task Reorder_BackToTheDefault_ClearsTheStoredArray()
    {
        var (client, _) = WebsiteFixture.CreateBrowser();
        using var __ = client;
        long userId = await SeedAndLoginAsync(client);

        await PostOrderAsync(client, userId, ProfileSections.Default.Reverse().ToArray());
        Assert.That(await StoredOrderAsync(userId), Is.Not.Null);

        // Dragging everything back where it started returns the user to "never reordered", so a
        // section added later still lands in its designed slot rather than at the bottom.
        using (var response = await PostOrderAsync(client, userId, ProfileSections.Default.ToArray()))
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        Assert.That(await StoredOrderAsync(userId), Is.Null);
    }

    [Test]
    public async Task Reorder_FetchRequest_GetsTheStoredOrderBackAsJson()
    {
        var (client, _) = WebsiteFixture.CreateBrowser();
        using var __ = client;
        long userId = await SeedAndLoginAsync(client);

        using var response = await PostOrderAsync(client, userId, [ProfileSections.Maps, ProfileSections.BestScores], fetch: true);
        string body = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/json"));
            Assert.That(body, Does.Contain("\"maps\""));
        });
    }

    [Test]
    public async Task Reorder_RefusesGarbage()
    {
        var (client, _) = WebsiteFixture.CreateBrowser();
        using var __ = client;
        long userId = await SeedAndLoginAsync(client);

        await PostOrderAsync(client, userId, [ProfileSections.Maps, ProfileSections.BestScores]);
        string[]? before = await StoredOrderAsync(userId);

        string[][] rubbish =
        [
            [ProfileSections.Maps, "kudosu"],                                  // one unknown id
            ["../../etc/passwd"],                                              // hand-made
            [],                                                                // nothing at all
            [.. ProfileSections.Default, ProfileSections.Maps],                // longer than the section set
        ];

        foreach (string[] order in rubbish)
        {
            using var response = await PostOrderAsync(client, userId, order);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), $"[{string.Join(",", order)}]");
        }

        Assert.That(await StoredOrderAsync(userId), Is.EqualTo(before), "not one refusal may reach the column");
    }

    [Test]
    public async Task Reorder_OnlyEverWritesTheSessionUsersOwnOrder()
    {
        var (ownerClient, _) = WebsiteFixture.CreateBrowser();
        using var __ = ownerClient;
        long ownerId = await SeedAndLoginAsync(ownerClient);
        await PostOrderAsync(ownerClient, ownerId, [ProfileSections.Maps]);
        string[]? ownerBefore = await StoredOrderAsync(ownerId);

        // A signed-in stranger POSTs at the owner's profile URL. The URL is ignored, exactly like
        // the pin handlers: the write lands on the stranger's own profile.
        var (strangerClient, _) = WebsiteFixture.CreateBrowser();
        using var ___ = strangerClient;
        long strangerId = await SeedAndLoginAsync(strangerClient);

        using (var response = await PostOrderAsync(strangerClient, ownerId, [ProfileSections.Favourites]))
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        string[]? ownerAfter = await StoredOrderAsync(ownerId);
        string[]? strangerAfter = await StoredOrderAsync(strangerId);

        Assert.Multiple(() =>
        {
            Assert.That(ownerAfter, Is.EqualTo(ownerBefore), "the owner's layout is untouched");
            Assert.That(strangerAfter, Is.EqualTo(new[] { ProfileSections.Favourites }));
        });
    }

    [Test]
    public async Task Reorder_RequiresSignIn()
    {
        long userId = await SeedUserAsync();

        var (anon, _) = WebsiteFixture.CreateBrowser();
        using var __ = anon;

        // A valid antiforgery token from /login is still a valid token; the handler refuses on the
        // missing session, not on the token.
        string token = await WebsiteFixture.GetAntiforgeryTokenAsync(anon, "/login");

        var fields = new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", token),
            new("order", ProfileSections.Maps),
        };

        using var response = await anon.PostAsync($"/users/{userId}?handler=Reorder", new FormUrlEncodedContent(fields));
        string[]? stored = await StoredOrderAsync(userId);

        Assert.Multiple(() =>
        {
            Assert.That(response.RequestMessage!.RequestUri!.AbsolutePath, Is.EqualTo("/login"));
            Assert.That(stored, Is.Null);
        });
    }

    [Test]
    public async Task Reorder_WithoutAntiforgeryToken_IsRefused()
    {
        var (client, _) = WebsiteFixture.CreateBrowser();
        using var __ = client;
        long userId = await SeedAndLoginAsync(client);

        using var response = await client.PostAsync($"/users/{userId}?handler=Reorder",
            new FormUrlEncodedContent([new KeyValuePair<string, string>("order", ProfileSections.Maps)]));

        string[]? stored = await StoredOrderAsync(userId);

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(stored, Is.Null);
        });
    }

    // ---- helpers ----

    private static NpgsqlConnection Db() => new NpgsqlConnection(WebsiteFixture.ConnectionString);

    private static async Task<long> SeedUserAsync()
    {
        string username = "orderer_" + Guid.NewGuid().ToString("N")[..10];
        return await WebsiteFixture.SeedUserAsync(username, username + "@example.com", password, verified: true);
    }

    private static async Task<long> SeedAndLoginAsync(HttpClient client)
    {
        string username = "orderer_" + Guid.NewGuid().ToString("N")[..10];
        long id = await WebsiteFixture.SeedUserAsync(username, username + "@example.com", password, verified: true);
        await WebsiteFixture.LoginAndVerifyAsync(client, username, password);
        return id;
    }

    private static async Task<string> GetProfileAsync(HttpClient client, long userId)
    {
        using var response = await client.GetAsync($"/users/{userId}");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>
    /// POSTs the Reorder handler the way the page does: one repeated <c>order</c> field per
    /// section, in the new order, plus a real antiforgery token.
    /// </summary>
    private static async Task<HttpResponseMessage> PostOrderAsync(HttpClient client, long profileId, string[] order, bool fetch = false)
    {
        string token = await WebsiteFixture.GetAntiforgeryTokenAsync(client, "/settings");

        var fields = new List<KeyValuePair<string, string>> { new("__RequestVerificationToken", token) };
        fields.AddRange(order.Select(id => new KeyValuePair<string, string>("order", id)));

        var request = new HttpRequestMessage(HttpMethod.Post, $"/users/{profileId}?handler=Reorder")
        {
            Content = new FormUrlEncodedContent(fields),
        };

        if (fetch)
            request.Headers.Add("X-Requested-With", "fetch");

        return await client.SendAsync(request);
    }

    private static async Task<string[]?> StoredOrderAsync(long userId)
    {
        await using var conn = Db();
        await conn.OpenAsync();
        return await conn.ExecuteScalarAsync<string[]?>(
            "SELECT profile_order FROM users WHERE id = @userId", new { userId });
    }

    private static async Task StoreOrderAsync(long userId, string[] order)
    {
        await using var conn = Db();
        await conn.OpenAsync();
        await conn.ExecuteAsync(
            "UPDATE users SET profile_order = @order WHERE id = @userId", new { order, userId });
    }

    /// <summary>The markup of the owner-only persist form, token and all.</summary>
    private static string ReorderForm(string html)
    {
        int start = html.IndexOf("js-reorder-form", StringComparison.Ordinal);
        Assert.That(start, Is.GreaterThan(-1), "expected the reorder form");

        int end = html.IndexOf("</form>", start, StringComparison.Ordinal);
        Assert.That(end, Is.GreaterThan(start));

        return html[start..end];
    }

    /// <summary>Asserts the given section headings appear in this order in the rendered page.</summary>
    private static void AssertHeadingOrder(string html, string[] headings)
    {
        var positions = headings.Select(h =>
        {
            int at = html.IndexOf($">{h}</h2>", StringComparison.Ordinal);
            Assert.That(at, Is.GreaterThan(-1), $"expected a '{h}' section");
            return at;
        }).ToList();

        for (int i = 1; i < positions.Count; i++)
        {
            Assert.That(positions[i], Is.GreaterThan(positions[i - 1]),
                $"'{headings[i]}' must render after '{headings[i - 1]}'");
        }
    }
}
