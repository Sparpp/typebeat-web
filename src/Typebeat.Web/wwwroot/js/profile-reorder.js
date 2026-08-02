/*
 * profile-reorder.js: drag (and keyboard) reordering of the profile's sections, for the profile
 * OWNER only. Loaded by Pages/Users/Profile.cshtml on your own profile and nowhere else.
 *
 * The model is osu-web's, with none of its stack (this site has no React, no jQuery, and no client
 * state store): ONE ordered array of section ids is the layout. On drop we walk the container's
 * children, read data-section-id in the new DOM order, and persist the WHOLE array in one request.
 * The server render is the truth on the next load, so there is no optimistic local cache to keep
 * in sync and nothing to reconcile.
 *
 * Dragging is native HTML5 drag-and-drop rather than pointer-event bookkeeping, because the
 * browser then owns the drag image, the autoscroll and the escape-to-cancel, which is most of what
 * a hand-written sortable spends its lines on. What that costs us:
 *   - TOUCH DEVICES DO NOT FIRE IT AT ALL. Deliberate, and matched by CSS: the whole control group
 *     is hidden below the narrow breakpoint, like osu's hidden-xs on its sortable handle.
 *   - Firefox refuses to start a drag unless dataTransfer.setData is called in dragstart, hence
 *     the otherwise-pointless setData below.
 *   - draggable=true anywhere on a section would make its text unselectable and its links
 *     drag as links, so it is switched on only while the pointer is resting on the grip.
 *
 * Reordering happens live during dragover (the dragged section moves through its siblings under
 * the pointer, wearing .is-dragging), so the gap you see IS the drop position; there is no separate
 * placeholder element to keep in sync with it.
 *
 * The move up/down buttons exist because a native drag has no keyboard story whatsoever. They move
 * a section past its previous/next VISIBLE sibling and take the same persist path, so the feature
 * is not mouse-only.
 */
(function () {
    'use strict';

    document.addEventListener('DOMContentLoaded', function () {
        const container = document.querySelector('[data-profile-sections]');
        const form = document.querySelector('.js-reorder-form');

        // The form is owner-only markup, so its absence is how a visitor's page opts out. (This
        // script is not even served to them; belt and braces, since the file is public.)
        if (!container || !form) return;

        const status = document.querySelector('[data-reorder-status]');

        // Handles are display:none until this class lands, so a browser with JS off shows no
        // controls at all rather than dead ones.
        container.classList.add('is-reorderable');

        // The last order the SERVER is known to hold. A failed persist rewinds the DOM to it.
        let saved = currentOrder();
        let dragging = null;
        let pending = Promise.resolve();

        // ---- drag ----

        // The grip arms the section it belongs to, and only for as long as the pointer is on it.
        container.addEventListener('pointerdown', function (e) {
            const grip = e.target.closest && e.target.closest('[data-reorder-handle]');
            const section = grip && grip.closest('[data-section-id]');
            sections().forEach(s => { s.draggable = (s === section); });
        });

        document.addEventListener('pointerup', function () {
            if (!dragging) disarm();
        });

        container.addEventListener('dragstart', function (e) {
            const section = e.target.closest && e.target.closest('[data-section-id]');
            if (!section || !section.draggable) return;

            dragging = section;
            e.dataTransfer.effectAllowed = 'move';
            e.dataTransfer.setData('text/plain', section.dataset.sectionId); // Firefox needs this

            // Deferred: styling the element inside dragstart would be baked into the drag image.
            window.setTimeout(() => section.classList.add('is-dragging'), 0);
        });

        container.addEventListener('dragover', function (e) {
            if (!dragging) return;
            e.preventDefault();
            e.dataTransfer.dropEffect = 'move';

            const before = insertionPoint(e.clientY);
            if (before !== dragging) container.insertBefore(dragging, before);
        });

        // Without a drop handler the browser treats the release as a cancel in some engines, and
        // Chrome plays the snap-back animation.
        container.addEventListener('drop', function (e) {
            if (dragging) e.preventDefault();
        });

        container.addEventListener('dragend', function () {
            if (!dragging) return;
            dragging.classList.remove('is-dragging');
            dragging = null;
            disarm();
            persist();
        });

        // ---- keyboard / click controls ----

        container.addEventListener('click', function (e) {
            const button = e.target.closest && e.target.closest('[data-reorder-move]');
            if (!button) return;

            const section = button.closest('[data-section-id]');
            if (!section) return;

            const up = button.dataset.reorderMove === 'up';
            const visible = sections().filter(isVisible);
            const at = visible.indexOf(section);
            const swap = visible[up ? at - 1 : at + 1];

            if (!swap) return; // already at the end it was asked to move towards

            // insertBefore is the whole move in both directions: before the one above, or before
            // the one after the one below.
            container.insertBefore(section, up ? swap : swap.nextSibling);

            // The button travelled with the section, so the focus ring did too; keep it there so a
            // keyboard user can move a section several places without re-finding the control.
            button.focus();
            persist();
        });

        // ---- persistence ----

        function persist() {
            const order = currentOrder();
            if (same(order, saved)) return;

            // Serialized: a fast second drag must not race the first request's result, or the
            // server could end up holding the older order.
            pending = pending.then(() => send(order)).catch(() => {});
        }

        async function send(order) {
            const body = new FormData(form); // carries the antiforgery token
            order.forEach(id => body.append('order', id));

            try {
                const res = await fetch(form.action, {
                    method: 'POST',
                    credentials: 'same-origin',
                    headers: { 'X-Requested-With': 'fetch' },
                    body: body
                });

                // Session gone (the server redirects to /login); honour it, like follow.js.
                if (res.redirected) { window.location.href = res.url; return; }
                if (!res.ok) throw new Error('reorder failed: ' + res.status);

                saved = order;
                say('', false);
            } catch (err) {
                console.error(err);
                restore();
                say('Could not save the new order, so your profile is unchanged.', true);
            }
        }

        function restore() {
            // saved was read out of this container, so every id in it is still one of its children.
            saved.forEach(function (id) {
                const section = container.querySelector(':scope > [data-section-id="' + id + '"]');
                if (section) container.appendChild(section);
            });
        }

        // ---- helpers ----

        function sections() {
            return Array.prototype.slice.call(container.querySelectorAll(':scope > [data-section-id]'));
        }

        // Every section id in DOM order, INCLUDING the hidden empty slots: they hold the place of
        // a section that has nothing to show today, so dragging must not drop them from the array.
        function currentOrder() {
            return sections().map(s => s.dataset.sectionId);
        }

        function isVisible(section) {
            return section.offsetParent !== null && section.offsetHeight > 0;
        }

        // The child the dragged section should sit before for a pointer at this Y, or null for
        // "last". Only visible siblings can be aimed at; a zero-height slot has no midpoint.
        function insertionPoint(y) {
            const candidates = sections().filter(s => s !== dragging && isVisible(s));

            for (const section of candidates) {
                const box = section.getBoundingClientRect();
                if (y < box.top + box.height / 2) return section;
            }

            return null;
        }

        function same(a, b) {
            return a.length === b.length && a.every((id, i) => id === b[i]);
        }

        function say(message, bad) {
            if (!status) return;
            status.textContent = message;
            status.classList.toggle('alert-error', !!message && !!bad);
            status.hidden = !message;
        }

        function disarm() {
            sections().forEach(s => { s.draggable = false; });
        }
    });
})();
