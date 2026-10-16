using System.Collections.Concurrent;

namespace Typebeat.Web.Auth;

/// <summary>
/// Per-key fixed-window rate limiter: the in-memory speed bump behind the /oauth/token password
/// grant (OAuthEndpoints) and the website forms and uploads that declare their own budgets.
/// Process-local; Cloudflare WAF is the real production layer. A key whose window has run out is
/// dropped by a sweep that runs at most once per window length, so the map holds only the keys
/// seen within about the last two windows (an expired window and a missing one count the same).
/// </summary>
/// <param name="maxPerWindow">Requests a key may make in one window.</param>
/// <param name="window">How long a window lasts from a key's first request in it.</param>
/// <param name="clock">Defaults to the system clock; tests pass their own.</param>
public sealed class FixedWindowLimiter(int maxPerWindow, TimeSpan window, TimeProvider? clock = null)
{
    private readonly ConcurrentDictionary<string, Window> windows = new();
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private long nextSweepTicks;

    public bool Allow(string key)
    {
        var now = clock.GetUtcNow();
        sweepExpired(now);

        var current = windows.AddOrUpdate(
            key,
            _ => new Window(now, 1),
            (_, existing) => now - existing.Start >= window
                ? new Window(now, 1)
                : existing with { Count = existing.Count + 1 });

        return current.Count <= maxPerWindow;
    }

    /// <summary>How many keys currently hold a window (for tests).</summary>
    public int TrackedKeys => windows.Count;

    private void sweepExpired(DateTimeOffset now)
    {
        long due = Interlocked.Read(ref nextSweepTicks);
        if (now.UtcTicks < due)
            return;

        // One caller per due sweep; the rest carry on.
        if (Interlocked.CompareExchange(ref nextSweepTicks, (now + window).UtcTicks, due) != due)
            return;

        foreach (var entry in windows)
        {
            // Removed only if unchanged, so a concurrent Allow that just renewed it wins.
            if (now - entry.Value.Start >= window)
                windows.TryRemove(entry);
        }
    }

    private sealed record Window(DateTimeOffset Start, int Count);
}
