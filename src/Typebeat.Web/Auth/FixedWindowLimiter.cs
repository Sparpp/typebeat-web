using System.Collections.Concurrent;

namespace Typebeat.Web.Auth;

/// <summary>
/// Per-key fixed-window rate limiter — the same in-memory speed-bump pattern as the /oauth/token
/// password-grant limiter (OAuthEndpoints), extracted so website forms (login, register) can
/// declare their own budgets. Process-local and unbounded in distinct keys; Cloudflare WAF is
/// the real production layer.
/// </summary>
public sealed class FixedWindowLimiter(int maxPerWindow, TimeSpan window)
{
    private readonly ConcurrentDictionary<string, Window> windows = new();

    public bool Allow(string key)
    {
        var now = DateTimeOffset.UtcNow;

        var current = windows.AddOrUpdate(
            key,
            _ => new Window(now, 1),
            (_, existing) => now - existing.Start >= window
                ? new Window(now, 1)
                : existing with { Count = existing.Count + 1 });

        return current.Count <= maxPerWindow;
    }

    private sealed record Window(DateTimeOffset Start, int Count);
}
