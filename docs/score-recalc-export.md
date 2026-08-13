# Handing a score-recalc sweep over without database access

`tools/score-recalc` normally reads the `scores` table directly. It does not have to. `--offline`
runs the whole re-derivation off a directory of files, which is the least-privilege way to get a
sweep reviewed by someone who should not be given a production connection string: they receive
replays and a couple of JSON files, and nothing else leaves the box.

This is the shape that directory has to be in, and exactly what each part buys.

## The directory

```
<export>/
  replays/<score id>.osr     one per score, named by its scores.id
  sets/*.osz                 one package per beatmap set the replays reference
  stars.json                 optional, passed as --stars
  scores.json                optional, passed as --scores
```

```
dotnet run --project tools/score-recalc -- supersede-report \
    --offline <export> --stars <export>/stars.json --scores <export>/scores.json \
    --out supersede.json
```

## What the sweep being reviewed actually does

Worth knowing before reading a report, because it decides what "before" and "after" mean.

A `supersede-report` re-judges every run under all of today's rules (`TypoRule.Deferred` plus
`ComboRestoreRule.OnFix`) **plus the Rhythmic mod**, which puts the run back on the MILLISECOND
ladder it was typed against, and the superseded row **gains `RH`** in `scores.mods`. That is what
makes the result reproducible: anyone can replay the score with Rhythmic selected and land on the
same numbers, where a no-mod row judged on the timing ladder would describe a game no client runs.

Two consequences a reviewer should expect to see, both accepted rather than accidental:

- **Every superseded score gains 10% pp**, because `RH` carries a 1.10 multiplier the player never
  chose. It is the price of the row being reproducible.
- **It does not reproduce the stored numbers, and cannot be tuned until it does.** The millisecond
  ladder's Great/Ok/Meh rows are byte-identical to the pre-133 windows, but backlog 133 added a
  fourth tier and moved the weights, so a press in the 125-250 ms early or 200-400 ms late band that
  used to score as the top tier now scores as the second. Accuracy falls for such presses.

A row that already carries `RH` is still superseded, for the typo and combo rules, but it does not
gain the mod or its price twice.

### `replays/` (required)

The `.osr` bytes as stored, one file per score, named `<score id>.osr`. The file name is the only
thing that carries the score id, so it has to be the real one.

An `.osr` is self-describing: its trailing `LegacyReplaySoloScoreInfo` blob carries the
`statistics`, `maximum_statistics`, mods, rank and total score exactly as the client computed them,
and `ScoreEndpoints.SubmitScore` stores the submitted dictionaries verbatim. So the replay alone is
a faithful copy of most of the row, which is what makes an offline before/after possible at all.

The mods come from that blob too, which is why an offline report can show the `RH` gain and price it
the same way a database run does: there is no `mods` field in `scores.json` and none is needed.

### `sets/` (required)

One `.osz` (or `.typb`) per set the replays reference. The tool matches a replay to its beatmap by
the replay's own MD5, never by id, so a set whose package has been re-uploaded since resolves the
exact `.osu` the run was judged against or resolves nothing at all rather than the wrong one.

A set that is missing here reads as `beatmap-missing`, which is the case a supersede apply refuses
to write around. An offline run cannot tell that apart from `beatmap-changed` (it has no beatmap row
to compare a checksum against), so it reports the conservative one.

### `stars.json` (optional, `--stars`)

```json
{ "<beatmap md5>": 4.31 }
```

The base star rating per beatmap hash. Without it every play prices at 0 pp and the report's pp
columns are empty.

### `scores.json` (optional, `--scores`)

Keyed by score id as a string. Every field is optional; supply what you can.

```json
{
  "9214": {
    "pp": 118.4,
    "ranked": true,
    "passed": true,
    "beatmap_id": 77,
    "user_id": 12,
    "stars": 4.31,
    "sr_dt": 5.66,
    "sr_ht": 3.12,
    "sr_literate": 3.90,
    "sr_literate_dt": 5.02,
    "sr_literate_ht": 2.81
  }
}
```

| field | what it buys |
|---|---|
| `pp` | the pp BEFORE column. An `.osr` does not carry pp, so without this the report can only show the superseding value. |
| `passed` | tells a failed run from a passed one. Without it every replay is assumed passed, and a failed run would be re-derived when it must not be (health is not simulated). |
| `ranked` | stops an already-unranked row being counted as a live leaderboard entry. |
| `beatmap_id`, `user_id` | the leaderboard-impact section, which groups by map and takes each user's best. Without them it prints nothing rather than guessing. |
| `stars` | same as `stars.json`, per score rather than per hash; takes precedence when both are present. |
| `sr_dt`, `sr_ht`, `sr_literate`, `sr_literate_dt`, `sr_literate_ht` | a rate or Literate play prices through its own converted rating. Without them such a play prices at nothing rather than wrongly. |

The straight SQL for it:

```sql
SELECT s.id, s.pp, s.ranked, s.passed, s.beatmap_id, s.user_id,
       b.difficulty_rating AS stars, b.sr_dt, b.sr_ht,
       b.sr_literate, b.sr_literate_dt, b.sr_literate_ht
FROM scores s JOIN beatmaps b ON b.id = s.beatmap_id
WHERE s.ruleset_id = 0;
```

None of those columns is a secret, and none of them is a replay.

## What an offline run still cannot do

- **It cannot apply.** Both `apply` and `supersede-apply` refuse `--offline`. Writing needs the
  database, and the database is the thing the export exists to avoid handing over.
- **Its row count is not the one `--expect-superseded` wants.** That guard compares against what the
  run in front of it would write, and an offline run's row list is whatever `.osr` files were
  exported, not what the `scores` table holds. Take the number from a **database**
  `supersede-report`, run with the same options as the apply.
- **It cannot tell `beatmap-missing` from `beatmap-changed`**, because the discrimination is a
  comparison against `beatmaps.checksum_md5`. Everything unresolved reads as `beatmap-missing`.

So the workflow is: export, review offline, then run the database `supersede-report` and the
`supersede-apply` from a box that has the connection string.
