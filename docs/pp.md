# type!beat performance points (pp) — canonical spec for backlog task 61

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
pp = C · SR_eff^2.70
       · (1 − miss/notes)^8.5                  # cleanliness (see the 2026-08-07 amendment)
       · (1 − mistypes/(notes+mistypes))^3.5   # mistyping  (see the 2026-08-07 amendment)
       · max(0.1, 1 + 0.70·log10(notes/100))   # length bonus (clamped)
       · acc^1.30                          # accuracy (timing quality)
       · (maxcombo/notes)^0.55             # combo
       · modMult                           # NOT for DT/HT; rate lives in SR_eff only

C = 4.0    # global scale constant, does not affect ranking order
```

Factor by factor, in descending priority:

* **SR_eff^2.70**: difficulty is the primary driver. SR_eff is the map's star rating
  **recomputed at the play's clock rate** for DT/HT (see mods below), not the base SR.
* **cleanliness^8.5**: dropped cells are the sharp cleanliness signal. This is what stops a sloppy
  high-SR play from farming pp. A give-up run (e.g. 900+ misses) collapses to ~0.
* **mistyping^3.5**: wrong keypresses, priced separately since the 2026-08-07 amendment. Real but
  much gentler than a dropped cell: a stumble you recover from is not the same failure as never
  typing the cell at all.
* **Length**: the standard osu log bonus, rewarding sustained play over long maps. Clamped to
  a small positive floor: the raw term crosses zero around 4 notes, and no play should ever
  compute to zero or negative pp from length alone.
* **acc^1.30**: deliberately **gentle**, unlike osu. In type!beat real accuracies live at
  55–93%, not 97–100%, so an osu-style steep curve (acc^6+) would crush everything and make
  accuracy dominate. Keep the exponent around 1–2.
* **(maxcombo/notes)^0.55**: mild. Combo overlaps with misses (a miss breaks combo), so it's
  only a light signal on top, not a second heavy penalty. Combo can also break without a miss
  (a badly-timed hit), so it still matters, and it distinguishes spread-out misses from one
  choke that dropped several.

**Definitions (pinned to the score row):**

* `acc` is standard osu hit accuracy: `(300·n300 + 100·n100 + 50·n50) / (300·notes)`. This is
  the stored `accuracy` column for a completed play.
* `notes = great + ok + meh + miss` from `statistics`. **`ignore_hit` is excluded**; the line
  containers would otherwise inflate `notes` and dilute every factor.
* `maxcombo` is the stored `max_combo`; the theoretical max equals `notes` for a typing map.

## Eligibility

pp is computed **only for ranked scores on ranked maps**. That inherits the existing gates for
free: fails, unranked-mod plays (RX/WU/WD/Mashing) and out-of-bounds submissions are already
stored `ranked = false` and therefore earn no pp.

## Mods

* **DT / HT**: rate is priced **exclusively through SR_eff** (SR recomputed at the play's clock
  rate); there is NO flat DT/HT multiplier in modMult, so nothing double-counts. DECIDED:
  only the **base rates** (DT 1.5x, HT 0.75x) are pp-eligible. A custom rate makes the play
  **pp-ineligible only**: it still ranks on the score leaderboards exactly as today (the
  variable-rate ranking feature is preserved, no retroactive unranking), it just earns 0 pp.
  Implementation consequence: the server only ever needs SR at three rates (1.0 / 1.5 / 0.75);
  store `sr_dt` / `sr_ht` per beatmap at ingest and backfill via the existing pace VERSION-bump
  mechanism. No on-the-fly rate-SR math.
* **LT** (like HD): flat × 1.06.
* **FL** (flashlight): × `max(1.0, 1 + 0.02 + 0.06·log10(notes/100))`. Much smaller than osu's
  flashlight bonus, but grows with song length, so it pays off on long maps. The `max` clamp is
  required: unclamped, the raw term dips **below 1.0 under ~46 notes**, which would punish FL on
  short maps rather than "barely move".
* **Fletcher**: × 0.90 (10% pp decrease).
* **NF** (No Fail): × 0.90, osu's pricing. DECIDED: NF cannot be free for pp, since it converts
  a would-be fail (which earns nothing) into a completed play, and it protects runs the miss
  penalty only partially catches. The 0.5x score multiplier stays score-side only; mirroring it
  in pp would double-punish on top of the miss term.
* **SD / MU**: × 1.0 (no effect, matching their score multipliers).

```
modMult = (LT       ? 1.06                                      : 1)
        · (FL       ? max(1.0, 1 + 0.02 + 0.06·log10(notes/100)) : 1)
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
