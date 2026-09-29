// card-diffs.js: the beatmapset card's difficulty stack (backlog 327).
//
// A set with two or more live difficulties renders its star/WPM chip as a BUTTON (see
// Pages/Shared/_BeatmapsetCard.cshtml): one star per difficulty, hardest first, the current one on
// top. A click rotates the stack (the top star goes to the back, the next comes forward) and swaps
// the rating text, its colour, the WPM text and the aria-label to the new top difficulty; after the
// last it wraps to the hardest again. While a non-default difficulty is on top, the card's title
// link and its webplay rail carry ?diff= / &diff= so following either lands on THAT difficulty.
//
// Every display string arrives already formatted in data-diffs, so nothing here re-derives a number
// or a colour from a rating. The order is not remembered: a reload shows the hardest again, which
// is also exactly what the card shows with JS off.
//
// Vanilla JS on purpose, no framework anywhere on this site.
(function () {
    'use strict';

    // Where the rotation stands after `clicks` clicks, for a stack of `count` difficulties of which
    // `drawn` stars are on the card. Pure, so the harness can drive it without a DOM:
    //   index  the difficulty now on top (its position in the hardest-first list)
    //   slots  for each drawn star ELEMENT (in markup order), the slot it sits in, 0 = on top
    //   shows  for each drawn star element, the difficulty whose colour it wears
    // Star elements keep their identity while they move, so their transform can transition: each
    // click moves every star up one slot and the old top one to the back. With more difficulties
    // than drawn stars, the one that wraps to the back takes on the next difficulty in line.
    function rotation(count, drawn, clicks) {
        var index = ((clicks % count) + count) % count;
        var slots = [];
        var shows = [];

        for (var e = 0; e < drawn; e++) {
            var slot = (((e - clicks) % drawn) + drawn) % drawn;
            slots.push(slot);
            shows.push((index + slot) % count);
        }

        return { index: index, slots: slots, shows: shows };
    }

    // The card's two deep links with the named difficulty, or without one for the default
    // (hardest) difficulty, which keeps today's hrefs exactly.
    function withDiff(href, diffId) {
        var bare = href.replace(/[?&]diff=[^&#]*/, '');
        if (diffId == null) return bare;

        return bare + (bare.indexOf('?') >= 0 ? '&' : '?') + 'diff=' + encodeURIComponent(diffId);
    }

    function readDiffs(button) {
        if (button._tbDiffs) return button._tbDiffs;

        try {
            button._tbDiffs = JSON.parse(button.getAttribute('data-diffs') || '[]');
        } catch (err) {
            button._tbDiffs = [];
        }

        return button._tbDiffs;
    }

    // Applies click number `clicks` to one stack button and its card.
    function show(button, clicks) {
        var diffs = readDiffs(button);
        if (diffs.length < 2) return;

        var stars = button.querySelectorAll('[data-diff-star]');
        var r = rotation(diffs.length, stars.length, clicks);
        var top = diffs[r.index];

        for (var i = 0; i < stars.length; i++) {
            stars[i].style.setProperty('--slot', String(r.slots[i]));
            stars[i].style.color = diffs[r.shows[i]].colour;
        }

        var colour = button.querySelector('[data-diff-colour]');
        if (colour) colour.style.color = top.colour;

        var rating = button.querySelector('[data-diff-rating]');
        if (rating) rating.textContent = top.stars;

        var wpm = button.querySelector('[data-diff-wpm]');
        // Emptied rather than hidden for a difficulty with no pace: the span keeps its reserved
        // width, so the chip does not shrink under the pointer.
        if (wpm) wpm.textContent = top.wpm == null ? '' : top.wpm + ' WPM';

        button.setAttribute('aria-label', top.label);
        button.setAttribute('data-diff-index', String(r.index));

        var card = button.closest ? button.closest('.bset-card') : null;
        if (!card) return;

        var named = r.index === 0 ? null : top.id;

        var title = card.querySelector('.bset-card__title');
        if (title) title.setAttribute('href', withDiff(title.getAttribute('href') || '', named));

        var play = card.querySelector('.bset-card__play');
        if (play) play.setAttribute('href', withDiff(play.getAttribute('href') || '', named));
    }

    function cycle(button) {
        var clicks = Number(button.getAttribute('data-diff-clicks') || '0') + 1;
        button.setAttribute('data-diff-clicks', String(clicks));
        show(button, clicks);
    }

    // Published before the DOM wiring so the node harness can reach it with a stub document.
    if (typeof window !== 'undefined') {
        window.TypeBeatCardDiffs = { rotation: rotation, withDiff: withDiff, show: show, cycle: cycle };
    }

    if (typeof document === 'undefined' || !document.addEventListener) return;

    document.addEventListener('click', function (e) {
        var button = e.target && e.target.closest ? e.target.closest('[data-diff-stack]') : null;
        if (!button) return;

        // The stack sits inside the card next to its links and rail: a cycle must never also
        // open, play or download anything.
        e.preventDefault();
        e.stopPropagation();

        cycle(button);
    });
})();
