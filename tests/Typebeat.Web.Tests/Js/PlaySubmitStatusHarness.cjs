// Node harness for what /play tells the player once /play/submit answers (play.js's
// submitStatus, backlog 321). play.js is an IIFE that publishes window.TypeBeatPlayPage before its
// stage guard, so a document whose getElementById answers null takes the guard's early return and
// nothing else runs. Emits { case: { html, cls } } as JSON for PlaySubmitStatusTest to assert.
//
// Usage: node PlaySubmitStatusHarness.cjs <absolute path to typebeat-core.js>

'use strict';

const nodePath = require('path');

const corePath = process.argv[2];
if (!corePath) {
    process.stderr.write('missing typebeat-core.js path\n');
    process.exit(2);
}

global.window = {};
global.document = { getElementById: function () { return null; } };
require(nodePath.join(nodePath.dirname(corePath), 'play.js'));

const PAGE = global.window.TypeBeatPlayPage;

const ranked = { status: 'ranked' };
const pending = { status: 'pending' };
const unranked = { status: 'unranked' };
const loved = { status: 'loved' };
const cleared = { passed: true };
const failed = { passed: false };

const out = {
    rankedNewBest: PAGE.submitStatus(
        { ranked: true, pp: 1234.6, pp_pending: false, board: 'ranked', position: 3, personal_best: true }, ranked, cleared),
    rankedNotBest: PAGE.submitStatus(
        { ranked: true, pp: 88.2, pp_pending: false, board: 'ranked', position: 3, personal_best: false }, ranked, cleared),
    rankedPricedZero: PAGE.submitStatus(
        { ranked: true, pp: 0, pp_pending: false, board: 'ranked', position: 9, personal_best: true }, ranked, cleared),
    rankedPending: PAGE.submitStatus(
        { ranked: true, pp: null, pp_pending: true, board: 'ranked', position: 1, personal_best: true }, ranked, cleared),
    pendingMap: PAGE.submitStatus(
        { ranked: false, pp: null, pp_pending: false, board: 'unranked', position: 2, personal_best: true }, pending, cleared),
    unrankedMapNotBest: PAGE.submitStatus(
        { ranked: false, pp: null, pp_pending: false, board: 'unranked', position: 4, personal_best: false }, unranked, cleared),
    failedRun: PAGE.submitStatus(
        { ranked: false, pp: null, pp_pending: false, board: null, position: null, personal_best: false }, ranked, failed),
    lovedChecksFailed: PAGE.submitStatus(
        { ranked: false, pp: null, pp_pending: false, board: null, position: null, personal_best: false }, loved, cleared),
    checksFailed: PAGE.submitStatus(
        { ranked: false, pp: null, pp_pending: false, board: null, position: null, personal_best: false }, ranked, cleared)
};

// Submission integrity (backlog 312): the token body, and what a refused token is said as.
out.tokenBodyFull = PAGE.tokenBody('7', 12, '0123456789abcdef', 'd41d8cd98f00b204e9800998ecf8427e');
out.tokenBodyBare = PAGE.tokenBody(7, 0, null, null);
out.refusedOutdated = PAGE.tokenFailureStatus('outdated client');
out.refusedStaleMap = PAGE.tokenFailureStatus('invalid or missing beatmap_hash');
out.refusedOther = PAGE.tokenFailureStatus(null);
out.playbackInvalid = PAGE.PLAYBACK_INVALID_STATUS;

process.stdout.write(JSON.stringify(out));
