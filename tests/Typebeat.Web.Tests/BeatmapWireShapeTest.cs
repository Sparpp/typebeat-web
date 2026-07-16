using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Typebeat.Web.Wire;

namespace Typebeat.Web.Tests;

/// <summary>
/// Locks the on-wire shape of the beatmap-lookup response against the client's APIBeatmap /
/// APIBeatmapSet [JsonProperty] contract: exact property names, the ranked status string, and
/// seconds-valued lengths. Serialized through WireJson.Settings (the same Newtonsoft path the
/// endpoint uses).
/// </summary>
public class BeatmapWireShapeTest
{
    private static JObject Serialize()
    {
        var payload = new APIBeatmapResponse
        {
            Id = 42,
            BeatmapsetId = 7,
            ModeInt = 0,
            Status = "ranked",
            Checksum = "0123456789abcdef0123456789abcdef",
            UserId = 3,
            DifficultyRating = 4.25,
            TotalLength = 123,
            HitLength = 110,
            Version = "type!beat",
            LastUpdated = DateTimeOffset.UnixEpoch,
            Beatmapset = new APIBeatmapSetResponse
            {
                Id = 7,
                Title = "Test Title",
                Artist = "Test Artist",
                Status = "ranked",
                Creator = "mapper",
                UserId = 3,
                Covers = BeatmapCovers.Placeholder("https://host/img/default-cover.jpg"),
                SubmittedDate = DateTimeOffset.UnixEpoch,
                RankedDate = DateTimeOffset.UnixEpoch,
                LastUpdated = DateTimeOffset.UnixEpoch,
            },
        };

        return JObject.Parse(JsonConvert.SerializeObject(payload, WireJson.Settings));
    }

    [Test]
    public void Beatmap_HasExactPropertyNames()
    {
        var json = Serialize();

        Assert.Multiple(() =>
        {
            Assert.That((int)json["id"]!, Is.EqualTo(42));
            Assert.That((int)json["beatmapset_id"]!, Is.EqualTo(7));
            Assert.That((int)json["mode_int"]!, Is.EqualTo(0));
            Assert.That((string)json["checksum"]!, Is.EqualTo("0123456789abcdef0123456789abcdef"));
            Assert.That((int)json["user_id"]!, Is.EqualTo(3));
            Assert.That((double)json["difficulty_rating"]!, Is.EqualTo(4.25));
            // total_length / hit_length are emitted in seconds (client converts to ms on read).
            Assert.That((double)json["total_length"]!, Is.EqualTo(123));
            Assert.That((double)json["hit_length"]!, Is.EqualTo(110));
            Assert.That((string)json["version"]!, Is.EqualTo("type!beat"));
            Assert.That(json["last_updated"], Is.Not.Null);
            Assert.That(json["beatmapset"], Is.Not.Null);
        });
    }

    [Test]
    public void Status_IsRankedStringOnBothLevels()
    {
        var json = Serialize();

        // BeatmapOnlineStatus has no [EnumMember] names, so the client binds enum members by name
        // (case-insensitive): "ranked" -> Ranked (=1), which unblocks status stamping + leaderboards.
        Assert.Multiple(() =>
        {
            Assert.That((string)json["status"]!, Is.EqualTo("ranked"));
            Assert.That((string)json["beatmapset"]!["status"]!, Is.EqualTo("ranked"));
        });
    }

    [Test]
    public void BeatmapSet_HasExactPropertyNames()
    {
        var set = (JObject)Serialize()["beatmapset"]!;

        Assert.Multiple(() =>
        {
            Assert.That((int)set["id"]!, Is.EqualTo(7));
            Assert.That((string)set["title"]!, Is.EqualTo("Test Title"));
            Assert.That((string)set["artist"]!, Is.EqualTo("Test Artist"));
            Assert.That((string)set["creator"]!, Is.EqualTo("mapper"));
            Assert.That((int)set["user_id"]!, Is.EqualTo(3));
            Assert.That(set["covers"], Is.Not.Null);
            Assert.That(set["submitted_date"], Is.Not.Null);
            Assert.That(set.ContainsKey("ranked_date"), Is.True);
            Assert.That(set["last_updated"], Is.Not.Null);
        });
    }

    [Test]
    public void Covers_HaveExactKeysIncludingRetinaVariants()
    {
        var covers = (JObject)Serialize()["beatmapset"]!["covers"]!;

        Assert.Multiple(() =>
        {
            foreach (string key in new[] { "cover", "cover@2x", "card", "card@2x", "list", "list@2x" })
            {
                Assert.That(covers.ContainsKey(key), Is.True, $"covers missing '{key}'");
                Assert.That((string)covers[key]!, Is.EqualTo("https://host/img/default-cover.jpg"));
            }
        });
    }
}
