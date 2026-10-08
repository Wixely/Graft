// Copying short strings to the clipboard, for the clone URLs and commands.
//
// The modern clipboard API needs a secure context, which Graft always is, but it can still be refused by
// permission policy or an older browser. The fallback selects a temporary element and uses the legacy copy
// command, so the button reports honestly whether the text actually reached the clipboard rather than
// claiming success and leaving the reader to discover otherwise when they paste.
(function () {
    function legacyCopy(text) {
        var field = document.createElement('textarea');
        field.value = text;
        field.setAttribute('readonly', '');
        field.style.position = 'fixed';
        field.style.top = '-1000px';
        field.style.opacity = '0';
        document.body.appendChild(field);
        try {
            field.select();
            field.setSelectionRange(0, field.value.length);
            return document.execCommand('copy');
        } catch (error) {
            return false;
        } finally {
            document.body.removeChild(field);
        }
    }

    window.graftClipboard = {
        // Selecting on focus has to come through here rather than an inline handler, which the page's
        // content security policy refuses.
        selectAll: function (element) {
            if (element && typeof element.select === 'function') element.select();
        },

        copy: function (text) {
            if (typeof text !== 'string' || text.length === 0) return Promise.resolve(false);
            if (navigator.clipboard && navigator.clipboard.writeText) {
                return navigator.clipboard.writeText(text).then(
                    function () { return true; },
                    function () { return legacyCopy(text); });
            }
            return Promise.resolve(legacyCopy(text));
        }
    };
})();
