# type!beat performance points (pp): canonical spec for backlog task 61

> Copied verbatim from the author's spec (backlog task 61) and committed here as the formula's
> canonical home. The implementation lives in `src/Typebeat.Web/Scoring/PerformancePoints.cs`
> (per-play formula, mod multipliers, rate eligibility) and
> `src/Typebeat.Web/Scoring/PpRanking.cs` (the read-time aggregation). Change this file and those
> two together, and bump `PerformancePoints.VERSION` so `PpBackfill` recomputes stored per-score
> values on the next boot.

Replaces total score as the main global ranking. The score-farming leaderboard survives as a
separate tab on the rankings page. Website ships first; in-game pp surfacing (results screen,
profile) is a separate follow-up with wire + client changes.

## The formula

Per play:

```
pp = C · SR_eff^2.30
       · max(0, 1 − (miss/difficult)^1.2)^13.5134       # cleanliness, over DIFFICULT characters
       · accShare(acc) · knee(acc)                      # accuracy (timing quality)
       · modMult                                        # NOT for DT/HT; rate lives in SR_eff only
       · (1 + comboBonus)                               # combo bonus, a FRACTION of the price

accShare(acc) = expCurve((acc − 0.5) / (1 − 0.5))       # 0 at or below the floor, 1 at a perfect play
expCurve(t)   = (e^(1.75·t) − 1) / (e^1.75 − 1)         # or t itself at a steepness of 0
knee(acc)     = 1                                       # acc_knee_width is 0: the knee is OFF
comboBonus    = min(10, cells/200 · 1)/100 · maxcombo/cells · (1.5 on a spotless full combo)

C = 9.0     # global scale constant, does not affect ranking order
```

Factor by factor, in descending priority:

* **SR_eff^2.30**: difficulty is the primary driver. SR_eff is the map's star rating
  **recomputed at the play's clock rate** for DT/HT, **on the map the conversion mods produced**
  for LT, and **in the play's JUDGEMENT ARM** for EZ/HR (see mods below), not the base SR. The three
  are orthogonal, so the server stores their cross product: three arms times two streams times three
  rates is eighteen readings, `beatmaps.ratings` (034_ratings_matrix.sql). The client recomputes the
  one it needs from the beatmap it has loaded.
* **`difficult`**: the map's DIFFICULT CHARACTERS at that same (arm, stream, rate), i.e.
  `LyricDifficulty.ModelResult.DifficultCharacters`, every cell weighted by how close its own bin
  sits to the map's peak. It is a property of the MAP, not of the play, and it travels in the same
  stored cell as the rating because a price needs both. A play whose cell is not stored yet is
  PENDING, exactly as an unfilled `sr_dt` has been since backlog 90.
* **cleanliness^13.5134**: cells the play did not type right. Since the backlog-213 amendment that
  is `miss + good`, i.e. a cell nobody finished PLUS one finished with the wrong character and never
  corrected; before it, `miss` alone. Since the v22 amendment the count is measured as a FRACTION of
  the map's DIFFICULT CHARACTERS rather than as a raw count over the plain note count, so the same
  miss RATE costs the same share of the core price on every map, where the old shape made the cliff
  move with map size. `count_power` (1.2) is the power the fraction carries and `miss_exponent`
  (13.5134) the power the base carries; the second is `ln(2)/ln(1/0.95)`, the exponent that puts
  HALF the core price at a 5% miss rate AT a count power of 1. At the live 1.2 the same 5% keeps
  0.686, 10% keeps 0.414, 25% keeps 0.059 and half the difficult characters keeps 0.00044. The base
  reaches zero only when EVERY difficult character was missed; the `max(0, ...)` is still
  load-bearing, because misses are cells and a map has fewer difficult characters than cells, so the
  fraction really can exceed 1 and a fractional power on a negative base is not a real number. A map
  with NO difficult characters cannot absorb a miss at all: any dropped cell zeroes the term rather
  than producing 0/0.
* **THERE IS NO TYPO TERM** since the v22 amendment. A wrong keypress the play RECOVERED from costs
  exactly nothing: the price is the rating, cleanliness, timing, the mods and the combo bonus alone.
  The COUNT is still derived (`typos = max(0, combo_break - good)`, backlog 213) and still travels on
  the wire, because the surfaces that display a typo count read it and because an UNCORRECTED typo is
  still folded into the miss count and priced as harshly as ever. The parameter stays on both
  mirrors' `Compute` signatures, defaulted, so every call site reads unchanged.
* **`count_power`** is where a rebalance of the miss penalty is made, rather than `miss_exponent`:
  at 1 the calibrated half-point is a plain miss RATE, and above it the curve buys a grace region at
  low miss rates and falls more steeply near the top. It stands at 1.2.
* **Length**: NOT A FACTOR HERE, since the backlog-152 amendment. pp carried the standard osu log
  bonus `max(0.1, 1 + 0.50·log10(notes/100))` through v15; length is now priced by the STAR RATING
  instead. Since backlog 273 `LyricDifficulty` has no separate length term at all: length counts
  only through the characters it adds to the envelope's difficulty sum, so pp still sees a long
  map only through `SR_eff`, a few percent where the old term paid up to 1.70x. Two length terms
  would double count, so pp keeps none. `notes` itself is
  still load-bearing: both penalty terms, the combo ratio and FL all read it.
* **accShare(acc)**: a NORMALISED EXPONENTIAL above a FLOOR since the v23 amendment, replacing
  `acc^accuracy_exponent`. Accuracy is rescaled onto `[acc_floor, 1]` and run through
  `(e^(k·t) − 1)/(e^k − 1)`, which pins both ends for every steepness: the floor is exactly where
  the price reaches zero and a perfect play is exactly 1, so `acc_steepness` (1.75) moves the SHAPE
  without moving either end. `acc_floor` (0.5) is the hard end: an accuracy at or below it earns
  nothing at all, which the power curve only ever approached. In type!beat real accuracies live at
  55–93%, not 97–100%, which is why the shaping is done on the bottom of the range rather than by a
  steeper exponent that would tax the top with it. The curve is strictly increasing above the floor,
  so it can respread the accuracy axis and never reorder two plays on it.
* **knee(acc)**: the backlog-227 SOFT KNEE, a logistic centred on `acc_knee` and `acc_knee_width`
  wide, still multiplying the timing term and OFF at the live dials. A width of 0 or less means there
  is no knee at all and the factor is exactly 1.0, which is what both mirrors compute today: the
  exponential above already does the shaping the knee was added for. The position is written as 0 as
  well, which is inert either way. Both constants are kept declared so a retune is a value change
  rather than a code change.
* **(1 + comboBonus)**: a PERCENTAGE OF THE PRICE since the v22 amendment, where backlog 270 had
  made it a number of pp added beside one. The ceiling is a straight line through the origin worth
  `combo_bonus_at_200_cells` (1.0) percent at 200 cells, capped at `combo_bonus_cap` (10.0) percent
  from 2000 cells up, times the share of the map the longest run held, times `combo_bonus_perfect`
  (1.5) when the play was SPOTLESS and held the whole map in one run. So a 2000-cell map pays +15%
  for 2000/2000 and just under +10% for 1999/2000, and a 400-cell map pays at most +3%.
  * WHY A PERCENTAGE. As an amount the bonus was worth the same number of pp on a play that earned
    150 and on one the miss term had zeroed, so a give-up run that happened to hold a short streak
    still collected something. As a percentage a price zeroed by misses stays zero, and the mod
    multiplier scales the bonus along with everything else it multiplies, which is the opposite of
    v21's placement and is what the version bump records.
  * WHY IT SCALES WITH LENGTH. A flat pp amount is worth far more on a short map than a long one
    relative to what the map is worth, and a full combo on a two-minute map is a different
    achievement from a full combo on eight bars. The ceiling grows with the note count instead, on a
    line through the origin, so the reward is proportional to what was held together.
  * IT IS DELIBERATELY NOT GATED BY ACCURACY, and the consequence is recorded rather than
    overlooked. Since backlog 199 a badly-timed hit (the right character struck outside the
    outermost Meh window) EXTENDS the run rather than breaking it, so combo does not break on an
    off-time press at all and a full-combo run at 69% accuracy collects the whole bonus.
  * IT STILL EARNS ITS PLACE, for the reason it always did: combo can break without a miss (a
    wrong keypress the player then corrects, or a word given up on with the space-skip setting:
    both break the run and neither is a miss), and it distinguishes spread-out misses from one
    choke that dropped several.
  * `combo_bonus_slope` (12.5) and `combo_bonus_zero` (1.0), the v21 pair, are kept DECLARED in both
    mirrors and read by nothing. They are the record of what v21 priced, and they are what lets the
    retune tool tell a mirror one generation behind from a mirror that is simply broken.

**Definitions (pinned to the score row):**

* `acc` is standard osu hit accuracy, over the three quality tiers a cell can land in:
  `(300·great + 100·ok + 50·meh + 0·good) / (300·notes)`. This is the stored `accuracy` column
  for a completed play. (`good` is the uncorrected typo. It was re-weighted to the `meh` value of
  50 from backlog 124 until the backlog-213 amendment, which takes it to a miss's 0; the cell's
  MAXIMUM stays a `great`, so the denominator does not move and the re-weight is paid in full.
  Backlog
  133 made the ladder four tiers deep, `perfect` 300 and `great` 200, and backlog 147 reverted it;
  a row stored while that shipped is read on the old weights, keyed off its own
  `maximum_statistics`. See `ScoringContract.JudgedUnderTheFourthTier`.)
* `notes = perfect + great + ok + meh + good + miss` from `statistics`. **`ignore_hit` is
  excluded**; the line containers would otherwise inflate `notes` and dilute every factor.
  `perfect` is on the list only for those four-tier rows, and no play judged today can produce
  one. **The backlog-213 amendment does NOT change this**: `good` stays in `notes`, because the
  cell is one cell of the map however it was typed.
* `miss = miss + good` from `statistics`, the cleanliness term's count, since the backlog-213
  amendment. Read straight off the `miss` key before it.
* `typos = max(0, combo_break - good)` from `statistics`, derived exactly as it always was and
  PRICED BY NOTHING since the v22 amendment. Read straight off `combo_break` before backlog 213. The
  clamp stays, because the two counts arrive off the wire independently and a surface that displays
  a negative typo count is still wrong.
* `difficult` is the map's DIFFICULT-CHARACTER count at the play's (arm, stream, rate), read off
  `beatmaps.ratings` server-side and computed from the loaded beatmap client-side. It is NOT derived
  from the score row and never can be: it is a property of the map. 0 means "no such reading", and
  any miss then zeroes the cleanliness term rather than falling back to the cell count; on the server
  that state is unreachable, because a play whose cell is missing is left PENDING instead of priced.
* `maxcombo` is the stored `max_combo`; the theoretical max equals `notes` for a typing map.

## Eligibility

pp is computed **only for ranked scores on ranked maps**. That inherits the existing gates for
free: fails, unranked-mod plays (RX/WU/WD/Mashing) and out-of-bounds submissions are already
stored `ranked = false` and therefore earn no pp.

## Mods

* **DT / HT**: rate is priced **exclusively through SR_eff** (SR recomputed at the play's clock
  rate); there is NO flat DT/HT multiplier in modMult, so nothing double-counts. That sentence is
  **literally true again since the 2026-09-06 amendment below**, which deletes the Half Time mirror
  penalty the 2026-08-07 amendment added: from v3 to v19 Half Time was the one rate priced by
  something other than its rating, and it is not any more. DECIDED:
  only the **base rates** (DT/NC 1.5x, HT/DC 0.75x) are pp-eligible. A custom rate makes the play
  **pp-ineligible only**: it still ranks on the score leaderboards exactly as today (the
  variable-rate ranking feature is preserved, no retroactive unranking), it just earns 0 pp.
  KNOWN AND UNPRICED since 2026-08-13 (backlog 150): the rate mods now scale the JUDGEMENT WINDOWS
  by the clock rate, so a rate play's real-time tolerance per character is the same as an unmodded
  one's, where a Double Time play used to be judged on windows 1/1.5 as wide in real terms. Nothing
  in pp moved with it: `sr_dt` is computed at rate from the map's own timing and never saw the
  windows, and modMult carries no rate term by design. So a DT play is now materially easier for the
  same pp. Measured against this file's own yardstick for what a window scale is worth, Easy's ×0.75
  for a ×2 widening (i.e. a multiplier of `scale^-0.415`), the honest DT term would be about 0.85,
  so DT is overpaid by roughly 18% and HT (windows tightened to 0.75x) underpaid by about 11%. Left
  alone deliberately: it is a rebalance, not a bug fix, and it would reprice every stored rate row.
  Implementation consequence: the server only ever needs SR at three rates (1.0 / 1.5 / 0.75)
  per cell stream and per judgement arm; store the whole eighteen-cell matrix per beatmap at ingest
  (`beatmaps.ratings`, 034_ratings_matrix.sql) and backfill via the existing pace VERSION-bump
  mechanism. The six legacy columns (`difficulty_rating`, `sr_dt`, `sr_ht`, `sr_literate`,
  `sr_literate_dt`, `sr_literate_ht`) are the matrix's ARM-NONE stars and stay written, for the
  pages, the client's own beatmap lookup and the search filters, none of which know about an arm. No
  on-the-fly rate-SR math.
* **LT** (Literate): priced **exclusively through SR_eff**, exactly as a rate is, and with no flat
  multiplier in modMult for the same reason there is none for DT/HT. Literate is a **conversion**
  mod: it makes every supported punctuation mark a typed cell of its own, so it changes the map's
  cell count, its pace and its rating. It used to carry a flat × 1.06 on top of the UNCONVERTED
  map's rating; see the 2026-08-12 Literate amendment for why that became a double count, and for
  the storage consequence (Literate is orthogonal to rate, so the server stores **six** ratings per
  difficulty, not three).
* **FL** (flashlight): × `max(1.0, 1 + 0.02 + 0.06·log10(notes/100))`. Much smaller than osu's
  flashlight bonus, but grows with song length, so it pays off on long maps. The `max` clamp is
  required: unclamped, the raw term dips **below 1.0 under ~46 notes**, which would punish FL on
  short maps rather than "barely move".
* **EZ** (Easy): a JUDGEMENT ARM of SR_eff, plus a flat × 0.85. The mod DOUBLES every judgement
  window and shelters the whole WORD rather than the syllable, and since the difficulty rework the
  star rating prices the intervals a press may land in, so the play is rated against the matrix's
  `ez` cell. "No rating input can see it", which every generation through v21 said here, stopped
  being true then. The flat term is what the PP Sandbox charges for what is LEFT after the arm, at
  its live dial. Its score multiplier is osu's 0.5x for a difficulty reduction, the same value No
  Fail carries; the pp value is separate.
* **HR** (Hard Rock): a JUDGEMENT ARM of SR_eff, and flat × 1.00, i.e. NEUTRAL in modMult. Since
  backlog 264 the mod judges under the classic per-character point-target rule at the same 1.0x
  windows as an unmodded play (a stored replay from before that change still resolves under the
  halved ladder, carried by the replay's own CONFIG frame). Those point targets are exactly what the
  rework's rhythm arm reads, so the play is rated against the matrix's `hr` cell and the flat term is
  left at 1.0 rather than paying for the same change twice, which is the double count this file
  exists to forbid. It used to be a flat × 1.25 against Easy's 0.75. Its score multiplier is a
  separate 1.10x, chosen so the fattest reachable ranked stack stays under the server's 2.0 stack
  cap.
* **RE** (Recite): × `1 + 2.0·(FL(notes) − 1)`, i.e. a SCALE on Flashlight's bonus rather than a
  flat term (the v23 amendment; it was a flat × 1.07 at v21). The lyric is hidden until the line is
  sung, which is what Flashlight charges for (the map is typed from memory rather than read ahead),
  and that cost grows with how much map there is to hold in the head, so the mod is length-scaled for
  the same reason Flashlight is. `recite_multiplier` (2.0) is the scale: 0 is free, 1 is exactly what
  Flashlight is worth, and the live 2.0 pays twice it. Under Flashlight's own floor (~46 notes) there
  is no bonus to scale, so Recite is worth exactly nothing there too. The two mods still MULTIPLY
  when both are selected, exactly as every other pair does.
* **FC** (Fletcher): flat × 1.02 (backlog 270). The caret is PINNED back to the line the song is
  on, which since backlog 208 is the harder half of the pair (the unpinned caret became the
  default for every play and the mod reversed). Flat for the same reason again, and NOT to be
  confused with **FT** below, which is the retired acronym for the opposite behaviour.
* **FT** (the retired Fletcher): × 0.90, for stored rows only. It is a `ModType.System` mod
  nobody can select, kept resolvable so the rows that carry it keep the price and the era they
  were played under.
* **RH** (Rhythmic): **no longer priced** (backlog 270). It was a flat × 1.10 from backlog 135.
  The mod judged a play on the MILLISECOND window ladder (each character against its own target
  time) instead of the character-distance one backlog 133 had made the default, which was the
  tighter pair on any map slower than 10 characters per second; backlog 147 made the millisecond
  ladder the default again and removed the mod from the client, so no NEW play can carry the
  acronym and exactly one stored row still does. That row reprices 10% down at v21, which is a
  VERSION bump doing what a VERSION bump is for. The argument that used to keep the arm alive
  cited `ModMultiplier.TotalScoreCeiling`, which is the SCORE-side table in a different file
  entirely: it still prices RH at 1.10 and is untouched, so the row's own stored total stays
  under its ceiling and stays ranked.
* **NF** (No Fail): × 0.90, osu's pricing. DECIDED: NF cannot be free for pp, since it converts
  a would-be fail (which earns nothing) into a completed play, and it protects runs the miss
  penalty only partially catches. The 0.5x score multiplier stays score-side only; mirroring it
  in pp would double-punish on top of the miss term.
* **SD / MU**: × 1.0 (no effect, matching their score multipliers).

```
FL(notes) = max(1.0, 1 + 0.02 + 0.06·log10(notes/100))

modMult = (FL ? FL(notes)              : 1)
        · (RE ? 1 + 2.0·(FL(notes)−1)  : 1)
        · (EZ ? 0.85                   : 1)
        · (HR ? 1.00                   : 1)     # neutral: the judgement ARM prices it
        · (FC ? 1.02                   : 1)
        · (FT ? 0.90                   : 1)
        · (NF ? 0.90                   : 1)
```

## Aggregation

DECIDED: osu semantics, per-SET dedup with a weighted sum over **all** deduped plays (not a
hard top-10 cutoff):

1. For each ranked **set** (the song, not the single difficulty), keep only the player's
   **best-pp play** anywhere in it (without this, replays of one hard map, or a clear of the
   Easy, Normal and Insane of one song, could fill the entire top list).
2. Sort those by pp descending and sum with decay:

```
total_pp = Σ pp_i · decay^i     (best play i = 0, over ALL deduped plays)
decay = 0.92, raised from the opening 0.85 (backlog 293), toward osu's 0.95 as the pool grows
```

With decay 0.92 the tail fades without ever hitting a cliff (the 10th play carries ~43% weight,
the 20th ~19%, the 50th ~1.5%), so a deep pool of clears counts while the best plays stay
dominant, and there is still no point where the next-best play contributes exactly nothing.

The decay opened low (0.85, where the 10th play carried ~20% and the 20th ~3.9%) to keep the
top plays dominant while the map count was small, with the standing instruction to raise it as
there were more maps for depth to matter; backlog 293 took that step to 0.92. Only totals move
(everyone's rises, a deeper tail counting for more): per-play pp is untouched, so `VERSION`
stays put and no stored row reprices. `total_pp` is **computed on read**, not stored, so this
was, and the next bump stays, a one-line config change with no migration or recompute job.

## What the system values

In order: clearing **harder** maps, and clearing them **cleanly**. Difficulty sets the ceiling
of a play and misses decide how much of that ceiling you actually keep. Length gives sustained hard
play its reward too, but through the star rating rather than here (backlog 152), where it is a soft
signal worth a flat 0.12 stars per decade of cells rather than a multiplier on the whole play.
Accuracy is a gentle secondary signal, because in a
typing game raw accuracy is already hard to push and largely tracks the misses. Combo is not a
signal at all since backlog 270: it is a BONUS added to the finished play rather than a factor
of it, so holding a long run adds pp and breaking one never takes any away. The per-set
dedup plus weighted top-N then makes your rank the sum of your best performances, not a reward
for volume, so grinding easy maps (or one hard map, or every difficulty of one song) stops
mattering once there are enough songs.
In short: it rewards the player who clears the hardest maps with the fewest misses, which is
the opposite of what cumulative score rewards today.

## Decision log (2026-07-28)

* DT/HT base-rate reward comes from **SR_eff only**; the earlier "1.5x pp weighting" phrasing
  described intent, not an extra multiplier. No flat rate multiplier exists in modMult. (Half Time
  carried a per-map MIRROR multiplier outside modMult from the 2026-08-07 amendment until the
  2026-09-06 one deleted it; this line is the rule again, without an exception.)
* Custom DT/HT rates are **pp-ineligible only**; score-leaderboard ranking at every rate is
  preserved, nothing retroactively unranked.
* Aggregation: **best play per set, weighted sum over all** with decay 0.92; no hard top-10
  truncation. (Amended by backlog 162: the dedup unit was the beatmap until then, so clearing
  three difficulties of one song banked three weighted entries. Amended by backlog 293: decay
  raised 0.85 to 0.92 as the ranked pool grew, moving totals only; per-play pp untouched.)
* **NF priced at × 0.90** for pp (osu's value); omission would have made it a free mod.
* `notes` excludes `ignore_hit`; the FL factor carries a floor clamp (so did the length factor,
  until the backlog-152 amendment deleted it).
* pp only from ranked scores on ranked maps; fails and unranked mods excluded by inheritance.
* Website rankings swap + score tab first; in-game pp display is a separate follow-up task.

> **A note on the name, before the amendments.** The second penalty term is spelled `typo_*` above
> and in the code (`typo_exponent`, `typo_shape`, `typos` in the formula), but it was called
> `mistype_*` from backlog 72 until backlog 141 renamed it. Every amendment below is dated and is a
> record of what a past version did **under the name it had at the time**, so they all still say
> "mistype" / "mistyping" / `mistype_exponent`. Those are the same term as today's `typos^4`, and
> nothing about the term's value, shape or the `combo_break` statistics key that feeds it changed
> with the rename. The amendments are deliberately not rewritten: doing so would falsify the record.

## Amendment (2026-08-03): the cleanliness term prices MISTYPES (backlog 72)

> **Partly superseded by the 2026-08-07 amendment below.** Mistypes are still priced, and still
> excluded from `notes`, but they no longer live in the cleanliness fraction and the `VERSION`
> argument at the end of this section no longer holds. Read the 2026-08-07 amendment for the
> arithmetic in force.

Wrong keypresses used to be invisible in a submitted score. In the default (strict) input mode the
client rejected a wrong key without raising any judgement, so `statistics` carried only
great/ok/meh/miss, the server recomputed a spotless accuracy, and the only surviving trace was a
broken `max_combo`. Backlog 72 persists them as their own statistics key, and pp is where they are
priced. The cleanliness factor becomes:

```
(1 − (miss + mistypes)/(notes + mistypes))^7.5     # cleanliness (misses AND wrong keypresses)
```

Nothing else in the formula changes.

**Definitions added:**

* `mistypes` is the `combo_break` key of `statistics`: one per wrong KEYPRESS (not per cell, and
  the same in both input modes). It is `HitResult.ComboBreak`, the base ruleset's combo-only,
  non-accuracy-affecting result, so the server's `ScoringContract` already ignores it everywhere:
  accuracy, completion and rank keep their exact previous meanings and an SS is still reachable
  after a stumble.
* `notes` **still** excludes mistypes; it remains `great + ok + meh + miss`, the map's cell count.
  This is deliberate. Feeding keypresses into `notes` would grow the LENGTH bonus and shrink the
  COMBO denominator, so mashing would partly pay for itself. Only the cleanliness term sees them.

**Why both sides of the fraction.** `miss ≤ notes` always, so adding the same count to numerator and
denominator can only move the ratio towards 1, never past it: the base stays in `[0, 1]` for any
mistype count, however absurd, and the term can never go negative (which, under a fractional
exponent, would not merely be wrong but non-real).

**Why no `VERSION` bump.** A bump forces `PpBackfill` to reprice every stored row at startup, and no
stored row can move: no client emitted `combo_break` before this change, so `CountNotes` reads 0 for
all of them and the amended term is algebraically identical to the original at 0. The bump would buy
a full sweep that rewrites every row with the value it already holds. Bump it the moment a change
values any stored row differently.

**History.** Old plays simply lack the stat; there is no backfill, because the presses were never
recorded. Every surface renders the mistype count only for a play that carries it (absence is not
zero). Note the one place absence is recoverable: re-simulating an old REPLAY produces the count,
since the wrong keys were always in the input stream.

## Amendment (2026-08-03): pp reaches the game client (backlog 74, 75, 76)

The "separate follow-up" promised above has landed. Nothing in the formula or the aggregation
changed; what changed is who can see the numbers.

* The game got its own mirror of the per-play formula
  (`typebeat.Game.Rulesets.TypeBeat/Scoring/PerformancePoints.cs`), pinned against this one by
  `tests/Typebeat.WireCompat/PerformancePointsParityTest.cs`.
* The submit response carries the play's pp, under a one-sentence contract: a non-null value means
  the server ran the formula and that is the answer (0 included); null means it did not run it.
* **`statistics.global_rank` on the client wire is now the pp RANK, not the cumulative-score rank.**
  The client has exactly one rank slot and every surface reading it pairs it with pp, osu defines
  the field that way, and the website has led with pp since task 61. The cumulative metric keeps its
  VALUE on the wire as `ranked_score`; only its rank has no client slot, and that board lives on the
  website. `statistics.pp` is the read-time `PpRanking` total, always a number (0 for a player with
  no pp-earning play, never null), while `global_rank` stays null while unranked.

## Amendment (2026-08-07): misses and mistypes are priced SEPARATELY (backlog 89)

> **Numbers partly superseded by the backlog-95 amendment below.** The SHAPE of the formula is
> exactly as this section describes it, and the reasoning for two separate terms still holds, but
> both exponents have since risen (8.5 to 10, 3.5 to 6), so the worked values in the table below are
> the values as of this amendment, not the values in force. Read the backlog-95 amendment for those.

The 2026-08-03 amendment folded wrong keypresses into the cleanliness fraction. That worked, but it
coupled the two penalties: because a mistype was added to both the numerator and the denominator of
the MISS ratio, a player carrying a heavy mistype count was charged less per dropped cell than a
clean player was. The two failures are different failures, so they now get one term each:

```
BEFORE:  (1 − (miss + mistypes)/(notes + mistypes))^7.5

AFTER:   (1 − miss/notes)^8.5  ·  (1 − mistypes/(notes + mistypes))^3.5
```

Nothing else in the formula changes: SR, length, accuracy, combo, the mod multipliers, eligibility
and the aggregation are all untouched.

* **Cleanliness reverts to misses over plain `notes`**, at the steeper exponent **8.5**. A dropped
  cell is the harshest thing a play can carry and this is where that is said.
* **Mistypes move entirely into their own factor** at exponent **3.5**. They are NOT priced in both
  terms; they appear nowhere in the cleanliness term any more.

**The net effect is that plays carrying mistypes are worth MORE than before, not less.** That is
deliberate and was decided with the numbers in front of the decision. Removing mistypes from both
sides of the miss ratio softens that term by more than the 7.5-to-8.5 rise tightens it:

| play | old cleanliness | new cleanliness x mistyping | change |
|------|-----------------|------------------------------|--------|
| `notes=500, miss=60, mistype=80` | `0.759^7.5 = 0.126` | `0.880^8.5 x 0.862^3.5 = 0.201` | +59% |
| `notes=500, miss=10, mistype=20` | `0.942^7.5 = 0.640` | `0.980^8.5 x 0.962^3.5 = 0.734` | +15% |

A play with NO mistypes moves the other way, and only because of the exponent: its mistyping term is
exactly 1.0, so it is priced by `(1 − miss/notes)^8.5` alone, strictly below the old `^7.5` for any
non-zero miss count and identical to it at zero misses.

**Why the mistype term keeps mistypes in its denominator** (unchanged reasoning from the 2026-08-03
amendment, and the reason the new term is not simply the miss term with another exponent): misses
are bounded by `notes`, but keypresses are UNBOUNDED. `1 − mistypes/notes` would go negative, and a
fractional exponent on a negative base is not merely wrong but non-real. Keeping the count on both
sides bounds the base to `[0, 1]` for any mistype count, however absurd, and decays it towards 0.
`notes` still excludes mistypes, for the same reason as before: feeding keypresses into it would
grow the LENGTH bonus and shrink the COMBO denominator, so mashing would partly pay for itself.

**`VERSION` bumps to 2, and this time it must.** The 2026-08-03 amendment did not bump, on the proof
that no stored row could carry a non-zero `combo_break` and so no stored value could move. That
proof does not survive this change: raising the miss exponent reprices every stored row carrying
even one miss, mistypes or not. `PpBackfill` therefore repasses every `scores` row at the next boot;
no migration is needed, since migration 020 already created the `pp_version` column.

## Amendment (2026-08-07): Half Time carries a mirror penalty (backlog 90)

> **SUPERSEDED by the 2026-09-06 amendment at the end of this file (backlog 265), which DELETES
> the mirror multiplier outright.** Nothing in this section is in force: there is no `m`, no
> `half_time_buff_clamp`, and no `rateMult` in the formula. Half Time is priced through `sr_ht`
> alone, exactly as Double Time is priced through `sr_dt` alone, and the asymmetry this section
> set out to close is accepted rather than corrected. Read it for the argument, which is still
> the honest statement of what a down-rate is emergently worth, and for the record of what every
> stored HT row was priced at from v3 to v19.
>
> **Figures were already superseded once before that.** The `D`, `H` and percentage figures below
> are all at `sr_exponent = 2.70`, which moved to 2.00 in v11. On the same fixture spread at 2.00:
> `D = 2.109`, `H = 0.655`, `m = 0.723`, and Half Time's total factor `0.474` against the `0.655`
> it carried without the mirror, which is the `0.655` in force today.

Rate has always been priced **exclusively through `SR_eff`**, and it still is for Double Time. That
is not a neutral choice for the down-rate, though, and the numbers say so. Because pp scales as
`SR^2.70`, each base rate already carries an emergent factor relative to the same play at 1.00x:

```
D = (sr_dt / sr_base)^2.70      # what Double Time is already worth on this map
H = (sr_ht / sr_base)^2.70      # what Half Time already costs on this map
```

On a typical map D is much further above 1 than H is below it. On the parity fixture's spread
(`sr_base = 4.2`, `sr_dt = 6.1`, `sr_ht = 3.4`), `D = 2.739` and `H = 0.565`: speeding up pays
**+174%** while slowing down costs only **-43%**. Half Time was therefore the cheap way to keep a
hard map's difficulty term while typing at a comfortable pace.

A base-rate Half Time play now takes one extra multiplier on top of its `sr_ht` rating:

```
m_mirror = 1 / (D · H)

m = 0.70          if m_mirror > 1     # the mirror would BUFF Half Time: flat 30% cut instead
m = m_mirror      otherwise           # the mirror is a nerf: use it exactly
```

With `m = m_mirror`, Half Time's **total** rate factor is `H · 1/(D·H) = 1/D`, exactly the
reciprocal of Double Time's, computed per map rather than guessed at. On the fixture spread above,
`m = 0.646` and HT's total factor becomes `0.365`, i.e. **-63%** where it used to be -43%.

**The guard is load-bearing, not defensive.** The mirror is a buff exactly when `1/D > H`, i.e.
`D·H < 1`, i.e. `sr_dt · sr_ht < sr_base²`: a map whose SR curve is concave in log-rate, where
slowing down helps far more than speeding up hurts. That is precisely the map an unguarded mirror
would **reward** for using Half Time. Worked example, `sr_base = 4.2`, `sr_dt = 4.5`, `sr_ht = 2.0`:
`D = 1.205`, `H = 0.135`, so `m_mirror = 6.15` and the total factor would jump from 0.135 to 0.830, a
six-fold buff. Clamped, it is `0.70 · 0.135 = 0.094`, still a nerf.

**It is not `min(m_mirror, 0.70)`.** A mirror multiplier of 0.90 is a mild, correct nerf and is used
as is; taking a minimum would deepen every mild nerf into a flat 30% cut and throw away the per-map
symmetry the term exists for. The clamp applies only on the wrong side of 1.0.

**Nothing else moves.** Double Time, Nightcore and no-mod plays are multiplied by exactly 1.0.
`modMult` still carries no rate term at all, which is deliberate: it takes only the mods and a note
count, and this needs all three star ratings. Custom rates stay pp-ineligible and never reach the
term. Eligibility, aggregation and every other factor are untouched.

**A new data dependency.** Pricing a Half Time play now needs `sr_dt` as well as `sr_ht`. A map with
`sr_ht` stored but `sr_dt` still null is `Pending`, exactly as a map with neither is: the row is left
stale for `PpBackfill` to retry on a later boot rather than priced at a value the next sweep would
have to disagree with.

**`VERSION` bumps to 3.** Every stored Half Time row is now worth less than the value beside it, so
the bump is mandatory. `PpBackfill` repasses every `scores` row at the next boot, reading only
columns; no migration is needed.

## Amendment (2026-08-07): both penalty exponents rise (backlog 95)

> **Numbers superseded by the 2026-08-08 amendment below.** Both exponents are still 10 and 6, and
> every word about why the two terms are separate still holds, but each penalty RATIO is now
> SQUARED inside its own term. That softens both worked values below by far more than this
> amendment tightened them, so the table here is the table as of this amendment, not the values in
> force. Read the 2026-08-08 amendment for those.

The backlog-89 amendment above gave misses and mistypes a term each, which was the right shape but
left both terms too soft. Both exponents rise, and nothing else in the formula moves:

```
BEFORE:  (1 − miss/notes)^8.5  ·  (1 − mistypes/(notes + mistypes))^3.5

AFTER:   (1 − miss/notes)^10   ·  (1 − mistypes/(notes + mistypes))^6
```

SR, length, accuracy, combo, the mod multipliers, the Half Time mirror multiplier of the amendment
above, eligibility and the aggregation are all untouched. So is the SHAPE of both terms, including
the mistype count sitting on both sides of its own fraction, for the reason the backlog-89 amendment
gives: keypresses are unbounded, and a fractional exponent on a negative base is non-real.

**Read this together with backlog 89, not on its own.** Splitting the terms apart SOFTENED sloppy
plays considerably, which was deliberate and was signed off with the numbers in view. This change
more than takes that back. Against the pre-split baseline:

| play | before 89 (`^7.5` combined) | after 89 (`^8.5`, `^3.5`) | now (`^10`, `^6`) |
|------|-----------------------------|---------------------------|-------------------|
| `notes=500, miss=60, mistype=80` | `0.125946` | `0.200678` | `0.114309` |
| `notes=500, miss=10, mistype=20` | `0.640391` | `0.734184` | `0.645745` |

So the net of the two changes together is: **a near-clean play is roughly where it always was**
(0.640391 to 0.645745, +0.8%), while **a sloppy play is hit harder than it has ever been**
(0.125946 to 0.114309, below even the pre-split value). A reader looking only at this diff would see
-43% and -12% and miss that the first of those is a return past a starting point rather than a raw
cut.

**Mistypes stay cheaper than misses, but by much less.** 6 against 10 still says what backlog 89
wanted it to say, that a stumble you recover from is not the same failure as never typing the cell
at all. It no longer says "much gentler", though, and the prose above has been softened
accordingly: 80 mistypes on a 500-note map now cost 59% of a play's pp where they cost 41% of it
before (the term goes `0.594836` to `0.410442`).

**Nothing with a zero count moves at all.** Both bases are exactly 1.0 at a count of zero, and 1.0
raised to any exponent is exactly 1.0, so a play with no misses AND no mistypes is priced
BIT-identically before and after. That is the cheapest check that a change here is confined to the
two terms, and it is pinned by a test in each repo.

**`VERSION` bumps to 4, and it must.** Every stored row carrying even one miss or one mistype is
now worth something different, which is the whole test for a bump. `PpBackfill` repasses every
`scores` row at the next boot, reading only columns; no migration is needed.

## Amendment (2026-08-08): both penalty RATIOS are squared (backlog 96)

> SUPERSEDED by the backlog-97 amendment below. Squaring the RATIO was a misreading of the intent:
> it made both penalties WEAKER, where what was meant was the raw COUNT squared, which makes them
> stronger. The exponents 10 and 6 this amendment left in place are the ones 97 keeps.

Backlog 89 gave misses and mistypes a term each, and backlog 95 then raised both exponents. This
change leaves the exponents exactly where 95 put them, 10 and 6, and squares the RATIO inside
each term instead.

**This is a large softening, and it runs opposite to backlog 89 and 95. That is intended.**
Squaring a value that already sits in `[0, 1]` makes it smaller, so `1 - r^2` is larger than `1 -
r` and both penalties shrink. Both ratios are still bounded by `[0, 1]` (squaring cannot leave
that interval), so both terms are still bounded by `[0, 1]` for any input, and both are still
exactly `1.0` at a count of zero.

**Read the table against the pre-89 baseline, not against the row before it.** Backlog 89
softened sloppy plays, backlog 95 took that back with interest, and this change goes past both.
The sloppy play lands at about six times what 95 left it at, and at about six times its pre-89
value as well; the near-clean play is now within 1.3% of a spotless one where it used to keep
64%. Misses and mistypes are now a gentle signal rather than the dominant one, and difficulty,
length, accuracy and combo carry correspondingly more of the ranking.

**A count of zero still moves nothing at all.** Both bases are exactly `1.0` at zero, and `1.0`
raised to any exponent is exactly `1.0`, so a play with no misses AND no mistypes is priced
BIT-identically before and after. That is the cheapest check that the change is confined to the
two terms, and it is pinned by a test in each repo.

**The mistype denominator is still summed in `double`, and that matters MORE now, not less.**
`notes + mistypes` as `int` overflows on a tamper-shaped count and drives the ratio below `-1`;
`1 - r^2` is then NEGATIVE, which under a fractional exponent is not merely wrong but non-real.
Squaring changes what the bug would cost (the old shape turned the penalty into a large BONUS,
this one would produce a negative base) but not that it must be prevented. The `int.MaxValue`
degenerate case is still swept by a test in each repo.

```
BEFORE:  (1 − miss/notes)^10  ·  (1 − mistypes/(notes + mistypes))^6

AFTER:   (1 − (miss/notes)^2)^10  ·  (1 − (mistypes/(notes + mistypes))^2)^6
```

SR, length, accuracy, combo, the mod multipliers, the Half Time mirror multiplier, eligibility
and the aggregation are all untouched. The mistype count still sits on both sides of its own
fraction, for the reason the backlog-89 amendment gives: keypresses are unbounded, and a
fractional exponent on a negative base is non-real.

| play | before 89 (`^7.5` combined) | after 89 (`^8.5`, `^3.5`) | before | after | change |
|------|--------|--------|--------|--------|--------|
| `notes=500, miss=60, mistype=80` | `0.125946` | `0.200678` | `0.114309` | `0.770823` | +574% |
| `notes=500, miss=10, mistype=20` | `0.640391` | `0.734184` | `0.645745` | `0.987200` | +53% |

**`VERSION` bumps to 5.** Every stored row the change values differently is repriced by
`PpBackfill` at the next boot, reading only columns; no migration is needed.

## Amendment (2026-08-08): both penalty COUNTS are squared, not the ratios (backlog 97)

> SUPERSEDED IN PART by the backlog-101 amendment below. The shape is exactly as this section
> describes it, clamp and cliff and all, and the exponents 10 and 6 are unchanged, but the power
> the raw count is raised to has dropped from 2 to 1.2. Every figure below, including the claim
> that essentially every real play now scores zero, is a figure at a power of 2 and is no longer
> in force.

Backlog 96 squared the RATIO inside each penalty term. That was a misreading of the intent and
it ran the wrong way: a value already in `[0, 1]` gets SMALLER when squared, so `1 - r^2` is
LARGER than `1 - r`, and both penalties WEAKENED. What was meant was the raw COUNT squared,
which is what this amendment does. The exponents stay exactly where backlog 95 put them, 10 and
6.

**The `max(0, ...)` clamp is required rather than decorative, and it is a cliff.** A squared
count over an unsquared denominator is not bounded by `[0, 1]` at all. The cleanliness base
crosses zero at `miss = sqrt(notes)` and runs NEGATIVE past it; misses really can equal `notes`,
so that is the ordinary case and not a hostile-input guard, and a fractional exponent on a
negative base is not merely wrong but non-real. The mistyping base crosses zero at the positive
root of `m^2 - m - notes = 0`, i.e. `(1 + sqrt(1 + 4*notes))/2`, which is later than the miss
cliff because the count sits in the denominator too. On a 500-note map that is 23 misses
(`sqrt(500)` is 22.36) and 23 mistypes (22.87). The clamped form was chosen knowingly, cliff and
all.

**ESSENTIALLY EVERY REAL PLAY NOW SCORES ZERO AT THESE EXPONENTS, AND THAT IS KNOWN.** The
figures, on a 500-note map: at 10 misses and 20 mistypes the miss term is `0.8^10 = 0.107374`
and the mistype term `0.230769^6 = 1.5103e-4`, so the play keeps about `1.62e-05` of a spotless
one. At 60 misses and 80 mistypes both bases clamp and the play keeps exactly `0`. At 22 misses,
one below the cliff and with no mistypes at all, the base is `0.032` and the term about
`1.1e-15`. This is not an oversight and it is not to be "fixed" by lowering the exponents; it is
recorded here, with the figures, so the record shows it was known when the change was made.

**Both numerators are computed in `double`,** written `(double)x * x` and never `x * x`.
Mistypes are unbounded, so `mistypes * mistypes` as an `int` overflows catastrophically (at
`int.MaxValue` the true square is about 4.6e18), and a tamper-shaped note count could do the
same to `misses * misses`. In `double`, `int.MaxValue` mistypes give a ratio of about 2.1e9, so
the base clamps to a well-defined zero rather than wrapping into a NaN or, worse, a bonus. The
`notes + mistypes` sum is still taken in `double` for the same reason, independently of the
numerator.

**A count of zero still moves nothing at all.** `max(0, 1 - 0)` is exactly `1.0`, and `1.0`
raised to any exponent is exactly `1.0`, so a play with no misses AND no mistypes is priced
BIT-identically before and after. That is the cheapest check that the change is confined to the
two terms, and it is pinned by a test in each repo.

```
BEFORE:  (1 − (miss/notes)^2)^10  ·  (1 − (mistypes/(notes + mistypes))^2)^6

AFTER:   max(0, 1 − miss^2/notes)^10  ·  max(0, 1 − mistypes^2/(notes + mistypes))^6
```

SR, length, accuracy, combo, the mod multipliers, the Half Time mirror multiplier, eligibility
and the aggregation are all untouched. The mistype count still sits on both sides of its own
fraction, for the reason the backlog-89 amendment gives: keypresses are unbounded, and a
fractional exponent on a negative base is non-real.

| play | before 89 (`^7.5` combined) | after 89 (`^8.5`, `^3.5`) | after 95 (linear) | before | after | change |
|------|--------|--------|--------|--------|--------|--------|
| `notes=500, miss=60, mistype=80` | `0.125946` | `0.200678` | `0.114309` | `0.770823` | `0.000000` | -100% |
| `notes=500, miss=10, mistype=20` | `0.640391` | `0.734184` | `0.645745` | `0.987200` | `0.000016` | -100% |

**`VERSION` bumps to 6.** Every stored row the change values differently is repriced by
`PpBackfill` at the next boot, reading only columns; no migration is needed.

## Amendment (2026-08-08): the count power drops from 2 to 1.2 (backlog 101)

> **Numbers superseded by v8 and RESTORED by v13**, which is why they read as live again. v8
> retuned `count_power` from the 1.2 this amendment settled on to 1.6, where the cleanliness cliff
> is 49 misses on a 500-note map rather than 178 and the mistyping cliff 52 rather than 249; the
> backlog-137 amendment (v13) put it back to 1.2 with the argument below quoted as its reason. The
> shape was unchanged throughout, so every figure in this section is a figure at the value in force.
> v8 carries no amendment of its own; see the note in the backlog-112 amendment below.

Backlog 97 gave both penalties the shape they still have, max(0, 1 - count^p/denominator) raised
to its own exponent, with p written out longhand as a square. That shape was right and the
exponents 10 and 6 were right; the POWER was too extreme, and this amendment changes only that.
It also lifts p out into a declared constant, `count_power`, so a future retune is one command
rather than a hand edit in both mirrors.

**The power sets the miss count at which a play is worth exactly nothing,** because the
cleanliness base reaches zero at `notes^(1/p)`. On a 500-note map that is 23 misses at `p = 2`,
i.e. 4.6% of the map, which is why everything collapsed. At `p = 1.5` it is 63 (12.6%), still
zeroing a bad-but-real play. At `p = 1.1` it is 285 (57%), so far out that the count power barely
does anything at all. At `p = 1.2` it is 178 (35%), which reads as: you dropped a third of the map.

**The second argument is about map size.** The cliff as a FRACTION of the map is
`notes^(1/p - 1)`. At `p = 2` that is `1/sqrt(notes)`, swinging from 10% of a 100-note map to 2.2%
of a 2000-note map, so long maps were drastically harsher than short ones for no reason anyone
chose. At `p = 1.2` it is `notes^(-1/6)`, which moves only 46% to 35% to 28% across 100, 500 and
2000 notes. The cliff is still there, and still moves with the map, but it no longer swings by a
factor of four and a half across the pool.

**The mistyping cliff moves with it,** from the positive root of `m^2 - m - notes = 0` to that of
`m^1.2 - m - notes = 0`: 23 mistypes to 249 on a 500-note map. That root had a closed form at
`p = 2` and has none at 1.2, so it is solved numerically wherever it is needed.

**The clamp is needed exactly as much as before, and the ordering around it more so.** Counts are
clamped non-negative BEFORE they reach `Math.Pow`, because `Math.Pow` of a negative base under a
fractional power is NaN rather than merely wrong. `Math.Pow(0, 1.2)` is exactly 0, so both bases
are still exactly 1.0 at a count of zero and a spotless play is bit-identical across this change.
`int.MaxValue` mistypes give `Math.Pow(int.MaxValue, 1.2)` of about 1.6e11 against a denominator of
about 2.1e9, so the base still clamps to a well-defined zero.

**What the two worked examples land on.** The near-clean play (10 misses, 20 mistypes on 500 notes)
keeps 0.469 of a spotless one, which is almost exactly where backlog 95 had it (0.646) and nowhere
near the 0.000016 a power of 2 left it at. The sloppy one (60 and 80) keeps 0.0037, which is a
harsh price rather than the zero it was: the cliff is still a cliff, it has simply stopped
swallowing ordinary plays. The percentage in the last column of the second row is large because it
is measured against a number that had collapsed to nearly nothing, not because this change is a
large buff over any earlier generation.

```
BEFORE:  max(0, 1 − miss^2/notes)^10  ·  max(0, 1 − mistypes^2/(notes + mistypes))^6

AFTER:   max(0, 1 − miss^1.2/notes)^10  ·  max(0, 1 − mistypes^1.2/(notes + mistypes))^6
```

SR, length, accuracy, combo, the mod multipliers, the Half Time mirror multiplier, eligibility
and the aggregation are all untouched. The mistype count still sits on both sides of its own
fraction, for the reason the backlog-89 amendment gives: keypresses are unbounded, and a
fractional exponent on a negative base is non-real.

| play | after 95 (linear) | after 96 (ratio squared) | before | after | change |
|------|--------|--------|--------|--------|--------|
| `notes=500, miss=60, mistype=80` | `0.114309` | `0.770823` | `0.000000` | `0.003729` | up from exactly 0 |
| `notes=500, miss=10, mistype=20` | `0.645745` | `0.987200` | `0.000016` | `0.468755` | +2890437% |

**`VERSION` bumps to 7.** Every stored row the change values differently is repriced by
`PpBackfill` at the next boot, reading only columns; no migration is needed.

## Amendment (2026-08-10): the global scale, the SR exponent and the mistype exponent are retuned (backlog 112)

The backlog-112 retune of three constants, with the SHAPE untouched: the global scale rises 3.0
to 5.5, sr_exponent 2.60 to 2.70, and mistype_exponent 8.0 to 4.0. count_power stays 1.6,
miss_exponent stays 10, and the length, accuracy and combo terms and every mod multiplier are
exactly as they were. scale and sr_exponent together are close to a pure rescale (they preserve
ranking order among plays on the same map, and steepen it only mildly across difficulties), and
roughly DOUBLE a clean mid-difficulty play. Halving the mistype exponent is the part that
changes ORDER: a mistype-heavy play is repriced far more than double, because 8 was steep enough
to price such plays at essentially nothing. Both penalty bases are still exactly 1.0 at a count
of zero, so a spotless play moves only by the rescale, while every stored row carrying a mistype
is repriced upwards, which is what forces the bump.

**Where the 1.6 came from, and the one gap in this file.** `count_power`'s 1.6 and the length
weight's 0.50 are v8's, and v8 is the only version that shipped without amending this spec. It is
game commit 51f1dc5 (2026-08-08), and its message is the whole of its record: "New pp coefficients:
scale 3, SR 2.6, count power 1.6, mistype exponent 8, length weight 0.5, combo 0.75". Against v7
that is scale 4.0 to 3.0, sr_exponent 2.70 to 2.60, mistype_exponent 6.0 to 8.0, count_power 1.2 to
1.6, length_weight 0.70 to 0.50 and combo_exponent 0.55 to 0.75, with the SHAPE untouched;
miss_exponent and accuracy_exponent did not move. Both mirrors' VERSION lists carry a v8 bullet
stating the same. No dated section is added for it after the fact: a heading here means a decision
recorded at the time, and there was none.

```
BEFORE:  max(0, 1 − miss^1.6/notes)^10  ·  max(0, 1 − mistypes^1.6/(notes + mistypes))^8

AFTER:   max(0, 1 − miss^1.6/notes)^10  ·  max(0, 1 − mistypes^1.6/(notes + mistypes))^4
```

SR, length, accuracy, combo, the mod multipliers, the Half Time mirror multiplier, eligibility
and the aggregation are all untouched. The mistype count still sits on both sides of its own
fraction, for the reason the backlog-89 amendment gives: keypresses are unbounded, and a
fractional exponent on a negative base is non-real.

| play | before | after | change |
|------|--------|--------|--------|
| `notes=500, miss=60, mistype=80` | `0.000000` | `0.000000` | 0% |
| `notes=500, miss=10, mistype=20` | `0.052744` | `0.151677` | +188% |

**`VERSION` bumps to 9.** Every stored row the change values differently is repriced by
`PpBackfill` at the next boot, reading only columns; no migration is needed.

## Amendment (2026-08-10): Accuracy and combo are priced far more steeply (backlog 121)

> Superseded by v11 before it ever shipped: a second sandbox export arrived first.

v10 = accuracy exponent 1.30 to 1.75 and combo exponent 0.75 to 1.50. A spotless play is priced
bit-identically (both bases are exactly 1.0 at a full combo and perfect accuracy, whatever the
exponent), so this repositions everything BELOW an FC rather than rescaling the pool: a 97% play
at 0.90 combo loses about 9%, a 90% play at 0.75 combo about 23%.

```
BEFORE:  max(0, 1 − miss^1.6/notes)^10  ·  max(0, 1 − mistypes^1.6/(notes + mistypes))^4

AFTER:   max(0, 1 − miss^1.6/notes)^10  ·  max(0, 1 − mistypes^1.6/(notes + mistypes))^4
```

SR, length, accuracy, combo, the mod multipliers, the Half Time mirror multiplier, eligibility
and the aggregation are all untouched. The mistype count still sits on both sides of its own
fraction, for the reason the backlog-89 amendment gives: keypresses are unbounded, and a
fractional exponent on a negative base is non-real.

| play | before | after | change |
|------|--------|--------|--------|
| `notes=500, miss=60, mistype=80` | `0.000000` | `0.000000` | 0% |
| `notes=500, miss=10, mistype=20` | `0.151677` | `0.151677` | +0% |

**`VERSION` bumps to 10.** Every stored row the change values differently is repriced by
`PpBackfill` at the next boot, reading only columns; no migration is needed.

## Amendment (2026-08-10): The difficulty curve flattens and accuracy/combo sharpen again (backlog 121, second export)

> Superseded by v12, which changes the SHAPE of the combo term. The exponent 2.50 this amendment
> set is still the exponent, but it is now applied to a log-bent ratio rather than to the ratio
> itself.

v11 = scale 5.5 to 12.5, sr_exponent 2.70 to 2.00, accuracy_exponent 1.75 to 1.80,
combo_exponent 1.50 to 2.50. v10 never shipped, so the meaningful comparison is against v9: the
SR exponent drop flattens the difficulty curve so easy maps gain and hard maps lose, while the
combo exponent sharpens what a broken combo costs.

```
BEFORE:  max(0, 1 − miss^1.6/notes)^10  ·  max(0, 1 − mistypes^1.6/(notes + mistypes))^4

AFTER:   max(0, 1 − miss^1.6/notes)^10  ·  max(0, 1 − mistypes^1.6/(notes + mistypes))^4
```

SR, length, accuracy, combo, the mod multipliers, the Half Time mirror multiplier, eligibility
and the aggregation are all untouched. The mistype count still sits on both sides of its own
fraction, for the reason the backlog-89 amendment gives: keypresses are unbounded, and a
fractional exponent on a negative base is non-real.

| play | before | after | change |
|------|--------|--------|--------|
| `notes=500, miss=60, mistype=80` | `0.000000` | `0.000000` | 0% |
| `notes=500, miss=10, mistype=20` | `0.151677` | `0.151677` | +0% |

**`VERSION` bumps to 11.** Every stored row the change values differently is repriced by
`PpBackfill` at the next boot, reading only columns; no migration is needed.

## Amendment (2026-08-12): the combo term's base goes log-shaped (backlog 131)

> Superseded by v13, which returns count_power to the 1.2 this amendment set. v8 moved it to 1.6
> without an amendment; backlog 137 is the decision to undo that.

The combo term stops being a plain powered ratio. Where v11 raised `maxcombo/notes` straight to
2.50, v12 bends that ratio through a log first: the base is `ln(1 + 9.0·r)/ln(1 + 9.0)` over `r =
maxcombo/notes`, and only then is raised to the same 2.50. `combo_exponent` does not move, and
nothing else in the formula does either.

A FULL COMBO IS PRICED BIT-IDENTICALLY, at every value of the shape constant: `ln(1 + k)/ln(1 +
k)` is exactly 1 and 1 raised to anything is exactly 1. So this repositions only what sits BELOW
an FC rather than rescaling the pool, exactly as the v10 and v11 combo retunes did.

What it buys is that a broken combo costs roughly its FACE VALUE. The log base is CONCAVE,
lifting every ratio under 1, where `^2.50` is convex, and at a shape constant of 9 the two very
nearly cancel over the range real plays live in: the term is 0.9007 at a combo ratio of 0.90,
0.7983 at 0.80 and 0.7458 at 0.75, i.e. near enough linear down to about 0.7. Under `^2.50`
alone those same three plays kept 0.7684, 0.5724 and 0.4871, so losing 10% of a combo cost 23%
of the term and losing a quarter of it cost slightly more than half. Further down the lift is
larger still: a play that held half the map's combo keeps 0.4716 where it kept 0.1768, up 167%.

The worked table below is the two PENALTY examples this file has tracked since backlog 89, and
both are unmoved, because neither carries a combo at all. They are not witnesses to this change;
the combo figures above are the ones to read.

```
BEFORE:  max(0, 1 − miss^1.6/notes)^10  ·  max(0, 1 − mistypes^1.6/(notes + mistypes))^4
         ·  (maxcombo/notes)^2.50

AFTER:   max(0, 1 − miss^1.6/notes)^10  ·  max(0, 1 − mistypes^1.6/(notes + mistypes))^4
         ·  (ln(1 + 9.0·maxcombo/notes)/ln(1 + 9.0))^2.50
```

SR, the global scale, length, accuracy, the mod multipliers, the Half Time mirror multiplier,
eligibility and the aggregation are all untouched. The mistype count still sits on both sides of
its own fraction, for the reason the backlog-89 amendment gives: keypresses are unbounded, and a
fractional exponent on a negative base is non-real.

| play | before | after | change |
|------|--------|--------|--------|
| `notes=500, miss=60, mistype=80` | `0.000000` | `0.000000` | 0% |
| `notes=500, miss=10, mistype=20` | `0.151677` | `0.151677` | +0% |

**`VERSION` bumps to 12.** Every stored row the change values differently is repriced by
`PpBackfill` at the next boot, reading only columns; no migration is needed.

## Amendment (2026-08-12): count_power returns to 1.2, the value backlog 101 argued for (backlog 137)

> Superseded by v14, which rescales pp globally. The count_power this amendment set is unchanged
> and still 1.2.

v13 = count_power 1.6 back to 1.2, restoring the value backlog 101 chose with a written argument
and that v8 silently replaced. The SHAPE is untouched and so is every other constant. At 1.6 the
cleanliness base hit zero at 49 misses on a 500-note map, 9.7% of it, within a factor of two of
the 4.6% that backlog 101 rejected as pricing essentially every real play to nothing; at 1.2 it
is 178, i.e. 35%. It also restores the mistyping term's separate cliff, which exists because its
count sits in its own denominator: the two cliffs were 52 and 49 at 1.6, three counts apart, and
are 249 and 178 at 1.2. Both bases are still exactly 1.0 at a count of zero, so a spotless play
is priced bit-identically, while every stored row carrying a miss or a mistype is repriced
upwards, many of them away from exactly zero, which is what forces the bump.

```
BEFORE:  max(0, 1 − miss^1.6/notes)^10  ·  max(0, 1 − mistypes^1.6/(notes + mistypes))^4

AFTER:   max(0, 1 − miss^1.2/notes)^10  ·  max(0, 1 − mistypes^1.2/(notes + mistypes))^4
```

SR, the global scale, length, accuracy, combo, the mod multipliers, the Half Time mirror
multiplier, eligibility and the aggregation are all untouched. The mistype count still sits on
both sides of its own fraction, for the reason the backlog-89 amendment gives: keypresses are
unbounded, and a fractional exponent on a negative base is non-real.

| play | before | after | change |
|------|--------|--------|--------|
| `notes=500, miss=60, mistype=80` | `0.000000` | `0.008341` | up from exactly 0 |
| `notes=500, miss=10, mistype=20` | `0.151677` | `0.542001` | +257% |

**`VERSION` bumps to 13.** Every stored row the change values differently is repriced by
`PpBackfill` at the next boot, reading only columns; no migration is needed.

## Amendment (2026-08-12): The global scale drops 12.5 to 9.6 (backlog 139)

v14 = the global scale drops 12.5 to 9.6, exported from the pp sandbox as the only change. scale
is the one constant that provably cannot move ranking order, within a map or across maps, since
it multiplies every play equally; it rescales absolute pp by 0.768 and nothing else. Every
stored row is repriced, which is what forces the bump, but no leaderboard reorders. Applied on
top of v13 rather than the v12 the sandbox export names as its baseline, because backlog 137
landed count_power 1.6 to 1.2 first.

```
BEFORE:  max(0, 1 − miss^1.2/notes)^10  ·  max(0, 1 − mistypes^1.2/(notes + mistypes))^4

AFTER:   max(0, 1 − miss^1.2/notes)^10  ·  max(0, 1 − mistypes^1.2/(notes + mistypes))^4
```

SR, length, accuracy, combo, the mod multipliers, the Half Time mirror multiplier, eligibility
and the aggregation are all untouched. The mistype count still sits on both sides of its own
fraction, for the reason the backlog-89 amendment gives: keypresses are unbounded, and a
fractional exponent on a negative base is non-real.

| play | before | after | change |
|------|--------|--------|--------|
| `notes=500, miss=60, mistype=80` | `0.008341` | `0.008341` | +0% |
| `notes=500, miss=10, mistype=20` | `0.542001` | `0.542001` | +0% |

**`VERSION` bumps to 14.** Every stored row the change values differently is repriced by
`PpBackfill` at the next boot, reading only columns; no migration is needed.

## Amendment (2026-08-12): Literate is priced through its star rating, and the flat 1.06 goes (backlog 144)

The flat multiplier of 1.06 leaves modMult, and Literate is priced the way a rate is:
exclusively through SR_eff, recomputed on the beatmap the mod converts this one into. Literate
is IApplicableAfterBeatmapConversion, so every supported punctuation mark becomes a typed cell
with a target time of its own; that lengthens words, changes a line's rhythm and moves the map's
rating. This file has said since task 61 that DT/HT get no flat multiplier precisely so nothing
double-counts, and the half-way state (the rating moves AND the multiplier stays) is the one
option that is definitely wrong. THE FLAT NUMBER WAS ALSO A POOR DESCRIPTION OF THE MOD.
Measured over the five reference maps, the honest rate-1.0 rating moves by +2.3%, +2.7%, +6.3%, -0.8%
and -0.7%: Literate makes two of the five EASIER, because the extra characters raise a word's
per-character window floor as well as its cost. A flat 1.06 paid every map the same 6%
regardless. In pp the net of dropping 1.06 and taking the honest rating is -7.1% to +6.6% at
rate 1.00, and -6.4% to +19.4% under Double Time. THE STORAGE CONSEQUENCE IS SIX RATINGS PER
DIFFICULTY, NOT FOUR. The server prices strictly from stored columns, so a combination whose
rating is not stored cannot be priced at all, and Literate is ORTHOGONAL to the rate: a Literate
Double Time play needs the CONVERTED map rated at 1.50x. Migration 029_literate_stars.sql adds
sr_literate, sr_literate_dt and sr_literate_ht beside the existing three, and LyricPace.VERSION
bumps to 13 so the startup sweep fills them. That cross product is not avoidable by arithmetic:
LyricDifficulty measured difficulty as star_scale times raw^star_power with the rate entering raw ADDITIVELY,
so predicting sr_literate_dt as sr_literate times (sr_dt/difficulty_rating) is wrong by up to
5.8% in stars and 11.2% in pp over the same five maps, in both directions. A Literate play on a
map the sweep has not reached yet is written UNPRICED and retried on a later boot, exactly as a
Double Time play on a map without sr_dt already is. ELIGIBILITY DOES NOT MOVE: Literate stays
RANKED, EligibleRate knows nothing about it, and a Literate play ranks on every leaderboard
exactly as before.

```
BEFORE:  max(0, 1 − miss^1.2/notes)^10  ·  max(0, 1 − typos^1.2/(notes + typos))^4

AFTER:   max(0, 1 − miss^1.2/notes)^10  ·  max(0, 1 − typos^1.2/(notes + typos))^4
```

SR, the global scale, length, accuracy, combo, the Half Time mirror multiplier, eligibility and
the aggregation are all untouched. The typo count still sits on both sides of its own fraction,
for the reason the backlog-89 amendment gives: keypresses are unbounded, and a fractional
exponent on a negative base is non-real.

| play | before | after | change |
|------|--------|--------|--------|
| `notes=500, miss=60, typo=80` | `0.008341` | `0.008341` | +0% |
| `notes=500, miss=10, typo=20` | `0.542001` | `0.542001` | +0% |

The worked table above is the two PENALTY examples this file has tracked since backlog 89, and both
are unmoved, because neither carries a mod at all. They are not witnesses to this change; the
per-map figures in the prose above are the ones to read.

**`VERSION` bumps to 15, AND THIS ONE DOES NEED A MIGRATION**, which makes it the first amendment
that does. Every previous bump was repriced by `PpBackfill` from columns that already existed;
this change gives a Literate play a rating that has never been stored, so `029_literate_stars.sql`
adds the three columns and `LyricPace.VERSION` bumps to 13 so `PaceBackfill` fills them from the
stored blobs at the next boot. `PpBackfill` runs after it in the same startup, exactly as it
already does for `sr_dt` / `sr_ht`, and any Literate row it reaches before its map is filled is
left stale and retried rather than stamped at zero. No non-Literate row is valued differently by
this change at all.

## Amendment (2026-08-13): Length leaves pp for the star rating

The backlog-152 length migration. The length factor `max(0.1, 1 + 0.50·log10(notes/100))` is
DELETED from this formula, and `length_weight` and `length_floor` with it. Length is priced by the
STAR RATING instead: `LyricDifficulty` gains an ADDITIVE `0.12·max(0, log10(cells/100))` star bonus,
where `cells` is the map's typeable cell count, counted inside `Compute` off the very lines it
already walks so the client and the server cannot end up with two definitions of it.

**Why it moved rather than being retuned.** SR ignored length almost entirely (only the `ln(sum)`
inside the soft max, 2-3% per doubling) while pp paid up to 1.70x for it, which put the whole of a
soft signal in the hard place. Adding a length term to SR while pp kept one would double count, so
pp keeps NONE.

**Why ADDITIVE, in SR.** A multiplier moves the hardest maps the most, which is exactly the wrong
shape here: "there is simply more of it" is worth the same on a 2 star map and on an 8 star one. A
flat log bonus prices it that way and leaves rhythm density and pace, through whatever difficulty
model SR is running (the strain one at the time of writing, the feats one since backlog 269, the
envelope one since backlog 273), as the hard signals. The `max(0, ·)` clamp gives a sub-100-cell
map nothing, which also keeps every short synthetic fixture rating byte-identically.

**What it does to the catalogue.** Measured live at 0.12: Nanana x Cola [Extreme] 7.81 to 7.97,
HYPER4ID [Hyper] 7.47 to 7.60, Riptide [Seaside] 6.20 to 6.35, Spectator [Wolf] 4.54 to 4.65, mean
3.27 to 3.33. Maximum movement +0.17, nothing crosses a whole star, and cuts drift down relative to
their full versions (the Hyper vs Hyper Cut gap widens by 0.04).

**The pp fallout is deliberate and is NOT neutral.** pp now sees length only as
`((SR + bonus)/SR)^2.00`, a few percent, so long-map plays deflate hardest: roughly -18% on a
340-cell map, -28% at 800, -38% at 2300. That reordering is the feature, length stops buying pp it
no longer earns. The GLOBAL deflation that rides along with it is not, and is to be taken out
separately by re-anchoring `scale` against the live pool, as a uniform, order-safe rescale.

**Rate plays.** The bonus depends on the cell count, not the clock, so both sides of a rate ratio
gain the same constant and `sr_dt/difficulty_rating` and `sr_ht/difficulty_rating` compress
slightly. D, H and the Half Time mirror therefore shift by a fraction of a percent. Accepted, with
no compensation.

`notes` STAYS. It is untouched here and still load-bearing for both penalty terms, the combo ratio
and Flashlight's bonus; only the length pair is deleted. `reference_notes` stays too, now owned by
Flashlight alone.

```
BEFORE:  max(0, 1 − miss^1.2/notes)^10  ·  max(0, 1 − typos^1.2/(notes + typos))^4

AFTER:   max(0, 1 − miss^1.2/notes)^10  ·  max(0, 1 − typos^1.2/(notes + typos))^4
```

SR, the global scale, accuracy, combo, the mod multipliers, the Half Time mirror multiplier,
eligibility and the aggregation are all untouched. The typo count still sits on both sides of
its own fraction, for the reason the backlog-89 amendment gives: keypresses are unbounded, and a
fractional exponent on a negative base is non-real.

| play | before | after | change |
|------|--------|--------|--------|
| `notes=500, miss=60, typo=80` | `0.008341` | `0.008341` | +0% |
| `notes=500, miss=10, typo=20` | `0.542001` | `0.542001` | +0% |

The worked table above tracks the two PENALTY examples only, and neither moves, because neither
penalty term reads length. They are not witnesses to this change; the per-map figures in the prose
above are the ones to read. The whole-play reference play IS a witness, and it moves the full width
of the deleted factor: 4 stars, 500 notes, 90%, full combo went from 171.473019 to 127.065524,
which is exactly the old `max(0.1, 1 + 0.50·log10(500/100))` of 1.349485 divided out.

**`VERSION` bumps to 16, and `LyricPace.VERSION` to 14 alongside it.** No migration is needed: every
column this touches already exists. The pace bump is what makes `PaceBackfill` re-rate every stored
map under the new star formula (`difficulty_rating`, `sr_dt`, `sr_ht` and the three Literate
columns, which also finally reprices the `sr_dt` rows still pinned at the old flat ceiling of 10),
and it stamps `pp_version = 0` on the scores of every row it rewrites. `PpBackfill` then runs after
it in the same startup and reprices those rows against the new pp formula, so the two halves of the
migration land on a stored row together rather than one at a time.

## Amendment (2026-08-14): Re-anchor the global scale after length left pp

Backlog 152 deleted pp's length factor, which paid up to 1.70x, and left length priced only
through the star rating. The REORDERING that produced is the feature and is untouched here; the
across-the-board deflation that came with it is not, and this corrects only that. Being a
uniform multiplier it provably cannot move a placement, within a map or across maps. The anchor
is the median ranked player's decayed total, measured on production: profile pp and global rank
are the numbers players see, and they deflated more (ratio 0.776) than individual plays did
(0.751), because the decayed sum is dominated by a player's best plays and those sit on the
longest maps. 12.374 holds it exactly flat; 12.4 is that rounded to the one-decimal shape 9.6
and 12.5 already use, leaving the median player 0.2 percent up. Per-player ratios span 0.683 to
0.873 and no uniform anchor can flatten that spread, which is backlog 152's intended reordering
and must survive.

```
BEFORE:  max(0, 1 − miss^1.2/notes)^10  ·  max(0, 1 − typos^1.2/(notes + typos))^4

AFTER:   max(0, 1 − miss^1.2/notes)^10  ·  max(0, 1 − typos^1.2/(notes + typos))^4
```

SR, accuracy, combo, the mod multipliers, the Half Time mirror multiplier, eligibility and the
aggregation are all untouched. The typo count still sits on both sides of its own fraction, for
the reason the backlog-89 amendment gives: keypresses are unbounded, and a fractional exponent
on a negative base is non-real.

| play | before | after | change |
|------|--------|--------|--------|
| `notes=500, miss=60, typo=80` | `0.008341` | `0.008341` | +0% |
| `notes=500, miss=10, typo=20` | `0.542001` | `0.542001` | +0% |

**`VERSION` bumps to 17.** Every stored row the change values differently is repriced by
`PpBackfill` at the next boot, reading only columns; no migration is needed.

## Amendment (2026-08-27): an uncorrected typo is a MISS (backlog 213)

> Not superseded: v19 leaves both count derivations below exactly as they are. What it adds is a
> SECOND factor on the ACCURACY term (a soft knee at 80%), so every pp figure in this section is a
> figure priced without it.

> No constant moves and neither does the SHAPE. What moves is the DERIVATION of two of the three
> counts the formula takes from a play's `statistics`, so the 2026-08-14 amendment above is not
> superseded and every number it states is still in force.

A cell the player finished with the WRONG character and never went back for is stored under its own
key, `good` (the game's `TypeBeatResultMapping.UNFIXED_TYPO`). Backlog 124 gave it that key so such
a cell could be told apart from one the line simply ran out of time on, and priced the difference:
50 of 300 in accuracy, and this file's TYPO term rather than its cleanliness term. The field report
that ended that reading was a stored score displaying MISS 0 while carrying `good: 2`: two
characters the player never typed right, appearing in no column at all and costing half what
dropping them would have.

The two events say the same thing about the play, which is that the character is not there. So they
are priced the same way from here on:

```
BEFORE:  misses = statistics.miss
         typos  = statistics.combo_break

AFTER:   misses = statistics.miss + statistics.good
         typos  = max(0, statistics.combo_break - statistics.good)
```

**ONE FLUB IS PRICED BY EXACTLY ONE TERM**, which is the whole of the second line and is not a
tidying of the first. Every uncorrected typo cell implied a wrong KEYPRESS, and that keypress is
already in `combo_break`; charging the cleanliness term for the cell AND the typo term for the
keypress would price one mistake twice, at exponent 10 and again at 4. A CORRECTED typo is
untouched and stays a typo event, because its cell resolved as an ordinary hit and never reached
the `good` key at all, so nothing subtracts it. Fix it and it stays a typo; leave it and it becomes
a miss.

**THE CLAMP ON THE SUBTRACTION IS LOAD-BEARING, NOT DEFENSIVE.** The two counts arrive off the wire
independently, so `good` can exceed `combo_break` on a row stored before the mistype stat existed at
all (backlog 72: no `combo_break` key, but `good` cells aplenty) and on any tamper-shaped
dictionary. A negative count goes into `Math.Pow(typos, count_power)` under a FRACTIONAL power and
comes back NaN, not merely wrong, which is the same reason the counts are clamped non-negative
before they reach the powers at all.

`notes` is UNTOUCHED and `good` stays in it: the cell is one cell of the map however it was typed,
and dropping it would shorten the map pp thinks was played, hardening both penalty terms and
inflating the combo ratio. `maxcombo`, the mod multipliers, the Half Time mirror multiplier,
eligibility and the aggregation are all untouched too.

Both derivations reduce to the pre-213 ones at `good = 0`, so a play with no uncorrected typo in it
is priced bit-identically and only rows carrying the key move, downwards:

| play | before | after | change |
|------|--------|--------|--------|
| `notes=500, miss=10, good=5, combo_break=20` | `0.542001` | `0.479586` | -11.5% |
| `notes=400, miss=43, good=7, combo_break=20` | `0.052257` | `0.033074` | -36.7% |
| `notes=100, miss=0, good=2, combo_break=5` | `0.761979` | `0.683689` | -10.3% |
| `notes=500, miss=60, good=0, combo_break=80` | `0.008341` | `0.008341` | +0% |
| `notes=500, miss=0, good=0, combo_break=0` | `1.000000` | `1.000000` | +0% |

(The figures are the two penalty terms multiplied together, which is the only part of the product
this amendment can move.)

**THE WIRE DOES NOT MOVE, AND THAT IS THE SHAPE OF THE WHOLE CHANGE.** The seal still writes the
same key, so nothing about submission, storage or the replay format changes, stored rows stay
comparable with new ones, and the typo-versus-timeout distinction survives in the DATA even though
nothing prices it any more. Every CONSUMER reclassifies instead, which is the pattern backlog 140
used for the mistype's `combo_break` key. Outside this file that means the accuracy weight of the
`good` key drops from 50 to 0 (`ScoringContract.BaseScore`, the game's
`TypeBeatScoreProcessor.GetBaseScoreForResult` under the new `UnfixedTypoWorthRule`, and
`typebeat-core.js`'s `HIT_BASE_SCORE`), and the MISS column of every display becomes `miss + good`
so the shown columns sum to the judged cell count again.

COMPLETION and therefore RANK are deliberately untouched, and the fold found that half already
done: backlog 126 took an uncorrected typo out of completion's numerator, so it has cost the grade
exactly as a miss does ever since. That is why accuracy could be re-weighted without touching
`ScoringContract.CountsAsTyped` or `RankFromCompletion`.

**`VERSION` bumps to 18.** Every stored row carrying a `good` key is repriced, downwards, by
`PpBackfill`'s sweep at the next boot; no migration is needed, since the sweep reads only columns
that already exist and the fold changes no stored value but `pp` and `pp_version`. A row's stored
`accuracy`, `total_score`, `completion` and `rank` do NOT move: the accuracy re-weight applies to
what a client computes and to what the contract recomputes at SUBMIT time, and nothing recomputes a
settled row's accuracy.

## Amendment (2026-08-28): the accuracy term gains a SOFT KNEE at 80% (backlog 227)

Accuracies here live at 55 to 93 (see the definitions above) and solid plays at 85 to 98, so a
play at 80% and below has been a viable way to earn pp while the difference between 90% and 95%
has been worth almost nothing. Raising `accuracy_exponent` cannot fix the first without breaking
the second, because an exponent is a single dial over the WHOLE range: steep enough to price 80%
out, it taxes the plays it is not aimed at too, and a 95% play that keeps 0.912 of the timing
term at 1.80 keeps only 0.774 at 5. So accuracy gains a SECOND factor instead, and the exponent
stays exactly where it was, still doing the ordering work above the knee.

The new factor is a SOFT KNEE, a logistic in the accuracy: `1/(1 + exp(-(acc -
acc_knee)/acc_knee_width))`, with `acc_knee = 0.80` saying WHERE the cliff falls and
`acc_knee_width = 0.025` saying HOW SHARPLY. Two dials, each retunable without touching the
other or the exponent, which is the whole reason this is a factor rather than a bend of the one
that was already there.

| acc | knee | what the knee costs the play |
|------|--------|--------|
| 0.95 | `0.997527` | 0.25% |
| 0.90 | `0.982014` | 1.8% |
| 0.85 | `0.880797` | 11% |
| 0.80 | `0.500000` | exactly half |
| 0.75 | `0.119203` | 88% |
| 0.70 | `0.017986` | 98% |

Three properties hold whatever the two constants are set to, which is what makes them safe to
retune later. THE KNEE IS EXACTLY 0.5 AT `acc == acc_knee`, since the argument to the
exponential is then exactly 0 and `1/(1 + exp(0))` is `1/2`: a play sitting on the knee is
priced identically across any retune of the width, exactly as a full combo is across any retune
of `combo_log_shape`. IT IS STRICTLY INCREASING in the accuracy, and so is `acc^1.80`, so their
product is too: the knee can RESPREAD the accuracy axis but never permute it, and no pair of
plays changes order because of it. And IT IS FINITE AND SMOOTH over the whole of `[0, 1]` with
no clamp needed, since accuracy is clamped into that interval before the term sees it: at a
width of 0.025 the argument to `exp` runs between -8 and +32, nowhere near the ~709 at which it
overflows, and the factor between 1.3e-14 and 0.9997.

A WIDTH OF ZERO OR LESS MEANS THERE IS NO KNEE and the factor is exactly 1.0, i.e. the v18
timing term. That is a real branch in both mirrors rather than a limit of the logistic, which is
where it differs from `combo_log_shape`'s 0: `log(1 + k*r)/log(1 + k)` tends to the plain ratio
as k tends to 0, but a width tending to 0 gives a STEP function, and `0/0` at the knee itself is
NaN. The branch is what makes "no knee" the same arithmetic in the mirrors, in a mirror one
generation behind, and in `tools/pp.py`, which reads an absent declaration as exactly that.

Validated against the live pool before shipping, which is what the two starting points were
chosen against: the deflation is TARGETED rather than global (it lands on the plays at and below
the knee, where the intent is), the top of the boards does not move, and `C` is therefore NOT
re-anchored with this change. Scope is pp alone: grades, ranks, the accuracy display and the
ranked gate are all untouched, so a 78% play still ranks and still shows its grade, it simply
prices at roughly a tenth of what it did.

```
BEFORE:  max(0, 1 − miss^1.2/notes)^10  ·  max(0, 1 − typos^1.2/(notes + typos))^4
         ·  acc^1.80

AFTER:   max(0, 1 − miss^1.2/notes)^10  ·  max(0, 1 − typos^1.2/(notes + typos))^4
         ·  acc^1.80 · 1/(1 + e^(−(acc − 0.80)/0.025))
```

SR, the global scale, combo, the mod multipliers, the Half Time mirror multiplier, eligibility
and the aggregation are all untouched. The typo count still sits on both sides of its own
fraction, for the reason the backlog-89 amendment gives: keypresses are unbounded, and a
fractional exponent on a negative base is non-real.

| play | before | after | change |
|------|--------|--------|--------|
| `notes=500, miss=60, typo=80` | `0.008341` | `0.008341` | +0% |
| `notes=500, miss=10, typo=20` | `0.542001` | `0.542001` | +0% |

**`VERSION` bumps to 19.** Every stored row the change values differently is repriced by
`PpBackfill` at the next boot, reading only columns; no migration is needed.

## Amendment (2026-09-06): Half Time is priced through its star rating alone (backlog 265)

> Superseded in part by the amendment below (backlog 270), which replaces the combo multiplier
> with an additive bonus. Everything this amendment says about Half Time and the rate factors
> still holds.

The mirror multiplier of the 2026-08-07 amendment is DELETED, and `half_time_buff_clamp` with it.
A base-rate Half Time play is priced by `sr_ht` and by nothing else, exactly as a base-rate Double
Time play is priced by `sr_dt` and by nothing else. `rateMult` leaves the formula, `RateStars`
stops carrying a multiplier, `PerformancePoints.Compute` stops taking one, and the client's
`TypeBeatDifficultyAttributes` (which existed solely to ship the number to a performance calculator
that had no beatmap to derive it from) is deleted along with it.

THE ASYMMETRY IS REAL AND IS BEING ACCEPTED, not fixed elsewhere by sleight of hand. On the parity
fixture's spread (`sr_base = 4.2`, `sr_dt = 6.1`, `sr_ht = 3.4`) speeding a map up pays **+111%**
while slowing it down costs only **-34.5%**, which is what backlog 90 set out to close. The mirror
closed it at a price this file has now decided is too high. It made ONE rate a function of all
three of a map's ratings, which is the only thing in pp that reads a rating the play was not set
on; it was the only exception to the rule stated in the very first section, that a rate is priced
EXCLUSIVELY through `SR_eff`, so that sentence has been false since v3 and is true again now; and
its own guard was load-bearing precisely because the mirror was wrong on a whole class of maps (the
`sr_dt · sr_ht < sr_base²` concave ones), where the answer was not a per-map symmetry at all but a
flat 0.70 that came from nowhere. **If Half Time ever reads as underpriced again, the fix is in the
strain model behind `sr_ht`, never in a reinstated multiplier here.** That is where a claim about
what typing a map slowly is worth belongs, and it is measurable there.

EVERY STORED HALF TIME PLAY REPRICES UPWARDS, by exactly the reciprocal of the multiplier it used
to carry. A row that was taking the flat clamp gains `1/0.70`, i.e. **+43%**, the largest move this
change can make; a row on the mirror branch gains `D · H`, which on the fixture spread is
`0.723 -> 1`, i.e. +38%. Nothing else moves at all: a Double Time row, a no-mod row, a custom-rate
row and every Literate row that is not also Half Time are priced bit-identically.

THE DATA DEPENDENCY RELAXES, which is the part with a second-order effect. Pricing a Half Time play
needed BOTH `sr_ht` (to price it) and `sr_dt` (to mirror against), so a map the SR sweep had filled
halfway left its HT rows `Pending` and unpriced; a Literate Half Time play needed `sr_literate_ht`
and `sr_literate_dt` the same way. Each rate now needs exactly its own one rating, so those rows
price on the v20 sweep instead of waiting for a column they never used. In the same movement, a
DEGENERATE `sr_dt` or base rating stops zeroing an HT play: the mirror returned 0 on a non-finite
or non-positive input and that 0 propagated through the whole product, so a map whose up-rate
rating was broken paid its honest down-rate plays nothing. Those ratings are simply not read now.

LITERATE HALF TIME FOLLOWS WITHOUT A BRANCH OF ITS OWN, on both sides. The server picks WHICH
TRIPLE (converted or plain) before it asks the rate question, so deleting the mirror from the rate
arm serves both triples at once; the client's Literate handling lived entirely inside the deleted
`RateMultiplier`, so it goes with it.

```
BEFORE:  SR_eff^2.00 · ... · modMult · rateMult
         rateMult = 1 / (D · H)  for base-rate HT (or 0.70 where that would be a buff), else 1.0

AFTER:   SR_eff^2.00 · ... · modMult
```

SR, the global scale, accuracy, combo, the mod multipliers, eligibility and the aggregation are all
untouched. The typo count still sits on both sides of its own fraction, for the reason the
backlog-89 amendment gives: keypresses are unbounded, and a fractional exponent on a negative base
is non-real.

| play | before | after | change |
|------|--------|--------|--------|
| `notes=500, miss=60, typo=80` | `0.008341` | `0.008341` | +0% |
| `notes=500, miss=10, typo=20` | `0.542001` | `0.542001` | +0% |

The worked table above is the two PENALTY examples this file has tracked since backlog 89, and both
are unmoved, because neither carries a rate mod at all. They are not witnesses to this change; the
+43% and the fixture spread in the prose above are the ones to read.

**`VERSION` bumps to 20.** Every stored base-rate Half Time row is now worth MORE than the value
beside it, so the bump is mandatory. `PpBackfill` repasses every `scores` row at the next boot,
reading only columns; no migration is needed, and the rows that were left `Pending` for a missing
`sr_dt` are picked up by the same sweep.

## Amendment (2026-09-06): combo becomes an additive bonus, and the mod table gains RE and FC (backlog 270)

COMBO STOPS BEING A FACTOR OF THE PRODUCT AND BECOMES AN ADDITIVE BONUS. The log-bent multiplier
backlog 131 introduced is deleted, constants and all, and pp gains a term ADDED after everything
else including modMult: `maxcombo/notes * max(0, combo_bonus_slope * (SR_eff -
combo_bonus_zero))`, at a slope of 12.5 pp per star above a zero point of 1.0. A full combo is
therefore worth 25 pp at 3 stars, 50 at 5 and 75 at 7, and a play with half the map's longest
run collects half of that. WHY: as a factor, combo scaled the WHOLE play down for a run it had
already been charged for by the miss and typo terms, and non-FC plays were crushed. As a bonus,
a play keeps whatever its difficulty, cleanliness, typos and accuracy say it is worth, and a
long run adds to it. THE BONUS IS DELIBERATELY NOT GATED BY ACCURACY, and the consequence is
recorded rather than overlooked: since backlog 199 combo does not break on an off-time press, so
a full-combo run at 69 percent accuracy collects the whole bonus (qoiauve on The Words I Never
Said +HRFLLT goes 2 to 44 pp), and 178 plays in the 2026-09-01 corpus gain more than 15 pp from
the ungated form. Gating it by `acc^accuracy_exponent` times the knee was measured and moves
top-20 totals by only 1 to 4 percent, which is not worth making the bonus a second accuracy
term. THE PLACEMENT IS THE SUBSTANCE: the bonus sits OUTSIDE the mod multiplier, so a mod stack
moves the core pp of a play and leaves what the run itself is worth alone. THE MOD TABLE CHANGES
TOO. `rhythmic_multiplier` (RH, 1.10) is deleted: the mod left the client at backlog 147, so no
play can carry the acronym and the one stored row that does reprices 10 percent down, which is a
VERSION bump working as designed. The XMLDoc that argued against the deletion cited
`ModMultiplier.TotalScoreCeiling`, which is the SCORE-side table in a different file: it still
prices RH at 1.10 and is untouched, so that row stays under its own ceiling and stays ranked.
`recite_multiplier` (RE, 1.07) and `fletcher_strict_multiplier` (FC, 1.02) are added, for mods
whose gameplay change no star rating can see. Their values EQUALLING their score multipliers is
the user's chosen numbers and not a derivation rule: EZ is 0.75 here against 0.5x score, HR 1.25
against 1.10x, NF 0.90 against 0.5x and FL a length-scaled bonus against a flat 1.12x. EXPECTED
EFFECT, measured in the pp sandbox over the 2026-09-01 corpus against the companion star-rating
item's feats ratings: Noe 4778 to 4034 (-16 percent), lily 3826 to 3437 (-10 percent), qoiauve
2286 to 2460, iys 1584 to 1822, Aethaels 1281 to 1819, qd4 1109 to 1507, Allizion 900 to 1456,
melonmystery 837 to 1103, emma 613 to 1006, eiko 715 to 835; 40 of 55 players change rank. The
top two fall because the feats model prices Double Time against the record for the shortened
window, and everyone below gains from non-FC plays keeping their core pp. ORDERING: this bump
must land AFTER the star-rating backfill has run, or every stored row is repriced against stale
ratings and nothing reprices them when the ratings later move.

```
BEFORE:  max(0, 1 − miss^1.2/notes)^10  ·  max(0, 1 − typos^1.2/(notes + typos))^4
         ·  (ln(1 + 9.0·maxcombo/notes)/ln(1 + 9.0))^2.50

AFTER:   max(0, 1 − miss^1.2/notes)^10  ·  max(0, 1 − typos^1.2/(notes + typos))^4
         +  maxcombo/notes · max(0, 12.5·(SR_eff − 1.0))
```

SR, the global scale, accuracy, eligibility and the aggregation are all untouched. The typo
count still sits on both sides of its own fraction, for the reason the backlog-89 amendment
gives: keypresses are unbounded, and a fractional exponent on a negative base is non-real.

| play | before | after | change |
|------|--------|--------|--------|
| `notes=500, miss=60, typo=80` | `0.008341` | `0.008341` | +0% |
| `notes=500, miss=10, typo=20` | `0.542001` | `0.542001` | +0% |

**`VERSION` bumps to 21.** Every stored row the change values differently is repriced by
`PpBackfill` at the next boot, reading only columns; no migration is needed.


## Amendment (2026-09-18): the pricing shape forks, and the rating gains a judgement arm (v22, v23, v24)

SUPERSEDES the 2026-09-06 combo amendment's placement and the 2026-08-28 accuracy knee, and deletes
the typo term the 2026-08-07 split created. The formula block and every bullet at the top of this
file are the LIVE shape; this section is the record of what moved and why.

This is the pp half of a cross-repo change: the game's difficulty model was reworked in the same
landing (its shipped star rating is now a CHUNKED ENDURANCE axis carrying a typability adjustment
and a rhythmic-complexity multiplier, and it takes a JUDGEMENT ARM), and the server's mirrors were
brought back into step with it. `LyricPace.VERSION` bumps to 21 in the same change, which is what
re-rates the catalogue; `PerformancePoints.VERSION` bumps to 24, which is what reprices the score
table. THE ORDERING MATTERS AND IS SAFE IN EITHER DIRECTION: a play whose map has no stored matrix
cell yet is left PENDING rather than priced, so whichever sweep runs second settles what the first
could not.

### THREE VERSIONS, LANDED TOGETHER

**v22 is the shape fork**, tuned in the PP Sandbox (`tools/pp-sandbox/`), whose module is the same
arithmetic with every constant lifted into a dial. Four departures:

1. **The typo term is gone.** A wrong keypress the player recovered from costs nothing. The count is
   still derived and still displayed; nothing prices it.
2. **The miss penalty is judged against the map's DIFFICULT CHARACTERS, not its cell count.**
   Dropping a cell therefore costs more on a map whose difficulty is concentrated in a few passages,
   and the count is a property of the map rather than of the play.
3. **The loss curve is calibrated in fractions.** The power sits on the missed FRACTION, so the same
   miss RATE costs the same share of the core price on every map, where the old count-based shape
   made the cliff move with map size (46% of a 100-note map against 28% of a 2000-note one).
4. **The combo bonus multiplies the price instead of adding to it**, at a ceiling that scales with
   the map. A price zeroed by misses now stays zero.

**v23 has no changelog entry on the client**, which is worth stating outright rather than leaving as
a hole: the two departures it carries are documented where they act and the version number is the
only record that they landed together.

5. **The accuracy shape is a normalised exponential above a floor**, replacing
   `acc^accuracy_exponent` and leaving the soft knee inert at width 0. An accuracy at or below
   `acc_floor` (0.5) now earns exactly nothing, where the power curve only approached zero.
6. **Recite is a multiplied Flashlight bonus** rather than a flat term, because hiding the lyric is
   what Flashlight charges for and that cost grows with map length.

**v24 is the PP Sandbox's LIVE dials**, re-read from the lab after the owner retuned it. The Easy
multiplier drops 0.9 to 0.85, and the knee position is written as the 0 the lab's panel holds (inert
either way, since the width is 0). Every other dial already agreed with the lab: scale 9,
sr_exponent 2.30, count_power 1.2, acc_steepness 1.75, acc_floor 0.5, knee off, miss_exponent
13.5134, the combo cap and kicker, reference_notes 100, Recite 2.0, Hard Rock neutral, Fletcher and
No Fail 0.9.

### THE JUDGEMENT ARM IS A RATING INPUT, NOT A MULTIPLIER

Easy and Hard Rock move the engine's own windows, and since the rework the star rating prices the
INTERVALS a press may land in: Easy doubles every window and shelters the whole word, Hard Rock keeps
normal windows but puts every cell on its own point target. Those are different ratings, so the arm
selects one, and paying for it again in `modMult` would be exactly the double count this file forbids
for DT/HT and LT. Hard Rock's flat term is therefore NEUTRAL, and Easy's 0.85 is only what the lab
charges for what is left.

### THE STORAGE CONSEQUENCE: EIGHTEEN READINGS, NOT SIX

The arm is orthogonal to the stream and to the rate, so the server stores their cross product: three
arms times two streams times three rates. Each cell carries BOTH halves a price needs, the stars and
the difficult characters, because a caller holding one without the other would have to invent the
missing half and the two ways of doing that price the same play very differently. That is
`beatmaps.ratings`, a nullable jsonb column (034_ratings_matrix.sql).

NULL IS THE UNFILLED STATE, not a rating of zero, and it has exactly the contract `sr_dt` has had
since 020: a play whose cell is missing is NOT SETTLED, earns 0 for now and is left stale for
`PpBackfill` to revisit. That is stricter than the six columns were, because `difficulty_rating` is
NOT NULL and a no-mod play could never be pending before; on a row the pace sweep has not reached,
every play is pending now, including a browser `/play` one.

The six legacy columns are the matrix's ARM-NONE stars and stay written, from the same parse: they
are what the set page and the listing cards print, what the client's own beatmap lookup reads off the
API, and what the search filters sort and range on, none of which know anything about a judgement
arm.

### WHAT REPRICES

Everything. The scale, the rating exponent, the cleanliness shape, the accuracy shape and the combo
placement all moved, so no stored row is left at its old value, and the star ratings the prices are
read from moved underneath them as well. Both sweeps run at the next boot.
