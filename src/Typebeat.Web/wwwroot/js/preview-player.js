// Site-wide beatmap preview player. Singleton <audio>: starting any preview stops the one
// already playing. Buttons are any .preview-btn with data-preview (the 30 s clip URL); the
// conic-gradient progress ring is driven by the --progress custom property on the button.
// Vanilla JS on purpose — no framework anywhere on this site.
(function () {
    'use strict';

    var audio = null;
    var current = null;

    function stop() {
        if (audio) {
            audio.pause();
            audio.src = '';
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

        current = btn;
        audio = new Audio(btn.dataset.preview);

        audio.addEventListener('timeupdate', function () {
            if (audio && isFinite(audio.duration) && audio.duration > 0)
                btn.style.setProperty('--progress', ((audio.currentTime / audio.duration) * 100).toFixed(1) + '%');
        });

        // The preview object is already a 30 s clip, so natural end == clip end.
        audio.addEventListener('ended', stop);
        audio.addEventListener('error', stop);

        btn.classList.add('is-playing');
        btn.setAttribute('aria-label', 'Stop preview');
        audio.play().catch(stop);
    });
})();
