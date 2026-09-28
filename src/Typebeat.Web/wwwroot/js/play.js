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

    // ---- the first-clear Discord nudge (backlog 289) ------------------------
    // Once per browser, on the first map it CLEARS, the results card invites the player to the
    // Discord server. typebeat-player.js renders it but decides nothing: the decision turns on
    // localStorage, which is a page concern and the only thing here that can throw, so it lives in
    // this file and reaches the player as a hook (opts.discordNudge).
    //
    // The flag is spent when the nudge is SHOWN, not when it is clicked. The point is to ask once,
    // and asking again because the first ask went unclicked is exactly the nagging this avoids.
    const DISCORD_NUDGE_KEY = 'tb_discord_nudged';

    // Pure but for the storage handed in, so the display harness can drive it with a fake one.
    // Answers with the invite URL to show, or null for "show nothing": a FAILED run neither shows
    // the nudge nor spends the flag, and a storage that refuses to answer (a private window, a
    // browser with storage blocked) reads as "already asked" rather than as an error, so blocked
    // storage costs the player the nudge and nothing else.
    function takeDiscordNudge(storage, passed, url) {
        if (!passed || !url) return null;
        try {
            if (!storage || storage.getItem(DISCORD_NUDGE_KEY)) return null;
            storage.setItem(DISCORD_NUDGE_KEY, '1');
        } catch (e) {
            return null;
        }
        return url;
    }

    // Reading window.localStorage can itself throw where storage is blocked, hence a guard around
    // the property access and not only around the calls.
    function nudgeStorage() {
        try { return window.localStorage; } catch (e) { return null; }
    }

    // ---- what the player is told after /play/submit (backlog 321) ----------
    // Pure over the submit response `d`, the picked map's card data (its status) and the run's
    // results (passed), so the harness can drive every wording without a DOM. Answers
    // { html, cls } for api.setSubmitStatus; every value interpolated is a number the server
    // produced or one of the fixed strings below, never free text.
    //
    // pp follows the desktop results panel's contract (ScoreEndpoints.SubmitScore): a number is
    // the price, and a null is NEVER printed as 0 (backlog 83). A null on a RANKED play is a
    // price still owed (pp_pending: the map's rating cell is not stored yet, a later boot fills
    // it), and a null on an unranked play has no price at all and prints as a dash.
    //
    // The position is the player's BEST row on the board this play landed on, and personal_best
    // says whether this play IS that row, so a run that did not beat it is told its best's
    // standing as such rather than as its own. A pending or unranked map serves the UNRANKED
    // board (as the game client's leaderboard does), which is said as not counting.
    function ppText(d) {
        if (typeof d.pp === 'number') return Math.round(d.pp).toLocaleString('en-US') + 'pp';
        return d.pp_pending ? 'pp pending' : '- pp';
    }

    function standingText(d) {
        if (!d.position) return '';
        const where = d.board === 'unranked' ? ' on the unranked board, not counted' : '';
        return d.personal_best ? 'new best, #' + d.position + where : 'your best is #' + d.position + where;
    }

    function submitStatus(d, map, results) {
        const standing = standingText(d);
        const tail = ' · ' + ppText(d) + (standing ? ' · ' + standing : '');
        if (d.ranked) return { html: 'submitted ✓ ranked' + tail, cls: 'tb-status-good' };
        return { html: 'recorded, not ranked (' + notRankedReason(map, results) + ')' + tail, cls: 'tb-status-muted' };
    }

    // Why the server stored this play unranked, said honestly. Since backlog 230 the picker offers
    // every PUBLISHED map, so "map not ranked" is the ordinary case rather than an edge one, and the
    // card that launched the play already told us which it is (data-status). Only a run on a map
    // that IS ranked leaves the vaguer wording, where the reason really is one of the tamper/gate
    // checks and the client cannot know which. A pending or unranked map HAS a board (the unranked
    // one the game client shows), so the reason says the play does not count, not that there is
    // nowhere for it to go.
    function notRankedReason(map, results) {
        if (!results.passed) return 'you failed this run';
        if (map.status && map.status !== 'ranked') return 'this map is ' + map.status + ', so it does not count';
        return 'checks failed';
    }

    // ---- submission integrity (backlog 312) ---------------------------------
    // The token body. `revision` is the page's script hash (TYPEBEAT_PLAY.revision), which the
    // server registers as this run's build ('web-' + revision); `beatmapHash` is the checksum the
    // /osu response carried for the exact text this tab is playing, so a map re-uploaded since
    // the tab loaded is refused rather than boarded. Either may be missing (an older cached page,
    // a response without the header) and is then left off, which the server answers as before.
    function tokenBody(setId, beatmapId, revision, beatmapHash) {
        const body = { setId: Number(setId), beatmapId: Number(beatmapId) || 0 };
        if (revision) body.revision = String(revision);
        if (beatmapHash) body.beatmapHash = String(beatmapHash);
        return body;
    }

    // What the player is told when no token could be minted, keyed off the server's error wording
    // (the desktop's two refusals, ScoreEndpoints.CreateToken). Both are fixed by a RELOAD: the
    // page then carries the current scripts and fetches the current map.
    function tokenFailureStatus(error) {
        if (error === 'outdated client')
            return { html: 'score not submitted: this page is out of date. <a href="">reload</a> to play on the current version.', cls: 'tb-status-bad' };
        if (error === 'invalid or missing beatmap_hash')
            return { html: 'score not submitted: this map was updated after it loaded. <a href="">reload</a> to play the current version.', cls: 'tb-status-bad' };
        return { html: 'score not submitted (could not open a play session).', cls: 'tb-status-bad' };
    }

    // The desktop's playback-validity veto (SubmittingPlayer.submitScore): the player flags a run
    // whose audio clock drifted from wall time (results.playbackValid === false, see
    // makePlaybackValidity in typebeat-player.js) and it is never sent.
    const PLAYBACK_INVALID_STATUS = {
        html: 'score not submitted: audio playback was not running at the right speed. check your audio device, then play again.',
        cls: 'tb-status-bad'
    };

    // Published BEFORE the stage guard below, so the display harness (which has no DOM at all, and
    // therefore takes that early return) can still drive the decision. Distinct from
    // window.TYPEBEAT_PLAY, which is the server-rendered page config read above.
    window.TypeBeatPlayPage = {
        takeDiscordNudge: takeDiscordNudge, DISCORD_NUDGE_KEY: DISCORD_NUDGE_KEY, submitStatus: submitStatus,
        tokenBody: tokenBody, tokenFailureStatus: tokenFailureStatus, PLAYBACK_INVALID_STATUS: PLAYBACK_INVALID_STATUS
    };

    const picker = document.getElementById('tb-picker');
    const stageWrap = document.getElementById('tb-stage');
    const stageMount = document.getElementById('tb-stage-mount');
    if (!picker || !stageWrap || !stageMount || !Core) return;

    // The one invite URL reaching the player, rendered onto the stage root from
    // SiteLinks.DISCORD_INVITE. Absent (an older cached page) means no nudge, and the flag stays
    // unspent so the browser is still asked once the attribute is there.
    const discordUrl = stageWrap.dataset.discordUrl || '';

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
    // server can refuse a beatmap that does not belong to the set whose media it served. The
    // revision and the served checksum ride along (backlog 312, see tokenBody). Answers
    // { id } on success, else { error } carrying the server's wording (null for a network failure).
    async function createToken(setId, beatmapId, beatmapHash) {
        if (!CFG.signedIn) return null;
        try {
            const r = await postJson('/play/token', tokenBody(setId, beatmapId, CFG.revision, beatmapHash));
            if (r.ok && r.data && (r.data.id != null)) return { id: r.data.id };
            return { error: (r.data && r.data.error) || null };
        } catch (e) { console.error(e); }
        return { error: null };
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
            wpm.title = "target words per minute: the average WPM across the fastest fifth of the map's lyric lines of three words or more";
            wpm.textContent = Math.round(d.wpm) + ' WPM';
            btn.appendChild(wpm);
        }
        btn.addEventListener('click', () => load(map, d.id, d));
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

    // `diff` is the difficulty's row off the diffs fetch (stars, version_name), when the caller
    // already has it; a deep link does not, so its row is looked up alongside the map fetch. The
    // results card prints both (backlog 320), and a row that cannot be found just leaves them out.
    async function load(map, diffId, diff) {
        const setId = map.setId;
        const title = map.title || '';
        const artist = map.artist || '';
        const query = diffId ? `?diff=${encodeURIComponent(diffId)}` : '';

        showStage();
        stageMount.innerHTML = '<div class="tb-loading">loading map…</div>';

        let osuText, audioBuf, beatmapHash = null, diffRow = diff || null;
        try {
            const [osuRes, audioRes, looked] = await Promise.all([
                fetch(`/play/map/${setId}/osu${query}`, { credentials: 'same-origin' }),
                fetch(`/play/map/${setId}/audio${query}`, { credentials: 'same-origin' }),
                (diffRow || !diffId) ? Promise.resolve(null)
                    : fetchDiffs(setId).then((ds) => ds.find((d) => d.id === diffId) || null)
            ]);
            if (looked) diffRow = looked;
            if (!osuRes.ok || !audioRes.ok) throw new Error('map fetch failed');
            osuText = await osuRes.text();
            // The checksum of exactly this text, handed back with every token minted from it.
            beatmapHash = osuRes.headers.get('X-Beatmap-Checksum');
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
                // The results card's metadata (backlog 320). The mapper is read off the .osu itself
                // (its [Metadata] Creator) by the player, so it needs no threading here.
                stars: diffRow && typeof diffRow.stars === 'number' ? diffRow.stars : null,
                difficulty: (diffRow && diffRow.version_name) || null,
                onExit: showPicker,
                onPlayStart: () => { tokenPromise = createToken(setId, diffId, beatmapHash); },
                // Consulted while the results card is being built, once per card, so a "play again"
                // that clears again asks the same question of the same flag and is answered no.
                discordNudge: (results) => takeDiscordNudge(nudgeStorage(), results.passed, discordUrl),
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
                    // The playback-validity veto (backlog 312): a run the audio clock did not play
                    // at wall speed is never sent, like the desktop's.
                    if (results.playbackValid === false) {
                        api.setSubmitStatus(PLAYBACK_INVALID_STATUS.html, PLAYBACK_INVALID_STATUS.cls);
                        return;
                    }
                    const minted = tokenPromise ? await tokenPromise : null;
                    const token = minted && minted.id != null ? minted.id : null;
                    if (token == null) {
                        const refused = tokenFailureStatus(minted && minted.error);
                        api.setSubmitStatus(refused.html, refused.cls);
                        return;
                    }
                    api.setSubmitStatus('submitting…', 'tb-status-muted');
                    try {
                        const r = await submitScore(token, results);
                        if (!r.ok) {
                            api.setSubmitStatus('submit failed: ' + ((r.data && r.data.error) || r.status), 'tb-status-bad');
                            return;
                        }
                        const status = submitStatus(r.data || {}, map, results);
                        api.setSubmitStatus(status.html, status.cls);
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

    // Entry point for a card click and for the deep link. A set with exactly one difficulty skips
    // the step entirely (there is no choice to make), and so does a deep link that already named a
    // difficulty the server vetted. A diffs fetch that fails or comes back empty falls back to the
    // set-addressed load, which is exactly what /play did before the picker existed.
    async function pick(map, preselectedDiffId) {
        showStage();
        stageMount.innerHTML = '<div class="tb-loading">loading map…</div>';

        if (preselectedDiffId) return load(map, Number(preselectedDiffId));

        const diffs = await fetchDiffs(map.setId);
        if (diffs.length === 1) return load(map, diffs[0].id, diffs[0]);
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
