/*
 * core/typing-engine.js: the frame-driven gameplay and judgement core (mirrors
 * Gameplay/TypingEngine.cs).
 *
 * One of the files typebeat-core.js assembles into window.TypeBeatCore: see that file for the
 * order they load in and how they share names.
 */
(function (parts) {
    'use strict';

    const {
        FREESTYLE_AUTO_CHAR, fold, syllableIndexOf, isCharTimedStretch, windowsFor, classify,
        awardedTier, betterTier, toHitResult, ScoreProcessorMirror, HealthAccount
    } = parts;

    // ---------------------------------------------------------------------------
    // TypingEngine: the frame-driven gameplay/judgement core.
    // ---------------------------------------------------------------------------
    // THERE IS NO RUSH CAP (PR 3, TypingEngine.InputEra2, bit 1 of the second CONFIG flags word): a
    // press any distance past the playhead is judged on its timing alone and credits combo like any
    // other. The C# keeps the older caps as ERAS for the stored runs it re-derives; the browser
    // only plays live, so it takes the live rule unconditionally and carries neither
    // TypingEngine.FLETCHER_MAX_CHARS_AHEAD nor TypingEngine.rushesPastCap. The countable stream
    // below survives for charsAheadOfPlayhead, the drift readout the C# keeps too.
    // History: docs/typebeat-core-history.md, "The rush cap".

    // TypingEngine.FLETCHER_DRAG_GRACE_MS: extra time past a line's normal hard deadline
    // (endTime + sealGraceMs) that the engine holds the line open while the PLAYER is still on it,
    // so a dragging player may finish the line the song has already left. Deliberately the same
    // magnitude as CUE_LEAD_MS: the beat of grace given to get ready, granted at the other end of
    // the line as well. Bounded so a run always terminates.
    //
    // Since backlog 218 it bounds BOTH directions, and the name is kept for the replays and the
    // mirrors that already use it. Drag holds a line open this long past its natural END
    // (sealPermitted); rush enters a line this long before its natural START (entryPermitted, gated
    // on boundedRush). One constant, so the two freedoms cannot drift apart: a player may run ahead
    // of the song by exactly the margin they may fall behind it.
    const FLETCHER_DRAG_GRACE_MS = 1500;

    // TypingEngine.FIRST_LINE_LEAD_MS (PR 2): how long before its first vocal the map's FIRST line
    // opens for typing. A later line is reachable early by rushing from the one before it, but the
    // first line has nothing to rush from and its boundary usually sits ON its first word, so the
    // activationTime clamp left the player unable to type a character until that word was already
    // being sung. A FLOOR, not a fixed window: a line whose own activation is earlier keeps it.
    const FIRST_LINE_LEAD_MS = 300;

    // TypingCell.IsCountable: the currency the drift readout measures in (and the rush cap did
    // until PR 3 removed it). A space spends no budget, so pressing one never moves the lead.
    function isCountable(cell) { return cell.typeable && cell.expected !== ' '; }

    // TypingEngine.isWordGap. A WORD GAP: the typeable SPACE cell that separates two words. The one
    // boundary both word-level queries below (wordBackspaceTarget, retypeSelectionAnchor) are
    // written against, and the same test skipCurrentWord scans a word with, so "word" means one
    // thing in this file. A non-typeable cell (punctuation the default stream kept, and every mark
    // under Literate) is NOT a boundary: it rides inside the word it is attached to.
    function isWordGap(cell) { return cell.typeable && cell.expected === ' '; }

    class TypingEngine {
        constructor(beatmap) {
            this.beatmap = beatmap;
            this.lines = beatmap.lines;
            // Cells carry play state on the (shared) beatmap, so a fresh engine, e.g.
            // "play again" reusing the same beatmap, must clear it first.
            for (const line of this.lines) {
                for (const c of line.cells) {
                    c.state = 'untyped';
                    c.judgeType = null;
                    c.typedChar = null;
                    c.judgedDelta = null;
                    c.firstCorrectDelta = null;
                    // The one place backlog 210's correction flag is ever cleared: it survives a
                    // backspace by design, so only a whole-run rebuild puts it back (the C# clears
                    // it in exactly the same place, TypingEngine.reset).
                    c.heldWrongBeforeJudged = false;
                    c.judged = false;
                    c.scoredResult = null;
                }
            }
            this.activeLineIndex = -1;
            this.caretIndex = 0;
            this.nextSealIndex = 0;
            // TypingEngine.lineAbandoned. Which lines the player WALKED OUT OF with a line skip
            // (processEnter), i.e. parked the caret past the last cell of while typeable cells were
            // still untyped. Read by exactly one thing, sealPermitted, which grants an abandoned
            // line the same drag grace a caret still sitting on it would have.
            //
            // WHY IT HAS TO BE REMEMBERED. Without it an Enter skip would move that line's seal
            // EARLIER (by up to FLETCHER_DRAG_GRACE_MS) than the identical run that simply stopped
            // typing there, because the deferral in sealPermitted keys on the caret and the caret
            // has moved on. The seal is where the abandoned cells become misses and where the
            // line's one combo break is taken, so an earlier seal would re-price every keypress
            // made on the NEXT line in between at a combo the player had not actually lost yet.
            // Holding the grace is what makes the skip PURE CARET MOVEMENT, and is therefore why
            // the desktop half carries no era bit for it: nothing judged changes value or timing.
            //
            // An array rather than a single index because a fast player can abandon line N and be
            // on line N+1 (and abandon that too) before N has sealed, and losing N's grace to N+1
            // is the very defect this exists to prevent. Entries are never cleared on seal, since
            // sealPermitted is only ever asked about nextSealIndex and that only moves forward. The
            // C# clears the array in TypingEngine.reset alongside lineSealed; this mirror has no
            // reset (no backwards seek and no replay rebuild), so allocating it here, where the
            // constructor already clears every cell's play state, is the same guarantee.
            this.lineAbandoned = new Array(this.lines.length).fill(false);
            // The engine's OWN live combo/score (TypingEngine.Combo / Score): what the HUD shows.
            // The submitted numbers do not come from here; they come from the score processor
            // mirror below, which the engine drives at exactly the points TypeBeatPlayfield drives
            // the real one. In vanilla play the two combos happen to track each other, but
            // they are separate accounts with separate rules and only one of them is submitted.
            this.combo = 0;
            this.maxCombo = 0;
            // TypingEngine.runPositions (backlog 259): WHERE each increment of the current run was
            // earned, one { line, cell } per unit of `combo`, in the order they were credited. The
            // ledger the back-dated seal break needs and the only thing that can answer "how much
            // of this run was earned past the cells this line is about to miss": combo is a single
            // integer, and a break that keeps part of a run has to know which part.
            //
            // `runPositions.length === combo` is the invariant, and it holds by construction
            // because the four ways combo moves all move this with it: an increment appends
            // (creditCombo), a break clears (breakRun, which hands the list to a redeemable break's
            // snapshot), a restore puts the snapshot's own entries back where they were earned, and
            // the back-dated seal drops exactly the entries it destroys (backDateBreakTo).
            //
            // Bounded by the map: a run is at most one increment per cell, since a cell judged
            // correct once earns no combo on any retype, so nothing here grows with the length of the
            // play.
            this.runPositions = [];
            this.totalKeypresses = 0;
            this.correctKeypresses = 0;
            this.errorCount = 0;
            this.consecutiveWrongKeys = 0;
            this.activeTimeMs = 0;
            // THE LAZY CLOCK ARM (backlog 222), mirroring TypingEngine.wpmClockArmedLine /
            // wpmClockArmedAt: the line the WPM clock has been armed on AHEAD OF ITS CUE, and the
            // instant it was armed at, which is the time of the first press the player put on that
            // line while the song had not reached it yet. -1 / 0 when nothing is armed, which is
            // every ordinary in-sync frame. See clockRunsFrom for what it buys.
            //
            // Read only through clockRunsFrom, which validates the arm against the CURRENT
            // activeLineIndex rather than clearing it at each of the sites that can move the caret.
            // An arm left behind on a line the caret has left is simply not this line's arm, and a
            // line is only ever entered once (the caret advances, or goes to -1 and comes back on a
            // LATER line, never on an earlier one), so a stale arm can never be mistaken for a live
            // one.
            //
            // A pure function of (keypress times, beatmap): no wall clock and no frame cadence, so
            // it cannot make the browser's account depend on the display rate the desktop's replay
            // of the same run would not have had.
            this.wpmClockArmedLine = -1;
            this.wpmClockArmedAt = 0;
            this.lastUpdateTime = null;
            this.finished = false;
            // The HP pool (backlog 306, see HealthAccount): the stand-in for the
            // TypeBeatHealthProcessor a Player would cache, fed at the seams TypeBeatPlayfield and
            // TypeBeatHealthFeed feed the real one. `failed` below is read off it.
            this.healthAccount = new HealthAccount(() => { if (this.onFailed) this.onFailed(); });
            this.counts = {};                 // JudgementType -> count (scored only)
            // The osu-side account the SUBMITTED score is read off (computeScore); the stand-in for
            // the TypeBeatScoreProcessor a Player would cache. Fed incrementally from here, exactly
            // where TypeBeatPlayfield feeds the real one.
            this.processor = new ScoreProcessorMirror();
            // Mods (TypingEngine.CaseSensitive / MashingEnabled). The browser player always plays
            // vanilla and never turns these on; they exist so processKey stays a line-for-line
            // mirror of the C# and so the freestyle exemptions in it are pinned by the JS-vs-C#
            // fidelity harness. Both default off, i.e. today's exact behaviour. Exposing either in
            // the UI would also mean wiring the submitted mods payload (both are unranked).
            this.caseSensitive = false;       // Literate: the typed char must match the exact case
            this.mashingEnabled = false;      // Mashing (Relax): any key is the right key
            // The wrong-key MODEL, and unlike the two above this one is ON, because it is the
            // DEFAULT gameplay on both sides since backlog 107: a wrong (non-space) char is typed
            // through and marked wrong instead of being rejected, and backspace can take it back.
            // The desktop client turns it off only for the Gatekeeper mod (acronym GK), and the
            // browser has no mods payload at all, so /play is permanently non-Gatekeeper, which is
            // exactly the default the shared leaderboards are now judged under. If /play ever grows
            // a mods payload, this is the flag GK would clear.
            this.allowWrongInput = true;
            // "Space to skip current word" (TypeBeatRulesetSetting.SpaceSkipsWord, backlog 110): a
            // space pressed inside a word abandons the rest of it and moves on to the next word.
            // Since backlog 167 abandoning is not giving up: the cells enter a PHANTOM state, one
            // backspace re-enters the word, and re-typing them earns their ordinary judgements and
            // the streak the skip broke. What the skip takes immediately is a single combo break;
            // the miss COUNT and the osu RESULTS wait for the seal, so a skip nobody goes back for
            // costs exactly what it always cost and one the player returns to costs nothing beyond
            // the detour. There is no era switch here (the C# WordSkipRule), for the same reason
            // there is no ComboRestoreRule: the browser only ever plays LIVE.
            // DEFAULTED ON since backlog 198, tracking the desktop's SHIPPED default (its config
            // row, not the C# engine property, which stays off for replay decoding): the browser
            // has no settings payload, so /play is permanently skip-on and the default path stays
            // the desktop's default feel. If /play ever grows a toggle, this is the flag it sets,
            // and it would ALSO have to travel in whatever the browser's equivalent of the replay
            // CONFIG frame is (the desktop carries it as bit 1), because it changes how a recorded
            // space is judged.
            this.spaceSkipsWord = true;
            // FLEXIBLE LINES (backlog 208): the player's caret is decoupled from the song's
            // playhead. Four behaviours, all confined to these two flags so the pinned path stays
            // byte-identical:
            //   RUSH FREEDOM      finishing a line moves the caret straight on to the next one
            //                     instead of waiting for its cue (rollForwardIfFinishedEarly), and
            //                     since backlog 218 no earlier than FLETCHER_DRAG_GRACE_MS before
            //                     that cue (boundedRush). The finished line is left unsealed and
            //                     seals on its own normal deadline with nothing missed.
            //   DRAG FREEDOM      a line the player is still typing is not force-sealed at its
            //                     normal deadline; the seal is deferred by FLETCHER_DRAG_GRACE_MS
            //                     so the caret is never yanked off a line mid-word (sealPermitted).
            //   NO RUSH CAP       a press any distance ahead of the playhead lands, is judged on its
            //                     clock and credits combo (PR 3's second input era; before it a cap
            //                     broke combo, backlog 208, and then awarded Meh, backlog 347).
            //   LINE-START SNAP   a caret sitting PAST the last character of its line is handed to
            //                     the next line the moment that line starts, so a player who has
            //                     FINISHED is still carried along by the song
            //                     (snapForwardOnLineStart). An UNFINISHED line is never taken,
            //                     which is the point of the freedom.
            //
            // Per-char judgement windows are untouched: rushing reads as early deltas and dragging
            // as late ones, so accuracy, sync% and the judgement counts report the drift honestly.
            //
            // THE SECOND INPUT ERA (PR 3, TypingEngine.InputEra2, bit 1 of the second CONFIG flags
            // word) is live for every desktop stack and therefore unconditional here, with no flag:
            // no rush cap (above), one backspace undoing a word skip (tryUndoWordSkip), Space to skip
            // only under wrong input (the skip gate in processKey) and the refined retype anchor
            // (retypeSelectionAnchor). The C# defaults it FALSE so stored runs re-derive, so every
            // parity fixture feeding the C# arm sets InputEra2 = true beside RushCapCostsAccuracy.
            //
            // ALL THREE TRUE UNCONDITIONALLY, the same shape every other live-only rule in this file
            // takes (the span judgement, the word-gap input model, the space discipline, the
            // stretch narrowing). Since backlog 208 this is the desktop's LIVE default for every
            // stack, and the mod named Fletcher is the one that turns it OFF and re-pins the caret
            // (acronym FC). The browser has no mods payload, so the strict FC arm is unreachable
            // here; if /play ever grows one, FC is the mod that would clear these.
            //
            // In the C# they default FALSE, because that engine must also RE-DERIVE a stored replay
            // under the pinned era every pre-208 row was played in, and the pair travels as CONFIG
            // frame bit 5 (the rush bound as bit 7). The browser has no era axis: it plays live
            // only, writes no replay frames and re-scores no stored row, so the live value is the
            // only value it can hold, and the pre-218 UNBOUNDED era is unreachable in it exactly as
            // the pinned one is. The C# arm of the fuzz parity test therefore has to SET bits 5 and
            // 7 in the frames it feeds, the same treatment bits 2, 3, 4 and 6 already get.
            //
            // The backspace at the head of a line stepping back up into the line behind it is
            // mirrored too: the C# gates that on FletcherEnabled alone, with no era bit, so it is
            // live rule on an ordinary /play run (see processBackspace and stepBackIntoLine).
            this.fletcherEnabled = true;
            this.flexibleLineSnap = true;
            // THE SYMMETRIC RUSH BOUND (backlog 218): a finished caret may enter the next line only
            // from FLETCHER_DRAG_GRACE_MS before that line's own activationTime onward
            // (entryPermitted), the exact mirror of the drag side (sealPermitted), which holds a
            // line open exactly that long past its natural end.
            // History: docs/typebeat-core-history.md, "The rush cap".
            //
            // A refused roll PARKS the caret past the last cell of its line, the state the
            // line-start snap already understands: keypresses there are inert (processKey answers
            // false on a complete line, so no judgement, no typo, no combo break and nothing in the
            // accuracy denominator), the WPM clock does not run (update accrues only while the
            // active line is INCOMPLETE), and snapForwardOnLineStart performs the deferred roll the
            // moment the bound opens. The DRAG side is untouched, and neither the drag cutoff's
            // hand-over nor the seal loop's ordinary one is refused: those are the SONG arriving, an
            // entry that is late rather than early.
            //
            // Under manualNewlines (below, the browser's arm since backlog 307) neither roll nor snap
            // runs: a finished caret parks for the player's own newline, and the bound instead
            // decides whether the line that newline lands on may be TYPED yet (awaitingEntryAt).
            this.boundedRush = true;
            // MANUAL NEWLINES (TypingEngine.ManualNewlines, CONFIG frame bit 14, backlog 307): the
            // player closes a finished line themselves. The press that finishes a line does not
            // roll the caret (rollForwardIfFinishedEarly) and the line-start snap goes quiet
            // (snapForwardOnLineStart); a SPACE or ENTER on the finished caret is the newline
            // (rollForwardManually), and a finished line is HELD open to its drag cutoff
            // (manualNewlineHoldsLineOpen, read by sealPermitted and dragCutoffAt), so a player who
            // never presses is handed on exactly when the push warning's red bar completes. The
            // newline always lands, and a line handed over before its entry window opens WAITS
            // (awaitingEntry): its keys and Enter are swallowed and typebeat-player.js greys it.
            //
            // It is a desktop SETTING rather than a live-stack rule, and it has defaulted ON there
            // since PR 2 (TypeBeatRulesetConfigManager). The browser has no settings surface, so it
            // takes the desktop's SHIPPED default unconditionally, exactly as spaceSkipsWord does
            // above (backlog 198). The C# ENGINE property stays FALSE by default, because that
            // engine must also re-derive every run stored with the automatic hand-over; so the
            // parity harnesses SET bit 14 on the CONFIG frames they feed (and ManualNewlines on the
            // bare engines they build), the treatment bits 2, 3, 4, 5, 6, 7, 8, 10, 11, 12 and 16
            // already get.
            //
            // NEWLINE ON A TYPED LETTER (TypingEngine.NewlineOnTypedLetter, bit 15): a letter on a
            // finished caret hands the line over and lands on the next line's first slot. It rides
            // the same desktop setting (TypeBeatPlayfield sets it from ManualNewlines) and is gated
            // on manualNewlines here as there, so it is set with it and its bit travels with 14.
            this.manualNewlines = true;
            this.newlineOnTypedLetter = true;
            // THE FIRST LINE'S HEAD START (PR 2, TypingEngine.FirstLineLeadIn, CONFIG frame bit 16):
            // a press up to FIRST_LINE_LEAD_MS before the map's first vocal opens the first line (see
            // firstLineTypingOpensAt). Defaulted FALSE in the C# so a replay stored before it
            // re-derives under the old gate; set for every live stack there, and therefore
            // unconditionally here, the browser having no era axis.
            this.firstLineLeadIn = true;
            // The COUNTABLE-CHARACTER STREAM (the C# constructor's countableTargets /
            // countableBase / countablePrefix): the whole map read as one run of countable cells,
            // which is the currency the drift readout (charsAheadOfPlayhead) measures in, and the
            // rush cap did before PR 3 removed it. countableTargets holds every
            // countable cell's target time sorted ascending, so the playhead's position is a binary
            // search; countableBase[k] plus countablePrefix[k][i] says where line k cell i sits in
            // that stream, so the caret's position is a lookup. All immutable after construction.
            // The C# rebuilds this stream when its subdivision-target era flips (PR 5,
            // AlignSubdivisionTargets), because its lines can be re-laid after construction. The
            // browser never flips: beatmap.lines already carries buildCells' final, aligned targets
            // (tokenCellTargets takes the rule unconditionally), so one build per play reads the
            // same stream the live C# engine ends up with.
            this.countableBase = new Array(this.lines.length);
            this.countablePrefix = new Array(this.lines.length);

            const countableTargets = [];

            for (let k = 0; k < this.lines.length; k++) {
                const cells = this.lines[k].cells;
                const prefix = new Array(cells.length + 1);

                this.countableBase[k] = countableTargets.length;
                prefix[0] = 0;

                for (let i = 0; i < cells.length; i++) {
                    prefix[i + 1] = prefix[i];

                    if (!isCountable(cells[i])) continue;

                    prefix[i + 1]++;
                    countableTargets.push(cells[i].target);
                }

                this.countablePrefix[k] = prefix;
            }

            // Overlapping lines can interleave their targets across a boundary, so sort rather than
            // assume the per-line order carries. NUMERIC comparator, because the JS default sorts
            // lexicographically and would put 10000 before 2000.
            countableTargets.sort((a, b) => a - b);
            this.countableTargets = countableTargets;
            // The one outstanding combo snapshot (TypingEngine.restorable, backlog 140, widened by
            // 167, again by 243, again by 259 and folded rather than replaced since 262):
            // { lineIndex, cellIndex, streak, ownPressCredit,
            // positions }, the cell a wrong keypress spoiled or a word
            // skip abandoned and the streak that break cost, or null when there is nothing to go
            // back for. Set by that keypress or skip (through snapshotRedeemableBreak, the one
            // write site the two share), redeemed by typing that same cell correctly, and discarded
            // by any other combo break that had a streak to take (discardRestorableStreak). The
            // seams that discard it here are the two the browser can reach: a seal with unforeseen
            // misses and a rejected key. A word skip TAKES a snapshot rather than discarding one
            // since backlog 167, because it is a break the player can walk back into and undo. An
            // off-time press and a press past the playhead break nothing, so neither discards a
            // claim; the C# eras in which they did (OffTimeRule.BreaksCombo, and the pre-347 rush
            // cap) have no arm here.
            // History: docs/typebeat-core-history.md, "What discards a combo claim (restorable)".
            //
            // "That had a streak to take" is backlog 176: a break landing while the run is ALREADY
            // at zero costs nothing, so it leaves an outstanding claim alone rather than replacing
            // it with an empty one, and correcting the older cell still resumes the run (see
            // snapshotRedeemableBreak).
            //
            // `positions` is backlog 259: the run this break took, cell for cell (see
            // runPositions), so redeeming the claim puts back not just HOW MUCH combo the break
            // cost but WHERE it was earned. Its length is always the `streak` beside it, and a
            // later seal on an earlier line back-dates against those very entries.
            //
            // `ownPressCredit` is backlog 243: how much of the CURRENT run was credited by the
            // claim's OWN press rather than typed after it. It is 1 for a claim a word skip took,
            // because the skipping space is judged on the word gap it lands on and rebuilds the run
            // to exactly 1 (creditTheClaimsOwnPress), and 0 for every other claim and for a skip
            // whose space credited nothing. A break standing on no more than that is passive the
            // same way a break landing at zero is, and SPENDS the credit when it does, so anything
            // the player really types afterwards arms the next break normally. There is no
            // SkipSpaceCreditRule here, for the reason the two rules above have no arm either: the
            // pre-243 era exists in the C# only to re-derive a stored row, and a live play is
            // permanently on NotAStreakOfItsOwn.
            //
            // There is no ComboRestoreRule here, and that is a statement about /play rather than a
            // simplification: the enum exists in the C# so that RE-DERIVING a score stored before
            // backlog 140 does not hand it combo its fingers never earned. The browser only ever
            // plays LIVE (it has no mods payload, no replay input, and nothing anywhere re-scores a
            // stored row through this file: computeScore is called once, at the end of the play it
            // just ran), so ComboRestoreRule.OnFix is the only rule it can be in and a snapshot is
            // always taken. There is no ComboClaimRule either, backlog 176's own era axis, for the
            // identical reason: LatestBreakWins exists in the C# to re-derive a row stored before
            // that rule, and StreakedBreakWins is the only arm a live play can be in.
            this.restorable = null;
            // event hooks (optional; set by the renderer)
            this.onCharJudged = null;
            this.onWrongKey = null;
            this.onComboBroken = null;
            // A wrong key TYPED THROUGH into a cell (the C#'s CharJudged(WrongChar)), carrying the
            // cell index and the line index it landed on. Display only (backlog 316).
            this.onTypoLanded = null;
            // TypingEngine.ComboRestored: a corrected typo just resumed the streak its wrong
            // keypress broke, carrying how much combo was put back. Raised with combo and maxCombo
            // already restored and BEFORE the corrected retype is judged.
            this.onComboRestored = null;
            this.onFinished = null;
            this.onFailed = null;
        }

        // The HP bar the HUD draws: the health account's value (HealthProcessor.Health), in [0, 1].
        // History: docs/typebeat-core-history.md, "The HP pool (HealthAccount)".
        get health() { return this.healthAccount.value; }

        // Whether the run has failed (HealthProcessor.HasFailed). Read by processKey, update and
        // computeScore, and by typebeat-player.js, which concludes the play on it.
        get failed() { return this.healthAccount.hasFailed; }

        // Wrong KEYPRESSES so far: the play's persisted mistype stat (TypingEngine.Mistypes).
        // Counted per keypress in BOTH models, so it means the same thing whether the key was
        // typed through (the default, and everything the browser can do) or rejected.
        get mistypes() { return this.counts.WrongChar || 0; }

        get liveAccuracy() { return this.totalKeypresses > 0 ? this.correctKeypresses / this.totalKeypresses : 1; }

        countCorrectCells() {
            let n = 0;
            for (const line of this.lines) for (const c of line.cells) if (c.state === 'correct') n++;
            return n;
        }

        get liveWpm() {
            return this.activeTimeMs <= 0 ? 0 : (this.countCorrectCells() / 5.0) / (this.activeTimeMs / 60000.0);
        }

        isLineComplete(idx) {
            const line = this.lines[idx];
            return this.caretIndex >= line.cells.length;
        }

        // The negation of TypingEngine.hasUntypedTypeable, which is what canSeal's EARLY seal
        // ("nothing left to type, do not hold the next line up") hangs off.
        //
        // An ABANDONED cell counts as untyped (backlog 167): the player owes that character exactly
        // as much as one they simply have not reached, and it is re-typeable until the line seals,
        // so a line the player can still come back into must not seal early. That is what keeps the
        // reclaim window running to the line's own deadline, which is the window the grace exists
        // to grant, and without it a skip near the end of a line closes its own escape hatch.
        noTypeableUntyped(line) {
            for (const c of line.cells) {
                if (c.typeable && (c.state === 'untyped' || c.state === 'abandoned')) return false;
            }
            return true;
        }

        canSeal(line, time) {
            return time >= line.endTime && (time >= line.endTime + line.sealGraceMs || this.noTypeableUntyped(line));
        }

        // TypingEngine.finishedEarly. EARLY FINISH (bit 4 of the second CONFIG flags word): the map's
        // FINAL line seals the moment every one of its typeable cells is typed correctly, rather than
        // at the line's own end, so the run (and the results card that follows) rides the player's
        // last word instead of the song. Set for every live stack like every other era on that word,
        // and the browser has no replay axis (it only plays live, writes no frames and re-derives no
        // stored row), so it is baked in here exactly as AuthoredSyllablesOnly and PR 5's aligned
        // targets are, rather than gated on a bit this file would have no way to receive.
        //
        // Only the FINAL line: an earlier one still seals on its own deadline, which is what keeps
        // the stack's pacing and the drag grace below keyed on the line's own end; the last line's
        // seal is the one that ends the run. A cell left WRONG (still fixable, and its combo break
        // already taken at the keypress) does NOT qualify, and neither does a line with no typeable
        // cell at all, so both keep the ordinary deadline. A player still typing when the line ends
        // is unaffected: finishedEarly reads no clock, so the line simply seals at its end as before.
        finishedEarly(index) {
            if (index !== this.lines.length - 1) return false;

            let anyTypeable = false;

            for (const c of this.lines[index].cells) {
                if (!c.typeable) continue;

                if (c.state !== 'correct') return false;

                anyTypeable = true;
            }

            return anyTypeable;
        }

        // TypingEngine.sealPermitted. DRAG FREEDOM (backlog 208): a line the player is still typing
        // must not be force-sealed out from under them at its normal deadline. The seal is deferred
        // while the caret is on the line, up to FLETCHER_DRAG_GRACE_MS past its hard deadline; past
        // that the line seals as usual (untyped cells become misses, one combo break) and the caret
        // is moved on. Always true with a pinned caret, and true under a flexible one for any line
        // the player is not currently on, so a finished-early line still seals exactly on its own
        // deadline, unless manualNewlines is holding it (see manualNewlineHoldsLineOpen).
        //
        // Its mirror is entryPermitted below (backlog 218): this one is how far past a line's
        // natural END a dragging player may still be on it, that one is how far before a line's
        // natural START a rushing player may already be on it, and both distances are the one
        // FLETCHER_DRAG_GRACE_MS.
        //
        // A line the player ABANDONED with a line skip keeps the grace after the caret has left it
        // (see lineAbandoned): the skip is caret movement only, so the line it walked out of has to
        // reach its misses and its one combo break at the very instant it would have with the
        // player still sitting there doing nothing.
        sealPermitted(index, time) {
            // MANUAL NEWLINES: a line the player has TYPED OUT and not closed is held to the drag
            // cutoff rather than left to seal on its own deadline. That deadline is the next line's
            // first word in any ordinary map, so sealing there is exactly the pull the setting
            // exists to prevent; the cutoff is the instant the push warning's red bar completes.
            // Held together, the seal, the seal loop's hand-over of the caret and the closed step
            // back (see processBackspace) all land on that one instant.
            if (this.manualNewlineHoldsLineOpen(index)) {
                const held = this.lines[index];
                return time >= held.endTime + held.sealGraceMs + FLETCHER_DRAG_GRACE_MS;
            }

            if (!this.fletcherEnabled || (this.activeLineIndex !== index && !this.lineAbandoned[index])) return true;

            const line = this.lines[index];

            // Nothing left untyped means there is no drag to protect: the line seals on its normal
            // deadline. (This is also what lets the FINAL line, which has no next line to roll on
            // to, finish the run on time once it is fully typed.)
            if (this.noTypeableUntyped(line)) return true;

            return time >= line.endTime + line.sealGraceMs + FLETCHER_DRAG_GRACE_MS;
        }

        // TypingEngine.manualNewlineHoldsLineOpen. Whether manualNewlines is holding `index` open:
        // a FINISHED line the player is still standing on, or one immediately behind a caret that
        // has been handed to the next line's head and could still step back up to it. The hold
        // ends at the drag cutoff, so a player who never presses is handed on exactly when they
        // would have been forced on with the setting off, not at the line's own deadline.
        //
        // A line that still owes a character is NOT held here (the drag rule already holds the
        // caret's own line, and holding one the player merely left behind untyped would put its
        // misses later than the song's own punishment), and neither is the LAST line, whose seal
        // is what ends the run.
        manualNewlineHoldsLineOpen(index) {
            if (!this.manualNewlines || !this.fletcherEnabled) return false;
            if (index + 1 >= this.lines.length || !this.noTypeableUntyped(this.lines[index])) return false;

            return this.activeLineIndex === index || (this.activeLineIndex === index + 1 && this.caretIndex === 0);
        }

        // TypingEngine.awaitingEntry. Whether the caret is on a line it may not TYPE on yet at
        // `time`: the player (or the song) has handed it on and that line's entry window has not
        // opened. The manual-newline era only, and only for a live line. processKey and
        // processEnter swallow presses while it holds; the getter below is the display's read.
        awaitingEntryAt(time) {
            return this.manualNewlines && this.fletcherEnabled && this.activeLineIndex >= 0
                && !this.entryPermitted(this.activeLineIndex, time);
        }

        // TypingEngine.AwaitingEntry, at the last update's time. typebeat-player.js greys the
        // current row while it is true (LyricStage's SetLineDim(0.4)).
        get awaitingEntry() {
            return this.awaitingEntryAt(this.lastUpdateTime === null ? -Infinity : this.lastUpdateTime);
        }

        // TypingEngine.entryPermitted. THE RUSH BOUND (see boundedRush), and the exact mirror of
        // sealPermitted above: may a FINISHED caret move on to line `index` at `time` yet? Drag
        // holds a line open up to FLETCHER_DRAG_GRACE_MS past its natural end (endTime +
        // sealGraceMs); rush enters a line up to the same FLETCHER_DRAG_GRACE_MS before its natural
        // start (activationTime). Always true under the unbounded era, which is what every run
        // stored before backlog 218 was played under and which this file cannot reach at all.
        //
        // Asked ONLY of a caret moving itself: the keypress roll (rollForwardIfFinishedEarly) and
        // the time-driven one (snapForwardOnLineStart). The hand-overs the SEAL LOOP performs, the
        // ordinary one and the drag cutoff's, do not consult it and must not: the song has moved off
        // the old line there, so entry is late rather than early, and refusing it would leave the
        // player in a dead zone the flexible caret does not otherwise have. On a loader-built map
        // that is never even a near thing, because a line's activation is clamped to its own
        // startTime, which IS the previous line's endTime, so this bound opens at most
        // FLETCHER_DRAG_GRACE_MS before the previous line could seal at all.
        entryPermitted(index, time) {
            return !this.boundedRush || time >= this.entryOpensAt(index);
        }

        // TypingEngine.entryOpensAt. The earliest instant the caret may be on line `index` by
        // RUSHING onto it: the line's own activationTime under the unbounded era, and
        // FLETCHER_DRAG_GRACE_MS before it under boundedRush, which is the head start a player earns
        // for having finished the line before it. Read by both arms that move a finished caret,
        // entryPermitted (the keypress roll) and snapForwardOnLineStart (the time-driven one).
        entryOpensAt(index) {
            return this.lines[index].activationTime - (this.boundedRush ? FLETCHER_DRAG_GRACE_MS : 0);
        }

        // TypingEngine.snapForwardOnLineStart. THE LINE-START SNAP (backlog 208): while the caret
        // sits PAST THE LAST CHARACTER of its line, the next line STARTING takes it, which is what
        // keeps the flexible default feeling like the pinned game it replaced (finish your line and
        // the song moves you on). A line the player has not finished is never touched: dragging
        // behind is precisely the freedom the flexible caret grants, and sealPermitted above makes
        // the same distinction for the same reason.
        //
        // Since backlog 218 this is also the DEFERRED ROLL arm (see boundedRush), which is why the
        // two are one method rather than two: both move a FINISHED caret onto the next line on a
        // TIME condition, they would fire on the same frame, and a second arm could only ever move a
        // caret the first one had already moved. The instant they fire at is entryOpensAt, the
        // line's activation under the unbounded era and FLETCHER_DRAG_GRACE_MS before it under the
        // bounded one, so the head start the bound grants a rushing player still exists: refusing
        // the keypress roll and then waiting for the full activation would take with one hand what
        // the mirror gives with the other.
        //
        // "Finished" is isLineComplete, i.e. the caret has walked off the end of the cell list.
        // That is exact rather than approximate: every caret advance runs autoSkipForward, so a
        // caret at cells.length is a caret with no typeable cell left in front of it, and it is the
        // same predicate the keypress-driven rollForwardIfFinishedEarly gates on. Cells left BEHIND
        // the caret wrong or abandoned do not hold the line: the player is done with them, and the
        // seal resolves them exactly as it always did.
        //
        // A LOOP rather than a single step, because the line it lands on can be finished the
        // instant it is reached (a line whose cells are all non-typeable is complete at caret 0),
        // and the roll-forward this backs up does not recurse.
        snapForwardOnLineStart(time) {
            // boundedRush belongs in this gate as well as flexibleLineSnap: this is the only arm
            // that can move a caret the bound parked, and a live stack always sets both anyway.
            if (!this.fletcherEnabled || (!this.flexibleLineSnap && !this.boundedRush) || this.finished) return false;

            // MANUAL NEWLINES: a finished caret is the PLAYER's to hand over, so this arm does
            // nothing for them. What hands them on otherwise is the seal itself, the instant the
            // engine takes the line away (sealPermitted), and a FINISHED line is held to that
            // instant (manualNewlineHoldsLineOpen), which is when the push warning's red bar
            // completes: the seal loop's own hand-over moves a manual caret exactly when a dragging
            // one would be moved.
            if (this.manualNewlines) return false;

            let snapped = false;

            while (this.activeLineIndex >= 0
                   && this.isLineComplete(this.activeLineIndex)
                   && this.activeLineIndex + 1 < this.lines.length
                   && time >= this.entryOpensAt(this.activeLineIndex + 1)) {
                this.activeLineIndex++;
                this.caretIndex = 0;
                this.autoSkipForward();
                snapped = true;
            }

            return snapped;
        }

        // TypingEngine.rollForwardIfFinishedEarly. RUSH FREEDOM (backlog 208): the moment a press
        // finishes a line, the caret moves straight on to the next one instead of waiting for its
        // activation cue. It is the KEYPRESS half of moving a finished caret on; the time-driven
        // half, for a caret that became finished without a press of its own, is
        // snapForwardOnLineStart above. The finished line is left UNSEALED and seals on its own
        // normal deadline (with nothing missed, since it is fully typed), so nothing about the
        // song's timeline moves; only the player's position does. No-op on the last line, which
        // keeps the default "line complete, wait for the song" behaviour.
        //
        // BOUNDED since backlog 218 (see boundedRush): "the moment a press finishes a line" is now
        // "the moment a press finishes a line, if that next line is within FLETCHER_DRAG_GRACE_MS of
        // starting". Refused, the caret parks past the last cell and snapForwardOnLineStart makes
        // the move for it when the bound opens. `time` is the keypress's own time, the same value
        // the press was judged on, so the bound is a pure function of (char, time) like everything
        // else here and the desktop's replay of the same run reproduces it exactly.
        rollForwardIfFinishedEarly(time) {
            if (!this.fletcherEnabled || this.finished || this.activeLineIndex < 0) return;

            // MANUAL NEWLINES: the press that finished the line does NOT hand the caret on. The
            // caret parks past the last cell and waits for the player's own newline
            // (rollForwardManually), or for the seal to force it, which is the same parked state a
            // refused rush leaves and therefore the same state every arm downstream understands.
            if (this.manualNewlines) return;

            if (this.caretIndex < this.lines[this.activeLineIndex].cells.length) return;
            if (this.activeLineIndex + 1 >= this.lines.length) return;
            if (!this.entryPermitted(this.activeLineIndex + 1, time)) return;

            // Lines seal in order and the player never leaves a line except by finishing it or by a
            // drag cutoff (which advances nextSealIndex with them), so the next line is always
            // unsealed.
            this.activeLineIndex++;
            this.caretIndex = 0;
            this.autoSkipForward();
        }

        // TypingEngine.rollForwardManually. THE MANUAL NEWLINE (see manualNewlines): the player's
        // own space-on-a-finished-line, Enter, or (under newlineOnTypedLetter) letter, which is the
        // ONLY thing that hands a parked caret on while the setting is armed. Returns whether the
        // caret moved, so a caller swallows only an effective press.
        //
        // Two conditions, and deliberately NOT the automatic roll's third: the caret must be
        // FINISHED and there must be a next line. The entry window does not gate the press: the
        // newline always lands, and the line it lands on waits greyed and untypeable
        // (awaitingEntry) until its window opens. No WPM clock work, for the reason processEnter
        // gives: a newline is not typing, so the landed line's clock arms lazily on its first real
        // press.
        rollForwardManually(time) {
            if (!this.manualNewlines || !this.fletcherEnabled || this.finished || this.activeLineIndex < 0) return false;
            if (this.caretIndex < this.lines[this.activeLineIndex].cells.length) return false;
            if (this.activeLineIndex + 1 >= this.lines.length) return false;

            this.activeLineIndex++;
            this.caretIndex = 0;
            this.autoSkipForward();
            return true;
        }

        // TypingEngine.clockRunsFrom. The instant from which the WPM/active-time clock runs across
        // the frame that STARTED at previousTime, or null when it does not run over that frame at
        // all. Normally that instant is previousTime itself, i.e. the whole frame counts; the one
        // case that returns something later is the lazy arm below.
        //
        // The whole frame, always, with a pinned caret. Under the flexible one the caret can be
        // sitting on a line the song has not reached yet, and a clock that ran through a 20-second
        // instrumental would read the wait as typing time; so being there is not by itself enough,
        // and the clock runs from that line's activationTime, which is exactly when the line would
        // have gone active while pinned.
        //
        // THE TWO PARKED STATES ARE NOT THE SAME FACT (backlog 222 correcting backlog 218, whose
        // note here said they were, and that claim is what hid the defect below). A caret the rush
        // bound left sitting past the last cell of its OWN line still needs nothing from this
        // method: the caller accrues only while the active line is INCOMPLETE, that caret's line is
        // complete by definition, and there is genuinely nothing the player can type. But a caret
        // that rollForwardIfFinishedEarly or snapForwardOnLineStart has moved on to the NEXT line
        // sits there from entryOpensAt, which is FLETCHER_DRAG_GRACE_MS BEFORE that line's
        // activation, and processKey has no time gate of its own: the player really can type there,
        // up to 1500 ms per line. Every character they land is counted by countCorrectCells for the
        // rest of the run, so counting them against a stopped clock walked liveWpm (and the HUD's
        // rolling readout in typebeat-player.js, which stamps its samples in this same currency)
        // upward for free.
        //
        // THE LAZY ARM. So the clock arms on the FIRST press the player puts on such a line
        // (armWpmClockAheadOfTheCue) and runs from that press's own time onward. Not from
        // entryOpensAt: that would hand the whole head start back as typing time for a player who is
        // sitting there NOT typing, which is the dilution the activationTime gate exists to prevent.
        // And nothing is back-dated: the arming press is credited no elapsed time at all, exactly
        // like a press made at activationTime + 0 on the ordinary path. Once the playhead reaches
        // the line, the activationTime branch above answers first and the arm is never consulted
        // again.
        clockRunsFrom(previousTime) {
            if (!this.fletcherEnabled || this.activeLineIndex < 0) return previousTime;

            if (previousTime >= this.lines[this.activeLineIndex].activationTime) return previousTime;

            // Ahead of the cue: the clock runs only from an arm, and only from an arm belonging to
            // the line the caret is on now (see wpmClockArmedLine for why that check is the reset).
            if (this.wpmClockArmedLine === this.activeLineIndex) {
                return Math.max(previousTime, this.wpmClockArmedAt);
            }

            return null;
        }

        // TypingEngine.armWpmClockAheadOfTheCue. Arm the WPM clock on the active line when the press
        // at `time` is the first one the player has put on it AHEAD OF ITS CUE (see clockRunsFrom).
        // No-op on every ordinary press, i.e. one made at or after the line's activationTime, where
        // the clock is already running.
        //
        // Called from processKey once the press is known not to be inert, so every press that can
        // resolve or spoil a cell arms the clock, a wrong key included: what the arm records is that
        // the player is TYPING here, not what the keystroke turned out to be worth. A press the
        // caret-parked guard refuses outright never reaches it, which is right, since that press
        // does nothing at all.
        armWpmClockAheadOfTheCue(time) {
            if (!this.fletcherEnabled || this.activeLineIndex < 0) return;

            if (time >= this.lines[this.activeLineIndex].activationTime) return;

            // Only the FIRST press arms. A later one must not push the arm forward, or the time
            // between the two presses (the player typing) would be swallowed.
            if (this.wpmClockArmedLine === this.activeLineIndex) return;

            this.wpmClockArmedLine = this.activeLineIndex;
            this.wpmClockArmedAt = time;
        }

        // TypingEngine.PlayheadCountablePosition. How many COUNTABLE characters the song has
        // reached by `time`: the count of countable cells across the whole map whose target time is
        // at or before it. The playhead's position in the countable stream, and the reference the
        // drift readout (and, before PR 3, the rush cap) is measured against. Monotonic in time and
        // a pure function of the beatmap.
        playheadCountablePosition(time) {
            let lo = 0;
            let hi = this.countableTargets.length;

            while (lo < hi) {
                const mid = (lo + hi) >> 1;

                if (this.countableTargets[mid] <= time) lo = mid + 1;
                else hi = mid;
            }

            return lo;
        }

        // TypingEngine.CaretCountablePosition. The player caret's position in the same countable
        // stream: every countable cell in the lines before the active one, plus the countable cells
        // behind the caret within it. 0 when no line is active.
        get caretCountablePosition() {
            return this.countablePositionAt(this.caretIndex);
        }

        // TypingEngine.countablePositionAt. caretCountablePosition for an ARBITRARY caret index on
        // the active line: the property above is exactly this at the live caret.
        // History: docs/typebeat-core-history.md, "The rush cap".
        countablePositionAt(index) {
            if (this.activeLineIndex < 0) return 0;

            const prefix = this.countablePrefix[this.activeLineIndex];
            const at = Math.min(Math.max(index, 0), prefix.length - 1);

            return this.countableBase[this.activeLineIndex] + prefix[at];
        }

        // TypingEngine.CharsAheadOfPlayhead. Signed countable-character drift of the caret against
        // the playhead: positive = rushing ahead, negative = dragging behind. The quantity the rush
        // cap bounded until PR 3 removed it, and still the honest read-out of what the flexible
        // caret is about. (TypingEngine.rushesPastCap is not mirrored: InputEra2 never consults it.)
        charsAheadOfPlayhead(time) {
            return this.caretCountablePosition - this.playheadCountablePosition(time);
        }

        // TypingEngine.NextUnsealedLineIndex. The first line that has not sealed yet; -1 once every
        // line has sealed. While no line is active (pre-roll, or the dead zone between a seal and
        // the next line's cue) this is the UPCOMING line. Read-only and judgement-free: nothing in
        // this file reads it, it exists because the line the SONG is on is no longer the line the
        // caret is on (backlog 208), and typebeat-player.js has to put the sung sweep on the former.
        get nextUnsealedLineIndex() {
            return this.nextSealIndex < this.lines.length ? this.nextSealIndex : -1;
        }

        // TypingEngine.SongWindowOpen. Whether the PLAYHEAD is inside a typeable line window: the
        // plain time rule on the first unsealed line, read independently of where the player's
        // caret has got to. Equal to "a line is active" with a pinned caret; under the flexible one
        // the two diverge, because the caret can be parked on a line the song has not reached
        // (rush) or still finishing one the song has left (drag).
        // TypingEngine.FirstLineTypingOpensAt. Whether the map's FIRST line is inside the head start
        // FIRST_LINE_LEAD_MS gives it at `time`: the window in which a press may open a line the
        // clock has not activated yet (see processKey). The line's own activationTime is NOT moved,
        // because the WPM clock is armed from it; a press made in the head start arms the clock
        // ahead of the cue exactly as a rushed press on any later line does (armWpmClockAheadOfTheCue).
        //
        // The C# gates it on FirstLineLeadIn, CONFIG frame bit 16, which the live playfield sets for
        // every stack and a replay stored before PR 2 carries clear. The browser only ever plays
        // live, so it takes the head start unconditionally (firstLineLeadIn below is always true),
        // and every parity fixture that feeds the C# arm has to set the bit, the same treatment bits
        // 2 through 12 already get.
        firstLineTypingOpensAt(time) {
            if (!this.firstLineLeadIn || this.finished || this.activeLineIndex !== -1) return false;
            if (this.nextSealIndex !== 0 || this.lines.length === 0) return false;

            const first = this.lines[0];

            return time >= Math.min(first.activationTime, first.firstVocalTime - FIRST_LINE_LEAD_MS)
                && time < first.endTime + first.sealGraceMs;
        }

        get songWindowOpen() {
            if (this.finished || this.nextSealIndex >= this.lines.length || this.lastUpdateTime === null) return false;

            const line = this.lines[this.nextSealIndex];

            return this.lastUpdateTime >= line.activationTime && this.lastUpdateTime < line.endTime + line.sealGraceMs;
        }

        // TypingEngine.SongIsOnTheCaretsLine. Whether the SONG is asking for characters on the very
        // line the player's caret is on. songWindowOpen ALONE is not that question, and the
        // difference is the whole of a real map's instrumental gap: a decoder-built line's window
        // runs to the NEXT line's start (contiguous, no holes), so through a twelve-second
        // instrumental the playhead is still inside line N's window and songWindowOpen stays true,
        // while the player who finished line N is parked at the head of line N+1 with nothing being
        // asked of them.
        //
        // On the desktop this is what lets Space reach the mid-song skip overlay from a parked
        // caret (TypeBeatPlayfield's key handler). Since backlog 230 the browser has a real skip
        // too (typebeat-player.js: a fresh BufferSource started at an offset seeks exactly), and
        // this predicate gates the same two things there: the countdown chip that labels a dead
        // stretch, and the chip's own skip button. With the caret parked on the next line, "a line
        // is active" no longer means "the song is asking for characters".
        get songIsOnTheCaretsLine() {
            return this.activeLineIndex >= 0 && this.activeLineIndex === this.nextSealIndex && this.songWindowOpen;
        }

        // TypingEngine.ActiveLineUntouched. True while the player has put nothing into the active
        // line yet: no cell behind the caret is 'correct' or 'wrong' (leading auto-skipped
        // punctuation, and cells a word skip abandoned, are not progress). A PURE READ, changing
        // nothing. The key handler (typebeat-player.js, routeKeyDown) pairs it with
        // songIsOnTheCaretsLine to drop a habitual Space on a caret parked at the head of a line
        // the song has not reached, exactly as TypeBeatPlayfield's key handler does.
        get activeLineUntouched() {
            if (this.activeLineIndex < 0) return false;

            const cells = this.lines[this.activeLineIndex].cells;
            const end = Math.min(this.caretIndex, cells.length);

            for (let i = 0; i < end; i++) {
                const state = cells[i].state;
                if (state === 'correct' || state === 'wrong') return false;
            }

            return true;
        }

        // TypingEngine.DragCutoffAt. THE PUSH (backlog 263), read out for DISPLAY ONLY: the instant
        // the caret's own line will be force-sealed out from under it and the caret landed on the
        // next line, or null when no such push is coming. Nothing here decides anything, it only
        // reports the deadline sealPermitted already compares against, so typebeat-player.js can
        // warn the player before it arrives (the red right-anchored bar, the mirror of the cue-in
        // bars) instead of the push landing with no notice at all.
        //
        // Non-null under exactly the three conditions the drag cutoff's hand-over arm needs. The
        // caret must be UNPINNED (fletcherEnabled), or the line was never held open for the player
        // in the first place; the browser holds that true unconditionally, and the clause is kept
        // because this is a mirror of the C# and a divergence in the CONDITION is as real as one in
        // the value. The caret's line must also be the next line due to seal, or the seal loop
        // reaches it with the caret elsewhere and that arm does not run: a line the player walked
        // out of with a line skip is still held open by lineAbandoned, but nobody is standing on it
        // to be pushed. And the line must still owe a character (the same noTypeableUntyped scan the
        // seal asks), because a line with nothing left untyped seals on its ordinary deadline with
        // no drag to protect and no punishment to warn about: typing the last cell out calls the
        // push off there and then.
        //
        // ONE EXCEPTION, and it warns about a push that really is coming: under manualNewlines a
        // line the player has typed out but not closed is held to its own cutoff
        // (manualNewlineHoldsLineOpen), so the bar keeps counting down to the instant that line is
        // taken from them, the press being what moves the caret on and the cutoff the backstop.
        get dragCutoffAt() {
            if (this.finished || !this.fletcherEnabled || this.activeLineIndex === -1
                || this.activeLineIndex !== this.nextSealIndex) return null;

            const line = this.lines[this.activeLineIndex];

            if (this.noTypeableUntyped(line) && !this.manualNewlineHoldsLineOpen(this.activeLineIndex)) return null;

            return line.endTime + line.sealGraceMs + FLETCHER_DRAG_GRACE_MS;
        }

        // DrawableTypeBeatCharObject.ApplyEngineResult: a cell hands the score processor its ONE
        // result and every later attempt on the same cell is dropped (`if (Judged) return;`). Every
        // processor.applyResult in this engine goes through here, so the submitted account can never
        // hold two entries for one cell however the play reaches it.
        //
        // The same one result is the one the HEALTH processor takes, so this is also where HP
        // recovers and where a seal Miss drains. The result is the AWARDED tier's, so a fix the
        // backlog 210 cap holds to Ok recovers OK_HEALTH_INCREASE however well it was timed, exactly
        // as on the desktop.
        //
        // HEALTH FIRST, then score, which is Player's own order (DrawableRuleset.NewResult) and is
        // not cosmetic: HealthProcessor stamps every result with JudgementResult.FailedAtJudgement
        // (whether the play had ALREADY failed when it arrived), and ScoreProcessor drops a result
        // so stamped. So the result that empties the bar still counts, and every result after it
        // (the rest of a failing seal, the word gap a failing skip's space lands on) reaches the
        // cell but not the submitted account.
        applyCellResult(cell, result) {
            if (cell.judged) return;
            cell.judged = true;

            const failedAtJudgement = this.healthAccount.hasFailed;

            this.healthAccount.applyResult(result);

            if (!failedAtJudgement) {
                this.processor.applyResult(result, cell);
                // The result the submitted account holds for this cell, which a better retype
                // amends (bestRetypeTier). Null while the cell has none, and for a result
                // stamped FailedAtJudgement, which UpdateScoringResult refuses to touch exactly as
                // ApplyResultInternal refused to count it.
                cell.scoredResult = result;
            }
        }

        // The award a correct RETYPE leaves on an already-judged cell (PR 43, BEST RETYPE
        // JUDGEMENT): the retype's own tier, through the same correction cap, when it is strictly
        // better than the `standing` award (TypingEngine.Keys.cs's inert-retype arm), else the
        // standing award untouched. An improvement is TypingEngine.improveRetypedAward plus the
        // playfield's amend of the cell's existing osu result (TypeBeatPlayfield.applyCharJudgement's
        // Upgraded arm, then TypeBeatScoreProcessor.UpdateCellResult): the engine's tier counts move
        // from the standing award to the better one and the stored delta is replaced; combo, HP,
        // the keypress counts and the judged-cell count are untouched. The C# also overwrites the
        // cell's sample in its sync timeline, a results-screen series this core does not keep.
        // A method rather than inline so the parity harness can stub it to the pre-PR 43 rule
        // (bit 13 clear: return `standing`) and hold that arm against the C# with the bit clear.
        bestRetypeTier(cell, standing, delta, w) {
            const tier = awardedTier(classify(delta, w), cell.heldWrongBeforeJudged);

            if (!betterTier(tier, standing)) return standing;

            this.counts[standing] = (this.counts[standing] || 0) - 1;
            this.counts[tier] = (this.counts[tier] || 0) + 1;
            cell.firstCorrectDelta = delta;

            if (cell.judged && cell.scoredResult) {
                const result = toHitResult(tier);
                this.processor.updateResult(cell.scoredResult, result);
                cell.scoredResult = result;
            }

            return tier;
        }

        sealLine(idx) {
            const line = this.lines[idx];

            // The cells the line really did run out of time on, as opposed to the ones a word skip
            // had already given up: the only group whose break has not been taken yet, and
            // therefore the only one that can break combo here. The C# keeps the wider MISSED count
            // alongside it for LineSealResult.MissedCells, which this mirror raises no event for.
            let unforeseen = 0;

            // The LAST of them, in cell order: the position this line's break is back-dated to
            // (backlog 259, see runPositions). Meaningless while unforeseen is 0, which is exactly
            // when nothing reads it.
            let lastUnforeseenCell = -1;

            // The cells a word skip had abandoned and nobody reclaimed (the C#'s `abandoned` list,
            // raised as AbandonSealed): their skip drain is refunded before their Miss results land.
            let phantoms = 0;

            // PASS ONE, the C#'s own seal loop (TypingEngine.Update): resolve the STATES and count,
            // and nothing else. The results are a second pass now, because the seal's one combo
            // break has to be taken between the two (backlog 259): back-dated, that break is
            // mirrored into the submitted account by hand, and it must land before any of this
            // seal's results are weighted by the combo it leaves. The C# gets that ordering from
            // its event shape (the break is taken in the seal loop, the results arrive later in
            // TypeBeatPlayfield.applySealResults); this file has to write it out.
            for (let i = 0; i < line.cells.length; i++) {
                const c = line.cells[i];

                // The engine's own miss count (TypingEngine.Update's seal loop), which drives the
                // HUD combo below and nothing that is submitted. A cell the line ran out of time on,
                // and ONLY that (backlog 124, reversing the predicate backlog 109 widened): a cell
                // left sitting WRONG is a character the player FINISHED, so it is a mistype and not
                // a miss, it keeps 'wrong' on screen, and it does not break the HUD combo here,
                // because its break was taken at the keypress. That is what puts the HUD combo back
                // in agreement with the submitted max_combo (backlog 123).
                //
                // An ABANDONED cell (backlog 167) is a miss, and this is where it becomes one: the
                // player skipped its word and never reclaimed it, so it turned out to be a character
                // they never typed. It counts and resolves exactly as an untyped cell does, and the
                // ONE thing it does not do is break combo, for precisely the reason a still-wrong
                // cell does not: that break was taken at the skip. It is excluded from the
                // back-dating pivot for the same reason: the break it is entitled to was taken then.
                const phantom = c.state === 'abandoned';

                if (c.typeable && (c.state === 'untyped' || phantom)) {
                    c.state = 'missed';
                    c.judgeType = 'Miss';

                    if (!phantom) {
                        unforeseen++;
                        lastUnforeseenCell = i;
                    } else {
                        phantoms++;
                    }
                }
            }

            if (unforeseen > 0) {
                // AT MOST ONE combo break per sealed line, no matter how many cells were missed, and
                // BACK-DATED to the last of them (backlog 259, TypingEngine.BackDatedSealBreak): the
                // break belongs to the cells the line ran out of time on, so it destroys the run as
                // it stood AT the last of them and leaves every increment earned strictly past it
                // standing. A line's misses only exist at its SEAL, which under the flexible caret
                // lands up to FLETCHER_DRAG_GRACE_MS after the song left the line, by which time the
                // player is on the next line rebuilding, so the wipe was taking a second break for a
                // fumble that had already cost one.
                //
                // UNCONDITIONAL here, where the C# reads the era flag its replays carry: the browser
                // only ever plays live (see `restorable`), and the live client sets the flag for
                // every mod stack, because when a break lands is not a window, an input model or a
                // caret and no mod has an opinion about it.
                const surviving = this.backDateBreakTo(idx, lastUnforeseenCell);

                // A real break, so it owns the streak (backlog 140). Distinct from the line-scoped
                // drop below, and since backlog 208 the two come apart HERE as well as in the C#:
                // the flexible caret can already be on a LATER line, holding a snapshot this break
                // has just cost it.
                //
                // Unconditional even when part of the run survived: a claim's streak was earned
                // EARLIER than the run this break cuts back, so redeeming it later could only put
                // back combo the break was entitled to take.
                this.discardRestorableStreak();

                // TypeBeatComboFeed.OnLineSealed, the hand-mirrored break, BEFORE the results below so
                // that every one of them, the seal's own unfixed typos included, is weighted by the
                // combo the break left. The Miss results no longer carry this break at all (they are
                // all combo-neutral now), which is what stops it landing a second time on a run the
                // player built past the missed cells.
                this.processor.breakComboTo(surviving);

                if (this.onComboBroken) this.onComboBroken();
            }

            // PASS TWO. DrawableTypeBeatHitObject.ApplySealResults: EVERY still-unjudged nested char
            // drawable of the line takes its result at seal time, in cell order, and the loop skips
            // cells that already carry one. `judged` is deliberately not the state test above: the
            // two come apart for a cell typed correctly and then BACKSPACED, which is 'untyped'
            // again and so is counted a miss for display, while its drawable keeps the Great it
            // already took.
            //
            // The result is a MISS for a cell nobody typed and a 'good' (the uncorrected-typo key,
            // TypeBeatResultMapping.UNFIXED_TYPO) for one left holding a wrong character, and BOTH
            // are marked COMBO-NEUTRAL immediately before they are applied, exactly as
            // TypeBeatComboFeed.SealedCellResult does it. Every seal miss since backlog 259, where only
            // the typos and the word skip's abandoned cells were marked before: the seal's break has
            // moved off the results and onto the hand-mirror above, and a Miss carries a break to
            // exactly one place, the combo it FINDS when it lands, which is the run the player holds
            // NOW rather than the one the back-dated break was entitled to cut.
            // A sealed line's cells can never be typed again, so a snapshot left on this one is
            // unredeemable whether or not the seal broke anything. Load-bearing since backlog 208:
            // the flexible caret DOES run ahead of the seal, so a run that finishes a line early
            // and then spoils a cell on the next one is holding a claim on a line the song is still
            // sealing behind it. Dropping it keeps the state truthful rather than relying on the
            // caret never going back.
            if (this.restorable !== null && this.restorable.lineIndex === idx) this.restorable = null;

            // TypeBeatHealthFeed.OnAbandonSealed: the skip drained each phantom
            // cell at the keypress, and the Miss each is about to take below drains again, so the
            // skip's drain is given back FIRST (the C# raises AbandonSealed before LineSealed). The
            // pair nets to one charge per cell, unless the bar was clamped at full in between.
            this.healthAccount.refundDeferredDrain(phantoms);

            for (const c of line.cells) {
                if (!c.typeable || c.judged) continue;

                this.processor.markComboNeutral(c);
                this.applyCellResult(c, c.state === 'wrong' ? 'good' : 'miss');
            }

            // DrawableTypeBeatHitObject.ApplySealResults' last act: the LINE object resolves as
            // IgnoreHit. Inert on the score account (not modelled there at all), but not on the
            // health one: HealthProcessor runs its fail test on EVERY applied result, so a bar a
            // run of rejections left at 0 fails here even if the line took nothing else.
            this.healthAccount.applyResult(null);
        }

        // TypingEngine.discardRestorableStreak. A combo break that is nobody's fixable typo just
        // happened, so the outstanding snapshot (if any) is discarded: the streak it was holding has
        // been lost to THIS break, and correcting the older cell later cannot bring back a run that
        // ended after it. Called at every combo-break seam except the two REDEEMABLE ones, a wrong
        // keypress and a word skip, which snapshot instead (snapshotRedeemableBreak): a break of
        // either kind that cost a streak of its own takes the claim there, so it needs no case here.
        discardRestorableStreak() {
            this.restorable = null;
        }

        // TypingEngine.creditCombo. Credit one combo increment, earned on the cell the press LANDED
        // on (not on the caret, which is not the same thing under the C#'s AnyOrderWithinWord; the
        // browser has no Dyslexia mod, so here the two coincide and the landed cell is still what is
        // recorded, because that is what the ledger means). The one place `combo` grows by a
        // keypress, so runPositions cannot fall behind it.
        creditCombo(cellIndex) {
            this.combo++;
            if (this.combo > this.maxCombo) this.maxCombo = this.combo;
            this.runPositions.push({ line: this.activeLineIndex, cell: cellIndex });
        }

        // TypingEngine.breakRun. Zero the run and hand back the positions that composed it: a
        // REDEEMABLE break puts them on its snapshot (snapshotRedeemableBreak) so a redemption can
        // restore them, and every other break simply drops them. The one place a break empties the
        // ledger, which is what keeps runPositions.length === combo true through all of them.
        breakRun() {
            const broken = this.runPositions;

            this.runPositions = [];
            this.combo = 0;

            return broken;
        }

        // TypingEngine.backDateBreakTo (backlog 259). A break dated at (lineIndex, cellIndex) rather
        // than at now: every increment earned AT OR BEFORE that cell is destroyed and every
        // increment earned strictly past it survives, in place. Returns the surviving run, which is
        // also `combo`'s new value.
        //
        // maxCombo is deliberately not touched: it records a run the player really did hold, and
        // this break is dated in the past, not a claim that the run never happened.
        backDateBreakTo(lineIndex, cellIndex) {
            const survivors = this.runPositions.filter(
                p => p.line > lineIndex || (p.line === lineIndex && p.cell > cellIndex)
            );

            this.runPositions = survivors;
            this.combo = survivors.length;

            return survivors.length;
        }

        // TypingEngine.snapshotRedeemableBreak. Take the snapshot for a REDEEMABLE break (a wrong
        // keypress, or a word skip): the streak it cost, against the cell the player has to come
        // back to. The one write site for `restorable` other than the discards, so the two breaks
        // that can be walked back into cannot drift apart, which is the shape the C# refactored to
        // when backlog 176 gave the two of them a condition to share.
        //
        // A break takes ownership of the streak only if it HAS a streak to own (backlog 176). One
        // landing at a combo of zero costs the player nothing, so it does not get to end an older
        // cell's claim on a run that is still redeemable: the claim it would write is empty, and
        // swapping a live claim for an empty one is a pure loss to a player who then goes back and
        // fixes both cells. With NOTHING outstanding it still writes its own empty claim, so that
        // redeeming it restores nothing, which is what resumeStreakIfThisRedeemsTheBreak has always
        // done with a zero.
        //
        // "A streak to own" excludes the streak the OUTSTANDING claim's own press credited (backlog
        // 243). One press can do both: a space struck inside a word abandons the rest of it and is
        // then judged on the word gap it lands on, which puts the combo back to 1. A break on that
        // very gap therefore broke a run of 1 rather than of 0, and under 176 alone that was enough
        // to overwrite a claim hundreds deep with a worthless one. The 1 was not progress, it was
        // the break's own press, so a break taking no more than the claim's own credit is passive
        // exactly as a zero-streak break is, and SPENDS that credit on the way past: whatever the
        // player rebuilds after this break is measured from zero, so the next break arms normally.
        // A correct character after the skipping space puts the run at 2 and the next break takes
        // the claim as it always did.
        //
        // A break that DOES own its streak takes the claim, and since backlog 262 it takes the
        // DISPLACED claim's streak with it rather than dropping it: the older break's increments are
        // just as unreachable as a passive break's, and a player who corrects both accidents in full
        // is entitled to both. The chain is then redeemed at the NEWEST of the broken cells, and the
        // seal's back-dated break (backlog 259) is what still takes back anything the line never
        // really earned. See the arm itself, below.
        //
        // The C# reads three era switches here that this file has no counterpart to, for the reason
        // set out on `restorable`: ComboRestoreRule, which decides whether any snapshot is taken at
        // all, ComboClaimRule, which decides the condition below, and SkipSpaceCreditRule, which
        // decides whether the ceiling is the claim's credit or a flat zero. The browser only ever
        // plays live, so all three are pinned to their live arms, the ceiling is just the credit,
        // and the condition stands unguarded.
        // `brokenPositions` is the run this break just took, cell for cell (breakRun, see
        // runPositions), so a redemption puts back not just HOW MUCH combo the break cost but WHERE
        // it was earned. Without it a restored streak would have to be dated at the cell that
        // redeemed it, and a later seal on an earlier line would then keep increments its misses are
        // entitled to destroy. Its length is always the `streak` beside it.
        snapshotRedeemableBreak(cellIndex, brokenStreak, brokenPositions) {
            const claim = this.restorable;

            if (claim !== null && brokenStreak <= claim.ownPressCredit) {
                // Passive: the claim stands where it is, with the credit this break just spent
                // taken off it.
                //
                // BACKLOG 260: it took nothing the player earned, but it still SPENT a run, so that
                // run FOLDS INTO the claim it left standing rather than being dropped. The call site
                // has already run breakRun, so anything the claim does not take is gone for good,
                // and the cells that earned it are resolved, so no retype can earn it back. Streak
                // and positions move together (positions.length === streak is the ledger's
                // invariant, see runPositions), and the broken ones append in run order because the
                // claim's own increments were earned before them. A NEW array either way: the broken
                // list is the old runPositions reference and must never be pushed onto.
                const positions = brokenStreak > 0 ? claim.positions.concat(brokenPositions) : claim.positions;

                this.restorable = {
                    lineIndex: claim.lineIndex, cellIndex: claim.cellIndex, streak: claim.streak + brokenStreak,
                    // The credit is still zeroed: the exemption is worth exactly one break (backlog
                    // 243), and folding the run in does not re-arm it.
                    ownPressCredit: 0,
                    positions: positions
                };
                return;
            }

            // BACKLOG 262: this break OWNS the streak it broke, so it takes the claim, but the claim
            // it displaces is not therefore worthless: the increments behind it were earned and the
            // cells that earned them are resolved, so discarding it loses them for good even though
            // the player can still go back and correct both accidents. The displaced claim FOLDS into
            // this one instead, which makes the NEWEST of the broken cells the one that redeems the
            // whole chain, and chains transitively through a third break and a fourth. Positions go in
            // front of this break's own, oldest first, because a redemption puts them back at the head
            // of the ledger (resumeStreakIfThisRedeemsTheBreak); the count still equals the streak;
            // and the credit still starts at zero, so backlog 243's one-break exemption is neither
            // granted nor extended by folding.
            //
            // A NEW array, for the reason the passive arm gives: the broken list is the old
            // runPositions reference and must never be pushed onto.
            //
            // The C# gates this on TypingEngine.FoldsDisplacedClaim (CONFIG frame bit 12) so that
            // every stored replay re-derives with the displaced claim discarded, which is the
            // max_combo its player was given. This file has no such arm, for the reason set out on
            // `restorable`: the browser only ever plays live, so the rule stands unguarded.
            //
            // What keeps it honest is backlog 259's back-dated seal break: the fold restores
            // increments earned before the older break without that break's own cell having been
            // fixed, but the restored positions go back WHERE THEY WERE EARNED, so a line sealing on
            // cells nobody typed still destroys every increment at or before its last unforeseen miss.
            if (claim !== null && claim.streak > 0) {
                this.restorable = {
                    lineIndex: this.activeLineIndex, cellIndex: cellIndex, streak: claim.streak + brokenStreak,
                    ownPressCredit: 0,
                    positions: claim.positions.concat(brokenPositions)
                };
                return;
            }

            this.restorable = {
                lineIndex: this.activeLineIndex, cellIndex: cellIndex, streak: brokenStreak, ownPressCredit: 0,
                positions: brokenPositions
            };
        }

        // TypingEngine.creditTheClaimsOwnPress. The press that just took (or passively kept) the
        // outstanding claim has itself credited one combo: record that on the claim, so a break
        // landing before the player has typed anything else takes nothing and leaves the claim alone
        // (backlog 243, see snapshotRedeemableBreak).
        //
        // Called from the one arm such a press can reach: a word skip's space, falling through to be
        // judged on the word gap the skip parked the caret on. Keyed on the press having ACTUALLY
        // credited combo rather than on the skip having happened, so a skip whose space earned
        // nothing (an inert retype of an already judged gap, the pre-347 rush cap refusing a caret
        // that was ALREADY out past its bound before the skip, an era the browser is never in since
        // PR 3 removed the cap, or a word abandoned all the way to the end of
        // a line, where there is no gap for the space to land on at all) records no credit and
        // behaves exactly as backlog 176 left it. The word this press gave up no longer counts
        // against that bound (backlog 260), which is why the cap refusing here is now a statement
        // about the player's own lead.
        creditTheClaimsOwnPress() {
            if (this.restorable !== null) this.restorable.ownPressCredit = 1;
        }

        // TypingEngine.resumeStreakIfThisRedeemsTheBreak. Redeem the outstanding snapshot if the
        // cell about to be typed correctly is the cell it was taken against: the run resumes at that
        // streak plus everything earned since, which is exactly combo + streak because no break has
        // landed in between (any that had would have discarded the snapshot). The claim is spent
        // either way, so a second correct retype of the same cell restores nothing.
        //
        // Two breaks can be waiting here, and the redemption is identical for both: the wrong
        // keypress that spoiled the cell (backlog 140), and the word skip that abandoned it
        // (backlog 167). In both cases the cell is the one the player has to come back to, so typing
        // it is what says they came back.
        //
        // Backlog 243's ownPressCredit is not read here, and the C# does not read it either: it
        // widened a positional destructure and discarded the new member. The credit only ever
        // decides who OWNS the claim, never what redeeming it is worth.
        resumeStreakIfThisRedeemsTheBreak(cellIndex) {
            const claim = this.restorable;

            if (claim === null) return;
            if (claim.lineIndex !== this.activeLineIndex || claim.cellIndex !== cellIndex) return;

            this.restorable = null;

            // A break that cost nothing restores nothing, and announcing it would have every
            // consumer write back a combo it already holds.
            if (claim.streak <= 0) return;

            this.combo += claim.streak;
            if (this.combo > this.maxCombo) this.maxCombo = this.combo;

            // The restored increments go back WHERE THEY WERE EARNED, at the head of the run, not at
            // the cell that redeemed them (backlog 259): a later seal on an earlier line back-dates
            // against those positions, and dating them here would let a break's misses keep combo
            // they are entitled to destroy. claim.positions.length is always claim.streak, so the
            // ledger comes back exactly as long as the run it is now describing.
            this.runPositions.unshift(...claim.positions);

            // TypeBeatComboFeed.OnComboRestored: the submitted account is moved by hand here, at the
            // same seam and for the same reason the break is (osu's combo is maintained
            // incrementally off results and no result carries this).
            this.processor.restoreCombo(claim.streak);

            if (this.onComboRestored) this.onComboRestored(claim.streak);
        }

        autoSkipForward() {
            if (this.activeLineIndex < 0) return;
            const cells = this.lines[this.activeLineIndex].cells;
            while (this.caretIndex < cells.length && !cells[this.caretIndex].typeable) {
                cells[this.caretIndex].state = 'autoskip';
                this.caretIndex++;
            }
        }

        // TypingEngine.skipCurrentWord. Abandon the whole word the caret is inside and leave the
        // caret on the word gap after it (or at the end of the line, for a word with no gap).
        // No `time` parameter, unlike the C#, which needs it only for the per-cell judgement deltas
        // this mirror does not raise (see the onCharJudged note below).
        //
        // Since backlog 167 the cells enter the PHANTOM state rather than being missed on the spot,
        // and everything except the BREAK moves out of here on to the two places a phantom cell can
        // end up: the backspace that reclaims it (processBackspace) and the seal that resolves it as
        // a miss (sealLine). What is left is the entry into that state, the break, and the SNAPSHOT
        // of the streak the break cost, taken against the first abandoned cell so that typing it
        // later resumes the run through the same backlog 140 machinery a corrected typo redeems.
        // That replaces the outright discard the skip used to do: a skip is a break the player can
        // walk back into, which is exactly what a typo's break is.
        //
        // Returns whether a redeemable claim is outstanding when it hands the press back, which is
        // what tells processKey that the combo the SAME press is about to earn on the word gap
        // belongs to the break rather than to the player (backlog 243, see
        // creditTheClaimsOwnPress). True for the claim this skip took AND for an older one it
        // passively left alone, because the argument is about the press and not about which break
        // wrote the claim.
        skipCurrentWord() {
            const cells = this.lines[this.activeLineIndex].cells;

            // The word: the run between the typeable SPACE cells either side of the caret. The whole
            // word rather than the tail from the caret, which gives up the same cells (everything
            // behind the caret is already resolved) but says what the feature promises.
            let start = this.caretIndex;
            let end = this.caretIndex;
            while (start > 0 && !(cells[start - 1].typeable && cells[start - 1].expected === ' ')) start--;
            while (end < cells.length && !(cells[end].typeable && cells[end].expected === ' ')) end++;

            // The cells this skip puts into the phantom state, in ascending order. The FIRST of them
            // is the one the snapshot is taken against, and there is always at least one (the cell
            // the caret is sitting on), so the break always has a cell behind it.
            const abandoned = [];

            for (let i = start; i < end; i++) {
                const c = cells[i];

                if (!c.typeable) {
                    c.state = 'autoskip'; // exactly what autoSkipForward would have done to it
                    continue;
                }

                // Only a cell nobody has put anything into is given up. A CORRECT one has already
                // handed its drawable the one result it will ever have and a Great cannot be revoked
                // (applyCellResult's `judged` guard is the same rule). A WRONG one is not given up
                // either, and since backlog 124 that is for its own reason: a typed-through wrong
                // character is a cell the player FINISHED, so abandoning the word cannot turn it
                // into a miss. Its deferred result is decided at the seal like every other unfixed
                // typo, which also leaves the promise intact that backspacing back into the word can
                // still fix it. Backlog 109 had it given up here, because at the time the only fate
                // available to an unfixed typo was a Miss.
                if (c.state !== 'untyped') continue;

                // The PHANTOM state, and NOT a resolution: no result is applied here, which is
                // precisely what leaves the cell's one and only result available to a retype (a
                // cell takes only its first, see applyCellResult). The miss COUNT waits for the
                // seal alongside it, because counting it now would say the character is lost while
                // the player can still walk back into it and type it.
                c.state = 'abandoned';
                c.judgeType = 'Abandoned';
                abandoned.push(i);
            }

            this.caretIndex = end;

            if (abandoned.length === 0) return false;

            // AT MOST ONE combo break for the whole word, the rule sealLine's misses follow.
            const brokenStreak = this.combo;

            const brokenPositions = this.breakRun();

            // Snapshotted against the FIRST abandoned cell, so re-typing that cell resumes the run.
            // A skip discards an older cell's claim the way any other intervening break would, but
            // only if it broke a streak of its own (backlog 176): a skip taken over a typo that has
            // already zeroed the run leaves that typo's claim redeemable, because the skip itself
            // cost nothing.
            //
            // The C# guards this call with its `reclaimable` flag and discards outright when that is
            // false, because under the pre-167 rule the abandoned cells are gone and there is
            // nothing to come back to. That arm has no counterpart here: the browser has no
            // WordSkipRule, so a skip in this file is permanently reclaimable.
            this.snapshotRedeemableBreak(abandoned[0], brokenStreak, brokenPositions);

            if (this.onComboBroken) this.onComboBroken();

            // TypeBeatComboFeed.OnWordAbandoned: the skip's one break on the SUBMITTED account, by
            // hand. It used to ride on the Miss the first abandoned cell took here; those results
            // now arrive at the seal, a whole line later, so with nothing left to carry the break
            // this seam carries it, exactly as the wrong-keypress path carries its own.
            this.processor.breakCombo();

            // The other half of the same C# seam (TypeBeatHealthFeed on WordAbandoned): HEALTH
            // drains MISS_HEALTH_DRAIN per abandoned cell now, at the skip, and gives it back at
            // whichever exit the cells take (the backspace that reclaims them, or the seal about to
            // miss them). Taken HERE, before the same press is judged on the word gap below, because
            // the C# drains before it recovers and the bar clamps at full in between.
            this.healthAccount.applyDeferredDrain(abandoned.length);

            // NO onCharJudged for the abandoned cells, deliberately, and the omission mirrors the
            // wrong-char path above: that hook is this renderer's rolling-WPM tap, and the C#
            // pushRollingSample() sits on the accepted-keypress path only, so tapping it here would
            // drift the browser's WPM readout away from the desktop's. The cells repaint from state.
            // engine `counts` is left alone too: it is the scored-only dict, and the C# records
            // nothing for an abandoned cell either (its Miss is counted at the seal).

            return this.restorable !== null;
        }

        update(time) {
            // (0) A FAILED run is frozen (backlog 306). The desktop fails inside whatever is being
            // processed (a keypress, or one of a seal's results), and Player.PerformFail SCHEDULES
            // ConcludeFailedScore rather than running it, so the step that failed runs to its end:
            // the rest of that seal pass, and every other line the same Engine.Update seals, is still
            // judged. None of it reaches the SUBMITTED account, though: each of those results arrives
            // stamped FailedAtJudgement and ScoreProcessor drops it (see applyCellResult). That is
            // this update() call running to its end, which is why the guard sits HERE, at the entry
            // of the NEXT one, and not inside the seal loop. processKey carries the same guard, and
            // typebeat-player.js concludes at the end of the tick.
            if (this.failed) return;

            // (1) accrue active typing time using this frame's span.
            //
            // The C# TypingEngine.Update takes a clockRate here and accrues dt / rate, because a
            // speed-adjusting mod makes beatmap milliseconds and real ones diverge and every WPM
            // readout is otherwise wrong by 1/rate. This mirror deliberately has no such parameter:
            // the browser player offers no rate mods at all (nothing here touches playbackRate), so
            // its rate is permanently 1 and the two accumulators agree. Adding one here is the
            // first thing to do if browser play ever gains a speed mod.
            //
            // clockRunsFrom is the flexible caret's own clause (backlog 208): a caret parked at the
            // head of a line the song has not reached yet is WAITING, not typing, so the clock does
            // not run through the instrumental it is waiting out. Since backlog 222 it answers WHERE
            // in the frame the clock starts rather than whether it ran at all, because such a caret
            // can also be TYPING: the frame the pre-cue lazy arm falls inside credits only the
            // stretch after the arm, and null is the no-accrual answer.
            //
            // KNOWN AND LEFT ALONE, exactly as in the C#: the predicate is evaluated on the CURRENT
            // activeLineIndex but with the PREVIOUS frame's time, so on a frame the caret rolled
            // forward inside, the part of it spent typing on the OLD line is tested against the NEW
            // line's cue and dropped. That is at most one frame of real typing time per line
            // transition (and only when a keypress-driven roll lands BETWEEN two frames), and
            // splitting the interval would need the old line index and the transition instant
            // carried as extra state, which is more machinery than the milliseconds are worth.
            if (this.lastUpdateTime !== null && this.activeLineIndex >= 0 && !this.finished
                && !this.isLineComplete(this.activeLineIndex)) {
                const from = this.clockRunsFrom(this.lastUpdateTime);

                if (from !== null) this.activeTimeMs += Math.max(0, time - from);
            }
            this.lastUpdateTime = time;

            // Whether the caret ends this update on a line it was not on when the update started.
            // Only the DRAG CUTOFF inside the seal loop needs it: the ordinary activation arm and
            // the snap both auto-skip where they stand, while the cutoff's auto-skip is deferred to
            // the end exactly as the C# defers it (there it rides the single LineActivated raise).
            // The deferral is behaviour, not tidiness: it leaves the cutoff's caret at index 0 while
            // the snap arm below runs, so a cutoff onto a line of pure punctuation is NOT read as
            // complete until the next update, which is when the snap may take it.
            let pendingActivation = false;

            // (2) seal every line whose deadline passed, in order. sealPermitted is the DRAG
            //     freedom: a line the player is still typing holds off its own seal for
            //     FLETCHER_DRAG_GRACE_MS, so the loop stops at it rather than skipping it. The
            //     deadline half is canSeal OR finishedEarly, and the OR is EARLY FINISH: the final
            //     line seals the instant it is fully typed, before its own end (see finishedEarly).
            while (this.nextSealIndex < this.lines.length
                   && (this.canSeal(this.lines[this.nextSealIndex], time) || this.finishedEarly(this.nextSealIndex))
                   && this.sealPermitted(this.nextSealIndex, time)) {
                const index = this.nextSealIndex;

                this.sealLine(index);
                this.nextSealIndex++;

                if (this.activeLineIndex === index) {
                    if (this.fletcherEnabled && index + 1 < this.lines.length) {
                        // DRAG CUTOFF: the player ran out of borrowed time mid-line. Land them on
                        // the next line immediately rather than in a dead zone. Setting the caret
                        // here, inside the loop, is also what stops one cutoff cascading into the
                        // line the player just landed on: the next sealPermitted call sees them on
                        // it and grants it its own drag grace. A cascade still happens when that
                        // grace has ALSO expired (an idle player), which is the intended catch-up
                        // to the song.
                        this.activeLineIndex = index + 1;
                        this.caretIndex = 0;
                        pendingActivation = true;
                    } else {
                        this.activeLineIndex = -1;
                        // The caret goes back to 0 with it, exactly as TypingEngine.Update's seal
                        // loop does it. Nothing here reads the caret while no line is active
                        // (typebeat-player.js gates every read on `active`, and processKey /
                        // processBackspace / autoSkipForward all bail), and the next activation
                        // sets it to 0 anyway, so this changes no behaviour today. It is mirrored
                        // because a stale caret sitting on a sealed line is a trap for the next
                        // reader of either file, not because anything can currently see it.
                        this.caretIndex = 0;
                    }
                }
            }

            // (3) finish, or activate the next line inside its cue window.
            if (this.nextSealIndex >= this.lines.length) {
                if (!this.finished) {
                    this.finished = true;
                    if (this.onFinished) this.onFinished();
                }
                return;
            }
            if (this.activeLineIndex < 0) {
                const cand = this.lines[this.nextSealIndex];
                if (time >= cand.activationTime && time < cand.endTime + cand.sealGraceMs) {
                    this.activeLineIndex = this.nextSealIndex;
                    this.caretIndex = 0;
                    this.autoSkipForward();
                }
            }

            // (4) THE LINE-START SNAP (backlog 208): the caret is sitting past the last character
            //     of its line and the next line has just started, so hand the player onto it,
            //     exactly as the pinned arm above would have. Placed AFTER the activation arm, as
            //     in the C#, so a fresh activation onto an already-finished line (every cell
            //     non-typeable) is snapped on in the same update rather than a frame later.
            //
            //     The C# additionally folds this, the activation above and the drag cutoff in the
            //     seal loop into ONE LineActivated raise, so a catch-up cascade through several
            //     stale lines relayouts the stage exactly once. This mirror raises no such event
            //     (typebeat-player.js reads engine.activeLineIndex per frame), so there is no
            //     announcement to deduplicate and the discipline has nothing to port: the one
            //     activation is the one caret position each of these arms leaves behind. What DOES
            //     port is the deferred auto-skip that rides that raise, below.
            this.snapForwardOnLineStart(time);

            // The drag cutoff's auto-skip, at the seam the C# runs it (guarded on there being a
            // line to skip on: a cutoff that cascaded off the end of the map has already parked the
            // caret nowhere and finished the run above).
            if (pendingActivation && this.activeLineIndex >= 0) this.autoSkipForward();
        }

        // The delta a press on cell cellIndex is judged, stored and announced on (mirrors
        // TypingEngine.judgedDeltaFor). A cell inside a SYLLABLE GROUP is judged against the
        // group's sung SPAN: 0 anywhere inside [startTime, endTime] (edge-inclusive), the signed
        // distance to the nearer edge outside it (negative early, positive late), so the same
        // classify ladder grades distance from the syllable's edge.
        //
        // A cell in NO group keeps the classic point delta (time minus the cell's own target), and
        // that fallback is what gives a stylised word its per-character judgement: space cells,
        // lines with no groups, and every cell of an unsyllabifiable token land in the same arm.
        //
        // Since backlog 209 a STRETCH cell lands there too, and it is IN a group: a freestyle slot
        // or a cell of a run of three or more identical characters inside one syllable (see
        // isCharTimedStretch). Those cells are interchangeable to the matcher, so the span paid a
        // whole mashed run a delta of zero seconds ahead of the vocal; they go back on their own
        // character's clock while the rest of the line keeps the span.
        //
        // Since backlog 247 the group's FIRST cell narrows again, and it is the one cell the span
        // never really described: it is judged on the signed distance from the span's START, so
        // pacing a syllable out beats bursting it at the window's edge. The early side is
        // byte-identical to the span rule (a press before the start already answered that distance),
        // only the late side tightens, and the stretch exclusion above still wins, so a stretch cell
        // that opens a group stays on its own character target.
        //
        // No era arm here, unlike the C#. The desktop engine defaults to CLASSIC on all three axes
        // and turns the span rule (CONFIG frame flags bit 2), the stretch narrowing (bit 6) and the
        // first-character hybrid (bit 8) on for live play, because it must also RE-DERIVE stored
        // replays under the rules their fingers were graded on. The browser only ever plays live: it
        // has no mods payload, no replay input, it writes no replay frames (a /play submission
        // carries the aggregate account alone, and PUT /api/v2/scores/{id}/replay is the desktop
        // client's own upload path), and nothing re-scores a stored row through this file. So the
        // live rule is the only rule this engine can be in, and all three parts of it are
        // unconditional.
        //
        // THE FOURTH SPAN, the EASY mod's word-level shelter (TypingEngine.WordShelter), is NOT
        // mirrored, and it is the one arm here that is a mod rather than an era. It draws the same
        // rule around the whole WORD (TypingLine.Words) instead of the syllable, so a press anywhere
        // inside the word is dead on. /play has no mods payload at all (see the scoreMultiplier note
        // where the total is computed), so the browser can no more turn Easy on than it can turn on
        // the window scale that travels with it, and porting the shelter would add a span nothing
        // could select. The day browser play gains a mods payload, this is the second place it has
        // to arrive, right after WINDOWS.
        judgedDeltaFor(line, cellIndex, time) {
            const syllable = syllableIndexOf(line, cellIndex);

            if (syllable >= 0 && !isCharTimedStretch(line, cellIndex)) {
                const group = line.syllables[syllable];

                // The precedence is the C#'s: the stretch exclusion above, then the group's opening
                // cell, then the span's own edges.
                if (cellIndex === group.startCell) return time - group.startTime;

                if (time < group.startTime) return time - group.startTime;
                if (time > group.endTime) return time - group.endTime;

                return 0;
            }

            return time - line.cells[cellIndex].target;
        }

        // TypingEngine.ProcessEnter. LINE SKIP (backlog 241): give up the rest of the active line
        // and move on. Deterministic in (input, time) exactly like processKey, and returns whether
        // it did anything, so the caller swallows the key only for an effective press.
        //
        // IT IS CARET MOVEMENT AND NOTHING ELSE. The caret parks past the line's last cell, which
        // is the SAME parked state a boundedRush-refused roll leaves behind (see
        // rollForwardIfFinishedEarly), and from there the machinery that already exists carries the
        // player onward: the roll below hands them the next line at once when its entry window is
        // open, and snapForwardOnLineStart performs the deferred hand over when it opens later. The
        // cells left behind are NOT judged here. They stay untyped and become misses in the seal
        // loop, all at once, with the line's one combo break, at the line's own deadline: precisely
        // what would have happened to a player who stopped typing and sat there. So no judged
        // quantity changes value or timing against the un-skipped run, and the abandoned line's
        // drag grace is held for it by lineAbandoned, which is the one piece of state that claim
        // depends on.
        //
        // Deliberately NOT the word skip's shape (skipCurrentWord): that one puts cells into the
        // phantom state, takes an immediate combo break and snapshots a redeemable claim, all of
        // which move the account at press time. A line skip gives up more and costs nothing extra
        // for it, because the player pays the same misses either way, just without having to sit
        // through them.
        //
        // NO-OP when there is nothing to skip: no active line, the run finished, or the caret
        // already past the last cell (a line typed out, or one already skipped). In particular
        // Enter on a COMPLETE line does NOT perform the roll the next keypress would UNDER THE
        // AUTOMATIC hand-over: the two time-driven arms already own that caret, so a second way in
        // could only duplicate them. Under manualNewlines (the browser's arm) there is no
        // time-driven arm left to duplicate and Enter IS the newline, so on a complete line it does
        // the same hand-over a space does (rollForwardManually).
        //
        // The WPM clock needs nothing here and is deliberately NOT armed: an Enter is not typing.
        // Accrual stops by itself the moment the caret parks (update accrues only while the active
        // line is INCOMPLETE, and a parked caret's line reads complete), and on the line the player
        // lands on clockRunsFrom answers exactly as it does after a refused rush: stopped until the
        // first press there arms it, because a stale arm belongs to a line index the caret has
        // left.
        //
        // The guard is the C# one verbatim, with no `failed` arm even though processKey has one:
        // that flag is the health account's (the desktop freezes a failed play outside the engine,
        // in Player), and a skip moves nothing judged, so refusing it there would buy nothing and
        // would be a rule the C# does not have.
        processEnter(time) {
            if (this.finished || this.activeLineIndex < 0) return false;

            // PINNED CARET: Enter is not a gameplay key at all, so the press does nothing here and
            // falls through to whatever the client binds it to. The skip below is CARET MOVEMENT,
            // and it is worth something only because the unpinned caret may carry the player on
            // early: with the caret pinned to the song there is nothing to move them to, and the
            // press would give the rest of the line up for NOTHING. The browser is permanently
            // unpinned (fletcherEnabled is set in the constructor and nothing turns it off), so this
            // is a mirror of the C# guard rather than an arm a /play run can reach; the harnesses
            // that build a pinned engine by hand do reach it.
            if (!this.fletcherEnabled) return false;

            // A line handed to the player before its window opens waits rather than judging: the
            // press is swallowed, exactly as a keypress on it is (see awaitingEntryAt).
            if (this.awaitingEntryAt(time)) return false;

            const line = this.lines[this.activeLineIndex];

            // Hop auto-skip cells before measuring, exactly as processKey does, so "the caret is at
            // the end" is asked of the same frontier a keypress would see.
            this.autoSkipForward();

            // Nothing left to give up: parked already, or the line is fully typed. That second
            // state is the manual newline's own, so Enter closes it here (and reports the move so
            // the caller swallows the key); under the automatic hand-over it stays inert.
            if (this.caretIndex >= line.cells.length) return this.rollForwardManually(time);

            // Only a line with something still untyped is ABANDONED. A caret walked to the end over
            // nothing but wrong cells owes no misses, so the seal has no drag to protect and the
            // flag would only hold the line open for cells that are already resolved.
            if (!this.noTypeableUntyped(line)) this.lineAbandoned[this.activeLineIndex] = true;

            this.caretIndex = line.cells.length;

            // The same call the last character of a line makes, so an Enter inside the next line's
            // entry window rolls on at once and one outside it parks, with no second rule. Under
            // manualNewlines that roll is the PLAYER'S own, so the skip hands the line over the way
            // the newline key does: one Enter still means "I am done with this line, move me on".
            if (!this.rollForwardManually(time)) this.rollForwardIfFinishedEarly(time);

            return true;
        }

        processKey(c, time) {
            if (this.finished || this.failed) return false;

            // THE MAP'S FIRST LINE OPENS EARLY (FIRST_LINE_LEAD_MS, mirrors the head of
            // TypingEngine.ProcessKey). There is no previous line to rush from, so the PRESS is what
            // opens it: the same hand-over update's activation arm performs, made on demand. The
            // clock alone still leaves the line alone.
            if (this.activeLineIndex < 0 && this.firstLineTypingOpensAt(time)) {
                this.activeLineIndex = 0;
                this.caretIndex = 0;
                this.autoSkipForward();
            }

            if (this.activeLineIndex < 0) return false; // dead zone / pre-roll: harmless

            // WAITING FOR THE WINDOW (see awaitingEntryAt): the player was handed this line early,
            // so nothing here is judged, not the character and not a typo, until the window opens.
            if (this.awaitingEntryAt(time)) return false;

            let line = this.lines[this.activeLineIndex];
            this.autoSkipForward();

            if (this.caretIndex >= line.cells.length) {
                // MANUAL NEWLINES: on a finished line the SPACEBAR is the newline, so it hands the
                // caret on instead of being inert.
                if (c === ' ') return this.rollForwardManually(time);

                // OR BY TYPING (newlineOnTypedLetter): ANY letter hands the caret on and then lands
                // on the next line's first slot, right or wrong. It moves on the space's own terms,
                // with NO window gate on the press, and the window then refuses the CHARACTER
                // exactly as it refuses every other press on a line that has not opened yet: the
                // move is reported (true) and the player types the letter again when it does.
                if (this.newlineOnTypedLetter && this.manualNewlines && this.fletcherEnabled
                    && this.activeLineIndex + 1 < this.lines.length
                    && this.rollForwardManually(time)) {
                    line = this.lines[this.activeLineIndex];

                    if (this.awaitingEntryAt(time)) return true;

                    // A landed line with nothing typeable on it (every cell auto-skipped) leaves the
                    // caret past its end with no slot for the letter: the move is kept and the letter
                    // goes nowhere, the answer the awaiting branch above already gives. The C# takes
                    // the same answer since backlog 326 (it used to read Cells[caretIndex] and throw),
                    // and KeyHandlerOrderLiveParityTest's cell-less section holds the two together.
                    if (this.caretIndex >= line.cells.length) return true;
                } else {
                    // Every other key stays inert here: the parked dead zone.
                    return false;
                }
            }

            // The press is going to do something, so the player is typing on this line; if the song
            // has not reached it yet, that is the instant the WPM clock starts counting (backlog
            // 222, see clockRunsFrom). Placed here, above every branch below, so it is one site and
            // no path can quietly skip it, and attributed to the line the press LANDS on: a press
            // that finishes this line and rolls the caret onward has typed here, not there.
            this.armWpmClockAheadOfTheCue(time);

            let cell = line.cells[this.caretIndex];

            // Backlog 243: set when this press is a skip that left a claim outstanding, so the combo
            // the SAME press goes on to earn on the word gap is recorded as the claim's OWN credit
            // rather than as a run the player built after the break. Read once, at the one arm that
            // increments the combo.
            let skipLeftAClaimOutstanding = false;

            // Mashing mod: any key is the right key; judge it as the caret cell's expected char.
            // A FREESTYLE cell is exempt: it already accepts any key, and rewriting c here would
            // stamp the authoring marker over the char the player actually pressed (the one thing
            // a freestyle cell must remember). Space is the single exception to that exemption: a
            // freestyle cell REJECTS space (see the match below), so mashing's "any key is the
            // right key" promise needs a substitute to hand it, and the char an automated player
            // presses into a freestyle slot is the canonical one.
            if (this.mashingEnabled) {
                if (!cell.freestyle) c = cell.expected;
                else if (c === ' ') c = FREESTYLE_AUTO_CHAR;
            }

            // SPACE-SKIP (spaceSkipsWord), evaluated BEFORE the match: a space pressed while the
            // caret sits on a lyric character abandons that word. The caret cell is typeable here
            // (autoSkipForward ran above), so "expected is not a space" is exactly "inside a word",
            // and a space pressed ON the word gap keeps its ordinary meaning. After the Mashing
            // rewrite on purpose: mashing has already turned the press into the expected char, so
            // this is unreachable under it.
            //
            // Since PR 3 (the second input era) the skip also needs wrong input allowed: the C# gate
            // is `SpaceSkipsWord && (AllowWrongInput || !InputEra2)`, which collapses to this with
            // the era permanently on. Under Gatekeeper the press falls through to the strict
            // rejection below, like any other wrong key. /play is never on Gatekeeper
            // (allowWrongInput is always true here), so the term never refuses a live browser skip;
            // it is carried so the gate reads the same as the C# one.
            if (this.spaceSkipsWord && this.allowWrongInput && c === ' ' && cell.expected !== ' ') {
                skipLeftAClaimOutstanding = this.skipCurrentWord();

                if (this.caretIndex >= line.cells.length) {
                    // The abandoned word ran to the end of the line, so there is no word gap for
                    // the space to land on. The line is complete, exactly as it would be had the
                    // player typed that last word out, and the same end-of-line handling applies.
                    this.rollForwardIfFinishedEarly(time);
                    return true;
                }

                cell = line.cells[this.caretIndex]; // the word gap, judged as an ordinary space below
            }

            // STEP OVER A SPOILED GAP (backlog 184, mirroring TypingEngine's StrictSpaces branch):
            // the caret is PARKED on a word gap that a wrong letter took, and the space that gap was
            // owed has arrived. It walks the caret past the gap and leaves the typo exactly as it is.
            // The cell is NOT rewritten to correct, because the character sitting in it is not the one
            // that was owed: it stays an unfixed, backspace-redeemable claim, and the seal resolves it
            // as an unfixed typo like every other one. It judges nothing: no tier, no points, no combo
            // gained and none broken (the typo already took the break, and this press cannot be asked
            // to pay for it twice).
            //
            // Counted as a CORRECT keypress, which is the same argument once more: the space IS the
            // right key for the cell it lands on, the gap it was owed, and the typo has already paid
            // an error and a break of its own. correctKeypresses feeds liveAccuracy alone, so this
            // credits accuracy and moves nothing else: the cell still resolves as an unfixed typo and
            // still costs COMPLETION, which is where an unfixed typo is supposed to be paid for.
            //
            // Gated on the CELL STATE rather than on an era flag, exactly as the C# gates it: the
            // caret can only come to rest on a wrong cell through the park below, so this branch is
            // unreachable for any run that never parked.
            if (c === ' ' && cell.expected === ' ' && cell.state === 'wrong') {
                this.totalKeypresses++;
                this.correctKeypresses++;

                this.caretIndex++;
                this.autoSkipForward();

                this.rollForwardIfFinishedEarly(time);
                return true;
            }

            let delta = this.judgedDeltaFor(line, this.caretIndex, time);

            // SPACES ARE UNTIMED (backlog 148), decided here rather than after the match so that
            // EVERY reading of this press agrees on what its cell was worth: the correct press
            // below, and the wrong one typed through it since backlog 181, whose judgement carries
            // exactly the delta a correct press on the same cell would have carried. The rule and
            // its argument are on the untimed-space block further down; only the POSITION moved,
            // and the move reaches nothing new: the predicate is false for every LYRIC cell, so the
            // only press it can newly touch is the gap typo it was moved for.
            const untimedSpace = cell.expected === ' ';
            if (untimedSpace) delta = 0;

            // FREESTYLE cell: every char EXCEPT SPACE matches, in any case, under every mod (so the
            // Literate mod's exact-case rule is bypassed for it). The press is then judged exactly
            // like a correct char: same windows, points, combo, accuracy and completion, with the
            // pressed char kept in typedChar.
            // SPACE is carved out (backlog 50): it is the word-advance key, not a glyph a player
            // means to leave sitting in a lyric, so it falls through to the ordinary non-match path
            // below and is judged exactly as a wrong key on any other cell would be. The strict
            // REJECTION is still the only outcome available to it on a freestyle slot, which is the
            // one cell backlog 184 left out of the type-through it opened to the space key: the slot
            // renders the character the player pressed, so a space typed into one would blank it
            // rather than mark it (see spaceMayLand below). That is the skip-OFF arm, which /play
            // never plays: spaceSkipsWord is on there (backlog 198), so the skip gate above has
            // already taken a space on a freestyle slot as a word skip before it could get here.
            const matched = (cell.freestyle && c !== ' ') ||
                (this.caseSensitive ? c === cell.expected : fold(c) === fold(cell.expected));

            if (!matched) {
                // DEFAULT (allowWrongInput): a wrong LETTER is typed through, marked wrong,
                // backspaceable, instead of rejected. This path never feeds the mash-fail streak, so
                // the browser has no 13-key fail at all for a letter, which is correct: that guard
                // belongs to the rejection model (the Gatekeeper mod) and the browser can never be
                // in it.
                //
                // The space KEY is admitted too since backlog 184 (the C# StrictSpaces era, which
                // the browser is permanently on for the reason the word-gap clause below states):
                // with spaceSkipsWord off there is no word for the press to skip, so it means nothing
                // but a wrong character and is treated as one, no differently from a wrong letter.
                // The cell still renders its own expected character in the error red (cellGlyph in
                // typebeat-player.js substitutes the typed char for GAPS only), which is what makes
                // an invisible red space a non-problem. With spaceSkipsWord on the same press never
                // arrives here: the skip gate above consumed it (except under Gatekeeper, where since
                // PR 3 it falls through to the rejection below). /play is always on that arm
                // (backlog 198), so spaceMayLand is false for every live browser press and this
                // clause is the skip-OFF arm, kept for the mirror. A FREESTYLE slot is the one cell
                // that keeps refusing the key, because it has no expected glyph to redden and would
                // render blank instead of wrong (backlog 50's promise: any character EXCEPT the
                // word-advance key). The knock-on is deliberate: mid-word spaces stop feeding the
                // mash-fail streak, because they no longer reach the rejection branch that grows it.
                //
                // The WORD GAP takes a wrong letter exactly as a lyric cell does (backlog 181), and
                // unconditionally, which is where this parts from the C# gate
                // (`AllowWrongInput && c != ' ' && (WrongInputOnWordGaps || cell.Expected != ' ')`).
                // That third clause is an ERA arm the browser cannot be on the far side of: it plays
                // live and only live, writes no replay frames and re-derives no stored row, so the
                // live value (true, for every mod stack, Hard Rock included, see
                // DrawableTypeBeatRuleset.createEngine) is the only value it can hold, and the clause
                // collapses. Same shape as the span rule above, and for the same reason. The C# arm
                // of the fuzz parity test therefore has to SET that flag (CONFIG flags bit 3) or
                // every wrong key its script lands on a gap would be rejected there and typed here.
                // Backlog 184's space half is the same shape and needs the same treatment on that
                // arm: it is CONFIG flags bit 4 there and unconditional here.
                const spaceMayLand = !this.spaceSkipsWord && !cell.freestyle;

                if (this.allowWrongInput && (c !== ' ' || spaceMayLand)) {
                    this.totalKeypresses++;
                    this.errorCount++;

                    // The streak this keypress is about to break, snapshotted against the cell it
                    // spoils: correcting that cell resumes it (backlog 140, see restorable and
                    // resumeStreakIfThisRedeemsTheBreak). A wrong key on a SECOND cell discards the
                    // first cell's claim the way any other intervening break would, but only if it
                    // broke a streak of its own (backlog 176, see snapshotRedeemableBreak).
                    const brokenStreak = this.combo;

                    const brokenPositions = this.breakRun();

                    this.counts.WrongChar = (this.counts.WrongChar || 0) + 1;

                    cell.state = 'wrong';
                    cell.typedChar = c;
                    cell.judgeType = 'WrongChar';

                    // The cell is now one whose eventual judgement, if the player goes back for it,
                    // will be a CORRECTION and not a clean first attempt, and backlog 210 prices
                    // those differently (see awardedTier). Recorded on the cell rather than counted,
                    // so a wrong-fix-wrong-fix cycle caps exactly once.
                    //
                    // Gated on the cell being UNJUDGED, which is what makes the flag mean what it
                    // says. A cell that was already judged CLEAN and then spoiled by a wrong key on
                    // the way back through keeps that clean judgement (a cell takes only its first
                    // result, and the retype that follows is inert), so flagging it would demote a
                    // judgement the player earned honestly before they ever fumbled it.
                    if (cell.firstCorrectDelta === null) cell.heldWrongBeforeJudged = true;

                    const wrongCellIndex = this.caretIndex;

                    this.snapshotRedeemableBreak(wrongCellIndex, brokenStreak, brokenPositions);

                    // PARK on a spoiled word gap (backlog 184), instead of moving on: the space is
                    // still owed, so the player pays it (which steps over the typo, see the branch
                    // above) or backspaces it away, rather than being carried into the next word
                    // behind a gap the skip gate can no longer read as one. A further wrong letter
                    // lands on this same cell and overwrites this same character, so one park is one
                    // unfixed typo however many letters arrive; the snapshot above is idempotent for
                    // the same reason (a break with no streak behind it leaves the standing claim
                    // alone, see snapshotRedeemableBreak).
                    //
                    // Scoped to spaceSkipsWord because that is where the damage was: with the setting
                    // off, an advancing gap typo costs the player one cell, and with it on the next
                    // space fed the skip gate a spoiled gap and gave up a whole word. Every typo on a
                    // LYRIC cell advances exactly as it always has, under both arms. The browser
                    // takes the desktop's shipped default and runs with the setting ON (backlog 198),
                    // so the park is LIVE on every /play run: a wrong letter on a word gap parks
                    // here, and the fuzz sweep counts those presses as parkedGapTypos.
                    if (!(this.spaceSkipsWord && cell.expected === ' ')) {
                        this.caretIndex++;
                        this.autoSkipForward();
                    }

                    if (this.onComboBroken) this.onComboBroken();
                    // NO result for the cell (backlog 109), exactly as in the C#: the CELL's
                    // judgement still travels on CharJudged for the stage, but
                    // DrawableTypeBeatHitObject.ApplyCharJudgement returns before applying anything
                    // for a WrongChar. A miss is a character the line ran out of time on; a typo is a
                    // typo, and backspace can still fix this one, so the cell's one result is
                    // DEFERRED: the fix earns its real Great/Ok/Meh, and a typo left alone resolves
                    // at the seal as an unfixed typo, a 'meh' and not a miss (backlog 124).
                    //
                    // Which leaves the submitted COMBO with nothing to break it, because osu's combo
                    // is maintained incrementally off results. So the break is mirrored by hand here,
                    // exactly as the rejection path below does it (TypeBeatComboFeed.OnMistyped,
                    // which fires for both models). Without this the browser's max_combo would count
                    // on through the rest of the line after a break the engine has already taken.
                    this.processor.breakCombo();
                    // ...and that is the ONLY break this keypress costs (backlog 122), and since
                    // backlog 124 it is the cell's whole combo consequence in BOTH directions. The
                    // cell's deferred result is now a hit, so the danger has flipped from a second
                    // break to a free increment; sealLine handles it there, at the seam that applies
                    // the result, rather than here, because a typo the player goes back for must
                    // still earn its retype's combo normally.
                    // Neither of the two older renderer hooks fires here, and both omissions mirror
                    // the desktop. onWrongKey is the REJECTED-key feedback (the char popping off the
                    // caret, mirroring LyricStage.onWrongKeyRejected), which the desktop does not
                    // play for a char it accepted into a cell. onCharJudged is this renderer's
                    // rolling-WPM tap, and the C# pushRollingSample() sits on the ACCEPTED path only,
                    // below the branch we are in, so logging a wrong char there would drift the
                    // browser's WPM readout away from the desktop's. The typo's OWN feedback is
                    // onTypoLanded, raised below.
                    //
                    // HEALTH is charged here, at the keypress (backlog 166: TypeBeatHealthFeed on
                    // CharJudged(WrongChar) -> ApplyTypoDrain), and refunded if the character is
                    // erased; the unfixed typo the seal resolves later is HP-inert. It cannot hang
                    // off onCharJudged, which this branch never raises (see above). The C# raises that
                    // judgement for EVERY typed-through wrong key, a second letter overwriting a
                    // PARKED gap typo included, so the drain is taken every time too, and the erase
                    // later refunds one.
                    this.healthAccount.applyDeferredDrain(1);
                    // THE TYPO'S DISPLAY FEEDBACK (backlog 316): the other two readers of the C#'s
                    // CharJudged(WrongChar), which LyricStage.onCharJudged routes to the cell's
                    // 2 px shake, the gap dot's pulse (LyricLineDisplay.PlayJudgementFeedback) and
                    // the caret blink reset (Caret.NotifyTyped). Raised once per typed-through key,
                    // at the instant the C# raises that judgement: after the break and the drain
                    // above, before the roll below, with the cell and line the key landed on.
                    // DISPLAY ONLY: it reads nothing back into the engine, and it is a hook of its
                    // own rather than onCharJudged precisely so the rolling WPM stays accepted-only.
                    if (this.onTypoLanded) this.onTypoLanded(wrongCellIndex, this.activeLineIndex);
                    //
                    // A typo on the line's LAST cell finishes it exactly as a correct press would
                    // (the character is finished, it is simply wrong), so this path rolls the caret
                    // forward too, at the same seam the C# does it.
                    this.rollForwardIfFinishedEarly(time);
                    return true;
                }

                // GATEKEEPER (strict). Wrong key REJECTED: costs a keypress + combo + streak; caret
                // unmoved. Unreachable from the browser for a letter, and since backlog 184 for a
                // mid-word space too: the one case the default path still refuses above is the space
                // KEY pressed on a FREESTYLE slot, and only on the skip-OFF arm. /play runs skip-ON
                // (backlog 198), where the skip gate takes that space first, so no live /play press
                // reaches this branch at all; it and its mash fail stay for the mirror.
                this.totalKeypresses++;
                this.errorCount++;
                this.consecutiveWrongKeys++;
                this.breakRun();
                // Nothing was written into a cell, so there is nothing to go back and correct: this
                // break is final, and it ends any older cell's claim on the streak (backlog 140).
                this.discardRestorableStreak();
                // ...and the SUBMITTED combo breaks with it (TypeBeatComboFeed.OnMistyped sets
                // scoreProcessor.Combo.Value = 0), the same hand-written break the typed-through
                // path above now makes. No judgement is raised, so this is the only trace the
                // rejected key leaves in the score account: it lowers max_combo and every
                // combo-portion contribution that comes after it.
                this.processor.breakCombo();
                // ...and it is a MISTYPE (TypingEngine.Mistyped -> TypeBeatScoreProcessor
                // .RecordMistype): the one thing about a rejected key that outlives the play,
                // reported to the server as the combo_break statistics key by computeScore.
                this.counts.WrongChar = (this.counts.WrongChar || 0) + 1;
                if (this.onComboBroken) this.onComboBroken();
                if (this.onWrongKey) this.onWrongKey(c, this.caretIndex);
                // TypeBeatHealthFeed.OnWrongKeyRejected -> ApplyWrongKeyStreak:
                // 1/13 of the bar, and the mash fail at WRONG_KEY_FAIL_STREAK. The fail is the
                // account's (a No Fail veto can refuse it), where this branch used to set it itself.
                this.healthAccount.applyWrongKeyStreak(this.consecutiveWrongKeys);
                return true;
            }

            this.consecutiveWrongKeys = 0;

            // SPACES ARE UNTIMED (backlog 148), mirroring TypingEngine.ProcessKey exactly; the
            // zeroing itself is done ABOVE the match, where a typed-through gap typo can read the
            // same value. Reaching HERE on a space CELL means a SPACE was typed on it: fold is only
            // toLowerCase so nothing but ' ' folds onto ' ', a freestyle cell refuses space
            // outright, and under mashing the press was already rewritten to the cell's expected
            // char. The spacebar is deliberately outside the timing challenge (the word gap is
            // where a typist's hands reset, not a note to hit), so the press is judged as though it
            // landed dead on target: top tier whatever the clock said, and never one of the two
            // zero-point tiers, which since backlog 199 cost accuracy rather than the run.
            //
            // A ZEROED DELTA rather than a forced judgement type, again as in the C#, so every
            // reader agrees with the judgement: classify(0) is 'Great', and the inert retype
            // re-classifies the stored firstCorrectDelta consistently.
            // History: docs/typebeat-core-history.md, "The sync tint and live sync readout".
            //
            // Scoped to the CELL and not to the KEY: a space that lands on a lyric character never
            // reaches here, it was consumed by the word skip (which has already given the abandoned
            // cells up and taken its one break before the caret ever reached the gap), typed through
            // as an ordinary typo above (backlog 184, the arm with no word to skip) or rejected there
            // on a freestyle slot. And an untimed space is not a
            // free one: a space cell nobody pressed still seals a miss like any other untyped
            // character (sealLine). Since backlog 181 the cell has a fourth way of being resolved,
            // a wrong LETTER typed into it, and that one takes the zeroed delta too, for the reason
            // the hoisted block above states: a typo is priced at what a correct press on the same
            // cell would have been priced at.
            //
            // The C# half of backlog 148 has one more clause with nothing to mirror here: it keeps
            // the exempt space OUT of its SyncTimeline, the offset-analysis series a play's results
            // screen is drawn from. This core has no such series (nothing here records per-press
            // samples), so there is no omission to fix, only an asymmetry to expect. The zeroing
            // stays: it is engine data, kept for WireCompat parity with the C#, not for a display
            // consumer here.
            // History: docs/typebeat-core-history.md, "The sync tint and live sync readout".

            // COMBO RESTORE (backlog 140, widened to the word skip by backlog 167), before anything
            // about this press is judged: if this is the cell a wrong keypress spoiled or a skip
            // abandoned, the run resumes at the streak that break cost plus everything earned since.
            // Placed here so the press below is
            // scored at the RESUMED streak, which is what makes fixing a typo worth score rather
            // than only accuracy: the combo portion weights every judgement by the combo AFTER it.
            // Not scoring-inert even for an inert retype, because the streak belongs to the FIX and
            // not to the cell's judgement.
            this.resumeStreakIfThisRedeemsTheBreak(this.caretIndex);

            const w = windowsFor(cell);
            const inertRetype = cell.firstCorrectDelta !== null;
            let type;

            if (inertRetype) {
                // Combo-inert on BOTH accounts: the engine leaves its own combo alone, no second
                // result reaches the processor (the cell drawable already carries one,
                // DrawableTypeBeatCharObject.ApplyEngineResult: `if (Judged) return;`), and HP does
                // not move. The branch below relies on the SAME guard rather than on this
                // condition, because with allowWrongInput a cell can carry a result without ever
                // having been correct.
                //
                // The award STANDING on the cell, re-derived: the stored delta through the same
                // ladder, and through the same backlog 210 cap, because the flag it reads is set
                // only before a cell is first judged and never cleared. The C# reads its stored
                // JudgedTier instead; the two agree because an upgrade below writes the delta it was
                // awarded on back into firstCorrectDelta, through this same cap.
                type = awardedTier(classify(cell.firstCorrectDelta, w), cell.heldWrongBeforeJudged);

                // BEST RETYPE JUDGEMENT (PR 43, extended CONFIG bit 13, TypingEngine.Keys.cs's
                // inert-retype arm and improveRetypedAward). A retype that earns a STRICTLY better
                // tier than the standing award replaces it: the tier counts move from the old tier
                // to the new, firstCorrectDelta takes the retype's delta, and the submitted account
                // amends the cell's existing result in place (ScoreProcessorMirror.updateResult,
                // TypeBeatScoreProcessor.UpdateCellResult). Still no combo increment and no HP:
                // the cell was already counted once, and the farming the inert rule exists to stop
                // is the second count, not a better grade. The candidate goes through the SAME
                // correction cap, so a corrected cell can climb from Meh to Ok but never to Great.
                // The live stack sets the bit and the browser only plays live, so it is
                // unconditional here. No rush-cap clause: the C# candidate's cap term is gated on
                // !InputEra2, which is always false live.
                type = this.bestRetypeTier(cell, type, delta, w);

                cell.state = 'correct';
                cell.typedChar = c;
                cell.judgedDelta = cell.firstCorrectDelta;
                cell.judgeType = type;
            } else {
                this.totalKeypresses++;
                this.correctKeypresses++;
                // The clock classifies the press, then backlog 210's CORRECTION CAP decides what it
                // is awarded: a cell that held a wrong character before it was ever judged resolves
                // at min(that tier, 'Ok'), so a corrected cell can never be worth what a clean one
                // is. Applied here, above everything the tier decides, so `counts`, the announced
                // onCharJudged and the cell's osu result all follow the one decision and cannot
                // say different things. The delta itself is untouched: it stays the press the
                // player actually made, for accuracy and WireCompat parity with the C# engine.
                // History: docs/typebeat-core-history.md, "The sync tint and live sync readout".
                type = awardedTier(classify(delta, w), cell.heldWrongBeforeJudged);

                // NO RUSH CAP (PR 3): the C# line is `FletcherEnabled && !RushCapExempt && !InputEra2
                // && rushesPastCap(...)`, which is always false in the second input era, so nothing
                // stands between the clock's tier and the judgement however far past the playhead
                // the caret is. Backlog 347's Meh award and the pre-347 combo break are both stored
                // eras only.

                // An OFF-TIME press (Premature/Lagging: the right character, outside the outermost
                // Meh window) earns no tier, and since backlog 199 that is the whole of what
                // it costs the tier ladder. It is a HIT: it extends the combo like any other
                // accepted character, raises no onComboBroken, and leaves an outstanding restorable
                // claim alone, because only a BREAK discards one, so fumbling the beat between a
                // typo and its fix no longer costs the fix its restore. Its cell resolves as an osu
                // 'meh' below (toHitResult), which is what makes ACCURACY the punishment and what
                // lets the submitted combo follow the engine's with nothing mirrored by hand.
                //
                // So the increment is unconditional on THAT axis, exactly as the C# arm is with its
                // off-time era arm collapsed: OffTimeRule.BreaksCombo is the pre-199 era, which a
                // browser play (live only) can never be in.
                //
                // A space can never reach the off-time tiers at all: an untimed space is judged on a
                // zeroed delta and always takes the top tier (see the block above).
                //
                // Nothing stands between the press and the increment: there is no rush cap.
                // History: docs/typebeat-core-history.md, "The rush cap".
                //
                // The cell the press LANDED on, which is where this increment is recorded in the
                // ledger (creditCombo): the caret has not moved yet (it rolls on below), and a word
                // skip has already re-pointed it at the gap the space is judged on.
                this.creditCombo(this.caretIndex);

                // The one press that can credit combo it also broke (backlog 243): the space that
                // skipped the word, now being judged on the gap the skip parked the caret on. The
                // combo is real and stands, but it belongs to the break, so the claim remembers it
                // and the next break has to beat it to take the claim away.
                if (skipLeftAClaimOutstanding) this.creditTheClaimsOwnPress();

                cell.state = 'correct';
                cell.typedChar = c;
                cell.judgedDelta = delta;
                cell.firstCorrectDelta = delta;
                cell.judgeType = type;
                this.counts[type] = (this.counts[type] || 0) + 1;
                // The cell's one-and-only osu result, applied the moment it is judged: this is
                // TypeBeatPlayfield.applyCharJudgement -> ApplyCharJudgement -> ApplyResult. Great/Ok/Meh
                // increase the submitted combo, and since backlog 199 so do Premature/Lagging
                // (they map to Meh, and the engine has just extended its own run on the same
                // press), and the combo portion is weighted by the combo as it stands right here.
                // Guarded rather than unconditional, because the guard is the mirror of
                // ApplyEngineResult's `if (Judged) return;` and not of the inert-retype rule. A cell
                // typed WRONG and then backspaced comes back through here with firstCorrectDelta
                // still null AND with no result yet (backlog 109 defers it), so this call is where
                // the fix is finally paid for: the cell earns its real Great/Ok/Meh, which is the
                // whole point. The guard still matters for the cells the seal or a word skip missed
                // first, which must not be re-judged.
                this.applyCellResult(cell, toHitResult(type));
            }

            const judgedIndex = this.caretIndex;
            this.caretIndex++;
            this.autoSkipForward();

            // TypeBeatComboFeed.OnCharJudged, the flexible-caret arm. It existed for the RUSH CAP's
            // combo break on a press still judged Great/Ok/Meh, which the hit result alone (it
            // INCREMENTS osu's combo) could not carry. Since backlog 347 the live rule broke nothing
            // there (the over-cap press was a Meh that credited combo), and since PR 3 there is no
            // cap at all, so on a scoring press the engine's combo is never zero here and this
            // carries nothing; the C# keeps the same seam for its pre-347 era. Kept rather than deleted because it is written against "the combo
            // this press left behind" (the C# judgement.ComboAfter), which covers the inert-retype
            // branch too, and the C# announces that through the very same raise.
            //
            // Under a pinned caret this is inert, which is why the C# gates it on the flag: there
            // every combo-zero judgement either maps to a Miss (which breaks osu's combo itself) or
            // is a WrongChar, whose break the mistype path has already carried.
            if (this.fletcherEnabled && this.combo === 0) this.processor.breakCombo();

            if (this.onCharJudged) this.onCharJudged(judgedIndex, type);
            this.rollForwardIfFinishedEarly(time);
            return true;
        }

        // TypingEngine.stepBackIntoLine. HAND BACK INTO A LINE the player was moved off: the undo for
        // a mid-line Enter that gave the rest of the line up (processEnter), and just as much the way
        // back to a line the SONG has handed them on from. The caret lands at the LAST CHARACTER THEY
        // ACTUALLY TYPED, just after the last cell they put something into, and everything that was
        // given up is handed back LIVE rather than left behind the caret to be missed at the seal.
        //
        // THE END OF THE LINE IS THE WRONG PLACE FOR IT: parked past the last cell the line reads
        // COMPLETE (isLineComplete), keypresses there are inert, the characters the player came back
        // for are unreachable, and the misses the return was meant to erase are the very ones it
        // guarantees. On the frontier they are all in front of the caret again, where typing them is
        // what erases them, the same "come back and type it" account the word skip's reclaim keeps.
        //
        // WHAT THE WALK BACK STOPS ON: a cell the player TYPED (correct or wrong) and a cell already
        // resolved as 'missed' both end it, so the caret lands after the last thing the line has an
        // answer for. The C#'s note about a Missed cell in that tail is about a stored run re-derived
        // under the pre-167 immediate-miss era, which the browser can never be in; the arm is
        // mirrored anyway, because it is the same walk and a state-keyed rule is not an era rule.
        //
        // The line's own abandonment goes with the skip (lineAbandoned): the flag exists to hold the
        // line open, past its deadline, for the misses the player walked away from, and there is
        // nothing left to hold it open for once they have walked back. An ABANDONED word caught in
        // the tail is re-opened the way processBackspace's own walk re-opens one, and the C# raises
        // AbandonReclaimed for it, which carries the HEALTH refund of what the skip drained (landed
        // on the account below, after the caret has moved, where the C# raises it). Its combo still
        // comes back at the retype, which is what the claim left in place is for.
        stepBackIntoLine(index) {
            const cells = this.lines[index].cells;

            let frontier = 0;

            for (let i = cells.length - 1; i >= 0; i--) {
                const state = cells[i].state;

                if (state === 'correct' || state === 'wrong' || state === 'missed') {
                    frontier = i + 1;
                    break;
                }
            }

            let reclaimed = 0;

            for (let i = frontier; i < cells.length; i++) {
                if (cells[i].state === 'abandoned') reclaimed++;

                if (cells[i].state === 'abandoned' || cells[i].state === 'autoskip') {
                    cells[i].state = 'untyped';
                    cells[i].judgeType = null;
                }
            }

            this.lineAbandoned[index] = false;

            this.activeLineIndex = index;
            this.caretIndex = frontier;
            this.autoSkipForward();

            // AbandonReclaimed -> TypeBeatHealthFeed -> RefundAbandonDrain.
            this.healthAccount.refundDeferredDrain(reclaimed);
        }

        // TypingEngine.ProcessBackspace. Erase the most recent typed cell within the active line,
        // stepping back transparently over auto-skipped punctuation (which is un-skipped so retyping
        // re-marks it) and, since backlog 167, over the ABANDONED cells of a skipped word (which go
        // back to 'untyped' for the same reason: retyping them re-earns them). Returns false when
        // there was nothing to do. The erased keypress stays in the accuracy counts.
        //
        // Since PR 3 (the second input era, TypingEngine.InputEra2) a word skip and the space it
        // typed on the following gap are undone TOGETHER by one press (tryUndoWordSkip): the
        // abandoned cells are re-opened and the caret returns to the FIRST of them, leaving every
        // correctly typed character of the word intact, and the same holds at the end of a line,
        // where a skipped final word has no following gap. The C# keeps the era before it (one
        // press takes the typed gap, the next steps transparently over the abandoned run onto the
        // last character actually typed and erases it) for the stored runs it re-derives; the
        // browser plays live only, so it takes the new rule unconditionally. The transparent walk
        // below still runs for everything the undo does not claim.
        //
        // The one case that does not erase BEHIND the caret is a typo the caret is parked ON, which
        // only the word-gap park can produce (backlog 184): that cell is cleared in place and the
        // caret does not move, because the gap it sits on is still owed its space.
        //
        // Scan first, mutate after, exactly as the C# does, because the two have to be told apart
        // before anything moves: a press with nothing typed behind it did SOMETHING if it reclaimed
        // a word, and nothing at all if it did not.
        // TypingEngine.CaretOnParkedTypo (PR 2): true when the caret sits on a PARKED typo, the one
        // cell state a backspace clears IN PLACE (see processBackspace below), reporting a mutation
        // without moving the caret. The player's erase runs (eraseBackTo in typebeat-player.js)
        // read it so an in-place clear is not mistaken for the end of a selection: a retype
        // selection opened over a word skip ends on exactly such a cell whenever the player has
        // since typed into the gap, and stopping there left the word it was opened for standing.
        get caretOnParkedTypo() {
            if (this.finished || this.activeLineIndex < 0) return false;

            const cells = this.lines[this.activeLineIndex].cells;

            return this.caretIndex < cells.length && cells[this.caretIndex].state === 'wrong';
        }

        processBackspace() {
            if (this.finished || this.activeLineIndex < 0) return false;

            // THE HEAD OF A LINE THE PLAYER ARRIVED ON: a backspace there steps BACK UP to the line
            // it came from. That is the way back to a line the SONG has just handed them on from, so
            // a typo noticed a beat too late is still the player's to fix, and equally the undo for a
            // mid-line Enter that gave the rest of a line up. It works for exactly as long as that
            // line is still theirs, whoever moved them: until the engine TAKES it, which is the seal
            // (sealPermitted, the instant the push warning's red bar has counted down to) and not the
            // vocals running out or the next cue arriving. Once the line is sealed the press is
            // inert, just as it is on any other line's head.
            //
            // AND IT LANDS ON WHAT THE PLAYER LAST TYPED rather than at the end of the line (see
            // stepBackIntoLine): the line it comes back to is one the player may have given the rest
            // of up to a mid-line Enter, and the characters that press gave up have to be in front of
            // the caret again for coming back to mean anything.
            //
            // NO ERA BIT. The C# gates it on FletcherEnabled alone, which the browser is permanently
            // in, so this is live rule and reachable on an ordinary /play run: a flexible caret that
            // finished a line early, or was moved on by the line-start snap, is one backspace from
            // the line behind it.
            if (this.caretIndex === 0 && this.activeLineIndex > 0 && this.fletcherEnabled) {
                const previous = this.activeLineIndex - 1;

                if (previous >= this.nextSealIndex) {
                    this.stepBackIntoLine(previous);
                    return true;
                }

                return false;
            }

            const cells = this.lines[this.activeLineIndex].cells;

            // A typo the caret is PARKED ON (backlog 184): cleared where it sits, not erased from
            // behind. The gap is still owed its space, so the caret has no business retreating into
            // the perfectly good word in front of it, and the character the player wants back is the
            // one they are looking at. One press, one cell, caret unmoved.
            //
            // Keyed on the STATE rather than on an era flag, exactly as the C# keys it: only the park
            // ever leaves the caret sitting on a wrong cell, because everywhere else resolving a cell
            // is how the caret got past it. The C# raises TypoErased here, which carries the HEALTH
            // refund of the drain the typo took (TypeBeatHealthFeed -> RefundTypoDrain): ONE drain
            // back, however many letters the park took, since the C# drained on each of them.
            if (this.caretIndex < cells.length && cells[this.caretIndex].state === 'wrong') {
                const parked = cells[this.caretIndex];

                parked.state = 'untyped';
                parked.typedChar = null;
                parked.judgedDelta = null;
                parked.judgeType = null;
                // firstCorrectDelta intentionally retained, as on the erase below.
                this.healthAccount.refundDeferredDrain(1);
                return true;
            }

            // THE SECOND INPUT ERA's undo of a word skip (TypingEngine.ProcessBackspace:
            // `if (InputEra2 && tryUndoWordSkip(cells)) return true;`), in the same place: after the
            // parked typo, before the transparent walk.
            if (this.tryUndoWordSkip(cells)) return true;

            let target = this.caretIndex - 1;
            while (target >= 0 && (cells[target].state === 'autoskip' || cells[target].state === 'abandoned')) target--;

            let reclaimed = 0;
            for (let i = target + 1; i < this.caretIndex; i++) {
                if (cells[i].state === 'abandoned') reclaimed++;
            }

            if (target < 0 && reclaimed === 0) return false;

            // Un-skip the punctuation and re-open the abandoned cells we stepped back over. The C#
            // announces the reclaimed ones on AbandonReclaimed, which carries HEALTH alone (the
            // refund of what the skip drained, landed on the account below at the C#'s raise sites).
            for (let i = target + 1; i < this.caretIndex; i++) {
                if (cells[i].state === 'autoskip' || cells[i].state === 'abandoned') {
                    cells[i].state = 'untyped';
                    cells[i].judgeType = null;
                }
            }

            if (target < 0) {
                // Nothing typed is left behind the caret. Ordinarily that is "nothing to erase", but
                // a word skipped at the very start of a line leaves phantom cells and no keypress
                // before them, and refusing here would make that one word the only unreclaimable one
                // on the map. The reclaim IS the state change, so the press did something: put the
                // caret back at the head of the word it just re-opened.
                this.caretIndex = 0;
                this.autoSkipForward();
                this.healthAccount.refundDeferredDrain(reclaimed);
                return true;
            }

            const cell = cells[target];

            // Read BEFORE the cell is cleared, as the C# reads it: a wrong character is being taken
            // back, which is the erase TypoErased (and so the health refund) is raised for.
            const erasedTypo = cell.state === 'wrong';

            cell.state = 'untyped';
            cell.typedChar = null;
            cell.judgedDelta = null;
            cell.judgeType = null;
            // firstCorrectDelta intentionally retained (inert-retype guard).
            this.caretIndex = target;

            // The C#'s raise order, reclaim first and then the typo, which the clamp at full makes
            // observable in the last bit of the bar.
            this.healthAccount.refundDeferredDrain(reclaimed);
            if (erasedTypo) this.healthAccount.refundDeferredDrain(1);
            return true;
        }

        // TypingEngine.CanUndoWordSkip (PR 3): whether one backspace can undo the word skip
        // adjacent to the caret. The C# answers false outside InputEra2; the browser is always in
        // it.
        get canUndoWordSkip() {
            return this.adjacentSkippedWord() !== null;
        }

        // TypingEngine.adjacentSkippedWord (PR 3). The skipped word a backspace here would undo, as
        // { firstAbandoned, wordEnd, gapIndex }, or null. The word is the one ENDING at the caret:
        // the caret sits ON the gap that closes it (wordEnd = caret, no gap to erase), or just PAST
        // that gap (the gap is behind the caret and is the one the skip typed), or at the end of the
        // line (a skipped final word, no gap). It counts only if a cell of it is still abandoned,
        // and firstAbandoned is the earliest such cell.
        adjacentSkippedWord() {
            if (this.finished || this.activeLineIndex < 0) return null;

            const cells = this.lines[this.activeLineIndex].cells;
            let wordEnd;
            let gapIndex = -1;

            if (this.caretIndex < cells.length && isWordGap(cells[this.caretIndex])) wordEnd = this.caretIndex;
            else if (this.caretIndex > 0 && isWordGap(cells[this.caretIndex - 1])) wordEnd = gapIndex = this.caretIndex - 1;
            else if (this.caretIndex === cells.length) wordEnd = this.caretIndex;
            else return null;

            let wordStart = wordEnd;

            while (wordStart > 0 && !isWordGap(cells[wordStart - 1])) wordStart--;

            for (let i = wordStart; i < wordEnd; i++) {
                if (cells[i].state === 'abandoned') return { firstAbandoned: i, wordEnd: wordEnd, gapIndex: gapIndex };
            }

            return null;
        }

        // TypingEngine.tryUndoWordSkip (PR 3). Re-open every abandoned (and auto-skipped) cell of the
        // adjacent skipped word from its first abandoned cell on, erase the space the skip typed onto
        // the following gap when that gap is CORRECT (a skip can also step over an already spoiled
        // gap, whose typo this space did not type and which keeps it for its own backspace), and park
        // the caret on the first abandoned cell. The C# raises AbandonReclaimed with the re-opened
        // cells, which carries HEALTH alone (the refund of what the skip drained), landed here as the
        // old walk lands it. firstCorrectDelta is left on the gap, as on every erase, so a retype of
        // it is inert.
        tryUndoWordSkip(cells) {
            const skip = this.adjacentSkippedWord();

            if (skip === null) return false;

            let reclaimed = 0;

            for (let i = skip.firstAbandoned; i < skip.wordEnd; i++) {
                if (cells[i].state === 'abandoned') {
                    cells[i].state = 'untyped';
                    cells[i].judgeType = null;
                    reclaimed++;
                } else if (cells[i].state === 'autoskip') {
                    cells[i].state = 'untyped';
                    cells[i].judgeType = null;
                }
            }

            if (skip.gapIndex >= 0) {
                const gap = cells[skip.gapIndex];

                if (gap.state === 'correct') {
                    gap.state = 'untyped';
                    gap.typedChar = null;
                    gap.judgedDelta = null;
                    gap.judgeType = null;
                }
            }

            this.caretIndex = skip.firstAbandoned;
            this.healthAccount.refundDeferredDrain(reclaimed);
            return true;
        }

        // TypingEngine.WordBackspaceTarget. Where a CTRL+BACKSPACE (backlog 182, the typing-site
        // "erase the previous word" gesture) should leave the caret: a PURE QUERY, mutating nothing.
        // The caller composes the gesture out of ordinary processBackspace calls
        // (`while (caretIndex > target && processBackspace());`), which is what keeps the whole
        // gesture inside the vocabulary the engine already has: the desktop records the same run of
        // backspace frames a player holding the plain key down would have produced, and this mirror
        // needs no new call at all.
        //
        // The rule is the one every typing site implements. Walk back over the word GAPS immediately
        // behind the caret, then over the word behind them, and stop at that word's first cell. So a
        // caret sitting mid-word erases back to the start of the word it is inside, and a caret
        // sitting at the head of a word (the gap immediately behind it) erases that gap AND the whole
        // word before it. At the head of the line the answer is caretIndex itself, which makes the
        // composed gesture a no-op: it never calls the engine.
        //
        // The target is a FLOOR, not a promise: one processBackspace steps transparently back over
        // auto-skipped cells (and, in the era before PR 3, over abandoned ones too), so a press can
        // land the caret further back than this, exactly as a plain backspace there would. Since
        // PR 3 undoing a word skip stops at the first abandoned cell, keeping the typed cells before
        // it.
        //
        // Answers caretIndex unchanged when no line is active or the run has finished, so the caller
        // needs no second guard.
        get wordBackspaceTarget() {
            if (this.finished || this.activeLineIndex < 0) return this.caretIndex;

            const cells = this.lines[this.activeLineIndex].cells;
            let target = Math.min(this.caretIndex, cells.length);

            // The gaps directly behind the caret (normally one; a map never authors two in a row,
            // and the loop costs nothing for being written to survive one that did).
            while (target > 0 && isWordGap(cells[target - 1])) target--;

            // Then the word they follow, back to the gap that opens it or to the line's head.
            while (target > 0 && !isWordGap(cells[target - 1])) target--;

            return target;
        }

        // TypingEngine.RetypeSelectionAnchor. Where a CTRL+A (backlog 182, "select back to the
        // mistake I have to retype") should put the start of its selection: the first cell of the run
        // holding the EARLIEST unfixed mistake behind the caret, or -1 when there is none at all (the
        // gesture is then a no-op). The selection itself is the half-open range [this, caretIndex),
        // and it is pure UI state: nothing in the engine knows it exists. Consuming it is composed,
        // like the gesture above, out of ordinary processBackspace calls back to this index plus at
        // most one processKey (see typebeat-player.js).
        //
        // An unfixed mistake is a cell in the 'wrong' state OR the 'abandoned' state (backlog 244): a
        // wrong character typed through and not yet backspaced away, or a cell a word skip gave up on
        // and nobody has come back for. Both are scanned in one pass, taking the EARLIEST cell in
        // EITHER state, so the selection covers every unfixed mistake behind the caret rather than
        // only the most recent (backlog 184) and a still-open typo wins over a later abandoned word,
        // or the reverse, purely on which one sits earlier on the line. The gesture is "fix what's
        // behind me", and it is one keystroke: offering the shortest retype would leave a player with
        // two spoiled words pressing it, retyping, pressing it again, and having no way to see from
        // the caret how many rounds are left. Retyping the cells in between costs nothing, since a
        // correct cell re-typed earns no second count (since PR 43 it can only raise its own grade).
        //
        // WHICH run the mistake's cell opens: for an ordinary lyric character (a typo or an abandoned
        // cell alike) it is its WORD's first cell, walking back to the gap before it. Since PR 3 (the
        // second input era, TypingEngine.InputEra2) three refinements ride on that, all live here
        // because the browser is always in the era: a WORD GAP holding a typo (possible since
        // backlog 181) selects from the beginning of the word BEFORE the gap, so the retype includes
        // its space; a word given up whole anchors on its own head, with no backlog 260 widening onto
        // the gap in front of it, because the era's backspace (tryUndoWordSkip) stops there; and
        // leading punctuation the word auto-skips is stepped over, since the first TYPEABLE cell is
        // the earliest place a backspace can stop without crossing the gap before the word. The C#
        // keeps the old anchor (the gap itself, and the widening) as legacyRetypeSelectionAnchor for
        // an engine without the era, so the anchor always agrees with the backspace it composes.
        //
        // The answer is never equal to caretIndex when it is non-negative: the scan is over
        // [0, caretIndex), so a selection always covers at least one cell. The one typo that can sit
        // AT the caret, the gap a park is holding (backlog 184), is deliberately outside that range:
        // it needs no selection, being one backspace away under the same rule that parked it.
        get retypeSelectionAnchor() {
            if (this.finished || this.activeLineIndex < 0) return -1;

            const cells = this.lines[this.activeLineIndex].cells;
            const limit = Math.min(this.caretIndex, cells.length);
            let mistake = -1;

            for (let i = 0; i < limit; i++) {
                // The two unfixed states, taken in one pass so the earliest of EITHER kind wins
                // (backlog 244 added the abandoned one).
                if (cells[i].state === 'wrong' || cells[i].state === 'abandoned') { mistake = i; break; }
            }

            if (mistake < 0) return -1;

            let anchor = mistake;

            // A typo on a space belongs to the word immediately before it for retyping. Step over
            // adjacent gaps first so even unusual repeated spaces find that word.
            if (isWordGap(cells[mistake])) {
                while (anchor > 0 && isWordGap(cells[anchor - 1])) anchor--;
            }

            while (anchor > 0 && !isWordGap(cells[anchor - 1])) anchor--;

            // A word can begin with punctuation that typing auto-skips. The first typeable cell is
            // the earliest place a backspace can stop without crossing the prior gap.
            while (anchor < mistake && !cells[anchor].typeable) anchor++;

            return anchor;
        }

        // The character to type right now (or null).
        get caretCell() {
            if (this.activeLineIndex < 0) return null;
            const cells = this.lines[this.activeLineIndex].cells;
            return this.caretIndex < cells.length ? cells[this.caretIndex] : null;
        }
    }

    Object.assign(parts, {
        FLETCHER_DRAG_GRACE_MS, FIRST_LINE_LEAD_MS, TypingEngine
    });
    parts.loaded.push('typing-engine.js');
})(window.TypeBeatCoreParts = window.TypeBeatCoreParts || { loaded: [] });
