// Runs inside the sandboxed diagram frame, never in the Graft page itself.
//
// The frame has an opaque origin because it is sandboxed without allow-same-origin, so it cannot read or
// touch anything belonging to Graft even if mermaid mishandles a diagram. It accepts one message carrying
// diagram source, renders it, and reports back how tall the result is so the embedder can size the frame.
(function () {
    var rendered = 0;

    function reply(height) {
        // The embedder checks the sender, not the origin: this frame's origin is the opaque value "null",
        // so there is no useful origin for it to state here.
        parent.postMessage({ graftDiagram: true, height: Math.ceil(height) }, '*');
    }

    window.addEventListener('message', function (event) {
        if (event.source !== parent) return;
        var data = event.data;
        if (!data || data.graftDiagram !== true || typeof data.source !== 'string') return;

        var host = document.getElementById('diagram');
        var id = 'd' + (++rendered);

        // Match the surface the embedder is showing. The colour comes from Graft's own stylesheet rather
        // than from repository content, but it is checked anyway: this is the only value from outside the
        // frame that reaches a style, and a strict shape costs nothing.
        document.documentElement.style.colorScheme = data.dark ? 'dark' : 'light';
        if (typeof data.background === 'string' &&
            /^(#[0-9a-fA-F]{3,8}|rgba?\([\d.,%\s/]+\))$/.test(data.background.trim())) {
            document.documentElement.style.background = data.background.trim();
        }

        try {
            mermaid.initialize({
                startOnLoad: false,
                securityLevel: 'strict',
                theme: data.dark ? 'dark' : 'default',
                fontFamily: 'Segoe UI, system-ui, -apple-system, Arial, sans-serif'
            });
            mermaid.render(id, data.source).then(function (result) {
                host.innerHTML = result.svg;
                reply(host.getBoundingClientRect().height);
            }).catch(function (error) {
                // A malformed diagram is an ordinary mistake in somebody's README, so it reports the problem
                // as text rather than failing silently or leaving a blank box.
                host.textContent = 'Diagram could not be drawn: ' + (error && error.message ? error.message : error);
                reply(host.getBoundingClientRect().height);
            });
        } catch (error) {
            host.textContent = 'Diagram could not be drawn.';
            reply(host.getBoundingClientRect().height);
        }
    });

    parent.postMessage({ graftDiagram: true, ready: true }, '*');
})();
