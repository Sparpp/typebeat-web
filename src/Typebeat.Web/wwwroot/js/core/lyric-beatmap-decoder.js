/*
 * core/lyric-beatmap-decoder.js: reads the ".osu"-derived type!beat lyric text, its format
 * version and its [General] / [Metadata] keys (mirrors LyricBeatmapDecoder, FreestyleColourKey and
 * the AudioGain case of LegacyBeatmapDecoder).
 *
 * One of the files typebeat-core.js assembles into window.TypeBeatCore: see that file for the
 * order they load in and how they share names.
 */
(function (parts) {
    'use strict';

    // ---------------------------------------------------------------------------
    // Parse the ".osu"-derived type!beat lyric text.
    // ---------------------------------------------------------------------------

    // The magic first line, up to but not including the version number, and the two versions that
    // matter (mirrors LyricBeatmapDecoder.MAGIC / FALLBACK_FORMAT_VERSION /
    // LITERAL_BRACKETS_FROM_VERSION, and the server's BeatmapPackageParser copies of them).
    const FORMAT_MAGIC = 'type!beat file format v';

    // A file with no readable number is read as the ORIGINAL format, so an unversioned or
    // unparseable magic line falls back to the historical reading (brackets stripped) rather than
    // the current one: the direction that cannot invent lyric content for a map that never had it.
    const FALLBACK_FORMAT_VERSION = 1;

    // The first format version whose [Lyrics] brackets are LITERAL lyric marks rather than
    // backing-vocal spans to strip (backlog 255). Below it the parse strips, at or above it it
    // preserves. No v1 write path could store a literal bracket, so a '(' in a v1 file IS a backing
    // vocal by construction and the version decides with no ambiguity.
    const LITERAL_BRACKETS_FROM_VERSION = 2;

    // The version number off the magic line, or FALLBACK_FORMAT_VERSION when there is none to read.
    // Only the digits immediately after the prefix are taken, so anything else on the line is
    // ignored rather than fatal (mirrors LyricBeatmapDecoder.ParseFormatVersion).
    function parseFormatVersion(magicLine) {
        if (!magicLine || magicLine.indexOf(FORMAT_MAGIC) !== 0) return FALLBACK_FORMAT_VERSION;
        let end = FORMAT_MAGIC.length;
        while (end < magicLine.length && magicLine[end] >= '0' && magicLine[end] <= '9') end++;
        if (end === FORMAT_MAGIC.length) return FALLBACK_FORMAT_VERSION;
        const version = parseInt(magicLine.slice(FORMAT_MAGIC.length, end), 10);
        return isFinite(version) ? version : FALLBACK_FORMAT_VERSION;
    }

    // The mapper's FREESTYLE colour (backlog 384): [General] "FreestyleColour: #rrggbb", display
    // only. Mirrors the game's FreestyleColourKey.Parse: a '#' and exactly six hex digits, either
    // case, anything else reads as ABSENT, and absent is the default violet (TypeBeatStyle.
    // FreestyleChar, also the CSS fallback of .tb-c-free). Returned lowercase, as the game writes it.
    const DEFAULT_FREESTYLE_COLOUR = '#c792ea';
    const FREESTYLE_COLOUR_VALUE = /^#[0-9a-fA-F]{6}$/;

    function parseFreestyleColour(value) {
        const text = value === undefined || value === null ? '' : String(value).trim();
        return FREESTYLE_COLOUR_VALUE.test(text) ? text.toLowerCase() : null;
    }

    // The map's own TRACK GAIN (PR 1), a linear multiplier on the song's samples: 1 plays the file
    // as imported, and a hand-edited value is clamped to [0, MAX_AUDIO_GAIN] on read. Mirrors
    // BeatmapMetadata.DEFAULT_AUDIO_GAIN / MAX_AUDIO_GAIN and the [Metadata] AudioGain case of the
    // game's LegacyBeatmapDecoder.
    const DEFAULT_AUDIO_GAIN = 1;
    const MAX_AUDIO_GAIN = 4;

    // The decoder reads the value with double.TryParse(NumberStyles.Float, InvariantCulture), which
    // takes the WHOLE string or nothing: an optional sign, digits with at most one decimal point
    // (either side may be empty but not both), and an optional exponent, or the invariant infinity
    // symbol. parseFloat would take "2x" as 2; TryParse rejects it, so this does too. A number too
    // large for a double reads as infinity on both sides (and so clamps to the maximum).
    const AUDIO_GAIN_NUMBER = /^[+-]?(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?$/;
    const AUDIO_GAIN_INFINITY = /^([+-]?)infinity$/i;

    // The parsed gain, or null where TryParse would fail (the caller then keeps what it had).
    // NaN is a value TryParse accepts that is refused here too: the game's decoder guards it the
    // same way (backlog 324), so both readers keep the default on a NaN line instead of one of
    // them clamping it straight through into a track of NaN samples.
    function parseAudioGain(value) {
        const v = String(value);
        let n;
        if (AUDIO_GAIN_NUMBER.test(v)) n = Number(v);
        else {
            const inf = AUDIO_GAIN_INFINITY.exec(v);
            if (!inf) return null;
            n = inf[1] === '-' ? -Infinity : Infinity;
        }
        if (n !== n) return null;
        return n < 0 ? 0 : n > MAX_AUDIO_GAIN ? MAX_AUDIO_GAIN : n;
    }

    // Applies a track gain to decoded audio IN PLACE: every sample of every channel (each a
    // Float32Array, as AudioBuffer.getChannelData hands them out) scaled by `gain` and clamped to
    // full scale. It runs once, on the decoded samples and ahead of every volume stage, because that
    // is where the desktop clips (ScaledAudioStream clamps the scaled sample to [-1, 1] before the
    // mixer sees it): a gain node ahead of the player's fixed 0.1 level would never reach full scale
    // (0.1 x 4 < 1), so a map boosted past what its samples hold would play clean here and clipped
    // there. A gain of exactly DEFAULT_AUDIO_GAIN touches nothing, as the desktop plays the file
    // itself then. Returns the number of samples that clipped.
    function applyTrackGain(channels, gain) {
        let clipped = 0;
        if (gain === DEFAULT_AUDIO_GAIN || !channels) return clipped;
        for (const data of channels) {
            for (let i = 0; i < data.length; i++) {
                const scaled = data[i] * gain;
                if (scaled > 1) { data[i] = 1; clipped++; }
                else if (scaled < -1) { data[i] = -1; clipped++; }
                else data[i] = scaled;
            }
        }
        return clipped;
    }

    function parseLyricOsu(text) {
        if (text && text.charCodeAt(0) === 0xFEFF) text = text.slice(1); // strip BOM
        const rows = String(text).split(/\r?\n/);
        const general = {}, metadata = {};
        const lyricObjs = [];
        // Read in the loop rather than off the key map, because the decoder only ASSIGNS on a
        // successful parse: a later unparseable AudioGain line leaves an earlier good one standing.
        let audioGain = DEFAULT_AUDIO_GAIN;
        let section = '';
        // The magic line is the first non-empty row, exactly as the server's parser locates it; a
        // file that does not carry one keeps the fallback version.
        let formatVersion = null;

        for (const raw of rows) {
            const t = raw.trim();
            if (t.length === 0) continue;
            if (formatVersion === null) formatVersion = parseFormatVersion(raw.replace(/^\s+/, ''));
            if (t[0] === '[' && t[t.length - 1] === ']') { section = t.slice(1, -1); continue; }

            if (section === 'General' || section === 'Metadata') {
                const idx = raw.indexOf(':');
                if (idx >= 0) {
                    const key = raw.slice(0, idx).trim();
                    const val = raw.slice(idx + 1).trim();
                    (section === 'General' ? general : metadata)[key] = val;
                    if (section === 'Metadata' && key === 'AudioGain') {
                        const g = parseAudioGain(val);
                        if (g !== null) audioGain = g;
                    }
                }
            } else if (section === 'Lyrics') {
                try { lyricObjs.push(JSON.parse(t)); } catch (e) { /* tolerate junk */ }
            }
        }

        // The [Lyrics] header is the first object WITHOUT a "text" key. No default
        // granularity; a header-less file derives it from whether lines carry words[].
        let header = { version: 2 };
        let lineObjs = lyricObjs;
        if (lyricObjs.length && !('text' in lyricObjs[0])) {
            header = lyricObjs[0];
            lineObjs = lyricObjs.slice(1);
        }

        const num = (v, d) => { const n = parseFloat(v); return isFinite(n) ? n : d; };
        return {
            audioFilename: general['AudioFilename'] || '',
            audioLeadIn: num(general['AudioLeadIn'], 0),
            previewTime: num(general['PreviewTime'], -1),
            title: metadata['Title'] || metadata['TitleUnicode'] || '',
            artist: metadata['Artist'] || metadata['ArtistUnicode'] || '',
            creator: metadata['Creator'] || '',
            beatmapId: parseInt(metadata['BeatmapID'] || '0', 10) || 0,
            beatmapSetId: parseInt(metadata['BeatmapSetID'] || '0', 10) || 0,
            formatVersion: formatVersion === null ? FALLBACK_FORMAT_VERSION : formatVersion,
            audioGain,
            // The LAST FreestyleColour line wins, malformed or not, as in the game's decoder.
            freestyleColour: parseFreestyleColour(general['FreestyleColour']),
            header,
            lineObjs
        };
    }

    Object.assign(parts, {
        FORMAT_MAGIC, FALLBACK_FORMAT_VERSION, LITERAL_BRACKETS_FROM_VERSION, parseFormatVersion,
        DEFAULT_FREESTYLE_COLOUR, parseFreestyleColour, DEFAULT_AUDIO_GAIN, MAX_AUDIO_GAIN,
        parseAudioGain, applyTrackGain, parseLyricOsu
    });
    parts.loaded.push('lyric-beatmap-decoder.js');
})(window.TypeBeatCoreParts = window.TypeBeatCoreParts || { loaded: [] });
