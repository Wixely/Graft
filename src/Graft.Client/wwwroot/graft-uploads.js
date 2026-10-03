// Bridges the vendored DnaX upload module's completion event to a .NET component.
//
// The event carries the server's receipt, but it is only a hint: it can be missed if the page was replaced
// mid-transfer, and it says the bytes arrived durably, not that the content was accepted. Treat a delivered
// event as "something changed, go and reconcile", and treat the authenticated session record as the truth.
(function () {
    var listeners = new WeakMap();

    window.graftUploads = {
        listen: function (element, dotNetRef) {
            if (!element || listeners.has(element)) return;
            var handler = function (event) {
                var id = event && event.detail && event.detail.id;
                if (!id) return;
                dotNetRef.invokeMethodAsync('Completed', id).catch(function () {
                    // The component may already be gone; the transfer is unaffected either way.
                });
            };
            element.addEventListener('dnax-upload-complete', handler);
            listeners.set(element, handler);
        },
        forget: function (element) {
            if (!element) return;
            var handler = listeners.get(element);
            if (!handler) return;
            element.removeEventListener('dnax-upload-complete', handler);
            listeners.delete(element);
        }
    };
})();
