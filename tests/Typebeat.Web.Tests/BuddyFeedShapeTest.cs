using Microsoft.AspNetCore.Http;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Typebeat.Web.Endpoints;
using Typebeat.Web.Wire;

namespace Typebeat.Web.Tests;

/// <summary>
/// The bot feed and the ops readouts answer through <see cref="WireJson"/> since S1-029 (they used
/// System.Text.Json's <c>Results.Json</c> and <c>Results.Problem</c>). Pure, no database: the two
/// named row types must serialize under exactly the names the bot has always read, which were
/// System.Text.Json's web casing, and a failure must be the wire error envelope.
/// </summary>
public class BuddyFeedShapeTest
{
    private static readonly System.Text.Json.JsonSerializerOptions web_defaults = new(System.Text.Json.JsonSerializerDefaults.Web);

    private static BuddyEndpoints.RecentScoreRow scoreRow() => new()
    {
        Id = 41,
        UserId = 7,
        Username = "typist",
        AvatarKey = "avatars/7/3.jpg",
        BeatmapId = 12,
        SetId = 5,
        Artist = "The Janitors",
        Title = "Mop Song",
        VersionName = "type!beat",
        SetStatus = "ranked",
        StarRating = 3.25,
        TotalScore = 987_654,
        Pp = 123.5,
        Rank = "S",
        Accuracy = 0.9725,
        MaxCombo = 310,
        Notes = 320,
        MissCount = 2,
        ModsJson = "[{\"acronym\":\"DT\"}]",
        Ranked = true,
        EndedAt = new DateTimeOffset(2026, 10, 1, 12, 34, 56, 789, TimeSpan.Zero),
        HasReplay = true,
        Position = 3,
        IsPersonalBest = true,
    };

    private static BuddyEndpoints.RankedSetRow setRow() => new()
    {
        EventId = 90,
        SetId = 5,
        Artist = "The Janitors",
        Title = "Mop Song",
        Language = "en",
        Explicit = false,
        CoverKey = "covers/5/2",
        Creator = "mapper",
        RankedBy = "reviewer",
        RankedAt = new DateTimeOffset(2026, 10, 2, 8, 0, 0, TimeSpan.Zero),
        DifficultyCount = 2,
        MinStars = 1.5,
        MaxStars = 3.25,
        LengthSeconds = 181.5,
    };

    private static readonly JsonSerializerSettings read_raw = new() { DateParseHandling = DateParseHandling.None };

    private static JObject parse(string json) => JsonConvert.DeserializeObject<JObject>(json, read_raw)!;

    [Test]
    public void RecentScoreRow_KeepsTheNamesAndValuesTheBotReads()
    {
        var row = scoreRow();

        var wire = parse(JsonConvert.SerializeObject(row, WireJson.Settings));
        var before = parse(System.Text.Json.JsonSerializer.Serialize(row, web_defaults));

        Assert.Multiple(() =>
        {
            Assert.That(wire.Properties().Select(p => p.Name), Is.EqualTo(before.Properties().Select(p => p.Name)));
            Assert.That(JToken.DeepEquals(wire, before), Is.True, $"wire {wire}\nbefore {before}");
        });
    }

    [Test]
    public void RankedSetRow_KeepsTheNamesAndValuesTheBotReads()
    {
        var row = setRow();

        var wire = parse(JsonConvert.SerializeObject(row, WireJson.Settings));
        var before = parse(System.Text.Json.JsonSerializer.Serialize(row, web_defaults));

        Assert.Multiple(() =>
        {
            Assert.That(wire.Properties().Select(p => p.Name), Is.EqualTo(before.Properties().Select(p => p.Name)));
            Assert.That(JToken.DeepEquals(wire, before), Is.True, $"wire {wire}\nbefore {before}");
        });
    }

    [Test]
    public async Task AFailure_IsTheWireErrorEnvelope()
    {
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();

        await WireJson.Error(StatusCodes.Status400BadRequest, "sha256 is not valid hex.").ExecuteAsync(ctx);

        ctx.Response.Body.Position = 0;
        string body = await new StreamReader(ctx.Response.Body).ReadToEndAsync();

        Assert.Multiple(() =>
        {
            Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status400BadRequest));
            Assert.That(ctx.Response.ContentType, Is.EqualTo("application/json; charset=utf-8"));
            Assert.That(body, Is.EqualTo("{\"error\":\"sha256 is not valid hex.\"}"));
        });
    }
}
