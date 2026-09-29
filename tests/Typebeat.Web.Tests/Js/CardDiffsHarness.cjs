// Node harness for the beatmapset card's difficulty stack (card-diffs.js, backlog 327). The script
// publishes window.TypeBeatCardDiffs before it wires the document, so with no document defined it
// stops there and the harness drives the published functions against a minimal fake card: a stack
// button (its stars, rating, colour and WPM spans) inside an article with a title link and a play
// rail. Emits, per scenario, the visible state after each click, as JSON for CardDiffsScriptTest.
//
// Usage: node CardDiffsHarness.cjs <absolute path to typebeat-core.js>

'use strict';

const nodePath = require('path');

const corePath = process.argv[2];
if (!corePath) {
    process.stderr.write('missing typebeat-core.js path\n');
    process.exit(2);
}

global.window = {};
require(nodePath.join(nodePath.dirname(corePath), 'card-diffs.js'));

const CD = global.window.TypeBeatCardDiffs;

function el(attrs) {
    const props = {};
    const node = {
        attrs: Object.assign({}, attrs || {}),
        textContent: '',
        style: {
            color: '',
            setProperty: function (name, value) { props[name] = value; },
            getPropertyValue: function (name) { return props[name]; }
        },
        getAttribute: function (name) { return name in this.attrs ? this.attrs[name] : null; },
        setAttribute: function (name, value) { this.attrs[name] = String(value); }
    };
    return node;
}

// diffs: [{ id, stars, wpm, colour, label }] as the server renders data-diffs; drawn = stars drawn.
function card(diffs, drawn) {
    const stars = [];
    for (let i = 0; i < drawn; i++) {
        const s = el();
        s.style.setProperty('--slot', String(i));
        s.style.color = diffs[i].colour;
        stars.push(s);
    }

    const colour = el();
    const rating = el();
    const wpm = el();
    const title = el({ href: '/beatmapsets/42' });
    const play = el({ href: '/play?set=42' });

    const article = {
        querySelector: function (sel) {
            if (sel === '.bset-card__title') return title;
            if (sel === '.bset-card__play') return play;
            return null;
        }
    };

    const button = el({ 'data-diffs': JSON.stringify(diffs), 'data-diff-index': '0' });
    button.querySelectorAll = function (sel) { return sel === '[data-diff-star]' ? stars : []; };
    button.querySelector = function (sel) {
        if (sel === '[data-diff-colour]') return colour;
        if (sel === '[data-diff-rating]') return rating;
        if (sel === '[data-diff-wpm]') return wpm;
        return null;
    };
    button.closest = function (sel) { return sel === '.bset-card' ? article : null; };

    return { button, stars, colour, rating, wpm, title, play };
}

function snapshot(c) {
    return {
        index: Number(c.button.getAttribute('data-diff-index')),
        label: c.button.getAttribute('aria-label'),
        rating: c.rating.textContent,
        colour: c.colour.style.color,
        wpm: c.wpm.textContent,
        slots: c.stars.map((s) => Number(s.style.getPropertyValue('--slot'))),
        starColours: c.stars.map((s) => s.style.color),
        titleHref: c.title.getAttribute('href'),
        playHref: c.play.getAttribute('href')
    };
}

function diff(id, stars, wpm) {
    return { id: id, stars: stars, wpm: wpm, colour: 'c' + id, label: 'L' + id };
}

function run(diffs, drawn, clicks) {
    const c = card(diffs, drawn);
    const states = [];
    for (let i = 0; i < clicks; i++) {
        CD.cycle(c.button);
        states.push(snapshot(c));
    }
    return states;
}

const three = [diff(11, '6.4', '150'), diff(12, '4.1', '195'), diff(13, '2.2', null)];
const six = [1, 2, 3, 4, 5, 6].map((n) => diff(20 + n, String(7 - n) + '.5', String(180 - 20 * n)));

const out = {
    three: run(three, 3, 4),
    six: run(six, 5, 7),
    rotation: {
        threeAt0: CD.rotation(3, 3, 0),
        sixAt1: CD.rotation(6, 5, 1),
        sixAt6: CD.rotation(6, 5, 6)
    },
    withDiff: {
        title: CD.withDiff('/beatmapsets/42', 9),
        titleBack: CD.withDiff('/beatmapsets/42?diff=9', null),
        play: CD.withDiff('/play?set=42', 9),
        playSwap: CD.withDiff('/play?set=42&diff=9', 10),
        playBack: CD.withDiff('/play?set=42&diff=9', null)
    }
};

process.stdout.write(JSON.stringify(out));
