// The header account menu (backlog 505): a native <details>, so opening and closing already work
// without this file. All it adds is closing the menu on a click outside it or on Escape, the way
// the notification bell's panel closes.
(function () {
    'use strict';

    function openMenus() {
        return document.querySelectorAll('[data-user-menu][open]');
    }

    document.addEventListener('click', function (e) {
        openMenus().forEach(function (menu) {
            if (!menu.contains(e.target)) menu.removeAttribute('open');
        });
    });

    document.addEventListener('keydown', function (e) {
        if (e.key !== 'Escape') return;

        openMenus().forEach(function (menu) {
            menu.removeAttribute('open');
            var summary = menu.querySelector('summary');
            if (summary && menu.contains(document.activeElement)) summary.focus();
        });
    });
})();
