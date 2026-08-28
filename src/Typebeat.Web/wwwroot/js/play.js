/*
 * play.js: website glue for the in-browser player at /play.
 *
 * Reads the map list rendered server-side (buttons carrying data-* attributes),
 * asks /play/map/{setId}/diffs which difficulties the set has, lets the player
 * choose one, fetches that difficulty's .osu + audio from the /play/map
 * endpoints, runs the shared TypeBeatCore player, and (when signed in) submits
 * the score through the cookie-authed two-phase /play/token + /play/submit flow,
 * landing it on the same leaderboards as the desktop client.
 *
 * ONE DIFFICULTY, END TO END. Everything downstream of the choice carries the
 * chosen beatmap id: the two media fetches (?diff=), and the token (beatmapId).
 * That is not cosmetic. score_tokens.beatmap_id is what the leaderboard row and
 * the anti-cheat play-time gate key off, and beatmaps.skippable_s (the skip
 * allowance that gate subtracts) is per DIFFICULTY, so a token minted against
 * the set's primary diff while the player played another one would board the
 * score on the wrong map and gate it against the wrong length.
 *
 * TYPEBEAT_PLAY.autoPlay is the /play?set={id}[&diff={beatmapId}] deep link (the
 * beatmapset card's webplay rail): the server has already resolved and vetted
 * both, so we skip the picker, and a vetted diffId skips the difficulty step too.
 */
(function () {
    'use strict';
    const Core = window.TypeBeatCore;
    const CFG = window.TYPEBEAT_PLAY || { signedIn: false, csrf: '' };

    const picker = document.getElementById('tb-picker');
    const stageWrap = document.getElementById('tb-stage');
    const stageMount = document.getElementById('tb-stage-mount');
    if (!picker || !stageWrap || !stageMount || !Core) return;

    let current = null; // active player controller

    function showPicker() {
        if (current) { try { current.destroy(); } catch (e) {} current = null; }
        stageWrap.hidden = true;
        picker.hidden = false;
        stageMount.innerHTML = '';
    }

    function showStage() {
        picker.hidden = true;
        stageWrap.hidden = false;
    }

    async function postJson(url, body) {
        const res = await fetch(url, {
            method: 'POST',
            credentials: 'same-origin',
            headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': CFG.csrf || '' },
            body: JSON.stringify(body)
        });
        let data = null;
        try { data = await res.json(); } catch (e) {}
        return { ok: res.ok, status: res.status, data: data };
    }

    // Both ids go on the wire. beatmapId is what the token keys off; setId is sent with it so the
    // server can refuse a beatmap that does not belong to the set whose media it served.
    async function createToken(setId, beatmapId) {
        if (!CFG.signedIn) return null;
        try {
            const r = await postJson('/play/token', { setId: Number(setId), beatmapId: Number(beatmapId) || 0 });
            if (r.ok && r.data && (r.data.id != null)) return r.data.id;
        } catch (e) { console.error(e); }
        return null;
    }

    async function submitScore(token, results) {
        const body = {
            token: token,
            passed: results.passed,
            totalScore: results.totalScore,
            maxCombo: results.maxCombo,
            statistics: results.statistics,
            maximumStatistics: results.maximumStatistics
        };
        return postJson('/play/submit', body);
    }

    // ---- the difficulty step ------------------------------------------------
    // An in-STAGE step, not a strip under every card: the picker renders up to 60 cards and would
    // need every set's difficulty list in its page query to show them there, where this costs one
    // small fetch for the one set that was actually chosen. It also sits where the player is
    // already looking, one gesture before the start gate it hands over to.

    async function fetchDiffs(setId) {
        try {
            const res = await fetch(`/play/map/${setId}/diffs`, { credentials: 'same-origin' });
            if (!res.ok) return [];
            const data = await res.json();
            return (data && data.diffs) || [];
        } catch (e) { console.error(e); return []; }
    }

    // The site's "0.0#" star format (at least one decimal, at most two, no trailing zero in the
    // second place), so a pill here reads exactly as the same map's pill on /beatmapsets does:
    // 5 -> "5.0", 4.2 -> "4.2", 4.25 -> "4.25".
    function formatStars(stars) {
        const s = Number(stars || 0).toFixed(2);
        return s.charAt(s.length - 1) === '0' ? s.slice(0, -1) : s;
    }

    function diffPill(d, map) {
        const btn = document.createElement('button');
        btn.type = 'button';
        btn.className = 'diff-pill tb-diff';
        btn.style.setProperty('--diff-colour', d.colour || 'var(--line)');
        btn.setAttribute('data-diff-id', String(d.id));
        btn.title = d.version_name || '';
        const star = document.createElement('span');
        star.className = 'diff-pill__star';
        star.textContent = '★ ' + formatStars(d.stars);
        const name = document.createElement('span');
        name.className = 'diff-pill__name';
        name.textContent = d.version_name || 'difficulty';
        btn.append(star, name);
        if (d.wpm != null) {
            const wpm = document.createElement('span');
            wpm.className = 'diff-pill__wpm';
            wpm.title = 'average words per minute, five keystrokes to the word';
            wpm.textContent = Math.round(d.wpm) + ' WPM';
            btn.appendChild(wpm);
        }
        btn.addEventListener('click', () => load(map, d.id));
        return btn;
    }

    function showDiffStep(map, diffs) {
        showStage();
        stageMount.innerHTML = '';
        const box = document.createElement('div');
        box.className = 'tb-diffstep';
        const head = document.createElement('div');
        head.className = 'tb-diffstep-title';
        head.textContent = map.title || 'this map';
        const sub = document.createElement('div');
        sub.className = 'tb-diffstep-sub';
        sub.textContent = 'choose a difficulty';
        const list = document.createElement('div');
        list.className = 'diff-selector tb-diffstep-list';
        for (const d of diffs) list.appendChild(diffPill(d, map));
        box.append(head, sub, list);
        stageMount.appendChild(box);
        const first = list.querySelector('.tb-diff');
        if (first) first.focus();
    }

    // ---- loading a chosen difficulty ---------------------------------------

    async function load(map, diffId) {
        const setId = map.setId;
        const title = map.title || '';
        const artist = map.artist || '';
        const query = diffId ? `?diff=${encodeURIComponent(diffId)}` : '';

        showStage();
        stageMount.innerHTML = '<div class="tb-loading">loading map…</div>';

        let osuText, audioBuf;
        try {
            const [osuRes, audioRes] = await Promise.all([
                fetch(`/play/map/${setId}/osu${query}`, { credentials: 'same-origin' }),
                fetch(`/play/map/${setId}/audio${query}`, { credentials: 'same-origin' })
            ]);
            if (!osuRes.ok || !audioRes.ok) throw new Error('map fetch failed');
            osuText = await osuRes.text();
            audioBuf = await audioRes.arrayBuffer();
        } catch (e) {
            console.error(e);
            loadError();
            return;
        }

        // A fresh token per play (minted at play start, so the server's min-play-time gate keys
        // off the real start). "Play again" re-fires onPlayStart, so each attempt can submit.
        let tokenPromise = null;

        try {
            current = Core.mountPlayer(stageMount, {
                osuText: osuText,
                audioArrayBuffer: audioBuf,
                title: title,
                artist: artist,
                onExit: showPicker,
                onPlayStart: () => { tokenPromise = createToken(setId, diffId); },
                onFinish: async (results, api) => {
                    if (!CFG.signedIn) {
                        api.setSubmitStatus('<a href="/login">sign in</a> to submit your score to the leaderboard.', 'tb-status-muted');
                        return;
                    }
                    // Cells TYPED, which deliberately excludes uncorrected typos: those are cells
                    // the player finished wrongly, they count for nothing in completion, and a run
                    // made entirely of them has hit no notes at all.
                    const hits = (results.counts.great + results.counts.ok + results.counts.meh);
                    if (hits <= 0 || results.totalScore <= 0) {
                        api.setSubmitStatus('score not submitted (no notes hit).', 'tb-status-muted');
                        return;
                    }
                    const token = tokenPromise ? await tokenPromise : null;
                    if (!token) {
                        api.setSubmitStatus('score not submitted (could not open a play session).', 'tb-status-bad');
                        return;
                    }
                    api.setSubmitStatus('submitting…', 'tb-status-muted');
                    try {
                        const r = await submitScore(token, results);
                        if (!r.ok) {
                            api.setSubmitStatus('submit failed: ' + ((r.data && r.data.error) || r.status), 'tb-status-bad');
                            return;
                        }
                        const d = r.data || {};
                        if (d.ranked) {
                            const pos = d.position ? ' · #' + d.position + ' on the board' : '';
                            api.setSubmitStatus('submitted ✓ ranked' + pos, 'tb-status-good');
                        } else {
                            api.setSubmitStatus('recorded, not ranked (' + notRankedReason(map, results) + ').', 'tb-status-muted');
                        }
                    } catch (e) {
                        console.error(e);
                        api.setSubmitStatus('submit failed (network).', 'tb-status-bad');
                    }
                }
            });
        } catch (e) {
            console.error(e);
            loadError();
        }
    }

    // Why the server stored this play unranked, said honestly. Since backlog 230 the picker offers
    // every PUBLISHED map, so "map not ranked" is the ordinary case rather than an edge one, and the
    // card that launched the play already told us which it is (data-status). Only a run on a map
    // that IS ranked leaves the vaguer wording, where the reason really is one of the tamper/gate
    // checks and the client cannot know which.
    function notRankedReason(map, results) {
        if (!results.passed) return 'you failed this run';
        if (map.status && map.status !== 'ranked') return 'this map is ' + map.status + ', so it has no leaderboard';
        return 'checks failed';
    }

    function loadError() {
        stageMount.innerHTML = '<div class="tb-loading">could not load this map. <a href="#" id="tb-back">back</a></div>';
        const back = document.getElementById('tb-back');
        if (back) back.addEventListener('click', (ev) => { ev.preventDefault(); showPicker(); });
    }

    // Entry point for a card click and for the deep link. A set with exactly one difficulty skips
    // the step entirely (there is no choice to make), and so does a deep link that already named a
    // difficulty the server vetted. A diffs fetch that fails or comes back empty falls back to the
    // set-addressed load, which is exactly what /play did before the picker existed.
    async function pick(map, preselectedDiffId) {
        showStage();
        stageMount.innerHTML = '<div class="tb-loading">loading map…</div>';

        if (preselectedDiffId) return load(map, Number(preselectedDiffId));

        const diffs = await fetchDiffs(map.setId);
        if (diffs.length === 1) return load(map, diffs[0].id);
        if (diffs.length === 0) return load(map, 0);
        showDiffStep(map, diffs);
    }

    picker.addEventListener('click', (e) => {
        const btn = e.target.closest('.tb-play');
        if (!btn) return;
        e.preventDefault();
        pick({
            setId: btn.getAttribute('data-set-id'),
            title: btn.getAttribute('data-title'),
            artist: btn.getAttribute('data-artist'),
            status: btn.getAttribute('data-status')
        }, 0);
    });

    const closeBtn = document.getElementById('tb-close');
    if (closeBtn) closeBtn.addEventListener('click', showPicker);

    // /play?set={id}[&diff={beatmapId}]: the server resolved the deep link to a playable set (and,
    // when named, to one of its live difficulties), so skip the picker and load it now. The player
    // still opens on its "press space to start" gate, so nothing plays without a user gesture (and
    // closing the stage drops back to the normal picker).
    const auto = CFG.autoPlay;
    if (auto && auto.setId) pick(auto, auto.diffId || 0);
})();
