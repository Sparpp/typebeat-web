// Node harness that reports the PUBLIC SURFACE of the browser scoring core: every name
// window.TypeBeatCore exports, in enumeration order, with its typeof, the same for its `constants`
// object, and every global the load leaves on `window`.
//
// typebeat-core.js assembles the engine from the files under js/core/. Two routes load them and
// both must build the same object: Node, where this file is required by path and loads the parts
// itself, and the browser, where the /play page runs every script of PlayEndpoints.RevisionScripts
// in order before typebeat-core.js. The second route is replayed here in a fresh vm context with no
// `module` or `require`, exactly as a page sees them, when the C# side passes that list.
//
// Usage: node CoreSurfaceHarness.cjs <absolute path to typebeat-core.js> [/js/... script paths]

'use strict';

const fs = require('fs');
const nodePath = require('path');
const vm = require('vm');

const corePath = process.argv[2];
if (!corePath) {
    process.stderr.write('missing typebeat-core.js path\n');
    process.exit(2);
}

const scripts = process.argv.slice(3);
const webRoot = nodePath.dirname(nodePath.dirname(corePath));

function surface(win, ownKeys) {
    const core = win.TypeBeatCore;
    return {
        exports: Object.keys(core).map(k => k + ':' + typeof core[k]),
        constants: Object.keys(core.constants).map(k => k + ':' + typeof core.constants[k]),
        windowGlobals: ownKeys.filter(k => k !== 'window')
    };
}

global.window = {};
require(corePath);
const node = surface(global.window, Object.keys(global.window));

// The page route: run the scripts in the page's order up to and including typebeat-core.js (the
// player and the page glue after it need a document and add nothing to the core).
let browser = null;
if (scripts.length) {
    const context = vm.createContext({});
    context.window = context;
    const loaded = [];
    for (const script of scripts) {
        const file = nodePath.join(webRoot, ...script.split('/').filter(s => s));
        vm.runInContext(fs.readFileSync(file, 'utf8'), context, { filename: file });
        loaded.push(script);
        if (script === '/js/typebeat-core.js') break;
    }
    browser = Object.assign({ loaded }, surface(context, Object.keys(context)));
}

process.stdout.write(JSON.stringify({ node, browser }));
