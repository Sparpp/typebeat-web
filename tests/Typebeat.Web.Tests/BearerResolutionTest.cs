using System.Net;
using Microsoft.AspNetCore.Http;
using Typebeat.Web.Auth;
using Typebeat.Web.Caching;

namespace Typebeat.Web.Tests;

/// <summary>
/// What the rate limiter and the output cache make of a request once its bearer has been resolved
/// (<see cref="AuthExtensions.UseBearerResolution"/>): only a bearer that resolved to a user is
/// keyed by its token and refused by the cache; any other Authorization header is the client IP's
/// guest. Driven on a bare <see cref="DefaultHttpContext"/> with the resolution's Items entry set
/// by hand, as the middleware leaves it. The host-driven half (the anonymous read cap and the
/// cached page) is Website/SpikeSurvivalTest.
/// </summary>
public class BearerResolutionTest
{
    private const string client_ip = "203.0.113.7";

    private static readonly AuthedUser player = new(42, "player", "US", IsAdmin: false, Restricted: false);

    [Test]
    public void TwoMadeUpBearers_FromOneIp_ShareOneBucket()
    {
        var first = request("Bearer made-up-one", resolved: null);
        var second = request("Bearer made-up-two", resolved: null);

        Assert.Multiple(() =>
        {
            Assert.That(RateLimitKeys.For(first), Is.EqualTo("ip:" + client_ip));
            Assert.That(RateLimitKeys.For(second), Is.EqualTo(RateLimitKeys.For(first)));
        });
    }

    [Test]
    public void AResolvedBearer_IsKeyedByItsToken()
    {
        string one = RateLimitKeys.For(request("Bearer token-one", player));
        string two = RateLimitKeys.For(request("Bearer token-two", player));

        Assert.Multiple(() =>
        {
            Assert.That(one, Does.StartWith("t:"));
            Assert.That(two, Does.StartWith("t:"));
            Assert.That(one, Is.Not.EqualTo(two), "a token is one game install");
            Assert.That(RateLimitKeys.For(request("Bearer token-one", player)), Is.EqualTo(one));
        });
    }

    [TestCase("x")]
    [TestCase("Bearer made-up")]
    [TestCase("Basic dXNlcjpwYXNz")]
    public void AnAuthorizationHeaderThatResolvesToNobody_IsAGuest(string header)
    {
        var ctx = request(header, resolved: null);

        Assert.Multiple(() =>
        {
            Assert.That(ctx.ResolvedBearer(), Is.Null);
            Assert.That(RateLimitKeys.For(ctx), Is.EqualTo("ip:" + client_ip));
            Assert.That(AnonymousOnlyPolicy.IsAnonymous(ctx), Is.True, "served the guest's cache entry");
        });
    }

    [Test]
    public void AResolvedBearer_IsRefusedByTheCache()
        => Assert.That(AnonymousOnlyPolicy.IsAnonymous(request("Bearer token-one", player)), Is.False);

    [Test]
    public async Task ResolveBearerAsync_ReusesTheMiddlewaresAnswer_WithoutASecondLookup()
    {
        // A bare context has no services: a second token lookup would throw here.
        var signedIn = request("Bearer token-one", player);
        var nobody = request("Bearer made-up", resolved: null);

        var signedInUser = await signedIn.ResolveBearerAsync();
        var nobodyUser = await nobody.ResolveBearerAsync();

        Assert.Multiple(() =>
        {
            Assert.That(signedInUser, Is.SameAs(player));
            Assert.That(nobodyUser, Is.Null);
        });
    }

    private static HttpContext request(string authorization, AuthedUser? resolved)
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Parse(client_ip);
        ctx.Request.Headers.Authorization = authorization;
        ctx.Items[AuthExtensions.ResolvedBearerKey] = resolved;
        return ctx;
    }
}
