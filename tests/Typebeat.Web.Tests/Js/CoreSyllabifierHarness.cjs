// Node harness for the browser syllabifier (backlog 179).
//
// typebeat-core.js carries a hand-written port of the game's Syllabifier, and since backlog 179 it
// is SCORING SURFACE: the groups it produces are the time spans a keypress on a grouped cell is
// judged against, so a split that lands one character off moves a real judgement and a browser
// /play score away from the identical desktop performance on the SAME leaderboard.
//
// The corpus below is the game's SyllabifierTest transcribed word for word, expectations included:
// its pinned splits are the byte-compat contract, so they are checked here against the shipped JS
// rather than described. The harness checks itself and reports every mismatch by name;
// SyllabifierParityTest.cs asserts the report is empty and that the corpus is still the whole list.
//
// It also emits every word it touched, so EngineFuzzLiveParityTest (the only project that compiles
// BOTH repos) can hold the same answers against the game's real Syllabifier and catch a drift the
// transcription would not: a rule that changed on the C# side without this file moving.
//
// Usage: node CoreSyllabifierHarness.cjs <absolute path to typebeat-core.js>

'use strict';

const corePath = process.argv[2];
if (!corePath) {
    process.stderr.write('missing typebeat-core.js path\n');
    process.exit(2);
}

global.window = {};
require(corePath);
const TB = global.window.TypeBeatCore;

const failures = [];

function fail(message) { failures.push(message); }

function sameNumbers(a, b) {
    if (a.length !== b.length) return false;
    for (let i = 0; i < a.length; i++) if (a[i] !== b[i]) return false;
    return true;
}

function expectSplits(word, expected, forced) {
    const actual = forced === undefined ? TB.splitPoints(word) : TB.splitPoints(word, forced);
    if (!sameNumbers(actual, expected)) {
        fail(`splitPoints(${JSON.stringify(word)}${forced === undefined ? '' : ', ' + forced}) = [${actual}], expected [${expected}]`);
    }
    return actual;
}

function expectCount(word, expected) {
    const actual = TB.countSyllables(word);
    if (actual !== expected) fail(`countSyllables(${JSON.stringify(word)}) = ${actual}, expected ${expected}`);
}

function expectGate(word, expected) {
    const actual = TB.isSyllabifiable(word);
    if (actual !== expected) fail(`isSyllabifiable(${JSON.stringify(word)}) = ${actual}, expected ${expected}`);
}

// ---------------------------------------------------------------------------------------------
// SyllabifierTest.OneSyllable: the bulk of any lyric stream. One group, no splits.
// ---------------------------------------------------------------------------------------------
const ONE_SYLLABLE = [
    'the', 'love', 'heart', 'night', 'world', 'girl',
    'friend', // "ie" stays one nucleus off the quiet/diet pattern
    'dream', 'time', 'life',
    'eyes', // y is a glide, the -es is silent after it
    'eye', // word-final silent e keeps "eye" at one nucleus
    'bye', 'make', 'gave',
    'fire', // "re" after a vowel is NOT the syllabic a|cre case
    'hour', 'know', 'strength',
    'loved', // -ed silent after v
    'called', // -ed silent after l
    'goes', // "oe" is one nucleus, so the -es rule never fires
    'times', // -es silent after m
    'clothes', // th before -es is not a sibilant (unlike wishes)
    'league', // gu + vowel: the u is silent, then the final e is too
    'one', 'once', 'here', 'where',
    'dont', 'cant', 'wont', 'aint', 'im', 'ive', 'its', 'youre', 'theyre', 'thats', // contractions, apostrophe stripped
    "don't", // and defensively, with it intact
    'hmm', // no vowel at all still counts as one group
    'a',
    '&', // freestyle marker: one sane group, no phantom syllables
    '24' // a digit run is one spoken chunk
];

for (const word of ONE_SYLLABLE) {
    expectCount(word, 1);
    expectSplits(word, []);
}

// ---------------------------------------------------------------------------------------------
// SyllabifierTest.PinnedSplits: multi-syllable words with their pinned split points.
// ---------------------------------------------------------------------------------------------
const PINNED = [
    ['probably', [3, 5]], // pro|ba|bly
    // boundary-placement basics
    ['open', [1]], // o|pen (V|CV)
    ['better', [3]], // bet|ter (doubled consonant)
    ['apron', [1]], // a|pron (pr onset cluster)
    ['table', [2]], // ta|ble (syllabic C+le)
    ['little', [3]], // lit|tle
    ['turtle', [3]], // tur|tle (C+le where "tl" is no onset)
    ['acre', [1]], // a|cre (syllabic C+re)
    ['people', [3]], // peo|ple (pinned exception)
    // hiatus
    ['quiet', [3]], // qui|et
    ['radio', [2, 4]], // ra|di|o
    ['video', [2, 4]], // vi|de|o
    ['usual', [1, 3]], // u|su|al
    ['diamond', [2, 3]], // di|a|mond
    // -tion / -sion / -cial family (the i fuses, no hiatus)
    ['nation', [2]], // na|tion
    ['vision', [2]], // vi|sion
    ['special', [3]], // spe|cial
    ['attention', [2, 5]], // at|ten|tion
    ['musician', [2, 4]], // mu|si|cian
    ['religion', [2, 4]], // re|li|gion
    // vowel + -ing is always split
    ['being', [2]], // be|ing
    ['going', [2]], // go|ing
    ['dying', [2]], // dy|ing
    ['saying', [3]], // say|ing
    ['carrying', [3, 5]], // car|ry|ing
    // common lyric vocabulary
    ['baby', [2]], // ba|by
    ['money', [2]], // mo|ney
    ['away', [1]], // a|way
    ['again', [1]], // a|gain
    ['alone', [1]], // a|lone
    ['inside', [2]], // in|side
    ['believe', [2]], // be|lieve
    ['another', [1, 3]], // a|no|ther (dictionary an|oth|er; maximal onset here)
    ['nothing', [2]], // no|thing (th digraph onsets; dictionary noth|ing)
    ['something', [4]], // some|thing (pinned exception, medial silent e)
    ['sometimes', [4]], // some|times
    ['someone', [4]], // some|one
    ['somebody', [4, 6]], // some|bo|dy
    ['somewhere', [4]], // some|where
    ['every', [2]], // ev|ery (pinned: sung/Merriam 2; formal ev|er|y is 3)
    ['everything', [2, 5]], // ev|ery|thing
    ['everyone', [2, 5]], // ev|ery|one
    ['everybody', [2, 5, 7]], // ev|ery|bo|dy
    ['everywhere', [2, 5]], // ev|ery|where
    ['remember', [2, 5]], // re|mem|ber
    ['yesterday', [2, 6]], // ye|ster|day (st kept as onset per cluster table)
    ['beautiful', [4, 6]], // beau|ti|ful ("eau" is one nucleus)
    ['together', [2, 4]], // to|ge|ther
    ['tonight', [2]], // to|night
    ['never', [2]], // ne|ver
    ['heaven', [3]], // hea|ven (dictionary heav|en; maximal onset here)
    ['ocean', [1]], // o|cean
    ['music', [2]], // mu|sic
    ['story', [3]], // sto|ry
    ['crazy', [3]], // cra|zy
    ['body', [2]], // bo|dy
    ['sorry', [3]], // sor|ry
    ['happy', [3]], // hap|py
    ['gonna', [3]], // gon|na
    ['wanna', [3]], // wan|na
    ['power', [2]], // po|wer
    ['flower', [3]], // flo|wer
    ['goodbye', [4]], // good|bye (final "ye" keeps its nucleus on the y)
    ['okay', [1]], // o|kay
    ['ready', [3]], // rea|dy
    ['maybe', [3]], // may|be (pinned exception)
    ['lovely', [4]], // love|ly (pinned exception, medial silent e)
    ['lonely', [4]], // lone|ly
    ['million', [3]], // mil|lion (pinned exception; li|on splits)
    ['create', [3]], // cre|ate (pinned exception)
    ['mountain', [4]], // moun|tain
    ['morning', [3]], // mor|ning
    ['burning', [3]], // bur|ning
    ['darling', [3]], // dar|ling
    ['falling', [3]], // fal|ling
    ['feeling', [3]], // fee|ling
    ['waiting', [3]], // wai|ting
    ['running', [3]], // run|ning
    ['dancing', [3]], // dan|cing
    ['wanted', [3]], // wan|ted (-ed after t is a nucleus)
    ['changes', [4]], // chan|ges (-es after g is a nucleus)
    ['wishes', [2]], // wi|shes (sh onsets; dictionary wish|es)
    ['memories', [2, 4]], // me|mo|ries ("ies" is one nucleus)
    ['singin', [3]] // sin|gin (dropped-g form does not trigger the -ing rule)
];

for (const [word, expected] of PINNED) {
    expectSplits(word, expected);
    expectCount(word, expected.length + 1);
}

// ---------------------------------------------------------------------------------------------
// SyllabifierTest: empty, case folding, digit runs, transparent punctuation.
// ---------------------------------------------------------------------------------------------
expectCount('', 0);
expectSplits('', []);
expectSplits('', [], 5);

// UppercaseIsFolded
expectSplits('PROBABLY', [3, 5]);
expectSplits('Quiet', [3]);
expectCount('People', 2); // the exception table hits case-insensitively

// DigitRunsAreTheirOwnSyllable
expectSplits('b2b', [1, 2]);
expectSplits('abc123def', [3, 6]);
expectCount('1000', 1);

// PunctuationNeverStartsASyllable: the apostrophe is transparent, "don't" is one group.
expectCount("don't", 1);
expectSplits("singin'", [3]);

// ---------------------------------------------------------------------------------------------
// SyllabifierTest: forcedCount reconciliation.
// ---------------------------------------------------------------------------------------------
// ForcedMatchingNaturalIsIdentity
expectSplits('probably', [3, 5], 3);
expectSplits('believe', [2], 2);

// ForcedBelowNaturalMergesWeakestBoundary.
// pro(3) ba(2) bly(3): both merges make a 5-char group, so the leftmost boundary goes.
expectSplits('probably', [5], 2);
// beau(4) ti(2) ful(3): merging ti into ful (5) beats merging beau+ti (6).
expectSplits('beautiful', [4], 2);
expectSplits('beautiful', [], 1);
expectSplits('probably', [], 0); // below 1 clamps to 1

// ForcedAboveNaturalAddsSplitsAtVowelConsonantEdges: the added split lands on the
// vowel-to-consonant edge, not the midpoint.
expectSplits('fire', [2], 2); // fi|re
expectSplits('make', [2], 2); // ma|ke

// OverForcingDegradesToOneCharGroups: a 3-letter word forced to 5 can only produce 3 groups.
expectSplits('cat', [1, 2], 5);

// ForcedInvariantsHoldAcrossTheCorpus
const FORCED_CORPUS = [
    'a', 'the', 'fire', 'cat', 'probably', 'beautiful', 'yesterday', 'everything',
    'people', 'goodbye', "don't", 'b2b', 'abc123def', '&', 'hmm', 'quiet', 'little'
];

const forcedProbes = [];

for (const word of FORCED_CORPUS) {
    for (let forced = 0; forced <= word.length + 2; forced++) {
        const splits = TB.splitPoints(word, forced);
        forcedProbes.push({ word: word, forced: forced, splits: splits });

        for (let i = 1; i < splits.length; i++) {
            if (splits[i] <= splits[i - 1]) fail(`${word} forced ${forced}: splits are not strictly ascending`);
        }

        for (const s of splits) {
            if (s < 1 || s > word.length - 1) fail(`${word} forced ${forced}: split ${s} is not interior`);
        }

        // group count is exactly min(max(forced, 1), length).
        const expectedGroups = Math.min(Math.max(forced, 1), word.length);
        if (splits.length + 1 !== expectedGroups) {
            fail(`${word} forced ${forced}: ${splits.length + 1} groups, expected ${expectedGroups}`);
        }
    }
}

// ---------------------------------------------------------------------------------------------
// SyllabifierTest: IsSyllabifiable, the stylised-word gate (backlog 178).
// ---------------------------------------------------------------------------------------------
const GATE_PASSES = [
    'little', 'good', 'hello', 'still', 'all', 'see', 'too',
    'ooh', // a held vowel spelled the ordinary way: two letters, not three
    'aah', 'la', 'na',
    'hmm', // deliberately passes: it is pinned above at one syllable, which is right
    'a', '&', '24',
    '1000', // three identical DIGITS are not three identical letters
    'b2b', "don't",
    'sh-h-h' // punctuation breaks the run rather than making one
];

const GATE_FAILS = [
    'wooooooords', 'heyyyyy', 'naaaah', 'ohhh', 'hmmmm', 'shhh', 'aaa', 'yeahhh', 'noooo',
    'WOOOO', // case is folded before the run is measured
    'Ohhh'
];

for (const word of GATE_PASSES) expectGate(word, true);
for (const word of GATE_FAILS) expectGate(word, false);

// NullAndEmptyAreNotSyllabifiable: matches countSyllables returning 0 for them.
expectGate('', false);
expectGate(null, false);
expectGate(undefined, false);

// TheGateStealsNothingFromThePinnedCorpus: read off the pinned corpus itself rather than retyped,
// so extending either list above extends this automatically.
const pinnedWords = ONE_SYLLABLE.concat(PINNED.map(entry => entry[0]));
if (pinnedWords.length <= 100) fail(`the pinned corpus is only ${pinnedWords.length} words`);
for (const word of pinnedWords) expectGate(word, true);

// TheGateDoesNotChangeWhatTheSyllabifierAnswers: the gate is a CALLER'S gate, not a mode, because a
// mapper's hand-authored subtimings have to be honoured on a stylised word too.
if (TB.countSyllables('wooooooords') < 1) fail('countSyllables still has to answer for a stylised word');
expectSplits('wooooooords', [1, 8], 3);

// NaturalInvariantsHoldForArbitraryJunk
const JUNK = ['&', "''", '!?', 'a&b', "'ere", "rock'n'roll", "y'all", '1,000', 'Mr.', 'co2'];

for (const word of JUNK) {
    const splits = TB.splitPoints(word);

    for (let i = 1; i < splits.length; i++) {
        if (splits[i] <= splits[i - 1]) fail(`${word}: junk splits are not strictly ascending`);
    }

    for (const s of splits) {
        if (s < 1 || s > word.length - 1) fail(`${word}: junk split ${s} is not interior`);
    }

    if (TB.countSyllables(word) < 1) fail(`${word}: junk must still be at least one group`);

    // None of this is stylised, so the gate leaves punctuation and digit junk grouped.
    expectGate(word, true);
}

// ---------------------------------------------------------------------------------------------
// Everything the harness touched, for the cross-repo check in EngineFuzzLiveParityTest.
// ---------------------------------------------------------------------------------------------
const probeWords = pinnedWords
    .concat(['PROBABLY', 'Quiet', 'People', 'b2b', 'abc123def', '1000', "don't", "singin'"])
    .concat(GATE_PASSES)
    .concat(GATE_FAILS)
    .concat(JUNK);

const seen = new Set();
const words = [];

for (const word of probeWords) {
    if (seen.has(word)) continue;
    seen.add(word);
    words.push({
        word: word,
        splits: TB.splitPoints(word),
        count: TB.countSyllables(word),
        syllabifiable: TB.isSyllabifiable(word)
    });
}

process.stdout.write(JSON.stringify({
    failures: failures,
    corpusSize: pinnedWords.length,
    words: words,
    forced: forcedProbes
}));
