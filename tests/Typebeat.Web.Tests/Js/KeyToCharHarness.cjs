// Node harness for the browser's keystroke-to-character translation (backlog 309):
// TypeBeatCore.display.keyToChar in typebeat-player.js. It carries no fixtures of its own: the
// caller (WireCompat's KeyToCharParityTest) generates the rows from the game's KeyCharMap, writes
// them to a JSON file and passes its path. Each row is { key, code, shift, dead }; the output is
// the matching array of answers, null where the key is dropped.
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

const out = rows.map(r => D.keyToChar({ key: r.key, code: r.code, shiftKey: !!r.shift }, !!r.dead));

process.stdout.write(JSON.stringify(out));
