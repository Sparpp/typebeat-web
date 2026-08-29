// card-download.js: the beatmapset card's two-option download.
//
// Cards whose set has a video render a download BUTTON plus a collapsed panel (see
// Pages/Shared/_BeatmapsetCard.cshtml). Clicking the button opens the panel over the tile with
// "with video" and "audio only, no video"; the sizes are fetched once, on first open, because
// nothing stores a package size and a listing grid must not pay for one per card.
//
// One panel is open at a time; outside click and Escape close it. Cards without a video never get
// here at all: their download control is a plain link, and it downloads on the first click exactly
// as it always has.
//
// Vanilla JS on purpose, no framework anywhere on this site.
(function () {
    'use strict';

    var openPanel = null;
    var openToggle = null;

    function close() {
        if (!openPanel) return;

        openPanel.classList.remove('is-open');
        if (openToggle) openToggle.setAttribute('aria-expanded', 'false');

        openPanel = null;
        openToggle = null;
    }

    function open(panel, toggle) {
        close();

        panel.classList.add('is-open');
        toggle.setAttribute('aria-expanded', 'true');

        openPanel = panel;
        openToggle = toggle;

        var first = panel.querySelector('.bset-card__dl-option');
        if (first) first.focus();

        loadSizes(panel);
    }

    // "approx 4.2 MB". The server sums the manifest's UNCOMPRESSED entry sizes (nothing stores the
    // real zip size), and the package is mostly already-compressed audio and video, so the totals
    // are close but not exact: the wording has to stay approximate.
    function formatSize(bytes) {
        if (typeof bytes !== 'number' || !isFinite(bytes) || bytes < 0) return null;

        var mb = bytes / (1024 * 1024);
        if (mb >= 10) return 'approx ' + Math.round(mb) + ' MB';
        if (mb >= 0.1) return 'approx ' + mb.toFixed(1) + ' MB';

        return 'approx ' + Math.max(1, Math.round(bytes / 1024)) + ' KB';
    }

    function loadSizes(panel) {
        var url = panel.dataset.dlSizes;
        if (!url || panel.dataset.dlLoaded) return;

        panel.dataset.dlLoaded = '1';

        fetch(url, { credentials: 'same-origin', headers: { 'X-Requested-With': 'fetch' } })
            .then(function (res) {
                if (!res.ok) throw new Error('download sizes failed: ' + res.status);
                return res.json();
            })
            .then(function (data) {
                // The server refuses an audio-only package when it would be a broken one: a map
                // imported from an mp4 alone has no separate audio file, so leaving the video out
                // would leave it silent. There is nothing to choose between then, so the button
                // goes back to being an ordinary download.
                if (!data.audioOnlyAvailable) {
                    panel.dataset.dlUnavailable = '1';
                    directDownload(panel);
                    return;
                }

                setSize(panel, '[data-dl-size-full]', data.full);
                setSize(panel, '[data-dl-size-audio]', data.audioOnly);
            })
            .catch(function (err) {
                // Sizes are a nicety; both options still work (and asking for the audio-only one is
                // safe whatever the answer would have been: the server falls back to the full
                // package rather than serving a broken one). Leave the static labels in place.
                console.error(err);
            });
    }

    function setSize(panel, selector, bytes) {
        var el = panel.querySelector(selector);
        var text = formatSize(bytes);

        if (el && text) el.textContent = text;
    }

    /** No choice to make: follow the full-package link, which is what the card used to do. */
    function directDownload(panel) {
        var full = panel.querySelector('.bset-card__dl-option');
        close();

        if (full) window.location.href = full.href;
    }

    document.addEventListener('click', function (e) {
        if (!e.target.closest) return;

        var toggle = e.target.closest('[data-dl-toggle]');

        if (toggle) {
            e.preventDefault();

            var card = toggle.closest('.bset-card');
            var panel = card ? card.querySelector('[data-dl-panel]') : null;
            if (!panel) return;

            if (panel.dataset.dlUnavailable) {
                directDownload(panel);
                return;
            }

            if (panel === openPanel) close();
            else open(panel, toggle);

            return;
        }

        // Anywhere else, including the options themselves: the panel has done its job once one of
        // them is clicked, and the navigation it starts is a download, so the card stays put.
        close();
    });

    document.addEventListener('keydown', function (e) {
        if (e.key !== 'Escape' || !openPanel) return;

        var toggle = openToggle;
        close();

        // Escape puts the caret back where it came from, not at the top of the document.
        if (toggle) toggle.focus();
    });
})();
