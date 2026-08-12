using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Typebeat.Web.Scoring;

/// <summary>
/// Performance points (pp) for a single play. The canonical spec is <c>docs/pp.md</c>; every
/// constant below is pinned there and must not drift from it.
///
/// <code>
/// pp = 9.6 · SR_eff^2.00
///      · max(0, 1 − miss^1.2/notes)^10                   cleanliness
///      · max(0, 1 − typos^1.2/(notes+typos))^4           typos
///      · max(0.1, 1 + 0.50·log10(notes/100))             length, floored
///      · acc^1.80                                        timing quality
///      · (ln(1 + 9.0·maxcombo/notes)/ln(1 + 9.0))^2.50   combo
///      · modMult
/// </code>
///
/// <para>
/// MISSES and TYPOS are priced by SEPARATE terms (backlog 89), and neither appears in the
/// other's. CLEANLINESS is dropped cells alone, over the plain note count, at the steeper exponent
/// 10. The TYPO term (wrong keypresses, the <c>combo_break</c> statistics key, backlog 72) is
/// its own factor at 4. Between backlog 72 and 89 the two rode inside one fraction, which quietly
/// made each penalty depend on the other: a typo pulled the miss ratio towards its own value, so a
/// player with a heavy typo count was charged LESS per dropped cell than a clean one. Split, a
/// play's misses cost the same whatever its keypresses did, and vice versa.
/// </para>
///
/// <para>
/// A typo is still the cheaper of the two failures (4 against 10): a stumble you recover from is
/// not the same thing as never typing the cell at all. Backlog 95 raised both exponents (8.5 to 10,
/// 3.5 to 6); the typo one has moved twice since, to 8 in v8 and back to 4 in v9.
/// </para>
///
/// <para>
/// BOTH PENALTIES RAISE THE RAW COUNT TO A POWER, NOT THE RATIO (backlog 97), and that power is
/// <see cref="count_power"/>, a tunable rather than part of the shape (backlog 101). Cleanliness is
/// <c>max(0, 1 − miss^1.6/notes)</c> and the typo term <c>max(0, 1 − typos^1.6/(notes + typos))</c>.
/// Backlog 96 squared the RATIO, which runs the opposite way (a value already in [0, 1] gets SMALLER
/// when squared, so <c>1 − r²</c> is LARGER than <c>1 − r</c>) and was a misreading of the intent;
/// backlog 97 corrected it at a power of 2, which was far too extreme, and 101 settled the power at
/// 1.2. v8 then retuned it to the 1.6 in force.
/// </para>
///
/// <para>
/// THE <c>Math.Max</c> CLAMP IS LOAD-BEARING, NOT DEFENSIVE. A powered COUNT over an unpowered
/// denominator is not bounded by [0, 1] at all: the base crosses zero at
/// <c>miss = notes^(1/1.6)</c> (49 misses on a 500-note map) and runs NEGATIVE past it, and a
/// fractional exponent on a negative base is not merely wrong but non-real. Misses really can equal
/// <c>notes</c> and typos have no bound whatever, so this is the ordinary case and not a
/// hostile-input guard. Clamped, the term is a well-defined 0 beyond that point: a CLIFF, chosen
/// knowingly. WHERE it falls is exactly what <see cref="count_power"/> sets, which is why backlog
/// 101 pulled that lever rather than the exponents; see the backlog-101 amendment in
/// <c>docs/pp.md</c>, which states the figures.
/// </para>
///
/// <para>
/// BOTH NUMERATORS GO THROUGH <c>Math.Pow</c>, WHICH CONVERTS TO DOUBLE FIRST (never <c>x * x</c> in
/// <c>int</c>). Typos are unbounded, so an <c>int</c> square overflows catastrophically (at
/// <c>int.MaxValue</c> the true square is about 4.6e18), and a tamper-shaped note count could do the
/// same to the misses. In double, <c>Math.Pow(int.MaxValue, 1.6)</c> is about 8.5e14 and the ratio
/// about 4.0e5, so the base clamps to a well-defined zero rather than wrapping to a NaN or, worse, a
/// bonus. THE COUNTS ARE CLAMPED NON-NEGATIVE BEFORE THEY REACH THE POWER, and that ordering is
/// load-bearing now the power is FRACTIONAL: <c>Math.Pow(-1, 1.6)</c> is NaN, not merely a wrong
/// sign. <c>Math.Pow(0, 1.6)</c> is exactly 0, so both bases are still exactly 1.0 at a count of
/// zero and a spotless play is priced bit-identically across any retune of the power.
/// </para>
///
/// <para>
/// Why the typo term keeps typos on BOTH sides of its fraction while the miss term does not:
/// misses are bounded by <c>notes</c> (a play cannot drop more cells than the map has), but
/// keypresses are UNBOUNDED, so a plain <c>typos^1.6/notes</c> would grow without limit and make
/// the clamp the only thing standing between a masher and a non-real result at any count at all.
/// Keeping the count in the denominator too moves the zero out to the positive root of
/// <c>m^1.6 − m − notes = 0</c> (about 51.71, i.e. 52 typos, on a 500-note map) and keeps the sum
/// itself in <c>double</c>, since <c>notes + typos</c> as <c>int</c> overflows as readily as the
/// power does. Do not "simplify" that denominator away.
/// </para>
///
/// <para>
/// Typos deliberately do NOT enter <c>notes</c>, which stays one entry per CELL
/// (<c>great + ok + meh + good + miss</c>, where <c>good</c> is an uncorrected typo), the
/// map's cell count. Letting keypresses inflate it would hand a masher a bigger LENGTH bonus and a
/// smaller COMBO denominator, paying for the mashing twice over. A play carrying no typo count at
/// all (every score submitted before the stat existed) collapses the typo term to exactly 1.0,
/// so such a play is priced by <c>max(0, 1 − miss^1.6/notes)^10</c> alone.
/// </para>
///
/// <para>
/// The ordering of the factors is the ordering of what the system values: difficulty sets the
/// ceiling of a play, misses decide how much of that ceiling you keep, length rewards sustained
/// hard play. Accuracy stays gentler than osu's (1.80, not an <c>acc^6</c>-shaped curve): type!beat
/// accuracies live at 55-93%, not 97-100%, so an osu-shaped accuracy term would crush everything and
/// make accuracy the whole ranking. Combo is steep by exponent (2.50) but not by TERM, because
/// backlog 131 bends its ratio through a log first (see <see cref="combo_log_shape"/>) and the
/// concave base very nearly cancels the convex exponent: a broken combo costs roughly its face
/// value, which is what keeps it from double-charging the misses it already overlaps with.
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
/// HALF TIME carries ONE extra term on top of that, and it is the only place in this file where a
/// rate is priced by anything but the rating: <see cref="HalfTimeMultiplier"/>, the reciprocal of
/// whatever Double Time is worth on the SAME map. Slowing a map down already lowers SR_eff, but on
/// most maps it lowers it by far less than speeding it up raises it, so HT was the cheap way to
/// keep a hard map's difficulty term while typing at a comfortable pace. Making the down-rate
/// factor exactly 1/(up-rate factor) prices the two symmetrically, per map, rather than by a flat
/// guess. It rides in <see cref="RateStars.Multiplier"/> and is applied by <see cref="Compute"/>'s
/// <c>rateMultiplier</c>, NOT by <see cref="ModMultiplier"/>, which still carries no rate term at
/// all (it sees only the mods and a note count, and could not compute this if it wanted to).
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
    /// by <see cref="Packages.PpBackfill"/>.
    ///
    /// <list type="bullet">
    /// <item>v1 = the initial formula (docs/pp.md), including the backlog-72 mistype term.</item>
    /// <item>v2 = the backlog-89 rebalance: the miss exponent rises 7.5 to 8.5, and mistypes leave
    /// the cleanliness fraction for a term of their own at exponent 3.5.</item>
    /// <item>v3 = the backlog-90 Half Time penalty: a base-rate HT play is multiplied by
    /// <see cref="HalfTimeMultiplier"/> on top of its <c>sr_ht</c> rating, which makes the
    /// down-rate factor the reciprocal of the up-rate one on the same map (or a flat 0.70 cut where
    /// that reciprocal would be a BUFF). Reprices every stored HT row and nothing else.</item>
    /// <item>v4 = the backlog-95 penalty rebalance: the miss exponent rises 8.5 to 10 and the
    /// mistype exponent 3.5 to 6. Both terms are exactly 1.0 at a count of zero whatever the
    /// exponent, so a spotless play is priced bit-identically; every stored row carrying even ONE
    /// miss or ONE mistype is repriced, which is what forces the bump.</item>
    /// <item>v5 = the backlog-96 squaring of both penalty RATIOS: cleanliness becomes (1 -
    /// (miss/notes)^2)^10 and mistyping (1 - (mistypes/(notes+mistypes))^2)^6, with the exponents
    /// 10 and 6 unchanged. Squaring a ratio that already sits in [0, 1] makes it SMALLER, so 1 -
    /// r^2 is LARGER than 1 - r and both terms soften a long way; this runs deliberately opposite
    /// to backlog 89 and 95, and is intended. Both bases are still exactly 1.0 at a count of zero,
    /// so a spotless play is priced bit-identically, while every stored row carrying even ONE miss
    /// or ONE mistype is repriced, upwards this time, which is what forces the bump.</item>
    /// <item>v6 = the backlog-97 squaring of both penalty COUNTS: cleanliness becomes max(0, 1 -
    /// miss^2/notes)^10 and mistyping max(0, 1 - mistypes^2/(notes + mistypes))^6, with the
    /// exponents 10 and 6 unchanged. Backlog 96 squared the RATIO, which softened both terms;
    /// squaring the raw COUNT hardens them instead, which is what was meant. The clamp is required
    /// rather than decorative: the base runs NEGATIVE once the squared count passes the
    /// denominator, and a fractional exponent on a negative base is non-real, so both terms end in
    /// a CLIFF at sqrt(notes) misses and at the positive root of m^2 - m - notes = 0 mistypes. Both
    /// bases are still exactly 1.0 at a count of zero, so a spotless play is priced
    /// bit-identically, while every stored row carrying even ONE miss or ONE mistype is repriced
    /// downwards, most of them to zero, which is what forces the bump.</item>
    /// <item>v7 = the backlog-101 retune of count_power, 2 to 1.2. The SHAPE is exactly the one backlog
    /// 97 introduced, max(0, 1 - count^count_power/denominator), and the exponents 10 and 6 do not
    /// move; only the power the raw count is raised to does, and it is now a declared constant
    /// rather than a squaring written out longhand. Squaring was too extreme: the cleanliness base
    /// hit zero at 23 misses on a 500-note map, 4.6% of it, so essentially every real play priced
    /// to nothing. At 1.2 it hits zero at 178, i.e. 35% of the map, which reads as dropping a third
    /// of it. Both bases are still exactly 1.0 at a count of zero, so a spotless play is priced
    /// bit-identically, while every stored row carrying even ONE miss or ONE mistype is repriced,
    /// upwards this time and mostly away from zero, which is what forces the bump.</item>
    /// <item>v8 = a retune of six constants at once (game commit 51f1dc5, 2026-08-08), with the SHAPE
    /// untouched: the global scale drops 4.0 to 3.0, sr_exponent 2.70 to 2.60, mistype_exponent 6.0
    /// to 8.0, count_power 1.2 to 1.6, length_weight 0.70 to 0.50 and combo_exponent 0.55 to 0.75.
    /// miss_exponent and accuracy_exponent do not move, and neither does any mod multiplier. It is
    /// the ONLY version that shipped without an amendment in docs/pp.md, so the record of it is its
    /// commit message and nothing more: "New pp coefficients: scale 3, SR 2.6, count power 1.6,
    /// mistype exponent 8, length weight 0.5, combo 0.75". Both penalty bases are still exactly 1.0
    /// at a count of zero, but scale, sr_exponent, length_weight and combo_exponent price a spotless
    /// play too, so this reprices every stored row rather than only the ones carrying a miss or a
    /// mistype. count_power is the part that moves both CLIFFS, from 178 misses on a 500-note map to
    /// 49 and from 249 mistypes to 52.</item>
    /// <item>v9 = the backlog-112 retune of three constants, with the SHAPE untouched: the global scale
    /// rises 3.0 to 5.5, sr_exponent 2.60 to 2.70, and mistype_exponent 8.0 to 4.0. count_power
    /// stays 1.6, miss_exponent stays 10, and the length, accuracy and combo terms and every mod
    /// multiplier are exactly as they were. scale and sr_exponent together are close to a pure
    /// rescale (they preserve ranking order among plays on the same map, and steepen it only mildly
    /// across difficulties), and roughly DOUBLE a clean mid-difficulty play. Halving the mistype
    /// exponent is the part that changes ORDER: a mistype-heavy play is repriced far more than
    /// double, because 8 was steep enough to price such plays at essentially nothing. Both penalty
    /// bases are still exactly 1.0 at a count of zero, so a spotless play moves only by the
    /// rescale, while every stored row carrying a mistype is repriced upwards, which is what forces
    /// the bump.</item>
    /// <item>v10 = v10 = accuracy exponent 1.30 to 1.75 and combo exponent 0.75 to 1.50. A spotless play
    /// is priced bit-identically (both bases are exactly 1.0 at a full combo and perfect accuracy,
    /// whatever the exponent), so this repositions everything BELOW an FC rather than rescaling the
    /// pool: a 97% play at 0.90 combo loses about 9%, a 90% play at 0.75 combo about 23%.</item>
    /// <item>v11 = v11 = scale 5.5 to 12.5, sr_exponent 2.70 to 2.00, accuracy_exponent 1.75 to 1.80,
    /// combo_exponent 1.50 to 2.50. v10 never shipped, so the meaningful comparison is against v9:
    /// the SR exponent drop flattens the difficulty curve so easy maps gain and hard maps lose,
    /// while the combo exponent sharpens what a broken combo costs.</item>
    /// <item>v12 = the combo term stops being a plain powered ratio. The base becomes ln(1 + 9.0·r)/ln(1
    /// + 9.0) over r = maxcombo/notes, and only then is raised to the same combo_exponent of 2.50;
    /// nothing else in the formula moves. A FULL COMBO IS EXACTLY 1.0 for any shape constant, so an
    /// FC is priced bit-identically and this repositions only what sits BELOW one, exactly as the
    /// v10 and v11 combo retunes did. The concave log base very nearly cancels the convex ^2.50
    /// over the range real plays live in, so the term reads as roughly LINEAR in the combo ratio
    /// down to about 0.7 (0.90 gives 0.9007, 0.80 gives 0.7983, 0.75 gives 0.7458): a broken combo
    /// now costs roughly its face value, where at ^2.50 alone losing 10% of a combo cost 23% of the
    /// term. Every stored row below a full combo is repriced upwards, which is what forces the
    /// bump.</item>
    /// <item>v13 = v13 = count_power 1.6 back to 1.2, restoring the value backlog 101 chose with a
    /// written argument and that v8 silently replaced. The SHAPE is untouched and so is every other
    /// constant. At 1.6 the cleanliness base hit zero at 49 misses on a 500-note map, 9.7% of it,
    /// within a factor of two of the 4.6% that backlog 101 rejected as pricing essentially every
    /// real play to nothing; at 1.2 it is 178, i.e. 35%. It also restores the typo term's
    /// separate cliff, which exists because its count sits in its own denominator: the two cliffs
    /// were 52 and 49 at 1.6, three counts apart, and are 249 and 178 at 1.2. Both bases are still
    /// exactly 1.0 at a count of zero, so a spotless play is priced bit-identically, while every
    /// stored row carrying a miss or a mistype is repriced upwards, many of them away from exactly
    /// zero, which is what forces the bump.</item>
    /// <item>v14 = v14 = the global scale drops 12.5 to 9.6, exported from the pp sandbox as the only
    /// change. scale is the one constant that provably cannot move ranking order, within a map or
    /// across maps, since it multiplies every play equally; it rescales absolute pp by 0.768 and
    /// nothing else. Every stored row is repriced, which is what forces the bump, but no
    /// leaderboard reorders. Applied on top of v13 rather than the v12 the sandbox export names as
    /// its baseline, because backlog 137 landed count_power 1.6 to 1.2 first.</item>
    /// </list>
    ///
    /// <para>Rows are ALSO invalidated back to 0 whenever the beatmap they were set on has its star
    /// ratings rewritten (ingest, or the pace/SR sweep in <see cref="Packages.PaceBackfill"/>), so a
    /// pp value can never outlive the SR it was computed from.</para>
    ///
    /// <para>The typo term (backlog 72) deliberately did NOT bump, and the reason is worth
    /// keeping because it is exactly the reason v2 HAD to: a bump exists to force a reprice of rows
    /// the arithmetic would now value differently, and back then no stored row qualified. The
    /// typo count lives in the <c>combo_break</c> statistics key, which no client had ever
    /// emitted, so <see cref="CountNotes"/> read 0 for every existing row and the amended term was
    /// algebraically the old one at 0. That argument does not survive backlog 89: raising the miss
    /// exponent reprices every stored row carrying even ONE miss, whatever its typo count, so
    /// there is no set of rows the change provably leaves alone. Bump this the moment a change
    /// values ANY stored row differently.</para>
    /// </summary>
    public const int VERSION = 14;

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

    private const double scale = 9.6;              // C: global scale, does not affect ranking order
    private const double sr_exponent = 2.00;
    private const double miss_exponent = 10.0;
    private const double typo_exponent = 4.0;

    /// <summary>
    /// The power the RAW COUNT is raised to inside both penalty bases, before its denominator
    /// divides it. A tunable, not a hard-wired square: backlog 97 introduced this shape with the
    /// power written out longhand as <c>(double)x * x</c>, which read as part of the shape and could
    /// only be retuned by hand in both mirrors; backlog 101 lifted it out here.
    ///
    /// <para>It is the constant that decides WHERE EACH TERM'S CLIFF FALLS, since the cleanliness
    /// base vanishes at <c>miss = notes^(1/count_power)</c>. At 2 that is 23 misses on a 500-note
    /// map, i.e. 4.6% of it, which zeroed essentially every real play; backlog 101 moved it to 1.2,
    /// where it is 178, i.e. 35%; at the 1.6 v8 set it is 49, i.e. 9.7%. It also decides how the
    /// cliff scales WITH map size: as a fraction of the map it is <c>notes^(1/count_power - 1)</c>,
    /// so at 2 it swung from 10% of a 100-note map to 2.2% of a 2000-note map (long maps drastically
    /// harsher, for no reason anyone chose), at 1.2 it moved 46% to 35% to 28% across 100, 500 and
    /// 2000 notes, and at 1.6 it moves 17.8% to 9.7% to 5.8% across the same three.</para>
    /// </summary>
    private const double count_power = 1.2;

    private const double length_weight = 0.50;
    private const double length_floor = 0.1;
    private const double accuracy_exponent = 1.80;
    private const double combo_exponent = 2.50;

    /// <summary>
    /// The CURVATURE of the combo base (backlog 131). The term is
    /// <c>(ln(1 + combo_log_shape·r)/ln(1 + combo_log_shape))^combo_exponent</c> over
    /// <c>r = maxcombo/notes</c>: the ratio is bent through a log BEFORE the exponent reaches it,
    /// where every generation through v11 raised the plain ratio.
    ///
    /// <para>A FULL COMBO IS EXACTLY 1.0 AT EVERY VALUE OF THIS CONSTANT, since
    /// <c>ln(1 + k)/ln(1 + k)</c> is 1 and 1 raised to anything is 1. So an FC is priced
    /// bit-identically across any retune of it and the constant repositions only what sits BELOW a
    /// full combo, exactly as the v10 and v11 combo retunes did.</para>
    ///
    /// <para>THE BEND RUNS OPPOSITE TO THE EXPONENT, which is why it is worth having: the log base
    /// is CONCAVE (it lifts every ratio under 1) where <c>^2.50</c> is convex. At 9 the two very
    /// nearly cancel over the range real plays live in, so the term reads as roughly LINEAR in the
    /// combo ratio down to about 0.7 (0.90 gives 0.9007, 0.80 gives 0.7983, 0.75 gives 0.7458). A
    /// broken combo therefore costs roughly its FACE VALUE, where under <c>^2.50</c> alone losing
    /// 10% of a combo cost 23% of the term.</para>
    /// </summary>
    private const double combo_log_shape = 9.0;

    private const double reference_notes = 100.0;  // the log bonus' pivot: 100 notes is the 1.0 point

    /// <summary>
    /// The flat cut a Half Time play takes when the mirror multiplier would be a BUFF, i.e. a 30%
    /// reduction. See <see cref="HalfTimeMultiplier"/> for when that happens and why the guard is
    /// not a <c>Math.Min</c>.
    /// </summary>
    private const double half_time_buff_clamp = 0.70;

    // ---- mod multipliers (docs/pp.md) ----

    private const double literate_multiplier = 1.06;

    /// <summary>
    /// Rhythmic (backlog 135): the play is judged on the millisecond ladder, so each character has
    /// to be pressed at its own target time instead of near the character the playhead is on. The
    /// two ladders coincide at a pace of 10 characters per second and the millisecond one is the
    /// tighter pair everywhere below that, which is where lyrics sit, so the mod is a real
    /// difficulty increase on essentially every map and is paid like one.
    /// </summary>
    private const double rhythmic_multiplier = 1.10;

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
    /// The judgement keys that count as a NOTE, one per CELL of the map. <c>ignore_hit</c> is
    /// deliberately absent: the line containers are ignore_hit judgements and counting them would
    /// inflate <c>notes</c> and dilute every single factor (cleanliness, length, combo). Anything
    /// else the base ruleset can emit (ticks, bonuses) does not occur in a typing map and is not a
    /// note either.
    ///
    /// <para><c>good</c> is the UNCORRECTED TYPO key (backlog 124/126, the client's
    /// <c>TypeBeatResultMapping.UNFIXED_TYPO</c>), and it belongs here for the same reason the
    /// others do: it is one cell of the map the player reached and finished, so leaving it out
    /// would shorten the map pp thinks was played and inflate both the length term and the combo
    /// ratio. It is deliberately NOT <see cref="miss_key"/>: a miss says the player was too slow to
    /// finish the character at all, a typo says they finished it wrongly, and the typo term
    /// already prices the second. That split is the whole reason the typo has its own key, even
    /// though <c>ScoringContract</c> makes it cost completion exactly as a miss does.</para>
    ///
    /// <para><c>perfect</c> is backlog 133's fourth quality tier and joins the list for the same
    /// reason. No score stored before that key existed carries it, so adding it reprices nothing
    /// and needs no <see cref="VERSION"/> bump; leaving it out would instead have made every play
    /// submitted AFTER it read as a map with almost no notes at all.</para>
    /// </summary>
    private static readonly string[] note_keys = ["perfect", "great", "ok", "meh", "good", "miss"];

    private const string miss_key = "miss";

    /// <summary>
    /// The MISTYPE key: wrong keypresses, persisted by the client under
    /// <c>HitResult.ComboBreak</c> (backlog 72). Not a note, not accuracy-affecting, and absent
    /// entirely from every score submitted before it existed. Priced by its own term since
    /// backlog 89.
    /// </summary>
    private const string mistype_key = "combo_break";

    /// <summary>
    /// Notes, misses and typos for a play, as the formula defines them. <see cref="Typos"/>
    /// defaults to 0 so a play that carries no typo count prices exactly as it always did.
    /// </summary>
    public readonly record struct NoteCounts(int Notes, int Misses, int Typos = 0);

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
    ///
    /// <para><see cref="Multiplier"/> is the play's RATE multiplier, 1.0 for everything except a
    /// base-rate Half Time play (see <see cref="HalfTimeMultiplier"/>). It rides here rather than in
    /// <see cref="ModMultiplier"/> because it is a function of all three star ratings, which the mod
    /// multiplier neither has nor should have. It is meaningless when <see cref="Stars"/> is null,
    /// since no price is computed at all in that case.</para>
    /// </summary>
    public readonly record struct RateStars(double? Stars, bool Pending, double Multiplier = 1)
    {
        public static RateStars Of(double stars) => new(stars, false);
        public static RateStars Of(double stars, double multiplier) => new(stars, false, multiplier);
        public static readonly RateStars Ineligible = new(null, false);
        public static readonly RateStars Unavailable = new(null, true);
    }

    /// <summary>
    /// The extra multiplier a base-rate HALF TIME play is priced by, on top of its <c>sr_ht</c>
    /// rating. 1.0 is NOT a possible answer here; every other rate's multiplier is 1.0 and never
    /// reaches this function.
    ///
    /// <para>Write <c>D = (sr_dt/sr_base)^2.00</c> and <c>H = (sr_ht/sr_base)^2.00</c>, the exponent
    /// being <c>sr_exponent</c> in both cases. Those are what the two base rates are ALREADY worth on
    /// this map, purely through <c>SR^2.00</c>, with no term of their own anywhere: D is Double
    /// Time's emergent bonus and H is Half Time's emergent discount, and both move with any retune of
    /// the exponent. The mirror multiplier is <c>1/(D·H)</c>, which makes Half Time's TOTAL rate factor
    /// <c>H · 1/(D·H) = 1/D</c>, exactly the reciprocal of Double Time's, per map. Speeding a map up
    /// and slowing it down are then equal and opposite by construction rather than by a flat guess,
    /// which is the whole point: HT used to be the cheap way to keep a hard map's difficulty term
    /// while typing at a comfortable pace, because slowing down costs far less than speeding up
    /// pays.</para>
    ///
    /// <para>THE GUARD IS LOAD-BEARING, NOT DEFENSIVE. The mirror is a BUFF exactly when
    /// <c>1/D &gt; H</c>, i.e. <c>D·H &lt; 1</c>, i.e. <c>sr_dt · sr_ht &lt; sr_base²</c>: a map
    /// whose SR curve is concave in log-rate, so slowing it down helps far more than speeding it up
    /// hurts. That is precisely the map an unguarded mirror would REWARD for using Half Time. Worked
    /// example: base 4.2, dt 4.5, ht 2.0 gives D = 1.148 and H = 0.227, so the mirror would make
    /// HT's total factor 0.871 against today's 0.227, a nearly four-fold buff. Clamped, it is
    /// <c>0.70 · 0.227 = 0.159</c>, still a nerf.</para>
    ///
    /// <para>IT IS NOT A <c>Math.Min</c>. A mirror multiplier of, say, 0.90 is a mild nerf and must
    /// be used AS IS. <c>Math.Min(mirror, 0.70)</c> would deepen every mild nerf into a flat 30% cut
    /// and quietly throw away the per-map symmetry this term exists for. The clamp applies only on
    /// the wrong side of 1.0.</para>
    ///
    /// <para>Hostile input yields 0, in keeping with the rest of this file: a non-finite or
    /// non-positive rating describes no map, and returning 0 makes the play price to 0 rather than
    /// to NaN. <see cref="Compute"/> would already return 0 for a non-positive
    /// <c>starRating</c>, but this is reached down a different path (<see cref="StarsFor"/>) and
    /// a NaN here would survive that guard and poison the product.</para>
    /// </summary>
    /// <param name="baseStars"><c>beatmaps.difficulty_rating</c>, the rate-1.0 rating.</param>
    /// <param name="starsDoubleTime"><c>beatmaps.sr_dt</c>, the rating at 1.50x.</param>
    /// <param name="starsHalfTime"><c>beatmaps.sr_ht</c>, the rating at 0.75x.</param>
    public static double HalfTimeMultiplier(double baseStars, double starsDoubleTime, double starsHalfTime)
    {
        if (!IsRateableRating(baseStars) || !IsRateableRating(starsDoubleTime) || !IsRateableRating(starsHalfTime))
            return 0;

        double doubleTimeFactor = Math.Pow(starsDoubleTime / baseStars, sr_exponent);
        double halfTimeFactor = Math.Pow(starsHalfTime / baseStars, sr_exponent);

        double mirror = 1.0 / (doubleTimeFactor * halfTimeFactor);

        if (!double.IsFinite(mirror) || mirror <= 0)
            return 0;

        // Strictly above 1.0 the mirror would PAY for playing slower; that, and only that, takes
        // the flat cut. Anything at or below 1.0 is already a nerf and is used exactly as computed.
        return mirror > 1 ? half_time_buff_clamp : mirror;
    }

    /// <summary>A star rating that can be divided by or raised to a power without producing nonsense.</summary>
    private static bool IsRateableRating(double stars) => double.IsFinite(stars) && stars > 0;

    /// <summary>
    /// Notes, misses and typos from a play's <c>statistics</c> dictionary. Negative counts
    /// (tamper-shaped) contribute nothing rather than subtracting, and a missing
    /// <c>combo_break</c> key (every pre-backlog-72 score) reads as 0 typos.
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

        int typos = statistics.TryGetValue(mistype_key, out int typoCount) && typoCount > 0 ? typoCount : 0;

        return new NoteCounts(notes, misses, typos);
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
    ///
    /// <para>A HALF TIME play needs BOTH down-rate ratings: <c>sr_ht</c> to price it and
    /// <c>sr_dt</c> to mirror against (<see cref="HalfTimeMultiplier"/>). A map with <c>sr_ht</c>
    /// stored but <c>sr_dt</c> still null is therefore <see cref="RateStars.Unavailable"/>, not
    /// priced off <c>sr_ht</c> alone: leaving the row stale for <see cref="Packages.PpBackfill"/> to
    /// retry costs one boot, whereas pricing it now would stamp a value the very next sweep has to
    /// disagree with.</para>
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
        if (range.Default > 1)
            return starsDoubleTime is double up ? RateStars.Of(up) : RateStars.Unavailable;

        // Half Time is priced off sr_ht AND mirrored against sr_dt, so it needs both (see the docs
        // above): either one missing leaves the row for the next backfill pass.
        if (starsHalfTime is not double down || starsDoubleTime is not double mirrorAgainst)
            return RateStars.Unavailable;

        return RateStars.Of(down, HalfTimeMultiplier(baseStars, mirrorAgainst, down));
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
                "RH" => rhythmic_multiplier,
                "FT" => fletcher_multiplier,
                "NF" => no_fail_multiplier,
                // SD / GK / MU are explicitly 1.0, matching their score multipliers. Anything else
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
    /// No play may ever compute to zero or negative pp from its LENGTH alone, which is what the floor
    /// is for. At the current weight of 0.50 the raw term crosses zero at exactly 1 note and the
    /// floor at about 1.585, so the clamp is close to vestigial and bites only on data that describes
    /// no real map; at the old weight of 0.70 those two crossings sat at about 3.73 and 5.18 notes.
    /// It stays because it is the guard, not because it currently fires.
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
    /// theoretical max combo of a typing map IS its note count), typos to non-negative (they have
    /// no upper bound: a player can press as many wrong keys as they like) and accuracy into
    /// <c>[0, 1]</c>. The result is guaranteed finite and non-negative.</para>
    ///
    /// <para><paramref name="typos"/> defaults to 0, which is both what a play from before the
    /// stat existed carries and the value at which the typo term is exactly 1.0, leaving the
    /// play priced by its misses alone.</para>
    ///
    /// <para><paramref name="rateMultiplier"/> is the play's RATE multiplier, which is 1.0 for
    /// every play except a base-rate Half Time one; <see cref="ForScore"/> supplies it from
    /// <see cref="RateStars.Multiplier"/>. It is a parameter rather than something computed here
    /// because it takes all three of the map's star ratings and this function is handed only the
    /// one it prices with. A caller that omits it prices the play WITHOUT the Half Time penalty, so
    /// every path that can see an HT play must pass it; the end-to-end parity test is what pins
    /// that. Non-finite or negative values fall out as 0 through the guard at the end, exactly like
    /// every other hostile input.</para>
    /// </summary>
    public static double Compute(
        double starRating,
        int notes,
        int misses,
        double accuracy,
        int maxCombo,
        IReadOnlyList<ScoreMod>? mods,
        int typos = 0,
        double rateMultiplier = 1)
    {
        // No notes describes no play; a zero or non-finite rating prices nothing.
        if (notes <= 0 || !double.IsFinite(starRating) || starRating <= 0)
            return 0;

        misses = Math.Clamp(misses, 0, notes);
        maxCombo = Math.Clamp(maxCombo, 0, notes);
        typos = Math.Max(typos, 0);
        accuracy = double.IsFinite(accuracy) ? Math.Clamp(accuracy, 0, 1) : 0;

        double difficulty = Math.Pow(starRating, sr_exponent);

        // Dropped cells, and nothing else. The RAW COUNT carries the power (count_power), not the
        // ratio, so this base falls off far faster than misses/notes ever did: it reaches 0 at
        // misses = notes^(1/count_power), i.e. 49 misses on a 500-note map, and would run NEGATIVE
        // past that. Math.Max is what makes it a well-defined cliff instead, and it is load-bearing:
        // misses can equal notes after the clamp above, so the unclamped base really does go
        // negative, and a fractional exponent on a negative base is non-real. THE Math.Clamp ABOVE
        // HAS TO COME FIRST for the same reason from the other direction: Math.Pow of a negative
        // count under a fractional power is NaN, not a wrong number. Math.Pow also converts to
        // double, which is what stops a tamper-shaped note count overflowing an int square and
        // flipping the sign of the whole penalty. What a miss costs does not depend on the
        // keypresses. At zero misses Math.Pow(0, count_power) is exactly 0, so the base is exactly
        // 1.0 and the term with it.
        double missBase = Math.Max(0.0, 1.0 - Math.Pow(misses, count_power) / notes);
        double cleanliness = Math.Pow(missBase, miss_exponent);

        // Wrong keypresses, and nothing else, under the same power. The count is UNBOUNDED, so it
        // still sits on BOTH sides of the fraction: that is what keeps the denominator growing with
        // the count, putting the zero at the positive root of m^count_power - m - notes = 0 (about
        // 51.71, i.e. 52 typos, on a 500-note map) rather than at notes^(1/count_power). The
        // numerator goes through Math.Pow and the sum is taken in DOUBLE, independently and for the
        // same reason: an int square overflows catastrophically (the true square at int.MaxValue is
        // about 4.6e18) and notes + typos as ints overflows too. In double, int.MaxValue typos
        // give a ratio of about 4.0e5, so the base clamps to a well-defined 0 rather than wrapping into
        // a NaN or a bonus. At zero typos this is exactly 1.0. notes is untouched by design (see
        // the class docs): only this term prices typos.
        double typoBase = Math.Max(0.0, 1.0 - Math.Pow(typos, count_power) / ((double)notes + typos));
        double typoPenalty = Math.Pow(typoBase, typo_exponent);

        double length = LengthBonus(notes);
        double timing = Math.Pow(accuracy, accuracy_exponent);
        // The longest run as a fraction of the map, bent through a log before the exponent reaches
        // it (see combo_log_shape). NO CLAMP IS NEEDED HERE and none would bite: maxCombo is
        // already clamped into [0, notes] above, so comboRatio is in [0, 1], the log's argument in
        // [1, 1 + combo_log_shape] and the base in [0, 1]. A FULL COMBO IS EXACTLY 1.0, since the
        // numerator and denominator are then the same Math.Log call on the same value, so an FC is
        // priced bit-identically across any retune of the shape.
        double comboRatio = (double)maxCombo / notes;
        double comboBase = Math.Log(1.0 + combo_log_shape * comboRatio) / Math.Log(1.0 + combo_log_shape);
        double combo = Math.Pow(comboBase, combo_exponent);

        double pp = scale * difficulty * cleanliness * typoPenalty * length * timing * combo * ModMultiplier(mods, notes) * rateMultiplier;

        return double.IsFinite(pp) && pp > 0 ? pp : 0;
    }

    /// <summary>
    /// pp straight from a stored score row: the whole per-play pipeline in one call, shared by the
    /// submission paths and by <see cref="Packages.PpBackfill"/> so the two can never disagree.
    ///
    /// <para><c>Pp</c> IS NULL WHENEVER THE FORMULA DID NOT RUN, which is a different thing from it
    /// running and returning 0, and the two must not be conflated:</para>
    /// <list type="bullet">
    /// <item><c>(value, settled: true)</c>: priced. <c>value</c> may legitimately be 0 (a give-up
    /// run on a ranked map earns exactly that), and it is an assertion: this play is worth this.</item>
    /// <item><c>(null, settled: true)</c>: REFUSED, permanently. An unranked score, or a custom
    /// rate, can never be priced at all. The caller stores 0 (the column is NOT NULL) but must not
    /// report that 0 as a price: <c>scores/{id}</c> style responses send null, and the game turns
    /// that into "no pp was ever on offer" rather than "you earned zero".</item>
    /// <item><c>(null, settled: false)</c>: NOT PRICED YET. A star rating the play needs is not
    /// stored, so the row is left stale (pp 0, version 0) for <see cref="Packages.PpBackfill"/>
    /// rather than being stamped at a value it would have to disagree with later. A Half Time play
    /// needs TWO of them, <c>sr_ht</c> and <c>sr_dt</c>; see <see cref="StarsFor"/>.</item>
    /// </list>
    ///
    /// <para>Callers writing the <c>pp</c> column coalesce with <c>?? 0</c>; callers putting the
    /// value on the wire send it as-is, so a non-null pp on the wire always means "the server
    /// priced this play, and this is the answer".</para>
    /// </summary>
    /// <param name="ranked">The score's stored <c>ranked</c> flag: an unranked play earns nothing.</param>
    public static (double? Pp, bool Settled) ForScore(
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
            return (null, true);

        var stars = StarsFor(mods, baseStars, starsDoubleTime, starsHalfTime);

        if (stars.Stars is not double effective)
            return (null, !stars.Pending);

        return (Compute(effective, notes.Notes, notes.Misses, accuracy, maxCombo, mods, notes.Typos, stars.Multiplier), true);
    }
}
