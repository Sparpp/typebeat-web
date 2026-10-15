// Site-wide beatmap preview player. Singleton <audio>: starting any preview stops the one
// already playing. Buttons are any .preview-btn with data-preview (the 30 s clip URL); the
// conic-gradient progress ring is driven by the --progress custom property on the button.
// Vanilla JS on purpose, no framework anywhere on this site.
(function () {
    'use strict';

    // Playback level for every preview on the site (0..1). Set to 0.1; the previews were
    // uncomfortably loud at the browser default of 1.0, so this is a 90% cut.
    var PREVIEW_VOLUME = 0.1;

    var audio = null;
    var current = null;

    function stop() {
        if (audio) {
            // Dropping the source this way, rather than assigning src = '', does not fire an
            // error event on the element being let go.
            audio.pause();
            audio.removeAttribute('src');
            audio.load();
            audio = null;
        }

        if (current) {
            current.classList.remove('is-playing');
            current.style.removeProperty('--progress');
            current.setAttribute('aria-label', 'Play preview');
            current = null;
        }
    }

    document.addEventListener('click', function (e) {
        var btn = e.target.closest ? e.target.closest('.preview-btn') : null;
        if (!btn || !btn.dataset.preview)
            return;

        e.preventDefault();

        if (btn === current) {
            stop();
            return;
        }

        stop();

        var clip = new Audio(btn.dataset.preview);
        current = btn;
        audio = clip;
        audio.volume = PREVIEW_VOLUME;

        // Every handler is bound to its own element and button and does nothing once another
        // preview has replaced it, so a late event from an old clip never stops the new one.
        function stopIfCurrent() {
            if (audio === clip)
                stop();
        }

        clip.addEventListener('timeupdate', function () {
            if (audio === clip && isFinite(clip.duration) && clip.duration > 0)
                btn.style.setProperty('--progress', ((clip.currentTime / clip.duration) * 100).toFixed(1) + '%');
        });

        // The preview object is already a 30 s clip, so natural end == clip end.
        clip.addEventListener('ended', stopIfCurrent);
        clip.addEventListener('error', stopIfCurrent);

        btn.classList.add('is-playing');
        btn.setAttribute('aria-label', 'Stop preview');
        clip.play().catch(stopIfCurrent);
    });
})();
