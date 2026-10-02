// Node harness for the browser's keystroke-to-characters translation (backlogs 309, 383):
// TypeBeatCore.display.foldTyped and keyToChars in typebeat-player.js. It carries no fixtures of its
// own: the caller (WireCompat's KeyToCharParityTest) generates the rows, writes them to a JSON file
// and passes its path. The file is { fold: [{ units, punctuation }], keys: [{ key, code }] }, where
// `units` is the committed string as UTF-16 code units (so a lone surrogate half survives the trip);
// the output is { fold: [string], keys: [string] }, each the presses typed, '' for none.
//
// Usage: node KeyToCharHarness.cjs <absolute path to typebeat-core.js> <rows.json>

'use strict';

const fs = require('fs');
const nodePath = require('path');

const corePath = process.argv[2];
const rowsPath = process.argv[3];
if (!corePath || !rowsPath) {
    process.stderr.write('usage: KeyToCharHarness.cjs <typebeat-core.js> <rows.json>\n');
    process.exit(2);
}

global.window = {};
require(corePath);
require(nodePath.join(nodePath.dirname(corePath), 'typebeat-player.js'));

const D = global.window.TypeBeatCore.display;
const rows = JSON.parse(fs.readFileSync(rowsPath, 'utf8'));

const out = {
    fold: rows.fold.map(r => D.foldTyped(String.fromCharCode(...r.units), !!r.punctuation)),
    keys: rows.keys.map(r => D.keyToChars({ key: r.key, code: r.code }, false))
};

process.stdout.write(JSON.stringify(out));
