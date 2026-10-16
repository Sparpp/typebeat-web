using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Typebeat.Web.Auth;
using Typebeat.Web.Data;

namespace Typebeat.Web.Tests;

/// <summary>
/// The refresh contract <see cref="TokenService"/> documents (the client logs the user out on any
/// non-5xx auth failure, so every case here is one a live client can hit): a refresh token rotates
/// on use, stays exchangeable for <see cref="TokenService.ROTATION_GRACE_SECONDS"/> after its first
/// use and no longer, is refused once revoked or expired, and a rotation never invalidates an access
/// token already handed out.
///
/// The service compares against the database's now(), so the clock moves by shifting the token
/// row's own timestamps back. Own database, drop+recreated, matching every other DB-backed fixture
/// in this repo; each test seeds its own user, so revocations never reach another test's tokens.
/// </summary>
[TestFixture]
[NonParallelizable]
public class TokenRefreshContractTest
{
    private static readonly string database_name = TestDatabase.NameFor("typebeat_tokentests");

    private static readonly string connection_string = TestDatabase.ConnectionStringFor(database_name);

    private NpgsqlDataSource dataSource = null!;
    private TokenService tokens = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        await TestDatabase.RecreateAsync(database_name);

        await Db.EnsureExtensionsAsync(connection_string);

        dataSource = NpgsqlDataSource.Create(connection_string);
        var db = new Db(dataSource);
        await db.MigrateAsync(NullLogger.Instance);

        tokens = new TokenService(db);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (dataSource != null)
            await dataSource.DisposeAsync();

        await TestDatabase.DropAsync(database_name);
    }

    [Test]
    public async Task Refresh_MintsAFreshPair_ThatResolvesAndRefreshesInTurn()
    {
        long userId = await insertUserAsync();
        var issued = await tokens.IssueAsync(userId);

        var refreshed = await tokens.RefreshAsync(issued.RefreshToken);

        Assert.That(refreshed, Is.Not.Null);
        var user = await tokens.ResolveAsync(refreshed!.AccessToken);
        var next = await tokens.RefreshAsync(refreshed.RefreshToken);

        Assert.Multiple(() =>
        {
            Assert.That(refreshed.AccessToken, Is.Not.EqualTo(issued.AccessToken));
            Assert.That(refreshed.RefreshToken, Is.Not.EqualTo(issued.RefreshToken), "the refresh token rotates");
            Assert.That(refreshed.ExpiresInSeconds, Is.EqualTo(TokenService.ACCESS_LIFETIME_SECONDS));
            Assert.That(user?.Id, Is.EqualTo(userId));
            Assert.That(next, Is.Not.Null, "the rotated refresh token is itself exchangeable");
        });
    }

    [Test]
    public async Task Rotation_LeavesThePreviousAccessTokenValid()
    {
        long userId = await insertUserAsync();
        var issued = await tokens.IssueAsync(userId);

        Assert.That(await tokens.RefreshAsync(issued.RefreshToken), Is.Not.Null);

        var user = await tokens.ResolveAsync(issued.AccessToken);
        Assert.That(user?.Id, Is.EqualTo(userId), "the old access token lives out its natural expiry");
    }

    [Test]
    public async Task Reuse_InsideTheGraceWindow_MintsAnIndependentPair()
    {
        long userId = await insertUserAsync();
        var issued = await tokens.IssueAsync(userId);

        var first = await tokens.RefreshAsync(issued.RefreshToken);
        await shiftConsumedAtAsync(issued.RefreshToken, seconds: TokenService.ROTATION_GRACE_SECONDS / 2);
        DateTime consumedBefore = await consumedAtAsync(issued.RefreshToken);

        var second = await tokens.RefreshAsync(issued.RefreshToken);
        DateTime consumedAfter = await consumedAtAsync(issued.RefreshToken);

        Assert.That(first, Is.Not.Null);
        Assert.That(second, Is.Not.Null, "a retry racing a successful rotation still succeeds");

        var firstUser = await tokens.ResolveAsync(first!.AccessToken);
        var secondUser = await tokens.ResolveAsync(second!.AccessToken);

        Assert.Multiple(() =>
        {
            Assert.That(second.AccessToken, Is.Not.EqualTo(first.AccessToken));
            Assert.That(second.RefreshToken, Is.Not.EqualTo(first.RefreshToken));
            Assert.That(firstUser?.Id, Is.EqualTo(userId), "the reuse does not invalidate the first pair");
            Assert.That(secondUser?.Id, Is.EqualTo(userId));
            Assert.That(consumedAfter, Is.EqualTo(consumedBefore), "the window runs from the first use; a reuse does not extend it");
        });
    }

    [Test]
    public async Task Reuse_AfterTheGraceWindow_IsRefused()
    {
        long userId = await insertUserAsync();
        var issued = await tokens.IssueAsync(userId);

        var first = await tokens.RefreshAsync(issued.RefreshToken);
        await shiftConsumedAtAsync(issued.RefreshToken, seconds: TokenService.ROTATION_GRACE_SECONDS + 1);

        var late = await tokens.RefreshAsync(issued.RefreshToken);

        Assert.That(first, Is.Not.Null);
        var stillLive = await tokens.RefreshAsync(first!.RefreshToken);

        Assert.Multiple(() =>
        {
            Assert.That(late, Is.Null);
            Assert.That(stillLive, Is.Not.Null, "the pair the first use minted is unaffected");
        });
    }

    [Test]
    public async Task Refresh_OfARevokedToken_IsRefused()
    {
        long userId = await insertUserAsync();
        var loggedOut = await tokens.IssueAsync(userId);
        var otherSession = await tokens.IssueAsync(userId);

        await tokens.RevokeByAccessTokenAsync(loggedOut.AccessToken);

        var revoked = await tokens.RefreshAsync(loggedOut.RefreshToken);
        var other = await tokens.RefreshAsync(otherSession.RefreshToken);

        Assert.Multiple(() =>
        {
            Assert.That(revoked, Is.Null);
            Assert.That(other, Is.Not.Null, "revoking one session leaves the others");
        });
    }

    [Test]
    public async Task Refresh_AfterRevokeAllForUser_IsRefused()
    {
        long userId = await insertUserAsync();
        var issued = await tokens.IssueAsync(userId);

        await tokens.RevokeAllForUserAsync(userId);

        Assert.That(await tokens.RefreshAsync(issued.RefreshToken), Is.Null);
    }

    [Test]
    public async Task Refresh_OfAnExpiredToken_IsRefused()
    {
        long userId = await insertUserAsync();
        var issued = await tokens.IssueAsync(userId);

        await using (var conn = await dataSource.OpenConnectionAsync())
        {
            await conn.ExecuteAsync(
                "UPDATE oauth_tokens SET refresh_expires_at = now() - interval '1 second' WHERE refresh_hash = @hash",
                new { hash = TokenService.Hash(issued.RefreshToken) });
        }

        Assert.That(await tokens.RefreshAsync(issued.RefreshToken), Is.Null);
    }

    [Test]
    public async Task Refresh_OfAnUnknownToken_IsRefused()
    {
        Assert.That(await tokens.RefreshAsync("not-a-token-this-service-issued"), Is.Null);
    }

    private async Task<long> insertUserAsync()
    {
        string username = "tok " + Guid.NewGuid().ToString("N")[..12];

        await using var conn = await dataSource.OpenConnectionAsync();
        return await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO users (username, email, password_hash, country_code)
            VALUES (@username, @email, 'x', 'US')
            RETURNING id
            """,
            new { username, email = username.Replace(' ', '_') + "@example.com" });
    }

    /// <summary>Moves the token row's first use <paramref name="seconds"/> into the past.</summary>
    private async Task shiftConsumedAtAsync(string refreshToken, int seconds)
    {
        await using var conn = await dataSource.OpenConnectionAsync();
        int updated = await conn.ExecuteAsync(
            "UPDATE oauth_tokens SET consumed_at = consumed_at - make_interval(secs => @seconds) WHERE refresh_hash = @hash AND consumed_at IS NOT NULL",
            new { hash = TokenService.Hash(refreshToken), seconds = (double)seconds });

        Assert.That(updated, Is.EqualTo(1), "the token was consumed before the clock moved");
    }

    private async Task<DateTime> consumedAtAsync(string refreshToken)
    {
        await using var conn = await dataSource.OpenConnectionAsync();
        return await conn.ExecuteScalarAsync<DateTime>(
            "SELECT consumed_at FROM oauth_tokens WHERE refresh_hash = @hash",
            new { hash = TokenService.Hash(refreshToken) });
    }
}
