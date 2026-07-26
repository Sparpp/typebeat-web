/*
 * play.js: website glue for the in-browser player at /play.
 *
 * Reads the map list rendered server-side (buttons carrying data-* attributes),
 * fetches the chosen map's .osu + audio from the /play/map endpoints, runs the
 * shared TypeBeatCore player, and (when signed in) submits the score through the
 * cookie-authed two-phase /play/token + /play/submit flow, landing it on the
 * same leaderboards as the desktop client.
 *
 * TYPEBEAT_PLAY.autoPlay is the /play?set={id} deep link (the beatmapset card's
 * webplay rail): the server has already resolved and vetted that set, so we skip
 * the picker and load it straight away.
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

    async function createToken(setId) {
        if (!CFG.signedIn) return null;
        try {
            const r = await postJson('/play/token', { setId: Number(setId) });
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

    async function pick(setId, title, artist) {
        title = title || '';
        artist = artist || '';

        showStage();
        stageMount.innerHTML = '<div class="tb-loading">loading map…</div>';

        let osuText, audioBuf;
        try {
            const [osuRes, audioRes] = await Promise.all([
                fetch(`/play/map/${setId}/osu`, { credentials: 'same-origin' }),
                fetch(`/play/map/${setId}/audio`, { credentials: 'same-origin' })
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
                onPlayStart: () => { tokenPromise = createToken(setId); },
                onFinish: async (results, api) => {
                    if (!CFG.signedIn) {
                        api.setSubmitStatus('<a href="/login">sign in</a> to submit your score to the leaderboard.', 'tb-status-muted');
                        return;
                    }
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
                            api.setSubmitStatus('recorded, not ranked (' + (results.passed ? 'map not ranked or checks failed' : 'you failed this run') + ').', 'tb-status-muted');
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

    function loadError() {
        stageMount.innerHTML = '<div class="tb-loading">could not load this map. <a href="#" id="tb-back">back</a></div>';
        const back = document.getElementById('tb-back');
        if (back) back.addEventListener('click', (ev) => { ev.preventDefault(); showPicker(); });
    }

    picker.addEventListener('click', (e) => {
        const btn = e.target.closest('.tb-play');
        if (!btn) return;
        e.preventDefault();
        pick(btn.getAttribute('data-set-id'),
             btn.getAttribute('data-title'),
             btn.getAttribute('data-artist'));
    });

    const closeBtn = document.getElementById('tb-close');
    if (closeBtn) closeBtn.addEventListener('click', showPicker);

    // /play?set={id}: the server resolved the deep link to a playable set, so skip the picker and
    // load it now. The player still opens on its "press space to start" gate, so nothing plays
    // without a user gesture (and closing the stage drops back to the normal picker).
    const auto = CFG.autoPlay;
    if (auto && auto.setId) pick(auto.setId, auto.title, auto.artist);
})();
