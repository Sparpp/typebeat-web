/*
 * follow.js: progressive enhancement for the profile header's Follow button and mapper bell.
 *
 * Same shape as favourite.js (and deliberately so): without JS each control is a normal POST
 * that redirects back to the profile; with JS we intercept the submit, POST via fetch, and
 * repaint the button plus the follower/following counters in place, so the page never reloads.
 * Vanilla JS on purpose (no framework anywhere on this site).
 */
(function () {
    'use strict';

    document.addEventListener('submit', async function (e) {
        const form = e.target.closest && e.target.closest('.js-follow-form');
        if (!form) return;
        e.preventDefault();

        const btn = form.querySelector('[data-follow-btn]');
        if (btn && btn.dataset.busy) return;
        if (btn) btn.dataset.busy = '1';

        try {
            const res = await fetch(form.action, {
                method: 'POST',
                credentials: 'same-origin',
                headers: { 'X-Requested-With': 'fetch' },
                body: new FormData(form) // carries the antiforgery token + returnUrl
            });

            // Not signed in (server redirects to /login); honour it.
            if (res.redirected) { window.location.href = res.url; return; }
            if (!res.ok) throw new Error('follow failed: ' + res.status);

            applyState(form, await res.json());
        } catch (err) {
            console.error(err);
            form.submit(); // fall back to a normal submit so the action still happens
        } finally {
            if (btn) delete btn.dataset.busy;
        }
    });

    function applyState(form, data) {
        const btn = form.querySelector('[data-follow-btn]');
        if (btn) {
            btn.classList.toggle('is-on', !!data.on);

            if (btn.dataset.followKind === 'mapper') {
                const label = data.on ? 'Stop watching this mapper' : 'Watch this mapper for new maps';
                btn.setAttribute('aria-label', label);
                btn.setAttribute('title', label);
            } else {
                // The follow button also swaps its fill: filled = "follow me", outlined = already following.
                btn.classList.toggle('btn-primary', !data.on);
                btn.classList.toggle('btn-ghost', !!data.on);
                btn.textContent = data.on ? 'following' : 'follow';
            }
        }

        setCount(document.querySelector('[data-follower-count]'), data.followers);
        setCount(document.querySelector('[data-following-count]'), data.following);
    }

    function setCount(el, value) {
        if (el && typeof value === 'number') el.textContent = value.toLocaleString('en-US');
    }
})();
