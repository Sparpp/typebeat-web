using System.Collections.Concurrent;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Caching.Memory;

namespace Typebeat.Web.Caching;

/// <summary>
/// The one place cached reads are invalidated (backlog 366): the output cache's tagged entries
/// and the two in-process memos the bearer API keeps (the leaderboard's shared slice, 5 s, and the
/// beatmap lookup row, 60 s). Every writer that changes what a cached read would show calls one
/// method here AFTER its transaction commits, so a reader can never re-cache the old state from a
/// still-open transaction.
///
/// <para>Memo keys carry a generation number instead of being deleted one by one: a board memo is
/// keyed per beatmap, limit and host, so "forget every board of beatmap 7" is a counter bump that
/// makes every old key unreachable (each still expires on its own TTL). Eviction is best-effort:
/// the write it follows has already committed, so a failure is logged and the TTL bounds the
/// staleness.</para>
/// </summary>
public sealed class CacheEviction(IOutputCacheStore store, IMemoryCache memo, ILogger<CacheEviction> logger)
{
    /// <summary>How long a leaderboard's shared slice (top rows, participant count, set status) is reused.</summary>
    public static readonly TimeSpan BoardMemoTtl = TimeSpan.FromSeconds(5);

    /// <summary>How long a resolved beatmap lookup row is reused.</summary>
    public static readonly TimeSpan LookupMemoTtl = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<long, long> boardGenerations = new();
    private long globalGeneration;

    public IMemoryCache Memo => memo;

    /// <summary>Memo key of one beatmap's shared leaderboard slice as served on one host.</summary>
    public string BoardKey(long beatmapId, int limit, HttpRequest request)
        => $"board:{beatmapId}:{boardGenerations.GetValueOrDefault(beatmapId)}:{Interlocked.Read(ref globalGeneration)}:{limit}:{request.Scheme}://{request.Host}";

    /// <summary>Memo key of one lookup identity (<paramref name="kind"/> "c" checksum or "i" id).</summary>
    public string LookupKey(string kind, string value)
        => $"lookup:{Interlocked.Read(ref globalGeneration)}:{kind}:{value}";

    /// <summary>A score was stored: its board, its set (play counts), its player's profile.</summary>
    public Task AfterScoreAsync(long? setId, long beatmapId, long userId)
    {
        forgetBoard(beatmapId);

        var tags = new List<string> { CacheTags.User(userId) };
        if (setId is long id)
            tags.Add(CacheTags.Set(id));

        return evictAsync(tags);
    }

    /// <summary>A replay was uploaded: the board row's has_replay flipped.</summary>
    public Task AfterReplayAsync(long? beatmapId)
    {
        if (beatmapId is long id)
            forgetBoard(id);

        return Task.CompletedTask;
    }

    /// <summary>
    /// A set's package or metadata changed (BSS upload, publish): its pages, the listing, the
    /// landing strip, its owner's profile and every lookup row (a lookup is keyed by checksum, which
    /// a new version replaces).
    /// </summary>
    public Task AfterSetChangedAsync(long setId, long? ownerId = null)
    {
        Interlocked.Increment(ref globalGeneration);

        var tags = new List<string> { CacheTags.Set(setId), CacheTags.Listing, CacheTags.Landing };
        if (ownerId is long owner)
            tags.Add(CacheTags.User(owner));

        return evictAsync(tags);
    }

    /// <summary>A set was ranked or unranked: everything a set change evicts, plus the rankings and every board (each one re-reads the status).</summary>
    public Task AfterSetStatusAsync(long setId, long? ownerId = null)
    {
        Interlocked.Increment(ref globalGeneration);

        var tags = new List<string> { CacheTags.Set(setId), CacheTags.Listing, CacheTags.Landing, CacheTags.Rankings };
        if (ownerId is long owner)
            tags.Add(CacheTags.User(owner));

        return evictAsync(tags);
    }

    /// <summary>Flushes everything: every output-cache entry and every memo. Tests call this after seeding through SQL.</summary>
    public Task EvictAllAsync()
    {
        Interlocked.Increment(ref globalGeneration);
        return evictAsync([CacheTags.Site]);
    }

    private void forgetBoard(long beatmapId)
        => boardGenerations.AddOrUpdate(beatmapId, 1, (_, generation) => generation + 1);

    private async Task evictAsync(IEnumerable<string> tags)
    {
        foreach (string tag in tags)
        {
            try
            {
                await store.EvictByTagAsync(tag, CancellationToken.None);
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Output-cache eviction of tag {Tag} failed; the entry ages out on its TTL.", tag);
            }
        }
    }
}
