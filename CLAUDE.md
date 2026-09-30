# CLAUDE.md

Guidance for anyone (human or agent) working inside `typebeat-web`, the **type!beat backend and
website**: an ASP.NET Core monolith (Razor Pages plus minimal-API endpoints) over Postgres. Runs in
production at `typebeat.sh`.

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
| `src/Typebeat.Web/Packages/Lyrics/ChunkedEndurance.cs` | `typebeat.Game.Rulesets.TypeBeat/Beatmaps/ChunkedEndurance.cs` |
| `src/Typebeat.Web/Packages/Lyrics/PausedWord.cs` (rest validation and stretch spans only; the optional cell rule PR 3 added defaults to `Typeability.IsCell`, and the server never passes another) | `typebeat.Game.Rulesets.TypeBeat/Gameplay/PausedWord.cs` |
| `src/Typebeat.Web/Packages/Lyrics/LyricTiming.cs` (the stored map's `[Lyrics]` parse: words, `syllables`, `pauses`) | `typebeat.Game.Rulesets.TypeBeat/Beatmaps/TimingJsonLoader.cs` |
| `src/Typebeat.Web/Packages/Lyrics/LyricWpmCurve.cs` | `typebeat.Game.Rulesets.TypeBeat/Beatmaps/LyricWpmCurve.cs` |
| `src/Typebeat.Web/Packages/Lyrics/InstrumentalGaps.cs` | `typebeat.Game.Rulesets.TypeBeat/Gameplay/InstrumentalGaps.cs` |
| `src/Typebeat.Web/wwwroot/js/typebeat-player.js` (`computeGaps` + its four constants) | the same `InstrumentalGaps.cs`, via the row above |
| `src/Typebeat.Web/wwwroot/js/typebeat-core.js` | the C# `TypingEngine` / `TypeBeatScoreProcessor` |
| `src/Typebeat.Web/wwwroot/js/typebeat-player.js` (`buildPaceBands` and its `pace*` helpers, the underline pace hue; display only. Since PR 3 a word is also cut at its `syllableMarkerCells` and `buildPaceBands` is the RELATIVE mode LyricStage draws, `BuildRelativeBands` at `DEFAULT_MAX_CHANGE_PERCENT`; the whole-map rank mode is `buildRankedPaceBands`. `UnderlinePaceParityTest` pins both on the synthetic and env-supplied real maps) | `typebeat.Game.Rulesets.TypeBeat/UI/UnderlinePace.cs` |
| `src/Typebeat.Web/Packages/Lyrics/Romaniser.cs` (identical below the `using` lines except the namespace line; pinned by `RomaniserParityTest` over the game repo's `NonVisual/fixtures/romaniser/*.tsv`, which the WireCompat csproj links in; the JS has no copy, since it only decodes already-romanised text). DELIBERATE DIVERGENCE since PR 3: the game's `LyricOriginals` runs `JapaneseReading` (Kawazu + the LibNMeCab IPA dictionary: kanji readings and word boundaries) BEFORE this romaniser. The server has no dictionary and will not get one, so its mirror covers exactly what it covered before; kanji romanisation happens only in the client at import and reaches the server as already-romanised stored text plus the `original` field | `typebeat.Game.Rulesets.TypeBeat/Beatmaps/Romaniser.cs` |

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
  gesture composition lives. The **backspace at the HEAD of a line**, which steps back up into the
  line behind it while that line is still unsealed, carries no era either (the C# gates it on
  `FletcherEnabled` alone, which is bit 5, already set): it is live rule on an ordinary `/play` run
  and is mirrored as `stepBackIntoLine`. Since backlog 262 **bit 12** joins them (a break that takes the claim
  off an older break FOLDS that claim into its own instead of discarding it, so two accidents both
  fully corrected cost the run nothing and the newest of the broken cells redeems the whole chain,
  transitively), so every such parity fixture also sets `FoldsDisplacedClaim = true`.
  `DisplacedClaimFoldLiveParityTest` is the pin, and unlike bits 10 and 11 the generated sweep DOES
  roll this shape, so `EngineFuzzLiveParityTest` needs the bit as well. Since backlog 307 **bits 14
  and 15 are SET too** (`manualNewlines: true, newlineOnTypedLetter: true` on the CONFIG frame,
  `ManualNewlines = true, NewlineOnTypedLetter = true` on every bare live engine). They are a desktop
  SETTING rather than a live-stack rule, but the setting has defaulted ON since PR 2 and `/play` has
  no settings surface, so the browser takes that shipped default unconditionally
  (`this.manualNewlines` / `this.newlineOnTypedLetter`, the same move backlog 198 made for
  `spaceSkipsWord`), while the C# engine property stays off so stored automatic-arm runs re-derive.
  Under it a finished line PARKS (no keypress roll, no line-start snap), Space, Enter or a typed
  letter on the finished caret is the newline (`rollForwardManually`), a typed-out line is HELD to
  its drag cutoff (`manualNewlineHoldsLineOpen`, which also keeps the push warning up), and a line
  handed over before its entry window opens WAITS (`awaitingEntry`, greyed at 0.6 alpha by
  `typebeat-player.js`, every key and Enter swallowed). `routeKeyDown` offers a Space on a complete
  line to the engine BEFORE the skip gate, after the parked-head drop, which is the desktop order.
  The fuzz sweep counts `manualHandOvers`, `typedLetterNewlines`, `awaitingSwallows` and
  `holdExtensions` and pins `rollForwards`/`lineSnaps` at ZERO; `ManualNewlinesParityTest` carries
  the game's `ManualNewlinesTest` cases. A Js harness that transcribes an AUTOMATIC-arm game test
  must declare that arm (`CoreFlexibleLinesHarness.cjs`'s `automaticArm`), as the backlog 198
  harnesses declare their space-skip arm. **Bit 16** (PR 2's first-line head start,
  `FirstLineLeadIn`: a press up to `FIRST_LINE_LEAD_MS` = 300 before the map's first vocal opens
  the first line) is the opposite: the live playfield sets it for every stack and the browser takes
  it unconditionally (`firstLineTypingOpensAt`), so every parity fixture that feeds the C# arm sets
  `FirstLineLeadIn = true` (or `firstLineLeadIn: true` on its CONFIG frame).
  `EngineFuzzLiveParityTest`'s generator presses inside the head start on half its runs and counts
  the openings (`leadInOpens`). **The first flags word is FULL at bit 16** (the .osr decoder's
  `Parsing.ParseFloat` rejects a MouseY above `MAX_COORDINATE_VALUE` 131072), so since backlog 347
  eras ride a **SECOND CONFIG-style header frame**: sentinel `TypeBeatReplayFrame.CONFIG_EXTENDED`
  (0x01), written straight after the CONFIG frame at the same time, MouseX = 1, MouseY = a second
  flags word numbered from bit 0 on the first word's terms (the same 131071 ceiling, so a third
  carrier, 0x02, is the move after bit 16 of this one). `ReplayEngineFeed.Apply` CLEARS every
  second-word flag on the CONFIG frame and sets them from the extended one, so a replay with no
  extended frame reads the word as all false, and an older client IGNORES the unknown sentinel
  (any code below 0x20 it does not know resolves no cell). **Second-word bit 0** is
  `RushCapCostsAccuracy`: a press that leaves the caret more than `FLETCHER_MAX_CHARS_AHEAD`
  countable characters past the playhead is AWARDED Meh (`RushCapTier`, a min over the ladder, so
  Premature/Lagging stay put) with its true delta, credits combo like any other hit (no break, no
  discarded claim), and marks the cell (`JudgedPastRushCap`), and the same bit loosens the cap from
  five to six. Clear, the C# re-derives the pre-347 rule (cap five, the press graded on the clock
  but the run zeroed once per excursion). Both are STORED eras since PR 3 (bit 1 below removed the
  cap live), but every live parity fixture still sets `RushCapCostsAccuracy = true` on its bare
  engine, because the live client records it, and appends `CreateExtendedConfigFrame(0, rushCapCostsAccuracy: true, inputEra2: true)` after its
  CONFIG frame. **Second-word bit 1** (value 2, PR 3) is `InputEra2`, the SECOND INPUT ERA, set for
  every live stack and so unconditional here with no flag: NO RUSH CAP at all (the C# skips
  `rushesPastCap`, so bit 0 is recorded but inert live; the browser carries no cap constant, no
  `rushesPastCap`, no `rushCapTier` and no `judgedPastRushCap` mark), ONE BACKSPACE UNDOES A WORD
  SKIP and the gap it typed, parking the caret on the first abandoned cell (`tryUndoWordSkip`,
  `adjacentSkippedWord`, `canUndoWordSkip`), SPACE TO SKIP needs `allowWrongInput` (under
  Gatekeeper a mid-word space is a rejected wrong key), and the refined RETYPE ANCHOR (a gap typo
  selects the word before it, a wholly abandoned word anchors on its own head, leading auto-skipped
  punctuation is stepped over). So every live parity fixture sets `InputEra2 = true` beside
  `RushCapCostsAccuracy = true`, and the extended word is 3. `EngineFuzzLiveParityTest` counts
  `farAheadPresses` / `farAheadClockHits` (presses past where the 347 cap stood) and pins
  `farAheadMehAwards` / `farAheadBreaks` at ZERO; `EachStoredRushEraReDerivesUnderItsOwnHeader`
  proves the scorer follows both bits; `WordSkipLiveParityTest` and `WordInputParityTest` pin the
  undo, the Gatekeeper space and the anchor; a stored-era arm replays the pre-PR 3 gesture
  composition (`legacyScript` in `CoreFlexibleLinesHarness.cjs`). `tools/score-recalc` needs no
  axis for either bit (the header travels in the .osr), pinned by
  `ScoreRecalcTest.TheRushCapEraTravelsInTheSecondHeaderThroughTheOsr`. PR 2 also made **authored pauses** (a word's `pauses` array, or the
  legacy single `pause` object) scoring surface: `usableRests` / `pausedWordOf` /
  `tokenCellTargets` mirror `Gameplay/PausedWord.cs` and `TypingLine.tokenCellTargets`, and move
  both the per-cell targets and the judgement groups. `SyllableSplitParityTest` pins them through
  both production loaders and the fuzz sweep's `pausedWords` fixture through the engines.
  **The JUDGEMENT WINDOWS are not an era at all.** One symmetric ladder (Great 150 ms, Ok 300, Meh
  600) grades every cell of every map: no granularity tier, no low-confidence fallback, no per-cell
  `tier` field on either side, and no CONFIG bit recording which ladder a run was played on. A
  retune is therefore an ordinary mirrored edit (`WINDOWS` here, `SyncWindows` there) that both
  clients take at once, and `tools/score-recalc` treats every stored row as unreproducible by
  construction because of it. The EASY mod's word-level shelter (`TypingEngine.WordShelter`) is not
  mirrored, for the reason the window scale is not: `/play` has no mods payload. **Polyglot (`PG`,
  backlog 331/332) exists only desktop side and is LOCAL ONLY** (`Mod.LocalOnly`): it types the
  lyric in its original script, the game never requests a token or submits for it, so it is never
  on the wire and has no rating matrix cell and no board. The server still knows the acronym, in
  `Scoring/LocalOnlyMods.cs` (a deny list like `UnrankedMods`, so a new local-only client mod must
  be added there), and REFUSES a token request or a submission naming it with a 422 before
  anything is written, since only a modified client could send one (`PolyglotSiteTest` pins
  both). The browser plays the romanised text and must merely IGNORE every `original` key in the
  stored .osu; `OriginalTextParityTest.TheBrowserDecoderIgnoresOriginals` pins that through
  `parseLyricOsu` + `buildBeatmap` against the game's decoder. The originals themselves reach the
  site as `beatmaps.lyrics_original` (039), aligned line for line with `beatmaps.lyrics`. A harness that
  SPIES on an engine method must forward every argument: a dropped one reads out of a table as NaN
  and silently answers the wrong thing for the entire sweep (it once did, on the since-removed
  `rushesPastCap`).
- **Lyric NORMALISATION is mirrored three ways** (game `Typeability.Normalize` in
  `Beatmaps/LyricBeatmap.cs`, server `Packages/Lyrics/Typeability.cs`, browser `normalize` in
  `typebeat-core.js`), and since backlog 329 that includes the 20-entry `SPECIAL_LETTERS` table
  (ss for ß, ae for æ, th for þ, and so on) applied right after the NFD fold; the `PUNCTUATION`
  set rides the same three-way rule. All three run at DECODE time, not only import, so an edit
  changes how stored maps play on every client at once: keep them in lockstep, and
  `LyricParserParityTest.AllThreeDecodersSpellTheSpecialLettersIdentically` is the pin.
- **The .osu FORMAT VERSION GATE exists three times** since backlog 255, and all three read the
  magic line the same way (digits after `type!beat file format v`, fallback 1): the game's
  `LyricBeatmapDecoder.ParseFormatVersion`, `BeatmapPackageParser.ParseFormatVersion` here, and
  `parseFormatVersion` in `typebeat-core.js`. It decides one thing, whether a bracket in a stored
  `[Lyrics]` line is a backing vocal to strip (v1, and anything unversioned) or a literal lyric
  mark to keep (v2 and up, what the game's writer stamps now). `/play` is served the same stored
  .osu blob desktop decodes, so a one-sided edit gives the two clients different cells on the same
  leaderboards. `PunctuationParityTest` pins the JS copy against the C# one over the same fixture
  at both versions.
- **A word's AUTHORED PAUSES are parsed twice and rated twice** since PR 2 (LyricPace v22). The
  game's editor writes each word's rests as a `pauses` array of `{start_ms, end_ms, split}` beside
  its `syllables` (an older build wrote one `pause` object, still read when no array is present);
  both loaders keep only the rests `PausedWord.UsableRests` accepts against the CLAMPED word, and
  both ratings read the survivors as dividers through `PausedWord.Of`. The server's `PausedWord.cs`
  is deliberately reduced to that (which rests survive, and each stretch's start and end), because
  the char and cell cuts the engine and the editor also derive there reach no rating. The same
  parse reads `syllables` as the OBJECTS every writer produces (it read bare numbers until v22).
  `LyricParserParityTest` feeds both production parsers the same `LyricOsuFormat.GenerateOsu`
  bytes and pins units, boundaries, rests and all eighteen matrix cells. Since LyricPace v23 the
  whole-map PACE reads the rests too, but only the part of a rest past the one-second pause cap.
- **PR 3's editor timing is in the stored .osu and is not gameplay.** The game's writer emits an
  `[Editor]` `BeatDivisor:` and every `[TimingPoints]` row the editor's BPM tools authored (kiai
  effect rows folded in) instead of the one placeholder row. The parser ignores the section and
  still takes the set's BPM from the first uninherited row, and neither reaches a cell, a rating or
  the gameplay fingerprint, so a BPM-only save keeps a ranked map's rank (the game's local status
  check compares the whole encoding and is stricter). `LyricParserParityTest` holds the game's own
  encoder output against the server parse, the validator, the fingerprint and the browser decode.
- **Display rules the browser mirrors moved in PR 3 too**: syllable markers (`syllableMarkerCells`)
  now include AUTOMATIC splits, and the space error dot (`spaceErrorDots`) marks only a gap holding a
  wrong character of its own. The sung-highlight brightness, text pop-in and caret smoothing
  settings PR 3 added are desktop-only presentation and are not mirrored. PR 3 also dropped the
  `bpm` keyword from song select; the site keeps `bpm:` as a site-only operator, like `lang:`.
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
  The PER-LINE figures are the other half of the same story and live in `Packages/Lyrics/LyricPace.cs`
  (mirroring the game's `Beatmaps/LyricPaceStatistics.cs`): `beatmaps.wpm` and, since backlog 272,
  `beatmaps.target_wpm` (`033_target_wpm.sql`), the average WPM across the fastest fifth of the map's
  lines, on the same not-on-the-wire, bump-the-VERSION rule. `LyricPaceParityTest` pins both halves.
  Since PR 3 (**LyricPace v23**) the whole-map average charges every pause (between words, inside a
  word, between lines) up to one PLAYBACK second and nothing after the final vocal end, and the
  curve's window is the SR axis's (at least `WINDOW_SECONDS` 1.5 s and `MIN_WINDOW_CELLS` 16) with
  every bar read from a centered window. Both take a `rate` and are recomputed rather than scaled
  at one; the server stores rate 1, and `LyricPaceParityTest` holds every stream and clock.
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
