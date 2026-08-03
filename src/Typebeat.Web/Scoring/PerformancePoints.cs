using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Typebeat.Web.Scoring;

/// <summary>
/// Performance points (pp) for a single play. The canonical spec is <c>docs/pp.md</c>; every
/// constant below is pinned there and must not drift from it.
///
/// <code>
/// pp = 4.0 · SR_eff^2.70
///          · (1 − (miss+mistypes)/(notes+mistypes))^7.5   cleanliness
///          · max(0.1, 1 + 0.70·log10(notes/100))          length, floored
///          · acc^1.30                                     timing quality
///          · (maxcombo/notes)^0.55                        combo
///          · modMult
/// </code>
///
/// <para>
/// MISTYPES (wrong keypresses, the <c>combo_break</c> statistics key, backlog 72) are priced in the
/// CLEANLINESS term only, and only there. Before they were persisted, a wrong key left no trace in
/// a submitted score but a broken combo, so a sloppy high-SR play farmed nearly the pp of a clean
/// one, which is the exact thing the cleanliness term exists to prevent. Adding them to BOTH sides
/// of the fraction keeps it inside [0, 1] no matter how much the player mashes (misses ≤ notes, so
/// the numerator can never outrun the denominator), and mistypes deliberately do NOT enter
/// <c>notes</c>: notes is the map's cell count, and letting keypresses inflate it would hand a
/// masher a bigger LENGTH bonus and a smaller COMBO denominator, paying for the mashing twice over.
/// A play carrying no mistype count at all (every score submitted before the stat existed) collapses
/// the term to its original <c>(1 − miss/notes)^7.5</c> exactly, so no stored row's pp moves.
/// </para>
///
/// <para>
/// The ordering of the factors is the ordering of what the system values: difficulty sets the
/// ceiling of a play, misses decide how much of that ceiling you keep, length rewards sustained
/// hard play. Accuracy and combo are deliberately GENTLE (exponents 1.30 / 0.55, not osu's steep
/// curves): type!beat accuracies live at 55-93%, not 97-100%, so an osu-shaped accuracy term would
/// crush everything and make accuracy the whole ranking, and combo already overlaps with misses.
/// </para>
///
/// <para>
/// SR_eff is the map's star rating AT THE PLAY'S CLOCK RATE, never the base rating with a flat
/// DT/HT bonus bolted on: the rate is priced exclusively through the recomputed star rating, so
/// nothing double-counts. Only the base rates are pp-eligible (DT/NC 1.50x, HT 0.75x), which is why
/// the server only ever needs three ratings and can store them per beatmap
/// (<c>beatmaps.difficulty_rating</c> / <c>sr_dt</c> / <c>sr_ht</c>) instead of doing rate maths at
/// query time. A CUSTOM rate is pp-ineligible ONLY: the play still ranks on the score leaderboards
/// exactly as before, it just earns nothing here (see <see cref="StarsFor"/>).
/// </para>
///
/// <para>
/// ELIGIBILITY is inherited, not re-derived: pp is only ever computed for a score stored
/// <c>ranked = true</c> on a set in status <c>'ranked'</c>. Fails, unranked-mod plays
/// (RX / WU / WD), out-of-bounds submissions and blocked builds are already stored unranked by the
/// submission path, so they earn nothing without a second gate here that could drift from the
/// first. The set-status half is re-checked at READ time (<see cref="PpRanking"/>) so an
/// admin un-rank takes effect immediately without a recompute.
/// </para>
///
/// Pure functions over primitives: no DB, no throwing on hostile input. A degenerate play (zero
/// notes, zero stars, NaN accuracy, a combo above the note count) yields 0, never NaN, Infinity or
/// a negative value.
/// </summary>
public static class PerformancePoints
{
    /// <summary>
    /// Bumped whenever this file's arithmetic changes shape. Stamped on score rows
    /// (<c>scores.pp_version</c>) when their pp is written; rows below it are recomputed at startup
    /// by <see cref="Packages.PpBackfill"/>. v1 = the initial formula (docs/pp.md).
    ///
    /// <para>Rows are ALSO invalidated back to 0 whenever the beatmap they were set on has its star
    /// ratings rewritten (ingest, or the pace/SR sweep in <see cref="Packages.PaceBackfill"/>), so a
    /// pp value can never outlive the SR it was computed from.</para>
    ///
    /// <para>NOT bumped for the mistype term (backlog 72), deliberately. A bump exists to force a
    /// reprice of rows the arithmetic would now value differently, and no stored row qualifies: the
    /// mistype count lives in the <c>combo_break</c> statistics key, which no client ever emitted
    /// before that change, so <see cref="CountNotes"/> reads 0 for every existing row and the new
    /// cleanliness term is algebraically the old one at 0. Bumping would therefore buy a full
    /// startup sweep that provably rewrites every row with the value it already holds. The proof
    /// rests on exactly one fact, worth restating if this is ever revisited: no historic row can
    /// carry a non-zero <c>combo_break</c>. Bump this the moment a change values ANY stored row
    /// differently.</para>
    /// </summary>
    public const int VERSION = 1;

    /// <summary>
    /// Decay of the per-play weighting in the total (see <see cref="PpRanking"/>): the i-th best
    /// deduped play contributes <c>pp_i · DECAY^i</c>.
    ///
    /// <para>0.85 is deliberately far below osu's 0.95 while the ranked map pool is small: the tail
    /// vanishes fast (the 10th play carries ~20%, the 20th ~3.9%), so "your top plays are what
    /// matters" holds without a hard cutoff where the 11th-best contributes exactly nothing. RAISE
    /// IT TOWARDS 0.95 AS THE RANKED POOL GROWS, which is a one-line change here: the total is
    /// computed on read, so nothing is stored and nothing needs recomputing.</para>
    /// </summary>
    public const double DECAY = 0.85;

    // ---- formula constants (docs/pp.md) ----

    private const double scale = 4.0;              // C: global scale, does not affect ranking order
    private const double sr_exponent = 2.70;
    private const double miss_exponent = 7.5;
    private const double length_weight = 0.70;
    private const double length_floor = 0.1;
    private const double accuracy_exponent = 1.30;
    private const double combo_exponent = 0.55;
    private const double reference_notes = 100.0;  // the log bonus' pivot: 100 notes is the 1.0 point

    // ---- mod multipliers (docs/pp.md) ----

    private const double literate_multiplier = 1.06;
    private const double fletcher_multiplier = 0.90;
    private const double no_fail_multiplier = 0.90;
    private const double flashlight_offset = 0.02;
    private const double flashlight_weight = 0.06;
    private const double flashlight_floor = 1.0;

    /// <summary>
    /// Slack when testing a submitted rate against a mod's base rate. Both sides are the same
    /// literal in practice (<see cref="RateMods.Normalize"/> rounds a stored value to 2 decimals,
    /// and 1.50 / 0.75 are exactly representable in binary), so this only guards a value that has
    /// round-tripped through jsonb and back; it is far tighter than the 0.01 slider step, so no
    /// genuinely custom rate can slip through it.
    /// </summary>
    private const double rate_epsilon = 1e-9;

    /// <summary>
    /// The judgement keys that count as a NOTE. <c>ignore_hit</c> is deliberately absent: the
    /// line containers are ignore_hit judgements and counting them would inflate <c>notes</c> and
    /// dilute every single factor (cleanliness, length, combo). Anything else the base ruleset can
    /// emit (ticks, bonuses) does not occur in a typing map and is not a note either.
    /// </summary>
    private static readonly string[] note_keys = ["great", "ok", "meh", "miss"];

    private const string miss_key = "miss";

    /// <summary>
    /// The MISTYPE key: wrong keypresses, persisted by the client under
    /// <c>HitResult.ComboBreak</c> (backlog 72). Not a note, not accuracy-affecting, and absent
    /// entirely from every score submitted before it existed.
    /// </summary>
    private const string mistype_key = "combo_break";

    /// <summary>
    /// Notes, misses and mistypes for a play, as the formula defines them. <see cref="Mistypes"/>
    /// defaults to 0 so a play that carries no mistype count prices exactly as it always did.
    /// </summary>
    public readonly record struct NoteCounts(int Notes, int Misses, int Mistypes = 0);

    /// <summary>
    /// The star rating a play should be priced at, or why there is none.
    ///
    /// <list type="bullet">
    /// <item><see cref="Stars"/> set: price the play at this rating.</item>
    /// <item>null with <see cref="Pending"/> false: the play is pp-INELIGIBLE by its own mods (a
    /// custom DT/HT rate). It earns 0 pp permanently; nothing will ever change that, so the row can
    /// be stamped at the current <see cref="VERSION"/> and never revisited.</item>
    /// <item>null with <see cref="Pending"/> true: the rating this play needs is not stored yet
    /// (a beatmap the SR sweep has not reached). It earns 0 pp FOR NOW; the row must be left
    /// stale so <see cref="Packages.PpBackfill"/> recomputes it once the column is filled.</item>
    /// </list>
    /// </summary>
    public readonly record struct RateStars(double? Stars, bool Pending)
    {
        public static RateStars Of(double stars) => new(stars, false);
        public static readonly RateStars Ineligible = new(null, false);
        public static readonly RateStars Unavailable = new(null, true);
    }

    /// <summary>
    /// Notes, misses and mistypes from a play's <c>statistics</c> dictionary. Negative counts
    /// (tamper-shaped) contribute nothing rather than subtracting, and a missing
    /// <c>combo_break</c> key (every pre-backlog-72 score) reads as 0 mistypes.
    /// </summary>
    public static NoteCounts CountNotes(IReadOnlyDictionary<string, int>? statistics)
    {
        if (statistics is null)
            return default;

        int notes = 0, misses = 0;

        foreach (string key in note_keys)
        {
            if (!statistics.TryGetValue(key, out int count) || count <= 0)
                continue;

            notes += count;

            if (key == miss_key)
                misses += count;
        }

        int mistypes = statistics.TryGetValue(mistype_key, out int mistypeCount) && mistypeCount > 0 ? mistypeCount : 0;

        return new NoteCounts(notes, misses, mistypes);
    }

    /// <summary>
    /// The same counts read straight from a stored <c>scores.statistics</c> jsonb text (the backfill
    /// path). Malformed JSON yields zero counts rather than throwing; one bad row must not abort a
    /// startup sweep.
    /// </summary>
    public static NoteCounts CountNotes(string? statisticsJson)
    {
        if (string.IsNullOrWhiteSpace(statisticsJson))
            return default;

        JObject parsed;

        try
        {
            parsed = JObject.Parse(statisticsJson);
        }
        catch (JsonException)
        {
            return default;
        }

        var counts = new Dictionary<string, int>(note_keys.Length + 1, StringComparer.Ordinal);

        foreach (string key in note_keys.Append(mistype_key))
        {
            if (parsed[key] is { Type: JTokenType.Integer } token)
                counts[key] = token.Value<int>();
        }

        return CountNotes(counts);
    }

    /// <summary>
    /// Which star rating prices this play, given the map's three stored ratings. See
    /// <see cref="RateStars"/> for the three outcomes.
    ///
    /// <para>A stack carrying MORE THAN ONE rate mod is tamper-shaped by construction (the client
    /// makes DT / NC / HT mutually exclusive), so it is treated as ineligible rather than guessed
    /// at, exactly as <see cref="ModMultiplier"/> treats it as the conservative case for scoring.</para>
    /// </summary>
    /// <param name="mods">The play's parsed mods (<see cref="ScoreMods.Parse"/>).</param>
    /// <param name="baseStars"><c>beatmaps.difficulty_rating</c>, the rate-1.0 rating.</param>
    /// <param name="starsDoubleTime"><c>beatmaps.sr_dt</c>, the rating at 1.50x; null until filled.</param>
    /// <param name="starsHalfTime"><c>beatmaps.sr_ht</c>, the rating at 0.75x; null until filled.</param>
    public static RateStars StarsFor(
        IReadOnlyList<ScoreMod>? mods,
        double baseStars,
        double? starsDoubleTime,
        double? starsHalfTime)
    {
        ScoreMod rateMod = default;
        int rateMods = 0;

        if (mods != null)
        {
            foreach (var mod in mods)
            {
                if (!RateMods.IsRateMod(mod.Acronym))
                    continue;

                rateMod = mod;
                rateMods++;
            }
        }

        if (rateMods == 0)
            return RateStars.Of(baseStars);

        if (rateMods > 1 || !RateMods.TryGetRange(rateMod.Acronym, out var range))
            return RateStars.Ineligible;

        // ScoreMods.Parse already snapped and clamped this, and fell back to the mod's default for a
        // historic row that carries no speed_change at all (under the old rules a ranked bare DT
        // could only have been 1.50x), so a legitimately base-rate play always lands exactly here.
        double rate = rateMod.Rate ?? range.Default;

        if (Math.Abs(rate - range.Default) > rate_epsilon)
            return RateStars.Ineligible;

        // The two rate mods' defaults straddle 1.0: DT / NC at 1.50x, HT at 0.75x.
        double? stars = range.Default > 1 ? starsDoubleTime : starsHalfTime;

        return stars is double value ? RateStars.Of(value) : RateStars.Unavailable;
    }

    /// <summary>
    /// The mod multiplier for a play. There is NO rate term here on purpose: DT / HT are priced
    /// entirely through the star rating (<see cref="StarsFor"/>).
    ///
    /// <para>Flashlight's bonus grows with song length, so it pays off on long maps and barely moves
    /// on short ones. Its floor clamp is load-bearing: unclamped, the raw term dips BELOW 1.0 under
    /// ~46 notes, which would turn a bonus mod into a penalty on short maps.</para>
    ///
    /// <para>No Fail is priced at 0.90 (osu's value) rather than left free: it converts a would-be
    /// fail, which earns nothing at all, into a completed play. Its 0.5x SCORE multiplier stays
    /// score-side; mirroring that here would double-punish on top of the miss term.</para>
    /// </summary>
    public static double ModMultiplier(IReadOnlyList<ScoreMod>? mods, int notes)
    {
        if (mods is null || mods.Count == 0)
            return 1;

        double multiplier = 1;

        // A stack cannot really hold the same mod twice (the client keys its mods by type), so a
        // duplicated acronym is tamper-shaped and is applied once.
        var applied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var mod in mods)
        {
            if (string.IsNullOrWhiteSpace(mod.Acronym) || !applied.Add(mod.Acronym))
                continue;

            multiplier *= mod.Acronym.ToUpperInvariant() switch
            {
                "LT" => literate_multiplier,
                "FL" => FlashlightMultiplier(notes),
                "FT" => fletcher_multiplier,
                "NF" => no_fail_multiplier,
                // SD / MU are explicitly 1.0, matching their score multipliers. Anything else
                // (including a mod a newer client ships before this table learns it) is neutral:
                // an unknown mod must never silently inflate or deflate a ranking.
                _ => 1.0,
            };
        }

        return multiplier;
    }

    /// <summary>Flashlight's length-scaled bonus, floored at 1.0 (see <see cref="ModMultiplier"/>).</summary>
    public static double FlashlightMultiplier(int notes)
        => notes <= 0
            ? flashlight_floor
            : Math.Max(flashlight_floor, 1 + flashlight_offset + flashlight_weight * Math.Log10(notes / reference_notes));

    /// <summary>The length bonus, floored (see <see cref="length_floor"/>).</summary>
    /// <remarks>
    /// The raw term crosses zero at about 4 notes and would go negative below that, so the floor is
    /// required: no play may ever compute to zero or negative pp from its LENGTH alone. It bites
    /// under roughly 5 notes, i.e. only on data that describes no real map.
    /// </remarks>
    public static double LengthBonus(int notes)
        => notes <= 0
            ? length_floor
            : Math.Max(length_floor, 1 + length_weight * Math.Log10(notes / reference_notes));

    /// <summary>
    /// pp for one play. <paramref name="starRating"/> is the play's EFFECTIVE rating
    /// (<see cref="StarsFor"/>), <paramref name="accuracy"/> the stored <c>scores.accuracy</c>,
    /// <paramref name="maxCombo"/> the stored <c>scores.max_combo</c>.
    ///
    /// <para>Inputs are clamped rather than trusted: misses and combo into <c>[0, notes]</c> (the
    /// theoretical max combo of a typing map IS its note count), mistypes to non-negative (they have
    /// no upper bound: a player can press as many wrong keys as they like) and accuracy into
    /// <c>[0, 1]</c>. The result is guaranteed finite and non-negative.</para>
    ///
    /// <para><paramref name="mistypes"/> defaults to 0, which is both what a play from before the
    /// stat existed carries and the value at which the cleanliness term is identical to the
    /// original one.</para>
    /// </summary>
    public static double Compute(
        double starRating,
        int notes,
        int misses,
        double accuracy,
        int maxCombo,
        IReadOnlyList<ScoreMod>? mods,
        int mistypes = 0)
    {
        // No notes describes no play; a zero or non-finite rating prices nothing.
        if (notes <= 0 || !double.IsFinite(starRating) || starRating <= 0)
            return 0;

        misses = Math.Clamp(misses, 0, notes);
        maxCombo = Math.Clamp(maxCombo, 0, notes);
        mistypes = Math.Max(mistypes, 0);
        accuracy = double.IsFinite(accuracy) ? Math.Clamp(accuracy, 0, 1) : 0;

        double difficulty = Math.Pow(starRating, sr_exponent);

        // misses ≤ notes after the clamp above, so adding the same mistype count to both sides can
        // only pull the ratio TOWARDS 1 and never past it: the base stays in [0, 1] and the term
        // stays in [0, 1] for any mistype count, however absurd. notes is untouched by design (see
        // the class docs): only this term prices mistypes.
        double cleanliness = Math.Pow(1.0 - (double)(misses + mistypes) / (notes + mistypes), miss_exponent);
        double length = LengthBonus(notes);
        double timing = Math.Pow(accuracy, accuracy_exponent);
        double combo = Math.Pow((double)maxCombo / notes, combo_exponent);

        double pp = scale * difficulty * cleanliness * length * timing * combo * ModMultiplier(mods, notes);

        return double.IsFinite(pp) && pp > 0 ? pp : 0;
    }

    /// <summary>
    /// pp straight from a stored score row: the whole per-play pipeline in one call, shared by the
    /// submission paths and by <see cref="Packages.PpBackfill"/> so the two can never disagree.
    /// Returns 0 and <c>settled = false</c> when the play's star rating is not stored yet, which
    /// tells the caller to leave the row stale for the backfill rather than stamping it.
    /// </summary>
    /// <param name="ranked">The score's stored <c>ranked</c> flag: an unranked play earns nothing.</param>
    public static (double Pp, bool Settled) ForScore(
        bool ranked,
        IReadOnlyList<ScoreMod>? mods,
        NoteCounts notes,
        double accuracy,
        int maxCombo,
        double baseStars,
        double? starsDoubleTime,
        double? starsHalfTime)
    {
        if (!ranked)
            return (0, true);

        var stars = StarsFor(mods, baseStars, starsDoubleTime, starsHalfTime);

        if (stars.Stars is not double effective)
            return (0, !stars.Pending);

        return (Compute(effective, notes.Notes, notes.Misses, accuracy, maxCombo, mods, notes.Mistypes), true);
    }
}
