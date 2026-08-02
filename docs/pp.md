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
       · (1 − miss/notes)^7.5              # cleanliness (misses)
       · max(0.1, 1 + 0.70·log10(notes/100))   # length bonus (clamped)
       · acc^1.30                          # accuracy (timing quality)
       · (maxcombo/notes)^0.55             # combo
       · modMult                           # NOT for DT/HT; rate lives in SR_eff only

C = 4.0    # global scale constant, does not affect ranking order
```

Factor by factor, in descending priority:

* **SR_eff^2.70**: difficulty is the primary driver. SR_eff is the map's star rating
  **recomputed at the play's clock rate** for DT/HT (see mods below), not the base SR.
* **(1 − miss/notes)^7.5**: misses are the sharp cleanliness signal. This is what stops a
  sloppy high-SR play from farming pp. A give-up run (e.g. 900+ misses) collapses to ~0.
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
