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

### `replays/` (required)

The `.osr` bytes as stored, one file per score, named `<score id>.osr`. The file name is the only
thing that carries the score id, so it has to be the real one.

An `.osr` is self-describing: its trailing `LegacyReplaySoloScoreInfo` blob carries the
`statistics`, `maximum_statistics`, mods, rank and total score exactly as the client computed them,
and `ScoreEndpoints.SubmitScore` stores the submitted dictionaries verbatim. So the replay alone is
a faithful copy of most of the row, which is what makes an offline before/after possible at all.

That includes the ERA STAMP, so an offline run classifies rows exactly as a database run does and
nothing extra has to be exported for it. A row judged in the backlog 133-to-147 window is told from
any other by its `maximum_statistics` carrying `perfect` and no `great`
(`ScoringContract.JudgedUnderTheFourthTier`), that dictionary rides in the blob, and the decoder only
synthesises one when the blob's is empty. A replay with no score-info blob at all therefore reads as
the current era, which is the same reading the row itself would get.

The same goes for the TYPO era: a row is reproduced under `TypoRule.Deferred` when its own
`statistics` carry an uncorrected typo (the `good` key, `ScoringContract.CarriesAnUncorrectedTypo`)
and under `TypoRule.ImmediateMiss` otherwise, and `statistics` rides in the same blob. The test only
runs one way: the key proves the newer rule judged the row, while its absence proves nothing, because
a run that left no wrong character standing has no such key whichever rule judged it.

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

## What the report now names, and what the apply now asks for

A supersede report prints one more population, in the headline block and again under the
reproduction section:

```
FROM THE 133-TO-147 WINDOW   7   <- pass this to --expect-unreproducible
...
reproduced exactly           184  (94.4%)
did not reproduce            11   (5.6%)
  from the 133-to-147 window 7
  unexplained                4
```

Those rows were judged on a four-tier character-distance ladder that backlog 147 deleted, so nothing
can re-derive them and nothing can check what they are being replaced with. They are superseded like
any other row, which is what superseding is for, but they are counted separately from the rows that
fail to reproduce for a reason nobody has explained: the second number is the one to read before
applying anything, and folding the two together would make a sweep with four anomalies in it look
like a sweep with none.

They are NOT an `--unreplayable` case. Every case there means nothing can be derived from the row;
one of these derives perfectly well, and only the CHECK is missing.

`supersede-apply` therefore wants a fourth confirmation, `--expect-unreproducible <n>`, alongside
`--expect-superseded`. It changes no behaviour: it exists so the sweep cannot be started by anyone
who has not read how many of its rows are being written on numbers nothing can verify.

## What an offline run still cannot do

- **It cannot apply.** Both `apply` and `supersede-apply` refuse `--offline`. Writing needs the
  database, and the database is the thing the export exists to avoid handing over.
- **Neither of its counts is the one the apply's guards want.** `--expect-superseded` and
  `--expect-unreproducible` are both compared against the run in front of them, and an offline run's
  row list is whatever `.osr` files were exported, not what the `scores` table holds. An offline
  export of ten replays says nothing about how many rows in the table carry the window's era stamp.
  Take both numbers from a **database** `supersede-report`, run with the same options as the apply.
- **It cannot tell `beatmap-missing` from `beatmap-changed`**, because the discrimination is a
  comparison against `beatmaps.checksum_md5`. Everything unresolved reads as `beatmap-missing`.

So the workflow is: export, review offline, then run the database `supersede-report` and the
`supersede-apply` from a box that has the connection string.
