using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Typebeat.Web.Auth;
using Typebeat.Web.Social;

namespace Typebeat.Web.Tests.Website;

/// <summary>
/// <see cref="BeatmapsetReview"/> driven directly through the shared WebsiteFixture host's own
/// singleton: who may pull a lever, the status it sets, and the <c>moderation_actions</c> row it
/// writes (actor, action, "old -> new" note). The HTTP side of the same levers is pinned by
/// SetPageTest and LovedStatusTest.
/// </summary>
[TestFixture]
[NonParallelizable]
public class BeatmapsetReviewTest
{
    private static BeatmapsetReview review => WebsiteFixture.Services.GetRequiredService<BeatmapsetReview>();

    private static long ownerId;
    private static long reviewerId;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        await PublicSiteSeed.EnsureSeededAsync();

        await using var conn = await openAsync();

        ownerId = PublicSiteSeed.MapperId;
        reviewerId = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO users (username, email, password_hash, country_code, map_reviewer)
            VALUES ('review service reviewer', 'review.service@example.com', 'x', 'US', true)
            RETURNING id
            """);
    }

    private static async Task<NpgsqlConnection> openAsync()
    {
        var conn = new NpgsqlConnection(WebsiteFixture.ConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    private static async Task<long> seedSetAsync(string status, string? intended = null, long? owner = null)
    {
        await using var conn = await openAsync();

        long id = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO beatmapsets (owner_id, title, artist, status, submitted_at, updated_at)
            VALUES (@ownerId, 'Review Service Song', 'The Reviewers', @status, now() - interval '6 days', now() - interval '6 days')
            RETURNING id
            """,
            new { ownerId = owner ?? ownerId, status });

        if (intended is not null)
            await conn.ExecuteAsync("UPDATE beatmapsets SET intended_status = @intended WHERE id = @id", new { id, intended });

        return id;
    }

    private static async Task<(string Status, List<(long ActorId, string Action, string Note)> Audit)> stateAsync(long id)
    {
        await using var conn = await openAsync();

        string status = await conn.ExecuteScalarAsync<string>("SELECT status FROM beatmapsets WHERE id = @id", new { id }) ?? "";
        var audit = (await conn.QueryAsync<(long, string, string)>(
            "SELECT actor_id, action, note FROM moderation_actions WHERE set_id = @id ORDER BY id", new { id })).ToList();

        return (status, audit);
    }

    private static AuthedUser reviewer => new(reviewerId, "review service reviewer", "US", IsAdmin: false, Restricted: false, MapReviewer: true);

    [Test]
    public async Task Rank_MovesPendingToRanked_AndRecordsTheReview()
    {
        long id = await seedSetAsync("pending");

        var outcome = await review.ApplyAsync(reviewer, id, BeatmapsetReview.Rank, CancellationToken.None);
        var (status, audit) = await stateAsync(id);

        Assert.Multiple(() =>
        {
            Assert.That(outcome, Is.EqualTo(BeatmapsetReview.Outcome.Applied));
            Assert.That(status, Is.EqualTo("ranked"));
            Assert.That(audit, Is.EqualTo(new[] { (reviewerId, "rank", "pending -> ranked") }));
        });
    }

    [Test]
    public async Task Unlove_ReturnsToTheIntendedStatus_AndTheNoteSaysWhich()
    {
        long id = await seedSetAsync("loved", intended: "unranked");

        var outcome = await review.ApplyAsync(reviewer, id, BeatmapsetReview.Unlove, CancellationToken.None);
        var (status, audit) = await stateAsync(id);

        Assert.Multiple(() =>
        {
            Assert.That(outcome, Is.EqualTo(BeatmapsetReview.Outcome.Applied));
            Assert.That(status, Is.EqualTo("unranked"));
            Assert.That(audit, Is.EqualTo(new[] { (reviewerId, "unlove", "loved -> unranked") }));
        });
    }

    [Test]
    public async Task AWrongStateLever_ChangesNothing_AndWritesNoAudit()
    {
        long id = await seedSetAsync("ranked");

        var outcome = await review.ApplyAsync(reviewer, id, BeatmapsetReview.Rank, CancellationToken.None);
        var (status, audit) = await stateAsync(id);

        Assert.Multiple(() =>
        {
            Assert.That(outcome, Is.EqualTo(BeatmapsetReview.Outcome.Unchanged));
            Assert.That(status, Is.EqualTo("ranked"));
            Assert.That(audit, Is.Empty);
        });
    }

    [Test]
    public async Task NonReviewers_OwnSets_AndMissingSets_AreNotFound()
    {
        long id = await seedSetAsync("pending");
        long ownSet = await seedSetAsync("pending", owner: reviewerId);

        var player = new AuthedUser(ownerId, "mapper", "US", IsAdmin: false, Restricted: false);

        var anonymous = await review.ApplyAsync(null, id, BeatmapsetReview.Rank, CancellationToken.None);
        var notReviewer = await review.ApplyAsync(player, id, BeatmapsetReview.Rank, CancellationToken.None);
        var own = await review.ApplyAsync(reviewer, ownSet, BeatmapsetReview.Rank, CancellationToken.None);
        var missing = await review.ApplyAsync(reviewer, long.MaxValue, BeatmapsetReview.Rank, CancellationToken.None);

        var (status, audit) = await stateAsync(id);
        var (ownStatus, ownAudit) = await stateAsync(ownSet);

        Assert.Multiple(() =>
        {
            Assert.That(anonymous, Is.EqualTo(BeatmapsetReview.Outcome.NotFound));
            Assert.That(notReviewer, Is.EqualTo(BeatmapsetReview.Outcome.NotFound));
            Assert.That(own, Is.EqualTo(BeatmapsetReview.Outcome.NotFound), "a reviewer may not rank their own set");
            Assert.That(missing, Is.EqualTo(BeatmapsetReview.Outcome.NotFound));
            Assert.That((status, audit.Count), Is.EqualTo(("pending", 0)));
            Assert.That((ownStatus, ownAudit.Count), Is.EqualTo(("pending", 0)));
        });
    }
}
