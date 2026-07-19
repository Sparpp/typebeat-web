/*
 * favourite.js — progressive enhancement for the beatmapset card favourite buttons.
 *
 * Without JS the favourite form is a normal POST that redirects (and reloads the page,
 * jumping to the top). With JS we intercept the submit, POST via fetch, and update the
 * heart + count in place — the grid never reloads and scroll position is preserved.
 * Vanilla JS on purpose (no framework anywhere on this site).
 */
(function () {
    'use strict';

    document.addEventListener('submit', async function (e) {
        const form = e.target.closest && e.target.closest('.js-fav-form');
        if (!form) return;
        e.preventDefault();

        const btn = form.querySelector('[data-fav-btn]');
        if (btn && btn.dataset.busy) return;
        if (btn) btn.dataset.busy = '1';

        try {
            const res = await fetch(form.action, {
                method: 'POST',
                credentials: 'same-origin',
                headers: { 'X-Requested-With': 'fetch' },
                body: new FormData(form) // carries the antiforgery token + returnUrl
            });

            // Not signed in (server redirects to /login) — honour it.
            if (res.redirected) { window.location.href = res.url; return; }
            if (!res.ok) throw new Error('favourite failed: ' + res.status);

            applyState(form, await res.json());
        } catch (err) {
            console.error(err);
            form.submit(); // fall back to a normal submit so the action still happens
        } finally {
            if (btn) delete btn.dataset.busy;
        }
    });

    function applyState(form, data) {
        const btn = form.querySelector('[data-fav-btn]');
        if (btn) {
            btn.classList.toggle('is-on', !!data.favourited);
            btn.textContent = data.favourited ? '♥' : '♡'; // ♥ / ♡
            btn.setAttribute('aria-label', data.favourited ? 'Remove from favourites' : 'Add to favourites');
        }
        const card = form.closest('[data-set-id]');
        const countEl = card ? card.querySelector('[data-fav-count]') : null;
        if (countEl && typeof data.count === 'number') {
            countEl.textContent = data.count.toLocaleString('en-US');
        }
    }
})();
