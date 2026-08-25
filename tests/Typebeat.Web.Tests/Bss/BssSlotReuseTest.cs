using System.Net;
using Dapper;
using Newtonsoft.Json.Linq;

namespace Typebeat.Web.Tests.Bss;

/// <summary>
/// Placeholder-slot reuse on PUT /bss/beatmapsets.
///
/// The incident: the create PUT allocates beatmaps_to_create rows BEFORE the package upload, and
/// rows are never deleted (scores FK). The client re-runs the identical create on every retry, so
/// a set whose upload kept failing accreted a fresh block of ids per attempt (five attempts, five
/// blocks, forty minutes). Allocation now hands back the set's own NEVER-LIVE placeholder rows and
/// inserts only the shortfall, so a retry is idempotent.
///
/// The load-bearing half is what must NOT be reused: a once-live diff has just had its filename
/// nulled by the keep UPDATE in the same transaction, so filename alone cannot tell the two apart.
/// word_count (written only by PackageIngest, never nulled) is the "was ingested" marker, and that
/// is what keeps an id carrying real stats and scores from being handed to a brand-new difficulty.
///
/// Each test owns its user and set, so nothing here couples to the shared-DB fixture state.
/// </summary>
[TestFixture]
[NonParallelizable]
public class BssSlotReuseTest
{
    /// <summary>
    /// The set-55 regression: the same create, repeated, must return the same ids and must not
    /// grow the row count.
    /// </summary>
    [Test]
    public async Task RepeatedCreate_ReusesTheSameNeverLiveRows()
    {
        var (_, bearer) = await BssFixture.CreateUserAsync("slot reuse retry", verified: true);
        var (setId, firstIds) = await CreateSetShellAsync(bearer, diffCount: 2);

        Assert.That(await RowCountAsync(setId), Is.EqualTo(2), "sanity: the initial create allocated two rows");

        // Attempt two: the upload failed, the wizard re-runs the identical create.
        long[] secondIds = await RecreateAsync(bearer, setId, keep: [], create: 2);
        int afterSecond = await RowCountAsync(setId);

        Assert.Multiple(() =>
        {
            Assert.That(secondIds, Is.EqualTo(firstIds), "a retried create must hand back the same never-live ids, in the same order");
            Assert.That(afterSecond, Is.EqualTo(2), "no rows leaked on the retry");
        });

        // Attempt three, because the leak in the incident compounded per attempt.
        long[] thirdIds = await RecreateAsync(bearer, setId, keep: [], create: 2);
        int afterThird = await RowCountAsync(setId);

        Assert.Multiple(() =>
        {
            Assert.That(thirdIds, Is.EqualTo(firstIds));
            Assert.That(afterThird, Is.EqualTo(2), "the leak compounded per attempt; it must not grow at all");
        });
    }

    /// <summary>Fewer placeholders than requested: reuse them all, insert exactly the shortfall.</summary>
    [Test]
    public async Task CreateBeyondTheAvailableSlots_ReusesAllOfThem_AndInsertsOnlyTheShortfall()
    {
        var (_, bearer) = await BssFixture.CreateUserAsync("slot reuse shortfall", verified: true);
        var (setId, existing) = await CreateSetShellAsync(bearer, diffCount: 2);

        // The mapper added a third difficulty between attempts.
        long[] ids = await RecreateAsync(bearer, setId, keep: [], create: 3);
        int rows = await RowCountAsync(setId);

        Assert.Multiple(() =>
        {
            Assert.That(ids, Has.Length.EqualTo(3));
            Assert.That(ids.Take(2), Is.EqualTo(existing), "both placeholders are reused first, in id order");
            Assert.That(ids[2], Is.Not.AnyOf(existing[0], existing[1]));
            Assert.That(ids[2], Is.GreaterThan(existing[1]), "the shortfall comes off the sequence");
            Assert.That(rows, Is.EqualTo(3), "exactly one row was inserted");
        });
    }

    /// <summary>
    /// A ONCE-LIVE diff must never be recycled, even though the keep UPDATE nulls its filename
    /// immediately before allocation runs. Reusing one would graft a new difficulty onto an id
    /// that already carries stats (and potentially scores).
    /// </summary>
    [Test]
    public async Task OnceLiveRows_AreNeverHandedOutAgain()
    {
        var (_, bearer) = await BssFixture.CreateUserAsync("slot reuse once live", verified: true);
        var (setId, liveIds) = await CreateSetShellAsync(bearer, diffCount: 2);

        byte[] zipBytes;
        using (var zip = SyntheticPackage.Zip(BuildPackageEntries("slot reuse once live", setId, liveIds)))
            zipBytes = zip.ToArray();

        using (var upload = await BssSubmissionFlowTest.SendAsync(
                   HttpMethod.Put, $"/bss/beatmapsets/{setId}", bearer, BssSubmissionFlowTest.PackageBody(zipBytes)))
            Assert.That(upload.StatusCode, Is.EqualTo(HttpStatusCode.NoContent), "v1 upload must succeed");

        // Drop BOTH diffs and ask for two new ones: the keep UPDATE nulls both filenames, so only
        // the word_count half of the predicate can save these ids.
        long[] ids = await RecreateAsync(bearer, setId, keep: [], create: 2);

        await using var conn = await BssFixture.OpenDbAsync();

        var dropped = (await conn.QueryAsync<(long Id, string? Filename, int? WordCount)>(
            """
            SELECT id AS Id, filename AS Filename, word_count AS WordCount
            FROM beatmaps WHERE set_id = @setId AND id = ANY(@liveIds) ORDER BY id
            """,
            new { setId, liveIds })).ToList();

        int rowsAfterRecreate = await RowCountAsync(setId);

        Assert.Multiple(() =>
        {
            Assert.That(dropped.Select(d => d.Filename), Is.All.Null, "the keep UPDATE dropped both once-live diffs");
            Assert.That(dropped.Select(d => d.WordCount), Is.All.Not.Null, "ingest stats survive the drop; that is the marker reuse keys on");
            Assert.That(ids.Intersect(liveIds), Is.Empty, "a once-live id must never be reallocated to a new difficulty");
            Assert.That(ids, Has.Length.EqualTo(2));
            Assert.That(rowsAfterRecreate, Is.EqualTo(4), "two fresh rows on top of the two retired ones");
        });

        // ...and the fresh pair IS reusable on the next retry, so the fix still applies here.
        long[] again = await RecreateAsync(bearer, setId, keep: [], create: 2);
        int rowsAfterRetry = await RowCountAsync(setId);

        Assert.Multiple(() =>
        {
            Assert.That(again, Is.EqualTo(ids));
            Assert.That(rowsAfterRetry, Is.EqualTo(4));
        });
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------------

    private static async Task<(long SetId, long[] Ids)> CreateSetShellAsync(string bearer, int diffCount)
    {
        using var response = await BssSubmissionFlowTest.SendAsync(HttpMethod.Put, "/bss/beatmapsets", bearer,
            BssSubmissionFlowTest.JsonBody(new
            {
                beatmapset_id = (long?)null,
                beatmaps_to_create = diffCount,
                beatmaps_to_keep = Array.Empty<long>(),
                target = "WIP",
                notify_on_discussion_replies = false,
            }));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var body = JObject.Parse(await response.Content.ReadAsStringAsync());
        return ((long)body["beatmapset_id"]!, body["beatmap_ids"]!.Select(t => (long)t).ToArray());
    }

    /// <summary>The retry the client issues after a failed upload: same set, same create count.</summary>
    private static async Task<long[]> RecreateAsync(string bearer, long setId, long[] keep, int create)
    {
        using var response = await BssSubmissionFlowTest.SendAsync(HttpMethod.Put, "/bss/beatmapsets", bearer,
            BssSubmissionFlowTest.JsonBody(new
            {
                beatmapset_id = setId,
                beatmaps_to_create = create,
                beatmaps_to_keep = keep,
                target = "Pending",
                notify_on_discussion_replies = false,
            }));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var body = JObject.Parse(await response.Content.ReadAsStringAsync());
        return body["beatmap_ids"]!.Select(t => (long)t).ToArray();
    }

    private static async Task<int> RowCountAsync(long setId)
    {
        await using var conn = await BssFixture.OpenDbAsync();
        return await conn.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM beatmaps WHERE set_id = @setId", new { setId });
    }

    /// <summary>One .osu per allocated id (ids embedded, so checksums are globally unique), audio, bg.</summary>
    private static (string Name, byte[] Content)[] BuildPackageEntries(string creator, long setId, long[] ids)
    {
        var entries = new List<(string Name, byte[] Content)>();

        for (int i = 0; i < ids.Length; i++)
        {
            entries.Add(($"map{i + 1}.osu", SyntheticPackage.Utf8(SyntheticPackage.OsuText(
                creator: creator, version: $"diff {i + 1}", beatmapId: ids[i], beatmapSetId: setId))));
        }

        entries.Add(("audio.mp3", SyntheticPackage.Utf8($"fake audio for set {setId}")));
        entries.Add(("bg.jpg", SyntheticPackage.TinyPng()));

        return entries.ToArray();
    }
}
