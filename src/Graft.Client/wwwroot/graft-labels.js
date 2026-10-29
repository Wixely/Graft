// Painting a label in its own colour.
//
// The page's content security policy forbids style attributes in markup, deliberately, because a style
// attribute is one of the places repository content could carry something into the page. A colour set
// through the CSS object model from script is not a style attribute and is not blocked, so this is how a
// label gets its colour. The values arrive from the server, which validated them as hexadecimal colours
// when the label was stored, and are checked again here so nothing but a colour ever reaches the element.
(function () {
    var hex = /^#[0-9a-f]{6}$/i;

    window.graftLabels = {
        paint: function (element, background, text) {
            if (!element || !element.style) return;
            if (hex.test(background)) {
                element.style.backgroundColor = background;
                element.style.borderColor = background;
            }
            if (hex.test(text)) element.style.color = text;
        },
        // A progress bar's width is the other inline measurement the pages need.
        width: function (element, percent) {
            if (!element || !element.style) return;
            var value = Number(percent);
            if (!isFinite(value)) return;
            element.style.width = Math.max(0, Math.min(100, value)) + '%';
        }
    };
})();
