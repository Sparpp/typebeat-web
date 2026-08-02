/*
 * notifications.js: progressive enhancement for the header bell.
 *
 * The bell is an <a href="/watching"> and every notification row is a plain form POST, so the
 * whole feature already works with scripting off. This file adds exactly two things on top:
 *
 *   1. clicking the bell opens a dropdown (fetching its contents once, from
 *      /watching?handler=Panel, which returns the SAME server-rendered partial the /watching page
 *      uses) instead of navigating;
 *   2. "mark all read" clears the badge in place rather than reloading the page.
 *
 * Deliberately NOT enhanced: clicking a notification row. Its POST marks the row read and
 * redirects to the map, which is what the user wanted anyway, so intercepting it would only add
 * a way for it to fail.
 *
 * Every failure path falls back to the no-JS behaviour (navigate to /watching, or let the form
 * submit normally). Vanilla JS, same shape as favourite.js / follow.js; no framework anywhere.
 */
(function () {
    'use strict';

    // ---- open / close the dropdown ----

    document.addEventListener('click', function (e) {
        var target = e.target instanceof Element ? e.target : null;
        if (!target) return;

        var toggle = target.closest('[data-notif-toggle]');

        if (toggle) {
            // Modifier-clicks and middle-clicks must keep behaving like the link this is
            // (open /watching in a new tab), so only a plain click becomes a dropdown.
            if (e.defaultPrevented || e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey || e.altKey) return;

            e.preventDefault();
            togglePanel(toggle.closest('[data-notif]'));
            return;
        }

        // A click anywhere outside an open bell closes it.
        var root = document.querySelector('[data-notif].is-open');
        if (root && !root.contains(target)) closePanel(root);
    });

    document.addEventListener('keydown', function (e) {
        if (e.key !== 'Escape') return;

        var root = document.querySelector('[data-notif].is-open');
        if (!root) return;

        closePanel(root);

        var toggle = root.querySelector('[data-notif-toggle]');
        if (toggle) toggle.focus();
    });

    function togglePanel(root) {
        if (!root) return;
        if (root.classList.contains('is-open')) closePanel(root);
        else openPanel(root);
    }

    function openPanel(root) {
        var panel = root.querySelector('[data-notif-panel]');
        var toggle = root.querySelector('[data-notif-toggle]');
        if (!panel) return;

        root.classList.add('is-open');
        panel.hidden = false;
        if (toggle) toggle.setAttribute('aria-expanded', 'true');

        loadPanel(root, panel);
    }

    function closePanel(root) {
        var panel = root.querySelector('[data-notif-panel]');
        var toggle = root.querySelector('[data-notif-toggle]');

        root.classList.remove('is-open');
        if (panel) panel.hidden = true;
        if (toggle) toggle.setAttribute('aria-expanded', 'false');
    }

    // ---- contents, fetched once per page ----

    function loadPanel(root, panel) {
        // Loaded already, or a load is in flight: the panel keeps whatever it has. Reopening
        // does not refetch, because nothing on the page can have changed the list in between
        // (marking read is handled locally, and clicking a row navigates away).
        if (root.dataset.loaded || root.dataset.loading) return;
        root.dataset.loading = '1';

        fetch('/watching?handler=Panel', {
            credentials: 'same-origin',
            headers: { 'X-Requested-With': 'fetch' }
        }).then(function (res) {
            // Session expired mid-page: the handler redirects to /login. Honour it.
            if (res.redirected) { window.location.href = res.url; return null; }
            if (!res.ok) throw new Error('panel failed: ' + res.status);
            return res.text();
        }).then(function (html) {
            if (html === null) return;
            panel.innerHTML = html;
            root.dataset.loaded = '1';
        }).catch(function (err) {
            console.error(err);
            // Fall back to the page the bell links to, which renders the same list.
            window.location.href = '/watching';
        }).then(function () {
            delete root.dataset.loading;
        });
    }

    // ---- mark all read ----

    document.addEventListener('submit', function (e) {
        var form = e.target instanceof Element ? e.target.closest('.js-notif-readall') : null;
        if (!form) return;

        e.preventDefault();

        var btn = form.querySelector('button');
        if (btn && btn.dataset.busy) return;
        if (btn) btn.dataset.busy = '1';

        fetch(form.action, {
            method: 'POST',
            credentials: 'same-origin',
            headers: { 'X-Requested-With': 'fetch' },
            body: new FormData(form) // carries the antiforgery token
        }).then(function (res) {
            if (res.redirected) { window.location.href = res.url; return; }
            if (!res.ok) throw new Error('mark all read failed: ' + res.status);

            clearUnread(form);
        }).catch(function (err) {
            console.error(err);
            form.submit(); // plain submit so the action still happens
        }).then(function () {
            if (btn) delete btn.dataset.busy;
        });
    });

    function clearUnread(form) {
        // The badge is absent at zero rather than styled away, so clearing it is a removal.
        var badge = document.querySelector('[data-notif-badge]');
        if (badge) badge.remove();

        var bell = document.querySelector('.notif-bell__btn');
        if (bell) bell.classList.remove('is-on');

        var rows = document.querySelectorAll('.notif-row.is-unread');
        for (var i = 0; i < rows.length; i++) {
            rows[i].classList.remove('is-unread');

            var dot = rows[i].querySelector('.notif-row__dot');
            if (dot) dot.remove();
        }

        // The control has nothing left to do; it comes back on the next page render if new
        // notifications have arrived.
        form.remove();
    }
})();
