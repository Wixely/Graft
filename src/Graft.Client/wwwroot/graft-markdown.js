// Turns mermaid code blocks in a rendered README into isolated diagram frames.
//
// The server renders a ```mermaid fence as an ordinary code block, so with scripting unavailable, or before
// this runs, a reader still sees the diagram source rather than an empty space. This replaces each one with
// a sandboxed frame that draws it. Mermaid is 3.2 MB and lives entirely inside that frame, so a page with no
// diagrams never pays for it.
(function () {
    var SANDBOX = '/-/mermaid-sandbox';
    var mounted = [];

    function isDark() {
        var explicit = document.documentElement.getAttribute('data-theme');
        if (explicit) return explicit === 'dark';
        return window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches;
    }

    function mount(block) {
        var pre = block.parentElement;
        if (!pre || pre.dataset.graftDiagram === 'done') return;
        pre.dataset.graftDiagram = 'done';

        // textContent, never innerHTML: the source is repository content and is only ever passed as data.
        var source = block.textContent;
        var frame = document.createElement('iframe');
        frame.className = 'diagram-frame';
        frame.setAttribute('sandbox', 'allow-scripts');
        frame.setAttribute('title', 'Diagram');
        frame.setAttribute('loading', 'lazy');
        frame.src = SANDBOX;

        function draw() {
            if (!frame.isConnected || !frame.contentWindow) return false;
            // The frame's document declares no colour scheme of its own, so a browser paints it a white
            // base regardless of what the page looks like. Telling it the surface colour this frame is
            // sitting on is what makes a diagram share the theme the reader actually chose.
            frame.contentWindow.postMessage({
                graftDiagram: true,
                source: source,
                dark: isDark(),
                background: window.getComputedStyle(frame).backgroundColor
            }, '*');
            return true;
        }

        function onMessage(event) {
            if (event.source !== frame.contentWindow) return;
            var data = event.data;
            if (!data || data.graftDiagram !== true) return;
            if (data.ready === true) {
                draw();
                return;
            }
            // Only a bounded number is accepted back; nothing from the frame reaches the DOM.
            if (typeof data.height === 'number' && isFinite(data.height)) {
                frame.style.height = Math.min(Math.max(data.height, 40), 4000) + 'px';
            }
        }

        window.addEventListener('message', onMessage);
        mounted.push(draw);
        pre.replaceWith(frame);
    }

    // A diagram is drawn once, so switching theme would otherwise leave every diagram on the page in the
    // old one until a reload. Both ways of changing it are watched: the application's own toggle, which
    // sets data-theme, and the system preference that applies when nothing is set.
    // Redrawing also prunes: this is a single-page application, so frames come and go as pages change and
    // a list that only ever grew would keep every one of them alive.
    function redraw() {
        var surviving = [];
        for (var index = 0; index < mounted.length; index++) if (mounted[index]()) surviving.push(mounted[index]);
        mounted = surviving;
    }

    new MutationObserver(redraw).observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] });
    if (window.matchMedia) {
        var scheme = window.matchMedia('(prefers-color-scheme: dark)');
        if (scheme.addEventListener) scheme.addEventListener('change', redraw);
    }

    window.graftMarkdown = {
        render: function (container) {
            var scope = container || document;
            var blocks = scope.querySelectorAll('pre > code.language-mermaid');
            for (var index = 0; index < blocks.length; index++) mount(blocks[index]);
            return blocks.length;
        }
    };
})();
