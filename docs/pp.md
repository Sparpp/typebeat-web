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
pp = C · SR_eff^2.00
       · max(0, 1 − miss^1.2/notes)^10                   # cleanliness
       · max(0, 1 − typos^1.2/(notes+typos))^4           # typos
       · max(0.1, 1 + 0.50·log10(notes/100))             # length bonus (clamped)
       · acc^1.80                                        # accuracy (timing quality)
       · (ln(1 + 9.0·maxcombo/notes)/ln(1 + 9.0))^2.50   # combo
       · modMult                                         # NOT for DT/HT; rate lives in SR_eff only
       · rateMult                                        # 1.0 except base-rate HT (see the Half Time amendment)

C = 9.6    # global scale constant, does not affect ranking order
```

Factor by factor, in descending priority:

* **SR_eff^2.00**: difficulty is the primary driver. SR_eff is the map's star rating
  **recomputed at the play's clock rate** for DT/HT and **on the map the conversion mods produced**
  for LT (see mods below), not the base SR.
* **cleanliness^10**: dropped cells. The raw COUNT carries a power, not the ratio, since the
  backlog-97 amendment, and that power has been the declared constant `count_power` since the
  backlog-101 one. It stands at 1.6. That makes this a steep curve and a CLAMPED one: the base
  `1 - miss^1.6/notes` reaches zero at `miss = notes^(1/1.6)`, i.e. 49 misses on a 500-note map, and
  `max(0, ...)` holds it there rather than letting it go negative. Past that point a play earns
  exactly nothing from any factor, and well before it the term is already negligible. A give-up run
  (e.g. 900+ misses) collapses to exactly 0.
* **typos^4**: wrong keypresses, priced separately since the backlog-89 amendment, and with its
  own count under the same power since the backlog-97 one. Still the cheaper of the two failures
  (4 against 10), because a stumble you recover from is not the same failure as never typing the
  cell at all, and because the count sits in its denominator too, which pushes its cliff out to the
  positive root of `m^1.6 - m - notes = 0` (52 typos at 500 notes) rather than to
  `notes^(1/1.6)`.
* **`count_power`** is where a rebalance of the two penalties is made, rather than the exponents 10
  and 4: it alone decides at what count each term reaches its cliff, and how that cliff scales with
  map size. The backlog-101 amendment records the two arguments that were used to set it, at 1.2;
  v8 retuned it to 1.6, so both cliffs now sit lower than the counts stated there.
* **Length**: the standard osu log bonus, rewarding sustained play over long maps. Clamped to
  a small positive floor so no play ever computes to zero or negative pp from length alone. At a
  weight of 0.50 the raw term crosses zero at exactly 1 note and the 0.1 floor at ~1.585, so the
  clamp is close to vestigial; at the old 0.70 those crossings sat at ~3.73 and ~5.18 notes. The
  floor stays because it is the guard, not because it currently fires often.
* **acc^1.80**: deliberately **gentle**, unlike osu. In type!beat real accuracies live at
  55–93%, not 97–100%, so an osu-style steep curve (acc^6+) would crush everything and make
  accuracy dominate. Keep the exponent around 1–2.
* **(ln(1 + 9.0·maxcombo/notes)/ln(1 + 9.0))^2.50**: near enough **linear** in the combo ratio
  down to about 0.7. The exponent is steep, but the log base is concave and very nearly cancels
  it over the range real plays live in (backlog 131), so a broken combo costs roughly its face
  value rather than several times it. That is the point: combo overlaps with misses (a miss
  breaks combo), so it must not read as a second heavy penalty. It still earns its place,
  because combo can break without a miss (a badly-timed hit) and because it distinguishes
  spread-out misses from one choke that dropped several.

**Definitions (pinned to the score row):**

* `acc` is standard osu hit accuracy, over the four quality tiers a cell can land in:
  `(300·perfect + 200·great + 100·ok + 50·meh + 50·good) / (300·notes)`. This is the stored
  `accuracy` column for a completed play. (`perfect` and the 200 for `great` arrived with backlog
  133's fourth tier; before it the top tier was `great` at 300. `good` is the uncorrected typo,
  re-weighted to the `meh` value.)
* `notes = perfect + great + ok + meh + good + miss` from `statistics`. **`ignore_hit` is
  excluded**; the line containers would otherwise inflate `notes` and dilute every factor.
* `maxcombo` is the stored `max_combo`; the theoretical max equals `notes` for a typing map.

## Eligibility

pp is computed **only for ranked scores on ranked maps**. That inherits the existing gates for
free: fails, unranked-mod plays (RX/WU/WD/Mashing) and out-of-bounds submissions are already
stored `ranked = false` and therefore earn no pp.

## Mods

* **DT / HT**: rate is priced **exclusively through SR_eff** (SR recomputed at the play's clock
  rate); there is NO flat DT/HT multiplier in modMult, so nothing double-counts. **Half Time
  additionally carries the mirror penalty of the 2026-08-07 amendment below**, which is still not in
  modMult (it needs all three star ratings, which modMult does not have). DECIDED:
  only the **base rates** (DT 1.5x, HT 0.75x) are pp-eligible. A custom rate makes the play
  **pp-ineligible only**: it still ranks on the score leaderboards exactly as today (the
  variable-rate ranking feature is preserved, no retroactive unranking), it just earns 0 pp.
  Implementation consequence: the server only ever needs SR at three rates (1.0 / 1.5 / 0.75)
  per cell stream; store `sr_dt` / `sr_ht` (and, since the Literate amendment, `sr_literate` /
  `sr_literate_dt` / `sr_literate_ht`) per beatmap at ingest and backfill via the existing pace
  VERSION-bump mechanism. No on-the-fly rate-SR math.
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
* **RH** (Rhythmic): flat × 1.10. The play is judged on the MILLISECOND window ladder (each
  character against its own target time) instead of the character-distance one (how far the press
  is from the character the playhead is on). The two coincide at a pace of 10 characters per
  second and the millisecond pair is tighter everywhere below that, which is where lyrics sit, so
  it is a genuine difficulty increase on essentially every map. Above that pace, in a burst faster
  than 10 chars/sec, it is the looser pair, which is why the bonus is a flat 1.10 rather than the
  much larger number the slow-map ratio alone would suggest.
* **Fletcher**: × 0.90 (10% pp decrease).
* **NF** (No Fail): × 0.90, osu's pricing. DECIDED: NF cannot be free for pp, since it converts
  a would-be fail (which earns nothing) into a completed play, and it protects runs the miss
  penalty only partially catches. The 0.5x score multiplier stays score-side only; mirroring it
  in pp would double-punish on top of the miss term.
* **SD / MU**: × 1.0 (no effect, matching their score multipliers).

```
modMult = (FL       ? max(1.0, 1 + 0.02 + 0.06·log10(notes/100)) : 1)
        · (RH       ? 1.10                                      : 1)
        · (Fletcher ? 0.90                                      : 1)
        · (NF       ? 0.90                                      : 1)
```

## Aggregation

DECIDED: osu semantics, per-map dedup with a weighted sum over **all** deduped plays (not a
hard top-10 cutoff):

1. For each ranked map, keep only the player's **best-pp play** (without this, replays of one
   hard map could fill the entire top list).
2. Sort those by pp descending and sum with decay:

```
total_pp = Σ pp_i · decay^i     (best play i = 0, over ALL deduped plays)
decay = 0.85, raised toward osu's 0.95 as the ranked pool grows
```

With decay 0.85 the tail vanishes fast (the 10th play carries ~20% weight, the 20th ~3.9%), so
"your top 10 is what matters" is effectively true without a cliff where the 11th-best play
contributes exactly nothing.

The low decay keeps the top plays dominant while the map count is small; grow it as there are
more maps for depth to matter. At current scale (tens of users, ~1.5k scores), `total_pp` is
**computed on read**, not stored, so a decay bump later is a one-line config change with no
migration or recompute job.

## What the system values

In order: clearing **harder** maps, and clearing them **cleanly**. Difficulty sets the ceiling
of a play, misses decide how much of that ceiling you actually keep, and length gives sustained
hard play its proper reward. Accuracy and combo are gentle secondary signals, because in a
typing game raw accuracy is already hard to push and largely tracks the misses. The per-map
dedup plus weighted top-N then makes your rank the sum of your best performances, not a reward
for volume, so grinding easy maps (or one hard map) stops mattering once there are enough maps.
In short: it rewards the player who clears the hardest maps with the fewest misses, which is
the opposite of what cumulative score rewards today.

## Decision log (2026-07-28)

* DT/HT base-rate reward comes from **SR_eff only**; the earlier "1.5x pp weighting" phrasing
  described intent, not an extra multiplier. No flat rate multiplier exists in modMult.
* Custom DT/HT rates are **pp-ineligible only**; score-leaderboard ranking at every rate is
  preserved, nothing retroactively unranked.
* Aggregation: **best play per map, weighted sum over all** with decay 0.85; no hard top-10
  truncation.
* **NF priced at × 0.90** for pp (osu's value); omission would have made it a free mod.
* `notes` excludes `ignore_hit`; length and FL factors carry floor clamps.
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

> **Figures superseded, mechanism in force.** The mirror multiplier is exactly as this section
> defines it, but `sr_exponent` has since moved from the 2.70 written throughout it to 2.00, so
> every D, H and percentage below is a figure at 2.70. On the same fixture spread at 2.00:
> `D = 2.109`, `H = 0.655`, `m = 0.723`, and Half Time's total factor `0.474` against the `0.655`
> it would carry without the mirror.

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

> **Numbers superseded by v8**, which retuned `count_power` from the 1.2 this amendment settled on
> to the 1.6 in force. The shape is unchanged and the two arguments below still apply, but every
> figure in this section is a figure at 1.2: at 1.6 the cleanliness cliff is 49 misses on a
> 500-note map rather than 178, and the mistyping cliff 52 rather than 249. v8 carries no amendment
> of its own; see the note in the backlog-112 amendment below.

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
LyricDifficulty ends in star_scale times raw^star_power with the rate entering raw ADDITIVELY,
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
