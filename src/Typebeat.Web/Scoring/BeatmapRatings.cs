using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Typebeat.Web.Packages.Lyrics;

namespace Typebeat.Web.Scoring;

/// <summary>
/// ONE CELL of a map's rating matrix: what the difficulty model said about this map for one
/// combination of judgement arm, typed stream and clock rate.
///
/// <para>Both halves are needed to price a play, which is why they travel together. The stars are
/// the rating pp raises to <c>sr_exponent</c>; the difficult characters are
/// <c>LyricDifficulty.ModelResult.DifficultCharacters</c>, the map's cells weighted by how close
/// each sits to its peak, which is what the MISS PENALTY is judged against since
/// <see cref="PerformancePoints.VERSION"/> 22. A caller holding one without the other would have to
/// invent the missing half, and the two ways of inventing it (0 difficult characters, or the map's
/// cell count) price the same play very differently.</para>
/// </summary>
public readonly record struct MapRating(double Stars, double DifficultCharacters);

/// <summary>
/// THE RATING MATRIX of one beatmap: <c>beatmaps.ratings</c> (034_ratings_matrix.sql), the
/// eighteen <see cref="MapRating"/> cells a play can be priced from.
///
/// <para>WHY EIGHTEEN. The difficulty model reads three inputs the server cannot recover from one
/// another, so the stored figures are their cross product.</para>
///
/// <list type="bullet">
/// <item><description>THE JUDGEMENT ARM, <c>LyricDifficulty.JudgementArm</c>: None, Easy or Hard
/// Rock. Since the difficulty rework the rating prices the INTERVALS a press may land in, and Easy
/// (doubled windows, sheltered by the whole word) and Hard Rock (normal windows, every cell on its
/// own point target) offer different ones. The arm is therefore a RATING INPUT, not a multiplier
/// over the finished price, and paying for it twice is the mistake the two mods' flat terms in
/// <see cref="PerformancePoints.ModMultiplier"/> are deliberately kept clear of.</description></item>
/// <item><description>THE STREAM: plain or Literate. Literate is a conversion mod, so a Literate
/// play is a play on a different map (every supported mark becomes a typed cell).</description></item>
/// <item><description>THE RATE: 1.00, Double Time's 1.50 and Half Time's 0.75, the only three rates
/// pp is ever eligible to price (a custom speed_change is refused outright).</description></item>
/// </list>
///
/// <para>NULL IS THE UNFILLED STATE, not a rating of zero, exactly as an unfilled <c>sr_dt</c> is.
/// A play whose cell the matrix does not carry is PENDING: it earns nothing for now and its row is
/// left stale for <see cref="Packages.PpBackfill"/> to revisit, rather than being stamped at a price
/// the next sweep would have to disagree with. That is the whole contract the column has, and it is
/// why <see cref="TryGet"/> returns null rather than a zeroed cell.</para>
///
/// <para>THE SIX LEGACY COLUMNS (<c>difficulty_rating</c>, <c>sr_dt</c>, <c>sr_ht</c>,
/// <c>sr_literate</c>, <c>sr_literate_dt</c>, <c>sr_literate_ht</c>) ARE THE ARM-NONE STARS of this
/// matrix and stay written. They are what the pages print, what the client's own lookup reads and
/// what every search filter sorts on; the matrix adds the two arms and the difficult-character
/// counts that only pp needs.</para>
/// </summary>
public sealed class BeatmapRatings
{
    /// <summary>
    /// The stored document's own shape version, so a future matrix (a fourth arm, a fourth rate) can
    /// be told from this one on sight rather than by guessing from its keys. It is NOT a content
    /// version: a rating change bumps <see cref="LyricPace.VERSION"/> and rewrites every row, which
    /// is how a recompute reaches stored maps.
    /// </summary>
    public const int SCHEMA_VERSION = 1;

    /// <summary>The three judgement arms, as the document keys them.</summary>
    public const string ARM_NONE = "none";
    public const string ARM_EASY = "ez";
    public const string ARM_HARD_ROCK = "hr";

    /// <summary>The two typed streams, as the document keys them.</summary>
    public const string STREAM_PLAIN = "plain";
    public const string STREAM_LITERATE = "literate";

    private readonly Dictionary<string, MapRating> cells;

    private BeatmapRatings(Dictionary<string, MapRating> cells)
    {
        this.cells = cells;
    }

    /// <summary>How many cells the matrix actually carries; 18 for a map this server rated itself.</summary>
    public int Count => cells.Count;

    /// <summary>
    /// The three rates pp can price, in the order the document writes them. They are read off
    /// <see cref="RateMods"/> rather than retyped, so a retune of either base rate moves the matrix
    /// and the eligibility check in <see cref="PerformancePoints.StarsFor"/> together.
    /// </summary>
    public static readonly double[] Rates = [1.0, RateMods.DoubleTimeBaseRate, RateMods.HalfTimeBaseRate];

    /// <summary>The three arms, in document order, paired with the model enum each one selects.</summary>
    public static readonly (string Key, LyricDifficulty.JudgementArm Arm)[] Arms =
    [
        (ARM_NONE, LyricDifficulty.JudgementArm.None),
        (ARM_EASY, LyricDifficulty.JudgementArm.Easy),
        (ARM_HARD_ROCK, LyricDifficulty.JudgementArm.HardRock),
    ];

    /// <summary>
    /// A cell's key: <c>arm/stream/rate</c>, with the rate formatted to two invariant decimals so
    /// "1.50" is the same key on every machine and in every locale. Flat rather than nested because
    /// the document is read by exact key and never walked.
    /// </summary>
    public static string Key(LyricDifficulty.JudgementArm arm, bool literate, double rate)
        => Key(ArmKey(arm), literate ? STREAM_LITERATE : STREAM_PLAIN, rate);

    /// <summary>
    /// Whether a cell is storable at all. NaN and the infinities are refused on BOTH
    /// sides of the round trip, at the writer and at the parse, and that is not
    /// defensiveness: PostgreSQL's <c>jsonb</c> REJECTS them outright ("cannot convert
    /// NaN to jsonb"), so a document carrying one cannot be written and an ingest that
    /// tried would fail the whole upload rather than one cell of it.
    ///
    /// <para>So a non-finite reading is DROPPED, which makes its play PENDING, which is
    /// the same answer a missing cell gets and the right one: a rating that is not a
    /// number is not a rating, and the next sweep may well produce one that is. The
    /// alternative, letting it through to <see cref="PerformancePoints.Compute"/>'s own
    /// guard, would settle the row at a price of 0 forever.</para>
    /// </summary>
    private static bool Finite(MapRating cell)
        => double.IsFinite(cell.Stars) && double.IsFinite(cell.DifficultCharacters);

    private static string Key(string arm, string stream, double rate)
        => $"{arm}/{stream}/{rate.ToString("0.00", CultureInfo.InvariantCulture)}";

    /// <summary>The document key of one judgement arm.</summary>
    public static string ArmKey(LyricDifficulty.JudgementArm arm) => arm switch
    {
        LyricDifficulty.JudgementArm.Easy => ARM_EASY,
        LyricDifficulty.JudgementArm.HardRock => ARM_HARD_ROCK,
        _ => ARM_NONE,
    };

    /// <summary>
    /// Rates the map eighteen times, once per (arm, stream, rate). EVERY CELL IS A FULL PASS over
    /// the map's words, so this is the most expensive thing ingest does and the caller is expected
    /// to hold the result rather than ask twice (see <c>BeatmapPackage.Ratings</c>, which caches).
    ///
    /// <para><c>ComputeDetail</c> rather than <c>Compute</c>, because the pair is what a price needs
    /// and the detail reading carries both halves off one pass; asking for the stars and the
    /// difficult characters separately would double the cost for nothing.</para>
    /// </summary>
    public static BeatmapRatings Compute(IReadOnlyList<LyricLine> lines)
    {
        var cells = new Dictionary<string, MapRating>(Arms.Length * 2 * Rates.Length, StringComparer.Ordinal);

        foreach ((string armKey, LyricDifficulty.JudgementArm arm) in Arms)
        {
            foreach (bool literate in (ReadOnlySpan<bool>)[false, true])
            {
                foreach (double rate in Rates)
                {
                    var detail = LyricDifficulty.ComputeDetail(lines, rate, literate, LyricDifficulty.Live, arm);

                    cells[Key(armKey, literate ? STREAM_LITERATE : STREAM_PLAIN, rate)] =
                        new MapRating(detail.Stars, detail.DifficultCharacters);
                }
            }
        }

        return new BeatmapRatings(cells);
    }

    /// <summary>
    /// The cell for one play, or null when the matrix does not carry it. Null is the PENDING signal
    /// (see the type's own docs), so a caller must not coalesce it into a zeroed rating.
    /// </summary>
    public MapRating? TryGet(LyricDifficulty.JudgementArm arm, bool literate, double rate)
        => cells.TryGetValue(Key(arm, literate, rate), out MapRating cell) ? cell : null;

    /// <summary>
    /// A copy with every ARM-NONE star replaced by the one <paramref name="stars"/> gives for that
    /// (stream, rate), keeping each cell's difficult characters and every other arm untouched. A
    /// null from the selector leaves that cell exactly as it was.
    ///
    /// <para>FOR ONE CALLER AND ONE PURPOSE: <c>tools/reprice-report</c>, which prices every stored
    /// score twice, once at the map's recomputed ratings and once at the six STORED star columns, so
    /// that a repricing splits into the part the ratings moved and the part the formula did. Those
    /// columns are stars and nothing else, and the difficult characters a v22 price also needs were
    /// never stored anywhere, so the only count there is to pair them with is the recomputed one.
    /// That is exactly the right pairing for what the report measures (it isolates the STAR move),
    /// and it is the only thing this method should ever be used for: it is not a way to assemble a
    /// matrix from loose numbers, and nothing that writes the column may go through it.</para>
    /// </summary>
    public BeatmapRatings WithArmNoneStars(Func<bool, double, double?> stars)
    {
        var replaced = new Dictionary<string, MapRating>(cells, StringComparer.Ordinal);

        foreach (bool literate in (ReadOnlySpan<bool>)[false, true])
        {
            foreach (double rate in Rates)
            {
                string key = Key(LyricDifficulty.JudgementArm.None, literate, rate);

                if (stars(literate, rate) is double replacement && replaced.TryGetValue(key, out MapRating cell))
                    replaced[key] = cell with { Stars = replacement };
            }
        }

        return new BeatmapRatings(replaced);
    }

    /// <summary>
    /// The document as it is stored: a <c>version</c> and a flat <c>cells</c> object of
    /// <c>arm/stream/rate</c> keys, each holding the stars and the difficult characters as
    /// <c>sr</c> / <c>dc</c>. Round trips exactly through <see cref="Parse"/>, doubles included:
    /// Newtonsoft writes a double round-trippably by default, so a reparsed cell prices a play at
    /// the same bits the ingest computed.
    /// </summary>
    public string ToJson()
    {
        var document = new JObject
        {
            ["version"] = SCHEMA_VERSION,
        };

        var body = new JObject();

        // Written in the enumeration order of the arms and rates rather than the dictionary's, so
        // two ingests of the same map produce byte-identical jsonb and a diff of two rows reads.
        foreach ((string armKey, LyricDifficulty.JudgementArm _) in Arms)
        {
            foreach (string stream in (ReadOnlySpan<string>)[STREAM_PLAIN, STREAM_LITERATE])
            {
                foreach (double rate in Rates)
                {
                    string key = Key(armKey, stream, rate);

                    if (!cells.TryGetValue(key, out MapRating cell) || !Finite(cell))
                        continue;

                    body[key] = new JObject
                    {
                        ["sr"] = cell.Stars,
                        ["dc"] = cell.DifficultCharacters,
                    };
                }
            }
        }

        document["cells"] = body;
        return document.ToString(Formatting.None);
    }

    /// <summary>
    /// A stored document back into a matrix, or null when there is nothing usable there: a null or
    /// blank column, a document that is not an object, or one whose <c>cells</c> object is missing
    /// or empty. Malformed input reads as an UNFILLED matrix rather than throwing, so one bad row
    /// leaves its plays pending instead of taking a submission path down with it.
    ///
    /// <para>A cell missing either number is dropped rather than defaulted: half a cell cannot price
    /// a play, and dropping it makes that play pending, which is the state the whole column's NULL
    /// contract already describes.</para>
    /// </summary>
    public static BeatmapRatings? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        JObject document;

        try
        {
            if (JsonConvert.DeserializeObject<JToken>(json) is not JObject parsed)
                return null;

            document = parsed;
        }
        catch (JsonException)
        {
            return null;
        }

        if (document["cells"] is not JObject body)
            return null;

        var cells = new Dictionary<string, MapRating>(body.Count, StringComparer.Ordinal);

        foreach (var property in body.Properties())
        {
            if (property.Value is not JObject cell)
                continue;

            double? stars = cell["sr"]?.Type is JTokenType.Float or JTokenType.Integer ? cell["sr"]!.Value<double>() : null;
            double? difficult = cell["dc"]?.Type is JTokenType.Float or JTokenType.Integer ? cell["dc"]!.Value<double>() : null;

            if (stars is not double sr || difficult is not double dc)
                continue;

            var rating = new MapRating(sr, dc);

            if (!Finite(rating))
                continue;

            cells[property.Name] = rating;
        }

        return cells.Count == 0 ? null : new BeatmapRatings(cells);
    }
}
