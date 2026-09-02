# CLAUDE.md

Guidance for anyone (human or agent) working inside `typebeat-web`, the **type!beat backend and
website**: an ASP.NET Core monolith (Razor Pages plus minimal-API endpoints) over Postgres. Runs in
production at `typebeat.mingda.sh`.

Most work here is done by an agent spawned into a **git worktree** under `.claude/worktrees/<slug>`
by an orchestrator running in the parent superrepo. The rules below are the ones that get re-typed
into every task brief otherwise, so they live here instead.

## Environment landmines, read these first

**`dotnet` on PATH is a broken Microsoft Store stub** ("No .NET SDKs were found"). Always the real
one: PowerShell `& 'C:\Program Files\dotnet\dotnet.exe' …`, Bash `"/c/Program Files/dotnet/dotnet.exe" …`.

**The database is native PostgreSQL 17**, Windows service `postgresql-x64-17` on `localhost:5432`,
**not Docker** (Docker Desktop crash-loops on this machine). `compose.dev.yml` exists but is not
used here. Default connection is
`Host=localhost;Port=5432;Database=typebeat;Username=typebeat;Password=typebeat`, overridable with
`TYPEBEAT_DB`.

## Build and test

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' test tests/Typebeat.Web.Tests
& 'C:\Program Files\dotnet\dotnet.exe' test tests/Typebeat.WireCompat
```

Run the app with `& 'C:\Program Files\dotnet\dotnet.exe' run --project src/Typebeat.Web` (port
**5089**). **Agents must not run the app**, run migrations, or write to the database. Reading it is
fine.

### WireCompat does not resolve from a worktree, and neither does a plain `dotnet build`

`tests/Typebeat.WireCompat` compiles **both** repos so it can hold the mirrored scoring code against
itself. Its csproj resolves the game two ways: it prefers a sibling checkout at
`..\..\..\typebeat-osu\…`, and falls back to the pinned `..\..\external\typebeat-osu\…` submodule.

**From a worktree both paths miss.** The sibling path resolves into `.claude/worktrees/`, and the
worktree's own `external/typebeat-osu` is an empty, unpopulated gitlink. A bare `dotnet build` then
fails with ~26 `CS0246`, because the solution build pulls WireCompat in.

The csproj anticipates this ("kept as its own property … so a caller can point either at a worktree
independently"). Override on the command line, where MSBuild global properties beat the in-file
`Condition="Exists(...)"`:

```
-p:TypebeatGameProject='<game path>\typebeat.Game\typebeat.Game.csproj'
-p:TypebeatRulesetProject='<game path>\typebeat.Game.Rulesets.TypeBeat\typebeat.Game.Rulesets.TypeBeat.csproj'
```

Pass them to `build` **and** to every `test` invocation. Point at the **game worktree** when both
halves of a cross-repo change must be proven together; point at the **main game checkout** for a
web-only change.

**Never populate `external/typebeat-osu`, advance the submodule pin, or edit the csproj fallback
paths.** That pin is CI's, and moving it is the orchestrator's job at landing time.

### The test suite shares one Postgres and is not isolated per run

Two agents running the suite at once corrupt each other. A red run is **not** automatically a
regression:

- Contention looks like a large, **changing** set of DB/upload/auth failures (`FullPackageUpload_*`,
  `Reset_*`, `Pin_*`, `CreateSet_*`). Observed: an unchanged tree went 25 failed, then 30+ failed,
  then fully green across three consecutive runs.
- A real regression fails the **same named tests every time**.

So: **re-run before concluding anything**, and report both runs. To prove your own change while the
box is busy, filter: `--filter "FullyQualifiedName~<Area>"`.

**Do not add a new `WebApplicationFactory` host.** They are heavy enough to destabilise the shared
fixture; one worker caused ~100 spurious failures that way, and another ~116. Extend an existing
host, or make the unit under test a pure static and drive it directly (`DownloadModel.DetectOs` and
`IndexModel.AnyGameDownloadAvailableAsync` are public static precisely so they can be tested without
a host).

## Gate protocol: filter while iterating, full suite once

The full suite is ~30s, and a non-vacuity protocol that re-runs everything for each deliberate break
burns that many times over for no extra signal (a filtered pp run is ~1s against ~30s).

- **While iterating, and for every break/revert cycle**, run only the area your change can affect.
- **Run the full suite plus WireCompat once, at the end**, and report both.

The orchestrator re-runs the full gate in the main checkout after merging, so your final run is a
check, not the last line of defence.

## Rules for agents working here

- **Never `git push`.** Commit on your own worktree branch. The user pushes; agents never do.
- **Never add `Co-Authored-By`, `Claude-Session`, or any AI-attribution trailer** to a commit
  message, and do not mention AI or agents anywhere in the repo.
- **Never use em dashes** anywhere: code, comments, XMLDoc, Razor, CSS, markdown, commit messages.
  Use commas, colons or parentheses.
- **No deploys, no SSH, no production data operations.** Surface them instead.
- Do not edit anything under `.claude/worktrees/` belonging to another branch.

## Code mirrored in the game repo, which must not drift

These exist twice and a divergence corrupts the **shared** leaderboards or lies to the player.
WireCompat is where that is provable, because it is the only project that compiles both repos.

| here | there (`typebeat-osu`) |
|---|---|
| `src/Typebeat.Web/Scoring/PerformancePoints.cs` | `typebeat.Game.Rulesets.TypeBeat/Scoring/PerformancePoints.cs` |
| `src/Typebeat.Web/Packages/Lyrics/LyricDifficulty.cs` | `typebeat.Game.Rulesets.TypeBeat/Beatmaps/LyricDifficulty.cs` |
| `src/Typebeat.Web/Packages/Lyrics/LyricWpmCurve.cs` | `typebeat.Game.Rulesets.TypeBeat/Beatmaps/LyricWpmCurve.cs` |
| `src/Typebeat.Web/Packages/Lyrics/InstrumentalGaps.cs` | `typebeat.Game.Rulesets.TypeBeat/Gameplay/InstrumentalGaps.cs` |
| `src/Typebeat.Web/wwwroot/js/typebeat-player.js` (`computeGaps` + its four constants) | the same `InstrumentalGaps.cs`, via the row above |
| `src/Typebeat.Web/wwwroot/js/typebeat-core.js` | the C# `TypingEngine` / `TypeBeatScoreProcessor` |

- **`typebeat-core.js` is a hand-written JS reimplementation of the C# engine** and must stay
  byte-compatible, or browser `/play` scores diverge from desktop on the same leaderboards. Any
  engine edit needs a matching JS edit. It does **not** compute pp. Since backlog 179 it also ports
  `Gameplay/Syllabifier.cs` and `TypingLine.buildSyllables`, because a press on a grouped cell is
  judged against its syllable's sung SPAN: a split one character off moves a real judgement. The
  browser has no era axis (it only plays live, writes no replay frames, and `/play/submit` carries
  the aggregate account alone), so it judges on spans unconditionally, which is why
  `EngineFuzzLiveParityTest` has to set **flags bit 2** in the CONFIG frames it feeds the C# arm:
  `TypeBeatReplayScorer` follows the frame and defaults to the classic point rule. Since backlog 247
  that is true of **bit 8** too (the syllable's OPENING cell is judged from the span's START, not paid
  0 anywhere inside it), and the JS pins whose scripts press an opening cell late therefore differ
  from the game fixtures they mirror, whose engines leave that era off. Since backlog 259 it is true
  of **bit 10** as well (a sealed line's combo break is BACK-DATED to the cells it misses, so the
  run earned past them survives instead of being wiped), which is also why every parity test that
  builds a bare `TypingEngine` for the live arm has to set `BackDatedSealBreak = true`:
  `SealComboBreakLiveParityTest` is the pin, and it is the only one whose scripts reach the shape
  (the generated sweep never rolls a seal a rebuilt run outlives). Since backlog 260 **bit 11**
  joins them (an accidental word skip that is then typed out in full costs the run NOTHING: the
  rush cap measures a skipping space at the caret it started from rather than the one the skip
  moved it to, and a passive claim break folds its own spent run into the claim it leaves
  standing), so every such parity fixture also sets `LosslessSkipReclaim = true`.
  `LosslessSkipReclaimLiveParityTest` is the pin. Its third defect, the Ctrl+A anchor being widened
  past a wholly abandoned word so the mass backspace cannot overshoot its own selection, carries no
  era at all (it is input layer) and is pinned in `WordInputParityTest`, which is where the whole
  gesture composition lives. A harness that SPIES on an engine method must forward every argument
  (`CoreFuzzHarness.cjs` wraps `rushesPastCap`): a dropped one reads out of the prefix table as NaN
  and silently answers "no rush" for the entire sweep.
- **The .osu FORMAT VERSION GATE exists three times** since backlog 255, and all three read the
  magic line the same way (digits after `type!beat file format v`, fallback 1): the game's
  `LyricBeatmapDecoder.ParseFormatVersion`, `BeatmapPackageParser.ParseFormatVersion` here, and
  `parseFormatVersion` in `typebeat-core.js`. It decides one thing, whether a bracket in a stored
  `[Lyrics]` line is a backing vocal to strip (v1, and anything unversioned) or a literal lyric
  mark to keep (v2 and up, what the game's writer stamps now). `/play` is served the same stored
  .osu blob desktop decodes, so a one-sided edit gives the two clients different cells on the same
  leaderboards. `PunctuationParityTest` pins the JS copy against the C# one over the same fixture
  at both versions.
- **`docs/pp.md` is the canonical pp spec**: every constant in `PerformancePoints.cs` is pinned there
  and must not drift from it. `PerformancePoints.VERSION` is shared with the game copy, stamps
  `scores.pp_version`, and drives `Packages/PpBackfill.cs`'s reprice-at-boot sweep, so bumping it is
  how a formula change reaches stored rows. No migration is needed for a bump.
- **Completion-rank cutoffs** (X/S/A/B/C = 100/95/90/80/70) are mirrored in three places that must
  agree: the game's `TypeBeatScoreProcessor`, `ScoringContract.RankFromCompletion`, and migration
  `008_completion_rank.sql`.
- **`LyricWpmCurve` is the map's rolling-window pace** (peak WPM, peak CPM, and the downsampled WPM
  curve), computed locally by song select and stored here on the beatmap row for the set page's WPM
  tab (`028_wpm_curve.sql`). It is deliberately NOT on the wire, so the mirror is the only thing
  keeping the two readouts equal. It stores, so a change needs a `LyricPace.VERSION` bump too.
- **`InstrumentalGaps` must stay in lockstep** with the game copy (`MIN_GAP_MS` 10000 and the
  perceived-gap/skip-window rules): the play-time anti-cheat gate subtracts the skip allowance it
  computes, so drift either re-unranks honest skip users or lets impossibly fast plays rank. Since
  backlog 230 the rule exists a THIRD time, as `computeGaps` in `typebeat-player.js`, because the
  browser player has a real skip button now and it must spend exactly the allowance the gate
  refunds. `WebplayDisplayTest` pins the JS copy against the C# one by running BOTH over the same
  lyric fixtures, so a one-sided edit fails there rather than in production; the JS constants carry
  the name of the C# field they mirror, and are read off it rather than retyped.

There is a tool for pp changes: `tools/pp.py` in the parent superrepo (`show` / `check` / `set`),
which propagates a constant across both mirrors, `docs/pp.md` and the test expectations. Prefer it
over hand-editing, and if it cannot express your change, say so rather than working around it.
