using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Typebeat.Web.Packages.Lyrics;

namespace Typebeat.Web.Scoring;

/// <summary>
/// Performance points (pp) for a single play. The canonical spec is <c>docs/pp.md</c>; every
/// constant below is pinned there and must not drift from it.
///
/// <code>
/// pp = 9 · SR_eff^2.30
///      · max(0, 1 − (miss/difficult)^1.2)^13.5134        cleanliness, over DIFFICULT characters
///      · accuracyShare(acc) · knee(acc)                  timing quality
///      · modMult
///      · (1 + comboBonus)                              combo bonus, a FRACTION of the price
///
/// accuracyShare(acc) = expCurve((acc − acc_floor) / (1 − acc_floor))
/// expCurve(t)        = (e^(k·t) − 1) / (e^k − 1), or t itself when k is 0
///
/// comboBonus = min(10, cells/200 · 1) / 100 · maxCombo/cells · (1.5 on a spotless full combo)
/// </code>
///
/// <para>VERSIONS 22 THROUGH 24 ARE A FORK OF THE PRICING SHAPE, tuned in the PP Sandbox
/// (<c>tools/pp-sandbox/</c>), whose module is the same arithmetic with every constant lifted into
/// a dial. Six departures from v21, each marked where it acts:</para>
///
/// <list type="number">
/// <item><description>THE TYPO TERM IS GONE. A wrong keypress the player recovered from costs
/// nothing: the price is the rating, cleanliness, timing, mods and the combo bonus alone. The typo
/// COUNT is still derived by <see cref="CountNotes(IReadOnlyDictionary{string, int})"/> for the
/// surfaces that display it, and still carried on <see cref="NoteCounts"/> and on this method's
/// signature, but nothing here reads it.</description></item>
/// <item><description>THE MISS PENALTY IS JUDGED AGAINST THE MAP'S DIFFICULT CHARACTERS, NOT ITS
/// CELL COUNT. That count is <c>LyricDifficulty.ModelResult.DifficultCharacters</c> at the played
/// rate, on the played stream and in the played judgement arm: every cell weighted by how close its
/// own bin sits to the map's peak, to the envelope power. Dropping a cell therefore costs more on a
/// map whose difficulty is concentrated in a few passages, and the count is a property of the MAP
/// rather than of the play, which is why the server stores it per map in
/// <c>beatmaps.ratings</c> (<see cref="BeatmapRatings"/>) rather than deriving it from a
/// score.</description></item>
/// <item><description>THE LOSS CURVE IS CALIBRATED IN FRACTIONS. The power sits on the missed
/// FRACTION of those difficult characters, so the same miss RATE costs the same share of the core
/// price on every map. <c>miss_exponent</c> 13.5134 is <c>ln(2)/ln(1/0.95)</c>: at
/// <c>count_power</c> 1, missing 5% of the difficult characters keeps exactly half the core. A map
/// with no difficult characters cannot absorb a miss at all: any dropped cell zeroes the term rather
/// than producing 0/0.</description></item>
/// <item><description>THE COMBO BONUS MULTIPLIES THE PRICE INSTEAD OF ADDING TO IT, at a ceiling
/// that scales with the map: +1% at 200 cells on a straight line through the origin, 10% from 2000
/// cells up, times the share of the map the longest run held, and times a 1.5 kicker on a spotless
/// full combo. Because it is a percentage rather than an amount, a price zeroed by misses stays
/// zero.</description></item>
/// <item><description>THE ACCURACY SHAPE IS AN EXPONENTIAL WITH A FLOOR, replacing
/// <c>acc^accuracy_exponent</c>: accuracy is rescaled onto <c>[acc_floor, 1]</c> and run through a
/// normalised exponential that pins both ends, so the floor is exactly 0 and a perfect play exactly
/// 1 at every steepness. The soft knee still multiplies it and ships at width 0, where it is exactly
/// 1.</description></item>
/// <item><description>RECITE IS A MULTIPLIED FLASHLIGHT BONUS rather than a flat term (see
/// <see cref="ReciteMultiplierFor"/>): the mod hides the lyric until it is sung, which is what
/// Flashlight charges for, and that cost grows with how much map there is to hold in the
/// head.</description></item>
/// </list>
///
/// <para>THE JUDGEMENT ARM IS A RATING INPUT, NOT A MULTIPLIER. Easy and Hard Rock move the
/// engine's own windows, and since the difficulty rework the rating prices the intervals a press may
/// land in, so a play in either arm is rated against a different number
/// (<see cref="JudgementArmFor"/>, <see cref="BeatmapRatings"/>). Their flat terms in
/// <see cref="ModMultiplier"/> are the sandbox's dials for what is LEFT after that, which is why
/// Hard Rock's is neutral.</para>
///
/// <para>
/// WHAT COUNTS AS A MISS AND WHAT COUNTS AS A TYPO (backlog 213). A miss is a cell the play did
/// not type right: <c>miss + good</c>, i.e. a cell nobody finished PLUS one finished with the
/// wrong character and never corrected. A typo is a wrong keypress the play recovered from:
/// <c>max(0, combo_break - good)</c>, the mistype count with the uncorrected ones taken back
/// out. So ONE FLUB IS PRICED BY EXACTLY ONE TERM: fix it and it stays a typo event, leave it
/// and it becomes a miss. Both derivations are in <see cref="CountNotes(IReadOnlyDictionary{string, int})"/>
/// and both reduce to the pre-213 ones at <c>good = 0</c>. <c>notes</c> is untouched by the fold,
/// and <c>good</c> stays in <see cref="note_keys"/>: the cell is one cell of the map however it
/// was typed.
/// </para>
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
/// (<c>great + ok + meh + good + miss</c>, where <c>good</c> is an uncorrected typo and, since
/// backlog 213, one of the cells the miss term prices), the
/// map's cell count. Letting keypresses inflate it would hand a masher a smaller COMBO denominator
/// and a bigger Flashlight bonus, paying for the mashing twice over. A play carrying no typo count
/// at all (every score submitted before the stat existed) collapses the typo term to exactly 1.0,
/// so such a play is priced by <c>max(0, 1 − miss^1.6/notes)^10</c> alone.
/// </para>
///
/// <para>
/// THERE IS NO LENGTH FACTOR HERE, AND ADDING ONE BACK WOULD DOUBLE COUNT (backlog 152). Through
/// v15 this file carried <c>max(0.1, 1 + 0.50·log10(notes/100))</c>, worth up to 1.70x, while the
/// star rating priced length barely at all. Length now lives entirely in
/// <see cref="Packages.Lyrics.LyricDifficulty"/>, which since backlog 273 has no separate length
/// term of its own: length counts only through the characters it adds to the envelope's
/// difficulty sum, so pp still sees a long map, but only a few percent through SR_eff rather than
/// up to 70. That is the intended reordering: length stops buying pp it no longer earns.
/// <c>notes</c> itself stays, and is still load-bearing
/// for both penalty terms, the combo ratio and <see cref="FlashlightMultiplier"/>.
/// </para>
///
/// <para>
/// The ordering of the factors is the ordering of what the system values: difficulty sets the
/// ceiling of a play and misses decide how much of that ceiling you keep. Accuracy stays gentler
/// than osu's (1.80, not an <c>acc^6</c>-shaped curve): type!beat
/// accuracies live at 55-93%, not 97-100%, so an osu-shaped accuracy term would crush everything and
/// make accuracy the whole ranking.
/// </para>
///
/// <para>
/// COMBO IS AN ADDITIVE BONUS AND NOT A FACTOR OF THE PRODUCT (backlog 270). Every generation from
/// v1 to v20 multiplied the whole play by a combo term, so a broken run scaled the core pp DOWN;
/// from v12 that term was a log-bent ratio raised to 2.50, tuned so the loss read as roughly the
/// combo's face value. It is now <c>maxcombo/notes · max(0, combo_bonus_slope · (SR_eff −
/// combo_bonus_zero))</c>, ADDED to the finished product (see
/// <see cref="combo_bonus_slope"/>): a play keeps its core pp whatever its longest run was, and a
/// full combo collects the whole of a bonus that is worth 25 pp at 3 stars, 50 at 5 and 75 at 7.
/// </para>
///
/// <para>
/// SR_eff is the map's star rating AT THE PLAY'S CLOCK RATE AND ON THE MAP ITS CONVERSION MODS
/// PRODUCED, never a base rating with a flat bonus bolted on: anything that moves the difficulty is
/// priced exclusively through the recomputed star rating, so nothing double-counts. Only the base
/// rates are pp-eligible (DT/NC 1.50x, HT 0.75x) and Literate is the one conversion mod that moves
/// the rating, which is why the server needs exactly six ratings and can store them per beatmap
/// (<c>beatmaps.difficulty_rating</c> / <c>sr_dt</c> / <c>sr_ht</c>, and
/// <c>sr_literate</c> / <c>sr_literate_dt</c> / <c>sr_literate_ht</c> for the converted map) instead
/// of doing difficulty maths at query time. A CUSTOM rate is pp-ineligible ONLY: the play still
/// ranks on the score leaderboards exactly as before, it just earns nothing here (see
/// <see cref="StarsFor"/>).
/// </para>
///
/// <para>
/// THE SIX ARE A CROSS PRODUCT, NOT A LIST, because Literate is orthogonal to rate and the two do
/// not compose. Since backlog 273 <see cref="Packages.Lyrics.LyricDifficulty"/> is the ENVELOPE
/// model: the hardest sliding window on the map, measured against <c>S(t)</c>, the pace the
/// fastest humans sustain for <c>t</c> seconds, sets the FLOOR and RANGE of the map's rating, and
/// how many characters sit near that peak difficulty decides where inside that range the map
/// lands (there is no length term). A rate change moves both sides of every one of those ratios
/// (the window's own pace AND the duration it is scored at, so a different point of a non-linear
/// <c>S</c>) and can move which characters sit near the peak, while Literate inserts
/// cells that change the bins themselves. Neither the strain model this replaced nor the envelope
/// model admits a rate FACTOR that could be carried across a change of baseline: measured over the
/// five reference maps under the strain model, predicting <c>sr_literate_dt</c> as
/// <c>sr_literate · (sr_dt/difficulty_rating)</c> was already wrong by up to 5.8% in stars and
/// 11.2% in pp. There is no exact relation, so each combination is stored.
/// </para>
///
/// <para>
/// HALF TIME IS PRICED BY ITS RATING AND NOTHING ELSE, exactly as Double Time is (backlog 265).
/// From v3 to v19 it carried one extra term, a MIRROR multiplier that made the down-rate factor
/// the reciprocal of the up-rate one on the same map, on the reading that slowing a map down
/// lowers SR_eff by far less than speeding it up raises it. That term is gone. It was the only
/// place in this file where a rate was priced by anything but the rating, it made one rate a
/// function of all three of a map's ratings (so an HT play could not be priced at all until
/// <c>sr_dt</c> was stored), and a degenerate <c>sr_dt</c> zeroed an otherwise honest play. If
/// Half Time ever reads as underpriced again the fix belongs in the SR model behind
/// <c>sr_ht</c>, never in a second multiplier here. So the claim docs/pp.md has made since task
/// 61, that a rate is priced EXCLUSIVELY through SR_eff, is now literally true of both rates.
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
    /// <item>v3 = the backlog-90 Half Time penalty: a base-rate HT play is multiplied by a MIRROR
    /// multiplier on top of its <c>sr_ht</c> rating, which makes the down-rate factor the
    /// reciprocal of the up-rate one on the same map (or a flat 0.70 cut where that reciprocal
    /// would be a BUFF). Reprices every stored HT row and nothing else. Removed again at v20.</item>
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
    /// <item>v15 = the flat Literate multiplier of 1.06 leaves modMult, and Literate is priced
    /// through the star rating of the map it CONVERTS instead. The mod is
    /// IApplicableAfterBeatmapConversion: it makes every supported punctuation mark a typed cell of
    /// its own, so it genuinely changes the map's cell count, its pace and its difficulty.
    /// docs/pp.md has always said a rate is priced EXCLUSIVELY through SR_eff so that nothing
    /// double-counts, and once Literate moves the rating too, a flat multiplier on top is precisely
    /// that double count. The SHAPE of the formula is untouched and no constant moves; every stored
    /// Literate row is repriced and nothing else is, which is what forces the bump.</item>
    /// <item>v16 = the backlog-152 length migration: the length factor max(0.1, 1 +
    /// 0.50*log10(notes/100)) is DELETED from this file and length is priced by LyricDifficulty
    /// instead, as an additive 0.12*max(0, log10(cells/100)) star bonus. Two length terms would
    /// double count, so pp keeps none: it now sees a long map only as ((SR +
    /// bonus)/SR)^sr_exponent, a few percent where the old term paid up to 1.70x. Long-map plays
    /// therefore deflate hardest (roughly 18% at 340 cells, 28% at 800, 38% at 2300), which is the
    /// intended reordering; the uniform part of that deflation is to be taken out by re-anchoring
    /// scale separately. notes stays, for both penalty terms, the combo ratio and Flashlight.</item>
    /// <item>v17 = Global scale 9.6 to 12.4, a uniform rescale that holds the median ranked player's
    /// total pp flat across backlog 152.</item>
    /// <item>v18 = the backlog-213 fold of the UNCORRECTED TYPO into the miss. No constant moves
    /// and neither does the SHAPE: what moves is the DERIVATION of two of the formula's three
    /// counts from a play's statistics (<see cref="CountNotes(IReadOnlyDictionary{string, int})"/>).
    /// <c>misses</c> becomes
    /// <c>miss + good</c> and <c>typos</c> becomes <c>max(0, combo_break - good)</c>, so a flub
    /// the player never fixed is priced by the cleanliness term at exponent 10 instead of the
    /// typo term at 4, and is priced ONCE rather than by both. <c>notes</c> is untouched:
    /// <c>good</c> stays in <see cref="note_keys"/>, because the cell is still one cell of
    /// the map. Every stored row carrying a <c>good</c> is repriced, downwards, which is what
    /// forces the bump; a row with no uncorrected typo is priced bit-identically, since both
    /// derivations reduce to the old ones at <c>good = 0</c>.</item>
    /// <item>v19 = the backlog-227 accuracy SOFT KNEE. The timing term becomes acc^1.80 multiplied
    /// by 1/(1 + exp(-(acc - acc_knee)/acc_knee_width)), a logistic whose two new constants say
    /// WHERE the accuracy cliff falls (acc_knee = 0.80) and how sharply (acc_knee_width = 0.025),
    /// each independently of the other and of the exponent. Raising accuracy_exponent could not do
    /// this: an exponent steep enough to price 80% out taxes the top of the range too (a 95% play
    /// keeps 0.912 of the term at 1.80 and only 0.774 at 5), where the knee costs that same play
    /// 0.25%, and 1.8% at 90%, 11% at 85%, HALF at 80%, while multiplying 75% by 0.12 and 70% by
    /// 0.02. Three properties hold at every value of the two constants: the knee is EXACTLY 0.5 at
    /// acc == acc_knee (the argument to exp is 0 there), it is STRICTLY INCREASING so it can never
    /// reorder two plays and only respreads them, and it is finite and smooth over the whole of [0,
    /// 1] with no clamp needed, since accuracy is clamped into that interval first and the argument
    /// to exp then stays inside [-8, +32]. A width of 0 or less MEANS no knee, a real branch in
    /// both mirrors that prices exactly as v18 did. Every stored row under a full accuracy is
    /// repriced, downwards and hardest at the bottom, which is what forces the bump; nothing
    /// outside pp moves.</item>
    /// <item>v20 = the backlog-265 removal of the Half Time MIRROR multiplier. v3's extra term and
    /// its 0.70 buff clamp are deleted, so Half Time is priced through <c>sr_ht</c> alone, exactly
    /// as Double Time is priced through <c>sr_dt</c> alone. No constant moves and neither does the
    /// SHAPE: what goes is a whole factor of the product, so every stored base-rate HT row is
    /// repriced UPWARDS by exactly the reciprocal of the multiplier it used to carry (up to 1/0.70,
    /// i.e. +43%, on a row that was taking the clamp), and no other row moves at all. It also
    /// RELAXES a data dependency: an HT play needed both <c>sr_ht</c> and <c>sr_dt</c> and now
    /// needs only <c>sr_ht</c>, so a map the SR sweep filled halfway stops holding its HT rows
    /// pending, and a degenerate <c>sr_dt</c> (which used to zero the multiplier and with it the
    /// whole play) is simply ignored. A Literate HT play follows without a branch of its own: the
    /// converted triple is still picked before the rate question is asked.</item>
    /// <item>v21 = the backlog-270 combo rewrite and mod-table edit. COMBO STOPS BEING A FACTOR: the
    /// log-bent multiplier (combo_log_shape 9.0, combo_exponent 2.50) is deleted and a BONUS of
    /// maxcombo/notes * max(0, 12.5 * (SR_eff - 1.0)) is ADDED to the finished product, outside
    /// every factor including modMult. A non-FC play therefore keeps its core pp instead of being
    /// crushed by a term that had already been charged for by the miss and typo terms, and a full
    /// combo earns 25 pp at 3 stars, 50 at 5 and 75 at 7. The bonus is deliberately NOT gated by
    /// accuracy: combo does not break on an off-time press, so a full-combo run at 69 percent
    /// collects all of it, and gating it would move top-20 totals by only 1 to 4 percent. MOD
    /// TABLE: the dead RH entry (rhythmic_multiplier 1.10) is deleted, which reprices the one
    /// stored Rhythmic row 10 percent down; recite_multiplier 1.07 (RE) and
    /// fletcher_strict_multiplier 1.02 (FC) are added, both mods whose gameplay change no star
    /// rating can see. Every stored row except a zero-combo one is repriced, which is what forces
    /// the bump.</item>
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
    ///
    /// <para>v22 is the fork described on the class: the typo term deleted, the miss penalty judged
    /// against the map's difficult characters, the loss curve calibrated in missed fractions, the
    /// combo bonus turned into a map-scaled percentage of the price, and the shape retuned to the PP
    /// Sandbox's dials as they stood then (scale 8, rating exponent 2.30, accuracy exponent 3, Hard
    /// Rock 1.15). EVERY stored row reprices, which is what the bump is for.</para>
    ///
    /// <para>v23 HAS NO CHANGELOG ENTRY ON THE CLIENT, which is worth saying outright rather than
    /// leaving as a hole: the departures it carries are documented where they act (DEPARTURE 5, the
    /// exponential accuracy curve with its <c>acc_floor</c>, replacing <c>acc^accuracy_exponent</c>;
    /// and DEPARTURE 6, Recite as a MULTIPLIED Flashlight bonus rather than a flat term) and the
    /// version number is the only record that they landed together. Both reprice every stored row
    /// that carries them: the accuracy curve reprices ALL of them, Recite the Recite ones.</para>
    ///
    /// <para>v24 is the PP Sandbox's LIVE dials, re-read from the lab after the owner retuned it.
    /// The Easy multiplier drops 0.9 to 0.85, and the knee position is written as the 0 the lab's
    /// panel holds, inert either way since the width is 0 and the knee is therefore exactly 1.0, so
    /// the only live move is Easy's and it reprices Easy rows alone. Every other dial already agreed
    /// with the lab: scale 9, sr_exponent 2.30, count_power 1.2, acc_steepness 1.75, acc_floor 0.5,
    /// knee off, miss_exponent 13.5134, the combo cap and kicker, reference_notes 100, Recite 2.0,
    /// Hard Rock neutral, Fletcher and No Fail 0.9.</para>
    ///
    /// <para>ALL THREE LAND AT ONCE HERE, and the ordering against the ratings matters: the server
    /// cannot price a v24 play at all until <see cref="Packages.PaceBackfill"/> has filled
    /// <c>beatmaps.ratings</c> (034_ratings_matrix.sql) for the map, because the difficult-character
    /// count is a stored figure now. A row whose cell is missing stays PENDING rather than being
    /// priced with a zeroed cleanliness term, exactly as an unfilled <c>sr_dt</c> already does, so
    /// the two sweeps compose in either order and the second one settles what the first could
    /// not.</para>
    /// </summary>
    public const int VERSION = 25;

    /// <summary>
    /// Decay of the per-play weighting in the total (see <see cref="PpRanking"/>): the i-th best
    /// deduped play contributes <c>pp_i · DECAY^i</c>.
    ///
    /// <para>0.92 sits between the 0.85 the ranked pool started on and osu's 0.95: the tail counts
    /// for real now (the 10th play carries ~43%, the 20th ~19%), so depth in a player's clears
    /// moves the total while the best plays still dominate, and there is still no hard cutoff
    /// where the 11th-best contributes exactly nothing. Raised from 0.85 (backlog 293) exactly as
    /// the note here always said to do once the pool grew, and only totals moved: per-play pp is
    /// untouched, so VERSION stays put and no stored row reprices. The last step towards 0.95
    /// stays the same one-line change, the total being computed on read.</para>
    /// </summary>
    public const double DECAY = 0.92;

    // ---- formula constants (docs/pp.md, which pins the PP Sandbox's active dials) ----

    private const double scale = 9.0;               // C: global scale, does not affect ranking order
    private const double sr_exponent = 2.30;

    /// <summary>
    /// <c>ln(2) / ln(1 / 0.95)</c>, rounded: the exponent that puts HALF the core price at a 5% miss
    /// rate with <see cref="count_power"/> 1, on maps of every size.
    /// </summary>
    private const double miss_exponent = 13.5134;

    /// <summary>
    /// The power the MISSED FRACTION carries. 1 is linear in the fraction, which is what makes the
    /// calibrated half-point a miss RATE; higher gives a grace region at low miss rates and a
    /// steeper fall near 100%. The base reaches zero only when every difficult character was missed,
    /// whatever this is set to.
    /// </summary>
    private const double count_power = 1.2;

    // DEPARTURE 5 (v23). The accuracy SHAPE, replacing the shipped acc^accuracy_exponent.
    // Accuracy is rescaled onto [acc_floor, 1] and run through a normalised exponential, which pins
    // both ends for every steepness: the floor is exactly 0 and a perfect play exactly 1.
    // `acc_steepness` is the shape dial, 0 being the straight line from the floor, and higher values
    // holding the price low through the middle of the range before climbing hard over the last few
    // points. These are the PP Sandbox's active dials, which is where the fork is tuned; see
    // tools/pp-sandbox/pp.mjs.
    private const double acc_steepness = 1.75;
    private const double acc_floor = 0.5;

    /// <summary>
    /// WHERE THE ACCURACY CLIFF SITS (backlog 227). The timing term is
    /// <c>acc^accuracy_exponent · 1/(1 + exp(-(acc - acc_knee)/acc_knee_width))</c>: a SOFT KNEE
    /// multiplying the gentle exponent, with this constant setting the accuracy the knee is
    /// centred on and <see cref="acc_knee_width"/> setting how sharply it falls. The two are
    /// independent of each other and of the exponent, which is the whole reason the knee is a
    /// second factor rather than a bigger exponent.
    ///
    /// <para>A BIGGER EXPONENT WOULD TAX THE TOP TOO. Real accuracies here live at 55 to 93 (see
    /// <c>docs/pp.md</c>) and solid plays at 85 to 98, so an exponent steep enough to price 80% out
    /// takes the plays it is not aimed at with it: a 95% play keeps 0.912 of the term at 1.80 and
    /// only 0.774 at 5. The knee costs that same play 0.25%, and 1.8% at 90%, 11% at 85%, HALF at
    /// 80%, while multiplying 75% by 0.12 and 70% by 0.02.</para>
    ///
    /// <para>THE KNEE IS EXACTLY 0.5 AT <c>acc == acc_knee</c> AT EVERY WIDTH, since the argument
    /// to the exponential is then exactly 0 and <c>1/(1 + exp(0))</c> is <c>1/2</c>. So a play
    /// sitting on the knee is priced identically across any retune of the width, and the width
    /// repositions only what sits either side of it, exactly as
    /// <see cref="combo_bonus_slope"/> leaves a play with no combo at all alone.</para>
    ///
    /// <para>IT CANNOT REORDER TWO PLAYS. The logistic is strictly increasing in accuracy and so
    /// is <c>acc^accuracy_exponent</c>, so their product is too: the knee RESPREADS the accuracy
    /// axis and never permutes it. It is also finite and smooth over the whole of <c>[0, 1]</c>
    /// with no clamp needed, since accuracy is clamped into that interval before it gets here: at
    /// a width of 0.025 the argument to <c>Math.Exp</c> runs between -8 and +32, nowhere near the
    /// ~709 at which it overflows to infinity.</para>
    /// </summary>
    // 0 at the PP Sandbox's live dials, which is the position its own panel holds. Inert either
    // way: the width below is 0, this file's declared-absence sentinel, so the whole knee is
    // exactly 1.0. Written as the lab writes it so the two sides do not disagree about a dial they
    // both ignore.
    private const double acc_knee = 0.0;

    /// <summary>
    /// How sharply the knee at <see cref="acc_knee"/> falls: the accuracy interval over which the
    /// factor moves from about 0.27 to about 0.73 (one width either side of the knee). Smaller is
    /// a harder edge.
    ///
    /// <para>A WIDTH OF ZERO OR LESS MEANS THERE IS NO KNEE, and the factor is then exactly 1.0,
    /// i.e. the pre-227 timing term. That is the DECLARED-ABSENCE sentinel: a mirror one
    /// generation behind declares neither constant and prices exactly that, which is what
    /// <c>tools/pp.py</c> reads an absent declaration as. It is a real branch in
    /// <see cref="AccuracyKnee"/> rather than a limit of the formula, which is where it differs
    /// from <see cref="combo_bonus_slope"/>'s 0: a slope of 0 really does make the bonus exactly
    /// 0 for every play, where a logistic has no width that returns 1.0 (a width tending to 0
    /// gives a STEP, and 0/0 at the knee itself is NaN), so only the branch makes the sentinel
    /// true of the arithmetic as well as of the tool.</para>
    /// </summary>
    // OFF at the sandbox's active dials: the exponential accuracy curve already does the shaping
    // the knee was added for, and a width of 0 is this file's declared-absence sentinel.
    private const double acc_knee_width = 0.0;

    /// <summary>
    /// WHAT A FULL COMBO IS WORTH, PER STAR (backlog 270). The combo bonus is
    /// <c>maxcombo/notes · max(0, combo_bonus_slope · (SR_eff − combo_bonus_zero))</c>, ADDED to
    /// the finished product rather than multiplied into it, so this constant is a number of pp
    /// per star rather than a fraction of anything: at 12.5 a full combo is worth 25 pp at 3
    /// stars, 50 at 5 and 75 at 7, and a play with half the map's combo collects half of that.
    ///
    /// <para>WHY ADDITIVE. Through v20 combo was a FACTOR, so a broken run scaled the whole play
    /// down and stacked on top of the two penalty terms that had already charged for the flubs
    /// that broke it. Non-FC plays were crushed: a real 69% run that never held a long streak
    /// priced at almost nothing however hard the map was. As a bonus, the core pp of a play is
    /// whatever its difficulty, cleanliness, typos and accuracy say it is, and a long run adds to
    /// it.</para>
    ///
    /// <para>THE BONUS IS DELIBERATELY NOT GATED BY ACCURACY, and the consequence was measured
    /// rather than overlooked: combo does not break on an off-time press, so a full-combo run at
    /// 69% accuracy collects the whole bonus. Gating it by
    /// <c>acc^accuracy_exponent · AccuracyKnee(acc)</c> was tried in the sandbox and moves top-20
    /// totals by 1 to 4%, which is not worth making the bonus a second accuracy term; the spec is
    /// a naive fraction of a naive bonus.</para>
    ///
    /// <para>A SLOPE OF ZERO MEANS THERE IS NO BONUS, exactly, for every play and every rating,
    /// which is what <c>tools/pp.py</c> reads an absent declaration of either constant as. That
    /// needs no branch here: <c>0 · (SR_eff − z)</c> is 0 and <c>max(0, 0)</c> is 0, so the
    /// sentinel is a value of the arithmetic and not merely a reading of it.</para>
    /// </summary>
    private const double combo_bonus_slope = 12.5;

    /// <summary>
    /// THE RATING BELOW WHICH A FULL COMBO IS WORTH NOTHING (backlog 270): the bonus is
    /// <c>max(0, combo_bonus_slope · (SR_eff − combo_bonus_zero))</c>, so it opens at
    /// <c>SR_eff = combo_bonus_zero</c> and grows linearly above it. The <c>Math.Max</c> is
    /// load-bearing and not defensive: a real map can rate below 1.0 (a map with no window long
    /// enough to fit the smallest scheduled duration rates EXACTLY 0 under the envelope model,
    /// there being no length term left to give it anything else), and without the clamp such a
    /// play would be handed a NEGATIVE bonus that a long run made worse.
    /// </summary>
    private const double combo_bonus_zero = 1.0;

    // ---- v22 combo bonus, which REPLACED the two constants above ----
    //
    // They are kept (and referenced by nothing) because they are the record of what v21 priced: the
    // fork's bonus is a PERCENTAGE of the price rather than a number of pp added beside it, and its
    // ceiling grows with the map instead of being a flat pp amount a short map and a long one
    // collect alike.

    /// <summary>
    /// The percentage a FULL COMBO is worth on a 200-cell map. The ceiling is a straight line
    /// through the origin, so this one number also sets the slope: 1% here is 2% at 400 cells, 5% at
    /// 1000 and 10% at 2000, where <see cref="combo_bonus_cap"/> takes over. 0 removes the bonus
    /// exactly.
    /// </summary>
    private const double combo_bonus_at_200_cells = 1.0;

    /// <summary>
    /// The most the combo bonus can ever be worth, as a percentage of the price, on any map.
    /// </summary>
    private const double combo_bonus_cap = 10.0;

    /// <summary>
    /// What a SPOTLESS full combo (every cell typed, none dropped) multiplies its own ceiling by: a
    /// 2000-cell map pays +15% for 2000/2000 and just under +10% for 1999/2000. 1 removes the kicker
    /// and leaves the line unbroken.
    /// </summary>
    private const double combo_bonus_perfect = 1.5;

    /// <summary>
    /// The pivot of <see cref="FlashlightMultiplier"/>'s log bonus: 100 notes is where it is worth
    /// exactly <c>1 + flashlight_offset</c>. It was shared with the length bonus until backlog 152
    /// deleted that; Flashlight owns it alone now, and it stays here rather than moving into the mod
    /// block because it is a property of the note count, not of the mod.
    /// </summary>
    private const double reference_notes = 100.0;

    // ---- mod multipliers (docs/pp.md) ----

    // THE NUMBERS IN THIS BLOCK ARE CHOSEN, NOT DERIVED FROM THE SCORE MULTIPLIERS. Two of them
    // (recite_multiplier and fletcher_strict_multiplier) happen to equal the mod's score
    // multiplier because the user picked the same number twice, and that is a coincidence rather
    // than a rule: Easy is 0.75 here against 0.5x score, Hard Rock 1.25 against 1.10x, No Fail
    // 0.90 against 0.5x and Flashlight a length-scaled bonus against a flat 1.12x. Never read one
    // table off the other.
    //
    // Rhythmic (RH) had an entry here from backlog 135 until backlog 270, at 1.10. The mod itself
    // went in backlog 147, so no client can send the acronym and exactly one stored row still
    // carries it; that row reprices 10% down at v21, which is a VERSION bump doing what a VERSION
    // bump is for. The XMLDoc that argued against this deletion cited
    // ModMultiplier.TotalScoreCeiling, which is a DIFFERENT FILE (Scoring/ModMultiplier.cs, the
    // score-side table): that table still prices "RH" at 1.10 and is untouched here, so the row's
    // stored total stays under its ceiling and stays ranked.

    /// <summary>
    /// Recite (backlog 236): the lyric text is hidden until the line is sung, so the play is typed
    /// from listening rather than from reading ahead. Nothing about the map changes, so there is no
    /// converted rating to price it through.
    ///
    /// <para>SINCE v23 IT IS NOT A FLAT TERM EITHER but the SCALE on Flashlight's bonus (see
    /// <see cref="ReciteMultiplierFor"/>), because hiding the lyric is what Flashlight charges for
    /// and that cost grows with how much map there is to hold in the head. 0 is free, 1 is exactly
    /// what Flashlight is worth, and the sandbox's active 2.0 pays twice Flashlight's bonus.</para>
    /// </summary>
    private const double recite_multiplier = 2.0;

    /// <summary>
    /// Fletcher (backlog 208): the caret is PINNED back to the line the song is on, which is the
    /// reverse of the mod's original meaning and the harder half of it, since the unpinned caret
    /// became the default for every play. The cells, their target times and the map's pace are
    /// identical, so like Easy and Hard Rock it is priced flat here.
    ///
    /// <para>DISTINCT FROM <see cref="fletcher_multiplier"/>, which is the retired <c>FT</c>
    /// acronym at 0.90: <c>FT</c> is a <c>ModType.System</c> mod nobody can select, kept
    /// resolvable so its stored rows keep their price, and it means the OPPOSITE thing (an
    /// unpinned caret, back when that was the mod rather than the default).</para>
    /// </summary>
    private const double fletcher_strict_multiplier = 1.02;

    /// <summary>
    /// Easy (backlog 149): the play was judged on DOUBLED windows, so every character was twice as
    /// forgiving to land. Priced as the difficulty reduction it is, at a value the user chose on
    /// 2026-08-13. Flat rather than routed through the star rating, unlike Literate: the mod
    /// converts nothing (the cells, their target times and the map's pace are identical), it only
    /// widens the tolerance around each target.
    ///
    /// <para>"WHICH NO RATING INPUT CAN SEE" WAS TRUE UNTIL THE DIFFICULTY REWORK AND IS NOT NOW:
    /// the rating prices the intervals a press may land in, so Easy's doubled, word-sheltered
    /// windows are a JUDGEMENT ARM of their own and the play is rated against a different number
    /// (<see cref="JudgementArmFor"/>, <see cref="BeatmapRatings"/>). This flat term is what the
    /// PP Sandbox charges for what is LEFT after that, at its live dial (0.85; the lab's own panel
    /// is where the value is chosen).</para>
    /// </summary>
    private const double easy_multiplier = 0.85;

    /// <summary>
    /// Hard Rock (backlog 150): the play was judged on HALVED windows, the exact mirror of Easy, so
    /// every character was half as forgiving to land. Priced flat for the same reason Easy is (the
    /// mod converts nothing, so no rating input can see it), at a value the user chose on
    /// 2026-08-13. Deliberately NOT the reciprocal of <see cref="easy_multiplier"/> (1.333...): the
    /// window scales mirror each other, the prices need not. Separate from the mod's 1.10x SCORE
    /// multiplier in <see cref="ModMultiplier"/>, exactly as Easy's flat term is separate from its
    /// 0.5x.
    /// </summary>
    // NEUTRAL at the PP Sandbox's active dials: Hard Rock's judgement windows are what the rhythm
    // arm of the star rating reads, so the sandbox leaves the flat term at 1.0 rather than paying
    // for the same change twice.
    private const double hard_rock_multiplier = 1.0;

    private const double fletcher_multiplier = 0.90;
    private const double no_fail_multiplier = 0.90;

    /// <summary>
    /// Classic (backlog 398): a SYNTHETIC SYSTEM MARK the server appends to a score whose played
    /// version is not the current gameplay of its (re-ranked) map. The play was on a different map
    /// from the one that got ranked (earlier timing, or a different recording), so it is marked and
    /// paid 5 percent less. Flat, because the mark converts nothing the rating can see: the play's
    /// own cells, target times and pace are whatever its version held, and there is no rating cell
    /// for "a different version" to read. Mirrors the 0.95x SCORE term in <see cref="ModMultiplier"/>.
    /// </summary>
    private const double classic_multiplier = 0.95;
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
    /// inflate <c>notes</c> and dilute every factor it appears in (cleanliness, typos, combo,
    /// Flashlight). Anything
    /// else the base ruleset can emit (ticks, bonuses) does not occur in a typing map and is not a
    /// note either.
    ///
    /// <para><see cref="unfixed_typo_key"/> IS a note, and that is the pp half of backlog 124
    /// and 126. It is one cell of the map, so leaving it out would shorten the map pp thinks the
    /// player played, hardening both penalty terms and inflating the combo ratio. Backlog 213
    /// does NOT change that: the key stays on this list, and what changed is only which of the
    /// two penalty terms it feeds. Through v17 it was deliberately not the
    /// <see cref="miss_key"/>, on the reading that a cell finished wrongly is not a cell
    /// nobody finished; since v18 it is counted as one, because the two say the same thing about
    /// the play (the character is not there) and pricing them apart charged one flub through the
    /// typo term while completion had already charged it as a miss.</para>
    ///
    /// <para><c>perfect</c> was backlog 133's fourth quality tier, and it stays on this list after
    /// backlog 147 took that tier back out. It is not dead weight: 133 SHIPPED, so rows stored
    /// while it was live carry the key, and pp is recomputed from a stored row on every
    /// <c>PpBackfill</c> sweep. Drop it and each of those rows reads as a map with almost no notes
    /// at all, hardening both penalty terms and inflating the combo ratio. No play made under
    /// today's three tiers can produce one, so the entry costs every other row nothing.</para>
    /// </summary>
    private static readonly string[] note_keys = ["perfect", "great", "ok", "meh", "good", "miss"];

    /// <summary>The <see cref="note_keys"/> member that is a DROPPED cell: nobody typed it.</summary>
    private const string miss_key = "miss";

    /// <summary>
    /// The <see cref="note_keys"/> member that is an UNCORRECTED TYPO: the player finished the
    /// cell with the wrong character and never went back for it (the client's
    /// <c>TypeBeatResultMapping.UNFIXED_TYPO</c>, which resolves to <c>HitResult.Good</c> and
    /// therefore to this wire key).
    ///
    /// <para>Since backlog 213 it is priced as a MISS and not as a typo: see
    /// <see cref="CountNotes(IReadOnlyDictionary{string, int})"/> for the derivation and for why
    /// the wrong keypress that produced it is taken back out of the typo count.</para>
    /// </summary>
    private const string unfixed_typo_key = "good";

    /// <summary>
    /// The MISTYPE key: wrong keypresses, persisted by the client under
    /// <c>HitResult.ComboBreak</c> (backlog 72). Not a note, not accuracy-affecting, and absent
    /// entirely from every score submitted before it existed. Priced by its own term since
    /// backlog 89.
    /// </summary>
    private const string mistype_key = "combo_break";

    /// <summary>
    /// Notes, misses and typos for a play, as the formula defines them. <see cref="Typos"/> defaults
    /// to 0 so a play that carries no typo count prices exactly as it always did, and since v22
    /// nothing in <see cref="Compute"/> reads it at all: it survives for the surfaces that display a
    /// typo count.
    /// </summary>
    public readonly record struct NoteCounts(int Notes, int Misses, int Typos = 0)
    {
        /// <summary>
        /// THE DIFFICULT CHARACTERS of the map the play was set on, at the played rate, on the
        /// played stream and in the played judgement arm: what the miss penalty is judged against
        /// since v22 (see the class docs). It is a property of the MAP rather than of the play, so
        /// the server fills it from the map's stored matrix (<see cref="BeatmapRatings"/>, through
        /// <see cref="StarsFor"/>) rather than from a score row, and a caller with no matrix cell
        /// leaves it at 0, which prices any miss as a zeroed cleanliness term rather than silently
        /// falling back to the cell count.
        ///
        /// <para>ON THE SERVER THAT FALLBACK IS UNREACHABLE BY DESIGN: a play whose cell is missing
        /// is <see cref="RateStars.Unavailable"/> and never reaches <see cref="Compute"/> at all.
        /// The property carries the game's contract anyway, because the two files are mirrors and a
        /// reader of either one has to find the same rule written down.</para>
        /// </summary>
        public double DifficultCharacters { get; init; }
    }

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
    /// <para>THERE IS NO RATE MULTIPLIER HERE any more (backlog 265). One rode alongside the rating
    /// from v3 to v19, for base-rate Half Time alone; every rate is now priced by the rating this
    /// type carries and by nothing else, so <see cref="Stars"/> is the whole of what a rate is
    /// worth.</para>
    /// </summary>
    /// <param name="DifficultCharacters">
    /// The other half of the map's reading for this play (<see cref="MapRating"/>): the difficult
    /// characters the miss penalty is judged against since v22. It travels with the rating rather
    /// than beside it because a caller holding one without the other would have to invent the
    /// missing half, and the two ways of doing that price the same play very differently. 0 on both
    /// null outcomes, where it is never read.
    /// </param>
    public readonly record struct RateStars(double? Stars, bool Pending, double DifficultCharacters = 0)
    {
        public static RateStars Of(MapRating rating) => new(rating.Stars, false, rating.DifficultCharacters);
        public static readonly RateStars Ineligible = new(null, false);
        public static readonly RateStars Unavailable = new(null, true);
    }

    /// <summary>
    /// Notes, misses and typos from a play's <c>statistics</c> dictionary. Negative counts
    /// (tamper-shaped) contribute nothing rather than subtracting, and a missing
    /// <c>combo_break</c> key (every pre-backlog-72 score) reads as 0 typos.
    ///
    /// <para>Since backlog 213 <c>misses</c> and <c>typos</c> are DERIVED rather than read
    /// straight off two keys: <c>misses = miss + good</c> and
    /// <c>typos = max(0, combo_break - good)</c>. The body says why.</para>
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

            // Backlog 213: an UNCORRECTED TYPO is a MISS. It keeps its own statistics key (the
            // wire does not move, so old rows stay comparable and the distinction survives in
            // the data), and this is the pp half of every consumer reclassifying instead.
            if (key == miss_key || key == unfixed_typo_key)
                misses += count;
        }

        int unfixedTypos = statistics.TryGetValue(unfixed_typo_key, out int unfixedCount) && unfixedCount > 0 ? unfixedCount : 0;
        int typos = statistics.TryGetValue(mistype_key, out int typoCount) && typoCount > 0 ? typoCount : 0;

        // NO DOUBLE JEOPARDY, which is the other half of the fold. Every uncorrected typo cell
        // implied a wrong KEYPRESS, and that keypress is already in combo_break, so charging the
        // miss term for the cell AND the typo term for the keypress would price one flub twice.
        // A corrected typo stays a typo event (its cell resolved as an ordinary hit, so nothing
        // subtracts it); an uncorrected one becomes a miss and leaves the typo term.
        //
        // The clamp is load-bearing rather than defensive: the two counts arrive off the wire
        // independently, so good can exceed combo_break on a row stored before the mistype stat
        // existed at all (no combo_break key, backlog 72) and on any tamper-shaped dictionary. A
        // negative typo count would raise Math.Pow(typos, count_power) to a NaN under the
        // fractional power, or price the play ABOVE a clean one.
        return new NoteCounts(notes, misses, Math.Max(0, typos - unfixedTypos));
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
    /// The acronym of the LITERATE mod, keyed the way <see cref="RateMods"/> keys the rate mods and
    /// for the same reason: the acronym is what travels on the wire and what this server and the
    /// game client share, where a mod TYPE exists only in the client.
    /// </summary>
    public const string LITERATE_ACRONYM = "LT";

    /// <summary>Whether this play was set with the Literate mod, i.e. on the CONVERTED beatmap.</summary>
    public static bool IsLiterate(IReadOnlyList<ScoreMod>? mods)
    {
        if (mods is null)
            return false;

        foreach (var mod in mods)
        {
            if (string.Equals(mod.Acronym?.Trim(), LITERATE_ACRONYM, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// WHICH JUDGEMENT ARM a play is rated in, from its mods: the two mods that change the engine's
    /// own windows rather than the map (Easy and Hard Rock, which the client makes mutually
    /// exclusive), read the way <see cref="IsLiterate"/> reads the typed stream. Everything else
    /// rates in the live span rule.
    ///
    /// <para>THE ARM IS A RATING INPUT, not a filter over the finished price: since the difficulty
    /// rework the star rating prices the intervals a press may land in, so a wider window or a point
    /// target is a different rating. It is deliberately NOT a multiplier as well, which would charge
    /// the same change twice, once in the rating and once in <see cref="ModMultiplier"/>.</para>
    ///
    /// <para>THE ONE DIFFERENCE FROM THE GAME'S COPY: the client keys on the mod TYPES
    /// (<c>TypeBeatModEasy</c>, <c>TypeBeatModHardRock</c>), which exist only in the client, so this
    /// keys on the ACRONYMS that travel on the wire, exactly as <see cref="IsLiterate"/> and
    /// <see cref="ModMultiplier"/> already do. A stack carrying both (which no client can produce)
    /// takes whichever comes first, matching the client's own loop.</para>
    /// </summary>
    public static LyricDifficulty.JudgementArm JudgementArmFor(IReadOnlyList<ScoreMod>? mods)
    {
        if (mods is null)
            return LyricDifficulty.JudgementArm.None;

        foreach (var mod in mods)
        {
            string? acronym = mod.Acronym?.Trim();

            if (string.Equals(acronym, EASY_ACRONYM, StringComparison.OrdinalIgnoreCase))
                return LyricDifficulty.JudgementArm.Easy;

            if (string.Equals(acronym, HARD_ROCK_ACRONYM, StringComparison.OrdinalIgnoreCase))
                return LyricDifficulty.JudgementArm.HardRock;
        }

        return LyricDifficulty.JudgementArm.None;
    }

    /// <summary>The acronym of the EASY mod, keyed as <see cref="LITERATE_ACRONYM"/> is.</summary>
    public const string EASY_ACRONYM = "EZ";

    /// <summary>The acronym of the HARD ROCK mod, keyed as <see cref="LITERATE_ACRONYM"/> is.</summary>
    public const string HARD_ROCK_ACRONYM = "HR";

    /// <summary>
    /// Which reading of the map prices this play, given the map's stored rating matrix
    /// (<c>beatmaps.ratings</c>, <see cref="BeatmapRatings"/>). See <see cref="RateStars"/> for the
    /// three outcomes.
    ///
    /// <para>THREE QUESTIONS, and the matrix answers all of them at once because they are a cross
    /// product rather than a list. WHICH MAP: Literate is a conversion mod, so a Literate play is a
    /// play on a different map, and since backlog 144 it is priced through that map's rating and
    /// carries no flat multiplier (see <see cref="ModMultiplier"/>). WHICH ARM: Easy and Hard Rock
    /// move the engine's own windows, which the rating now prices
    /// (<see cref="JudgementArmFor"/>). WHICH RATE: the play's own clock. The three are orthogonal,
    /// so eighteen cells are stored and this reads exactly one of them.</para>
    ///
    /// <para>A stack carrying MORE THAN ONE rate mod is tamper-shaped by construction (the client
    /// makes DT / NC / HT / DC mutually exclusive), so it is treated as ineligible rather than guessed
    /// at, exactly as <see cref="ModMultiplier"/> treats it as the conservative case for
    /// scoring.</para>
    ///
    /// <para>A CELL THE MATRIX DOES NOT CARRY IS <see cref="RateStars.Unavailable"/>, which covers
    /// both a NULL column (a row the sweep has not reached) and a matrix written by an older shape
    /// that has no such cell. That play is left stale for <see cref="Packages.PpBackfill"/> rather
    /// than priced off a rating that is not its own, which is the same contract an unfilled
    /// <c>sr_dt</c> has had since backlog 90.</para>
    /// </summary>
    /// <param name="mods">The play's parsed mods (<see cref="ScoreMods.Parse"/>).</param>
    /// <param name="ratings">The map's stored matrix, or null when the column is NULL.</param>
    public static RateStars StarsFor(IReadOnlyList<ScoreMod>? mods, BeatmapRatings? ratings)
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

        double rate = 1;

        if (rateMods > 1)
            return RateStars.Ineligible;

        if (rateMods == 1)
        {
            if (!RateMods.TryGetRange(rateMod.Acronym, out var range))
                return RateStars.Ineligible;

            // ScoreMods.Parse already snapped and clamped this, and fell back to the mod's default
            // for a historic row that carries no speed_change at all (under the old rules a ranked
            // bare DT could only have been 1.50x), so a legitimately base-rate play always lands
            // exactly on the default here.
            rate = rateMod.Rate ?? range.Default;

            if (Math.Abs(rate - range.Default) > rate_epsilon)
                return RateStars.Ineligible;

            // The matrix is keyed on the DEFAULT rather than on the parsed figure, so a value that
            // cleared the epsilon above by a hair still reads the cell it was meant to read.
            rate = range.Default;
        }

        // THE COLUMN'S NULL IS PENDING AND NOT A PRICE, so this is checked before the lookup rather
        // than folded into it: a map the sweep has not reached carries no cell for any stack.
        if (ratings is null)
            return RateStars.Unavailable;

        return ratings.TryGet(JudgementArmFor(mods), IsLiterate(mods), rate) is MapRating rating
            ? RateStars.Of(rating)
            : RateStars.Unavailable;
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
    ///
    /// <para>Easy IS a flat term here (<see cref="easy_multiplier"/>), and so is Hard Rock
    /// (<see cref="hard_rock_multiplier"/>), and that is not in tension with the Literate rule
    /// below: scaling the judgement windows changes nothing the star rating is computed from, so
    /// there is no converted map to price either of them through. The RATE mods scale the same
    /// windows since backlog 150 and still carry no term here, because a rate does move the rating
    /// and is priced exclusively through it.</para>
    ///
    /// <para>THERE IS NO LITERATE TERM HERE EITHER, and for exactly the reason there is no rate one
    /// (backlog 144). Literate is a CONVERSION mod: it turns every punctuation mark into a typed
    /// cell, so it changes the map's cell count, its pace and its rating, and it is priced through
    /// <see cref="StarsFor"/>'s <c>sr_literate*</c> ratings. It used to carry a flat 1.06 ON TOP of
    /// the unconverted map's rating, which was the only place in this file where a mod that moves
    /// the rating was also paid a multiplier; keeping both once the rating moves would be precisely
    /// the double count docs/pp.md exists to forbid. The flat number was also a poor description of
    /// the mod: measured over the five reference maps the honest rate-1.0 rating moves between
    /// -0.8% and +6.3%, i.e. Literate makes two of them EASIER, where a flat 1.06 paid every map
    /// the same 6%.</para>
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
                "FL" => FlashlightMultiplier(notes),
                "EZ" => easy_multiplier,
                "HR" => hard_rock_multiplier,
                "RE" => ReciteMultiplierFor(notes),
                "FC" => fletcher_strict_multiplier,
                "FT" => fletcher_multiplier,
                "NF" => no_fail_multiplier,
                // The synthetic Classic mark (backlog 398, see classic_multiplier): the play was on
                // a version of the map that is not the one that was ranked.
                "CL" => classic_multiplier,
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

    /// <summary>
    /// DEPARTURE 6 (v23). Recite is a MULTIPLIED Flashlight bonus rather than a flat term:
    /// <c>1 + recite_multiplier * (FlashlightMultiplier(notes) - 1)</c>. The mod hides the lyric
    /// until the line is sung, which is what Flashlight charges for (the map is typed from memory
    /// rather than read ahead), and that cost grows with how much map there is to hold in the head.
    /// So <see cref="recite_multiplier"/> is the SCALE on that bonus, not a bonus of its own: 0 is
    /// free, 1 is exactly what Flashlight is worth, and the sandbox's active 2.0 pays twice
    /// Flashlight's bonus. The two mods still multiply when both are selected, exactly as every
    /// other pair in the table does.
    /// </summary>
    public static double ReciteMultiplierFor(int notes)
        => 1 + recite_multiplier * (FlashlightMultiplier(notes) - 1);

    /// <summary>
    /// DEPARTURE 5 (v23). The share of its core pp a play keeps from accuracy alone:
    /// <c>ExpCurve((accuracy - acc_floor) / (1 - acc_floor))</c>, so the floor is exactly where the
    /// price reaches zero and a perfect play is exactly 1. The normalisation is what makes the two
    /// dials independent: the steepness moves the shape without moving either end.
    /// </summary>
    private static double AccuracyShare(double accuracy)
    {
        if (!double.IsFinite(accuracy) || accuracy <= acc_floor)
            return 0;

        double t = accuracy >= 1 ? 1 : (accuracy - acc_floor) / (1 - acc_floor);
        double share = ExpCurve(t, acc_steepness);
        return double.IsFinite(share) ? Math.Clamp(share, 0, 1) : 0;
    }

    /// <summary>
    /// The normalised exponential both the accuracy curve and its steepness dial are built on:
    /// <c>(e^(k*t) - 1) / (e^k - 1)</c>, exactly 0 at t = 0 and exactly 1 at t = 1 for every k.
    /// k = 0 is the limit and is taken directly rather than through the ratio, which is 0/0 there:
    /// the curve is then the straight line from the floor to perfection.
    /// </summary>
    private static double ExpCurve(double t, double steepness)
        => steepness > 1e-6 ? (Math.Exp(steepness * t) - 1) / (Math.Exp(steepness) - 1) : t;

    /// <summary>
    /// The accuracy SOFT KNEE (backlog 227): a logistic centred on <see cref="acc_knee"/> and
    /// <see cref="acc_knee_width"/> wide, multiplying the timing term. See those two constants
    /// for what the dial does and why it is not simply a steeper exponent.
    /// </summary>
    private static double AccuracyKnee(double accuracy)
    {
        // A width of zero or less MEANS there is no knee, and the factor is then exactly 1.0
        // rather than the step function the logistic degenerates to (see acc_knee_width). Written
        // as a conditional expression rather than an if, so the dead half of a compile-time
        // constant folds away instead of reading as unreachable code.
        return acc_knee_width > 0
            ? 1.0 / (1.0 + Math.Exp(-(accuracy - acc_knee) / acc_knee_width))
            : 1.0;
    }

    /// <summary>
    /// pp for one play. <paramref name="starRating"/> is the play's EFFECTIVE rating
    /// (<see cref="StarsFor"/>), <paramref name="accuracy"/> the stored <c>scores.accuracy</c>,
    /// <paramref name="maxCombo"/> the stored <c>scores.max_combo</c>.
    ///
    /// <para><paramref name="difficultCharacters"/> is the MAP's difficult-character count at the
    /// played rate, stream and judgement arm (<see cref="MapRating.DifficultCharacters"/>, off the
    /// stored matrix), which is what the miss penalty is judged against since v22. 0 means "no such
    /// count", and any miss then zeroes the cleanliness term rather than falling back to the cell
    /// count; on this server a play with no cell never reaches here, because
    /// <see cref="StarsFor"/> leaves it pending.</para>
    ///
    /// <para>Inputs are clamped rather than trusted: misses and combo into <c>[0, notes]</c> (the
    /// theoretical max combo of a typing map IS its note count), the difficult characters to a
    /// finite non-negative, and accuracy into <c>[0, 1]</c>. The result is guaranteed finite and
    /// non-negative.</para>
    ///
    /// <para><paramref name="typos"/> IS NO LONGER READ (v22 deleted the typo term). It stays on
    /// the signature, defaulted, so every call site that passes a count reads unchanged and the two
    /// mirrors keep one shape.</para>
    ///
    /// <para>THERE IS NO RATE ARGUMENT (backlog 265). A rate is priced entirely by the
    /// <paramref name="starRating"/> it is handed, so a caller holding the effective rating holds
    /// the whole price. From v3 to v19 a base-rate Half Time play took an extra multiplier here,
    /// which every path that could see one had to remember to pass; nothing does now, and the
    /// forgetting-to-pass-it failure mode is gone with it.</para>
    ///
    /// <para>Difficulty is rated at the played rate. The separate short-map factor uses
    /// <paramref name="playedDurationSeconds"/>, already divided by that rate. An unknown
    /// duration preserves legacy callers' price; every beatmap-backed caller supplies it.</para>
    /// </summary>
    public static double Compute(
        double starRating,
        int notes,
        double difficultCharacters,
        int misses,
        double accuracy,
        int maxCombo,
        IReadOnlyList<ScoreMod>? mods,
        int typos = 0,
        double playedDurationSeconds = double.PositiveInfinity)
    {
        // No notes describes no play; a zero or non-finite rating prices nothing.
        if (notes <= 0 || !double.IsFinite(starRating) || starRating <= 0)
            return 0;

        misses = Math.Clamp(misses, 0, notes);
        maxCombo = Math.Clamp(maxCombo, 0, notes);
        difficultCharacters = double.IsFinite(difficultCharacters) ? Math.Max(0, difficultCharacters) : 0;
        accuracy = double.IsFinite(accuracy) ? Math.Clamp(accuracy, 0, 1) : 0;

        double difficulty = Math.Pow(starRating, sr_exponent);

        // CLEANLINESS, over the map's DIFFICULT CHARACTERS rather than its cells, with the power on
        // the missed FRACTION. Two special cases are load-bearing rather than defensive: a spotless
        // play must be exactly 1.0 on every map, and a map whose difficulty sits entirely below its
        // own peak has NO difficult characters to spend, so any dropped cell zeroes the term instead
        // of producing 0/0. The clamp is load-bearing too: misses are cells and a map has fewer
        // difficult characters than cells, so the fraction can exceed 1 and the base would run
        // negative, where a fractional power is non-real rather than merely wrong.
        double difficultShare = difficultCharacters > 0 ? misses / difficultCharacters : 0;
        double missBase = misses == 0 ? 1
            : difficultCharacters > 0 ? Math.Max(0, 1 - Math.Pow(difficultShare, count_power))
            : 0;
        double cleanliness = Math.Pow(missBase, miss_exponent);

        // THERE IS NO TYPO TERM since v22: a wrong keypress the player recovered from costs nothing.
        // The count is still derived by CountNotes for the surfaces that display it, and the
        // parameter stays on this signature so those call sites read unchanged.

        // The play's accuracy, curved by the normalised exponential (DEPARTURE 5) and then bent
        // through the SOFT KNEE (see acc_knee). The knee ships at width 0, where AccuracyKnee is
        // exactly 1 and the curve is the whole of the accuracy shape; at a live width it is exactly
        // 0.5 at accuracy == acc_knee and strictly increasing everywhere, so it can respread this
        // axis but never reorder two plays on it. NO CLAMP IS NEEDED HERE and none would bite:
        // accuracy is clamped into [0, 1] above and AccuracyShare pins both ends.
        double timing = AccuracyShare(accuracy) * AccuracyKnee(accuracy);

        // THE COMBO BONUS MULTIPLIES THE PRICE, at a ceiling this map's LENGTH earns it: a straight
        // line through the origin worth combo_bonus_at_200_cells percent at 200 cells, capped, times
        // the share of the map the longest run held, times the kicker when nothing was dropped at
        // all. Because it is a percentage of the product rather than a number of pp beside it, a
        // price zeroed by misses stays zero, and the mod multiplier scales the bonus along with
        // everything else it multiplies. NO CLAMP IS NEEDED ON THE RATIO: maxCombo is already
        // clamped into [0, notes] above, so it is in [0, 1].
        double comboCeiling = Math.Min(combo_bonus_cap, Math.Max(0, notes) / 200.0 * combo_bonus_at_200_cells) / 100.0;
        double comboRatio = (double)maxCombo / notes;
        bool comboPerfect = misses == 0 && maxCombo >= notes;
        double comboBonus = comboCeiling * comboRatio * (comboPerfect ? combo_bonus_perfect : 1);

        // THE BONUS IS INSIDE THE PRODUCT, deliberately, because it is a percentage OF the price and
        // not a number of pp beside it: a mod stack scales it along with the core, and a core zeroed
        // by misses cannot keep a consolation bonus. That is the opposite of v21's placement, which
        // is what the version bump records.
        double core = scale * difficulty * cleanliness * timing * ModMultiplier(mods, notes);
        double pp = core * (1 + comboBonus) * ShortMapMultiplier(playedDurationSeconds);

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
    /// rather than being stamped at a value it would have to disagree with later. Each rate needs
    /// exactly ONE rating since backlog 265 (<c>sr_dt</c> for Double Time, <c>sr_ht</c> for Half
    /// Time); a LITERATE play needs the converted map's rating instead of the plain one; see
    /// <see cref="StarsFor"/>.</item>
    /// </list>
    ///
    /// <para>Callers writing the <c>pp</c> column coalesce with <c>?? 0</c>; callers putting the
    /// value on the wire send it as-is, so a non-null pp on the wire always means "the server
    /// priced this play, and this is the answer".</para>
    /// </summary>
    /// <param name="ranked">The score's stored <c>ranked</c> flag: an unranked play earns nothing.</param>
    /// <param name="ratings">
    /// The map's stored rating matrix (<c>beatmaps.ratings</c>, <see cref="BeatmapRatings"/>), or
    /// null when the column is NULL. Since v22 a price needs the map's DIFFICULT CHARACTERS as well
    /// as its stars, and both travel in one cell, so the matrix replaced the six rating parameters
    /// this method used to take. Null, or a matrix with no cell for this stack, is PENDING.
    /// </param>
    public static (double? Pp, bool Settled) ForScore(
        bool ranked,
        IReadOnlyList<ScoreMod>? mods,
        NoteCounts notes,
        double accuracy,
        int maxCombo,
        BeatmapRatings? ratings,
        double basePlayedDurationSeconds = double.PositiveInfinity)
    {
        if (!ranked)
            return (null, true);

        var stars = StarsFor(mods, ratings);

        if (stars.Stars is not double effective)
            return (null, !stars.Pending);

        // The stored span (044_played_duration.sql) is measured at the BASE rate; move it to the
        // played rate here, the same division the game's PlayedDurationFor does, so every caller
        // passes the map's stored figure and the cut is read at the length the player actually heard.
        double playedDurationSeconds = double.IsFinite(basePlayedDurationSeconds)
            ? basePlayedDurationSeconds / (EligibleRate(mods) ?? 1)
            : basePlayedDurationSeconds;

        return (Compute(effective, notes.Notes, stars.DifficultCharacters, notes.Misses, accuracy, maxCombo, mods, notes.Typos, playedDurationSeconds), true);
    }

    /// <summary>
    /// The play's pp-eligible base rate (1.0 for no rate mod), or null when the stack carries more
    /// than one rate mod or a non-default speed. Mirrors the game's <c>EligibleRate</c>: the server
    /// has no live bindable, so a rate mod read here always sits at its default (which is how a
    /// stored row with no speed_change is read), and any non-default is refused as ineligible.
    /// </summary>
    public static double? EligibleRate(IReadOnlyList<ScoreMod>? mods)
    {
        if (mods == null)
            return 1.0;

        double? rate = null;

        foreach (var mod in mods)
        {
            if (!RateMods.IsRateMod(mod.Acronym))
                continue;

            // A second rate mod is ineligible outright.
            if (rate != null)
                return null;

            if (!RateMods.TryGetRange(mod.Acronym, out var range))
                return null;

            rate = range.Default;
        }

        return rate ?? 1.0;
    }

    /// <summary>
    /// First-to-last playable unit span, in seconds at the played rate. Instrumental breaks within
    /// the mapped lyrics count, while leading audio, seal deadlines and trailing audio do not.
    /// Empty caption-only lines have no playable units and cannot extend the span. Byte-identical
    /// to the game's <c>PerformancePoints.PlayedDurationFor</c>.
    /// </summary>
    public static double PlayedDurationFor(IEnumerable<LyricLine> lines, IReadOnlyList<ScoreMod>? mods)
    {
        double first = double.PositiveInfinity, last = double.NegativeInfinity;
        bool literate = IsLiterate(mods);

        foreach (LyricLine line in lines)
        {
            foreach (TimedUnit unit in line.Units)
            {
                string text = literate ? unit.Text : Typeability.ToDefaultStream(unit.Text);

                if (string.IsNullOrWhiteSpace(text.Replace("&", string.Empty)) || !double.IsFinite(unit.StartTime)
                    || !double.IsFinite(unit.EndTime) || unit.EndTime < unit.StartTime)
                    continue;

                first = Math.Min(first, unit.StartTime);
                last = Math.Max(last, unit.EndTime);
            }
        }

        double rate = EligibleRate(mods) ?? 1;
        return double.IsFinite(first) && double.IsFinite(last) ? Math.Max(0, last - first) / (1000 * rate) : 0;
    }

    /// <summary>
    /// A short-map cut: 60% at zero seconds, 15% at 30, and none from 60 onward.
    /// The quadratic joins the full-length price with zero slope. Unknown legacy durations
    /// preserve the price; malformed durations use the maximum cut rather than evading it.
    /// Byte-identical to the game's <c>PerformancePoints.ShortMapMultiplier</c>.
    /// </summary>
    public static double ShortMapMultiplier(double playedDurationSeconds)
    {
        if (double.IsPositiveInfinity(playedDurationSeconds))
            return 1;

        double duration = double.IsFinite(playedDurationSeconds) ? Math.Max(0, playedDurationSeconds) : 0;
        double remaining = 1 - Math.Clamp(duration / 60, 0, 1);
        return 1 - 0.60 * remaining * remaining;
    }
}
