// A published page's own browser icon (ADR 0059).
//
// The shell names Graft's icon. A page that names one of its own swaps the link for one at the page's icon address,
// which carries the icon's blob id, so no two projects, and no two versions of one icon, share an address; a browser
// keeps icons by address, and a shared one is how a page would show another's. The link is replaced rather than
// edited because some browsers only look again when an icon link is added.
(function () {
    var original = null;

    function replace(href, type) {
        var current = document.querySelector('link[rel="icon"]');
        var next = document.createElement('link');
        next.rel = 'icon';
        if (type) next.type = type;
        next.href = href;
        if (current) current.parentNode.replaceChild(next, current);
        else document.head.appendChild(next);
        return current;
    }

    window.graftIcon = {
        set: function (href) {
            var previous = replace(href, null);
            if (original === null && previous) original = { href: previous.getAttribute('href'), type: previous.getAttribute('type') };
        },
        reset: function () {
            if (original === null) return;
            replace(original.href, original.type);
            original = null;
        }
    };
})();
