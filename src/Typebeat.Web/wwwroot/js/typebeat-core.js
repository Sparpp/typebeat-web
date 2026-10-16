/*
 * typebeat-core.js: a faithful, dependency-free reimplementation of the
 * type!beat typing gameplay for the browser.
 *
 * This mirrors the desktop client's headless gameplay core
 * (typebeat.Game.Rulesets.TypeBeat: TypingEngine, TypingLine, Syllabifier,
 * Judgement, TypeBeatHealthProcessor) and the standardised scorer (osu ScoreProcessor +
 * TypeBeatScoreProcessor) closely enough that a play here produces the same
 * statistics / total_score the desktop would, so scores interoperate on the
 * same leaderboards. See the recon spec in the repo history for the exact rules
 * each block reproduces.
 *
 * Vanilla JS on purpose, no framework, no bundler (matches the rest of the site).
 * Exposes window.TypeBeatCore.
 */
(function (global) {
    'use strict';

    // The engine lives in core/, one file per C# counterpart, each a plain script that adds its names
    // to a shared parts object. This file assembles them into the one public object. The /play page
    // loads them, in this order and before this file, from its own script tags (PlayEndpoints
    // RevisionScripts, which the page renders and the build revision hashes); in Node, where the
    // fidelity harnesses require this file by path, it loads them itself.
    const CORE_PARTS = [
        'typeability.js', 'lyric-beatmap-decoder.js', 'syllabifier.js', 'typing-line.js',
        'judgement.js', 'score-and-health.js', 'typing-engine.js', 'freestyle-glyphs.js'
    ];

    if (typeof module === 'object' && module.exports && typeof require === 'function') {
        for (const part of CORE_PARTS) require('./core/' + part);
    }

    const parts = global.TypeBeatCoreParts;
    if (!parts || parts.loaded.join() !== CORE_PARTS.join()) {
        throw new Error('typebeat-core.js needs core/' + CORE_PARTS.join(', core/') + ' loaded first, in that order');
    }
    delete global.TypeBeatCoreParts;

    const {
        isTypeable, FREESTYLE_MARKER, isFreestyle, PUNCTUATION, WORD_BREAK, isPunctuation, isCell,
        stripBackingVocals, SPECIAL_LETTERS, spellSpecialLetters, normalize, defaultChar,
        projectDefault, toDefaultStream, FORMAT_MAGIC, FALLBACK_FORMAT_VERSION,
        LITERAL_BRACKETS_FROM_VERSION, parseFormatVersion, DEFAULT_FREESTYLE_COLOUR,
        parseFreestyleColour, DEFAULT_AUDIO_GAIN, MAX_AUDIO_GAIN, parseAudioGain, applyTrackGain,
        parseLyricOsu, CUE_LEAD_MS, isAuthoredValid, derivedSplits, splitsFor, cellCuts, segmentOf,
        usableRests, pausedWordOf, tokenCellTargets, isSyllabifiable, countSyllables, splitPoints,
        buildSyllables, STRETCH_RUN_LENGTH, buildCharTimedStretch, syllableIndexOf,
        isCharTimedStretch, syllableCharTarget, buildBeatmap, WINDOWS, FREESTYLE_WINDOW_SCALE,
        FREESTYLE_WINDOWS, windowsFor, classify, toHitResult, COMBO_PORTION_MAX,
        ACCURACY_PORTION_MAX, ACCURACY_PORTION_EXPONENT, WRONG_KEY_FAIL_STREAK,
        GREAT_HEALTH_INCREASE, OK_HEALTH_INCREASE, MEH_HEALTH_INCREASE, MISS_HEALTH_DRAIN,
        WRONG_KEY_HP_DRAIN, HEALTH_EPSILON, almostBigger, FLETCHER_DRAG_GRACE_MS,
        FIRST_LINE_LEAD_MS, TypingEngine, rankFromAccuracy, rankFromStatistics,
        uncachedMaxComboPortion, maxComboPortionOf, totalScoreOf, computeScore, SHIMMER_INTERVAL_MS,
        freestyleTick, freestyleGlyph
    } = parts;

    global.TypeBeatCore = {
        // low-level
        isTypeable, isFreestyle, isCell, isPunctuation, normalize, spellSpecialLetters,
        // Exported since backlog 255 took it out of normalize: it is the pre-v2 half of the format
        // version gate, and the fidelity harness holds it against the C# copy on its own.
        stripBackingVocals,
        defaultChar, projectDefault, toDefaultStream,
        parseLyricOsu, parseFormatVersion, buildBeatmap, syllableCharTarget,
        // The map's track gain (PR 1): the TryParse-mirroring read and the in-place scale and clamp
        // the player runs on the decoded audio, exported so the harnesses can pin both.
        parseAudioGain, applyTrackGain,
        // The map's freestyle colour (backlog 384): its strict read and the default it falls back to.
        parseFreestyleColour, DEFAULT_FREESTYLE_COLOUR,
        // The syllabifier and the group derivation, exported so the fidelity harnesses can hold
        // them against the game's own Syllabifier / TypingLine.Syllables word for word.
        isSyllabifiable, countSyllables, splitPoints, buildSyllables, syllableIndexOf,
        // The stretch narrowing (backlog 209), exported for the same reason: the harnesses hold it
        // against the game's own TypingLine.IsCharTimedStretch cell for cell.
        buildCharTimedStretch, isCharTimedStretch,
        // SyllableSegments (backlog 181): the shared authored-vs-derived split derivation, exported
        // for the same reason, so the game's own SyllableSegments can be held against it.
        isAuthoredValid, derivedSplits, splitsFor, cellCuts, segmentOf,
        TypingEngine, computeScore, totalScoreOf, rankFromAccuracy, rankFromStatistics,
        WINDOWS, FREESTYLE_WINDOWS, FREESTYLE_WINDOW_SCALE, windowsFor, classify, toHitResult,
        COMBO_PORTION_MAX, ACCURACY_PORTION_MAX, ACCURACY_PORTION_EXPONENT,
        freestyleTick, freestyleGlyph,
        constants: {
            CUE_LEAD_MS, WRONG_KEY_FAIL_STREAK, FREESTYLE_MARKER,
            SHIMMER_INTERVAL_MS, PUNCTUATION, SPECIAL_LETTERS, WORD_BREAK, STRETCH_RUN_LENGTH,
            // The format version gate (backlog 255), exported so the harnesses pin the same numbers
            // the C# decoder carries rather than transcribing them.
            FORMAT_MAGIC, FALLBACK_FORMAT_VERSION, LITERAL_BRACKETS_FROM_VERSION,
            DEFAULT_AUDIO_GAIN, MAX_AUDIO_GAIN,
            // The flexible caret's two tuning points (backlog 208), exported so the harnesses pin
            // the same numbers the game's own FletcherEngineTest does rather than transcribing them.
            FLETCHER_DRAG_GRACE_MS,
            // The first line's head start (PR 2), exported for the same reason.
            FIRST_LINE_LEAD_MS,
            // The HP pool (backlog 306), exported so the cross-repo parity test holds each one
            // against the TypeBeatHealthProcessor constant it mirrors, and the empty test's
            // epsilon against osu-framework's own Precision.DOUBLE_EPSILON.
            GREAT_HEALTH_INCREASE, OK_HEALTH_INCREASE, MEH_HEALTH_INCREASE, MISS_HEALTH_DRAIN,
            WRONG_KEY_HP_DRAIN, HEALTH_EPSILON
        },
        // Precision.AlmostBigger with its default difference, exported for the same pin.
        almostBigger,
        // PausedWord (PR 2): the authored-pause derivation, exported so the harnesses can hold it
        // against the game's own PausedWord and TypingLine.
        usableRests, pausedWordOf, tokenCellTargets,
        // computeScore's read-side memo (backlog 319), exported so a harness can hold the cached
        // answer against the loop it replaces.
        uncachedMaxComboPortion, maxComboPortionOf,
        // the renderer/high-level mount is attached in typebeat-player.js
    };
})(window);
