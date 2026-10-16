# typebeat-core.js: history

The comments in the browser engine (`src/Typebeat.Web/wwwroot/js/typebeat-core.js` and the files it
assembles under `wwwroot/js/core/`) describe what the code does now and which C# member each part
mirrors. How a rule got to its current shape is kept here instead, moved from those comments when
the engine was split into one file per C# counterpart. Each code site that lost a paragraph points
at its heading below with a one-line `// History:` comment.

## core/judgement.js

### The judgement ladder

The single symmetric ladder replaced a three-tier ladder (Line 250/400, Word 150/240, Syllable
112.5/180, six late-biased constants scaled by 1.0 / 0.6 / 0.45) whose tier came from the beatmap's
granularity, widened back to the Line tier for an estimated line or a low-confidence word. The whole
of that went on both sides: the timing data a map carries still varies, but the JUDGEMENT no longer
does, so unreliable timing buys no extra tolerance and a well-subdivided map is not judged more
tightly than a coarse one. The per-cell `tier` the browser engine used to carry went with it,
exactly as TypingCell.JudgeGranularity did, because a cell no longer selects anything.

### The correction cap (awardedTier)

Before backlog 210, a word typed wrong and fixed could score bit-identically to a word typed right,
because the mistype is accuracy-inert by design and the corrected cell's deferred result was graded
purely on the RETYPE's timing.

## core/score-and-health.js

### The incremental score account

An end-of-play reconstruction from the per-cell result stream is what the browser engine used to do,
and it is why a browser play used to submit a higher max_combo and total_score than the desktop
client for the identical mistyped performance (see backlog 72/73).

### The HP pool (HealthAccount)

Before backlog 306 the browser had no account at all: health was a read of the rejection streak, and
the only fail was the 13-rejection branch, which a /play run can no longer reach. So a near-AFK or
typo-drowned run played out and submitted passed=true, where the desktop fails the identical input
(rank F, unranked, pp 0).

The engine's `health` getter (core/typing-engine.js, the HP bar the HUD draws) said the same from
its side: until backlog 306 it was a read of the rejection streak, which a /play run can no longer
grow (every wrong letter and every mid-word space is typed through or consumed by the word skip), so
the bar sat at full whatever the player did.

### Grades

Grades used to be awarded on completion; the completion cutoffs the accuracy rule replaced are gone
from the client too.

## core/typeability.js

### normalize and backing vocals

normalize stopped stripping backing vocals at backlog 255. That call used to be folded into
normalize, which was a structural divergence from the C#; it now sits at the one call site that
knows the map's format version (buildBeatmap).

## core/typing-engine.js

### The rush cap

TypingEngine.FLETCHER_MAX_CHARS_AHEAD bounded how many COUNTABLE characters the caret could sit
ahead of the playhead before a press was penalised: a combo break at five before backlog 347, a Meh
award at six after it. The second input era (PR 3) removed the cap outright.

Before the symmetric rush bound (backlog 218), RUSH was time-UNBOUNDED while DRAG never was:
rollForwardIfFinishedEarly handed the caret to the next line the instant the last cell of the
current one landed, however many seconds before that line's cue, and the roll is TRANSITIVE, so a
fast player could walk the whole map at the top of the song with nothing but the rush cap (which
cost combo and blocked nothing, and is gone since PR 3) in the way.

TypingEngine.countablePositionAt was split out for backlog 260, where the rush cap had to measure a
skipping space against the caret as it stood BEFORE the skip moved it (the cap is gone since PR 3).

In processKey, nothing stands between the press and the increment: the RUSH CAP (which the browser
gained with the flexible default, backlog 208) used to be a combo penalty there, a break once per
excursion, then a Meh award (backlog 347), and is gone since PR 3. The C# keeps both as stored eras;
a browser play is always live.

### What discards a combo claim (restorable)

The list of seams that discard the outstanding combo claim (TypingEngine.restorable) has shrunk. An
off-time press left the list at backlog 199: it is a hit now, it breaks nothing, and only a break
discards a claim, so fumbling the beat between a typo and its fix no longer costs the fix its
restore. It rejoins the list in the C# under OffTimeRule.BreaksCombo, the pre-199 era, which the
browser has no arm for. The flexible caret's RUSH CAP (backlog 208) left the list the same way at
backlog 347 (an over-cap press was awarded Meh and credited combo) and left the engine at PR 3,
which removed the cap. It rejoins the list in the C# only when both TypingEngine.InputEra2 and
TypingEngine.RushCapCostsAccuracy are clear, the pre-347 era, which the browser has no arm for
either.

### The sync tint and live sync readout

Before backlog 251 removed the browser's sync tint and live sync readout, typebeat-player.js also
read the zeroed delta of an untimed space back for both, which would otherwise still have docked a
space for its timing; the delta itself is unchanged, kept for WireCompat parity with the C#, only
that display consumer is gone.

The other consumer of the zeroed delta WAS mirrored (the C# half of backlog 148 keeps the exempt
space out of its SyncTimeline): typebeat-player.js read judgedDelta back for the cell tint and its
live sync percent, excluding space cells from both halves of that mean the way LiveSyncPercent does
(and a cell left WRONG was out of it too, on both sides and in every state, since that readout
filtered on the CELL, typeable and not a space, never on what happened to it). That readout and tint
are gone from the browser now (the desktop metric they mirrored is off by default too).

The correction cap leaves the delta of a corrected press untouched; before backlog 251, that delta
was also what the browser's sync tint and live sync percent saw. That display is gone, the delta is
not.

## core/typing-line.js

### Granularity

Granularity used to pick the judgement ladder a cell was graded on, and the widest-ladder fallback
for an estimated line or a low-confidence word (the deleted SyncWindows.LOW_CONFIDENCE_SCORE of
0.15) lived alongside it; the single symmetric ladder retired both.
