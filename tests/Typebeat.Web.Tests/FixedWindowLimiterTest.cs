using Typebeat.Web.Auth;

namespace Typebeat.Web.Tests;

/// <summary>
/// The in-handler speed bump behind the /oauth/token password grant, the website forms and the
/// uploads: a budget per key per fixed window, and the sweep that drops expired windows so the map
/// does not grow with every address ever seen. Driven through its injectable clock, no host.
/// </summary>
public class FixedWindowLimiterTest
{
    private static readonly TimeSpan window = TimeSpan.FromMinutes(5);

    [Test]
    public void TheBudgetIsPerKey_AndRenewsWithTheWindow()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var limiter = new FixedWindowLimiter(2, window, clock);

        bool first = limiter.Allow("a");
        bool second = limiter.Allow("a");
        bool third = limiter.Allow("a");
        bool other = limiter.Allow("b");

        clock.Advance(window);
        bool renewed = limiter.Allow("a");

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.True);
            Assert.That(second, Is.True);
            Assert.That(third, Is.False, "over the budget");
            Assert.That(other, Is.True, "another key has its own budget");
            Assert.That(renewed, Is.True, "a new window starts afresh");
        });
    }

    [Test]
    public void ExpiredWindows_AreSwept_OnTheNextCallAfterAWindow()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var limiter = new FixedWindowLimiter(1, window, clock);

        limiter.Allow("a");
        limiter.Allow("b");
        int before = limiter.TrackedKeys;

        clock.Advance(window / 2);
        limiter.Allow("c");
        int halfway = limiter.TrackedKeys;

        clock.Advance(window);
        bool d = limiter.Allow("d");
        int after = limiter.TrackedKeys;

        Assert.Multiple(() =>
        {
            Assert.That(before, Is.EqualTo(2));
            Assert.That(halfway, Is.EqualTo(3), "nothing has expired yet, and no sweep is due");
            Assert.That(after, Is.EqualTo(1), "a, b and c have expired and are gone; only d is left");
            Assert.That(d, Is.True);
        });
    }

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan by) => now += by;
    }
}
