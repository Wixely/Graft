// Graft theme selection. Loaded as a blocking classic script in <head> so the stored choice is applied
// before first paint; the Content-Security-Policy is script-src 'self', so an inline snippet is not an
// option here. Absent or unrecognised storage means "follow the operating system", which the stylesheet
// handles through prefers-color-scheme.
(function () {
    var KEY = 'graft-theme';

    function stored() {
        try {
            var value = window.localStorage.getItem(KEY);
            return value === 'light' || value === 'dark' ? value : null;
        } catch (error) {
            return null; // Private modes and blocked site data throw rather than returning null.
        }
    }

    function apply(theme) {
        if (theme) document.documentElement.setAttribute('data-theme', theme);
        else document.documentElement.removeAttribute('data-theme');
    }

    function system() {
        return window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
    }

    window.graftTheme = {
        // 'light', 'dark', or null when the operating system decides.
        preference: stored,
        // The theme actually showing, which is what a toggle label needs.
        effective: function () { return stored() || system(); },
        set: function (theme) {
            var next = theme === 'light' || theme === 'dark' ? theme : null;
            try {
                if (next) window.localStorage.setItem(KEY, next);
                else window.localStorage.removeItem(KEY);
            } catch (error) {
                // A per-viewer convenience only: keep rendering the chosen theme for this page either way.
            }
            apply(next);
            return next || system();
        },
        toggle: function () { return window.graftTheme.set(window.graftTheme.effective() === 'dark' ? 'light' : 'dark'); }
    };

    apply(stored());
})();
