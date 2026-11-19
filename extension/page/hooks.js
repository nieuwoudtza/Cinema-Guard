// Runs in the page before its own scripts: watches the page's requests for subtitle files, so captions a player
// downloads (Netflix, Prime Video, Disney+ and other players) can be read in full, ahead of when they are shown.
(() => {
    const CG = (globalThis.__cinemaGuard ??= {});
    if (CG.hooks) return;

    const { captions } = CG;
    const MAX_BYTES = 6 * 1024 * 1024;
    const URL_HINT = /timedtext|subtitle|caption|\.vtt|\.ttml|\.dfxp|\.srt|\.ssa\b|\.ass\b|\.xml|[?&]o=|texttrack|\/tt\/|\/subs?\/|[?&/_-]subs?[=._-]|[?&](?:format|fmt)=(?:vtt|srt)/i;
    const TYPE_HINT = /text\/|xml|vtt|ttml|subrip|x-ssa|x-ass/i;

    const originalFetch = window.fetch;
    const listeners = new Set();
    const recent = []; // the last captures, for a session that starts after its subtitles arrived

    function deliver(url, body) {
        if (!body || !captions.looksLikeCaptions(body.slice(0, 300))) return;
        const parsed = captions.parse(body);
        if (!parsed.format || parsed.cues.length === 0) return;

        const capture = { url, at: Date.now(), format: parsed.format, language: parsed.language, cues: parsed.cues };
        recent.push(capture);
        if (recent.length > 40) recent.shift();
        for (const listener of listeners) {
            try { listener(capture); } catch (error) { console.warn('[Cinema Guard]', error); }
        }
    }

    const worthReading = (url, type, length) =>
        (!length || Number(length) <= MAX_BYTES) && (URL_HINT.test(url) || TYPE_HINT.test(type || '')) && !/json/i.test(type || '') || /timedtext/.test(url);

    window.fetch = function (input) {
        const promise = originalFetch.apply(this, arguments);
        try {
            const url = input instanceof Request ? input.url : String(input);
            promise.then(response => {
                if (!worthReading(url, response.headers.get('content-type'), response.headers.get('content-length'))) return;
                return response.clone().text().then(body => deliver(url, body));
            }).catch(() => {});
        } catch { /* never break the page's own request */ }
        return promise;
    };

    const originalOpen = XMLHttpRequest.prototype.open;
    XMLHttpRequest.prototype.open = function (method, url) {
        try {
            const href = String(url);
            this.addEventListener('load', () => {
                try {
                    const type = this.getResponseHeader('content-type');
                    if (this.responseType === 'arraybuffer' && this.response) {
                        // Binary responses are mostly video; a look at the first bytes is enough to tell.
                        if (this.response.byteLength > MAX_BYTES) return;
                        const start = new TextDecoder().decode(new Uint8Array(this.response, 0, Math.min(300, this.response.byteLength)));
                        if (captions.looksLikeCaptions(start)) deliver(href, new TextDecoder().decode(this.response));
                    } else if ((this.responseType === '' || this.responseType === 'text') && worthReading(href, type)) {
                        deliver(href, this.responseText);
                    } else if (this.responseType === 'blob' && this.response && this.response.size <= MAX_BYTES && URL_HINT.test(href)) {
                        this.response.text().then(body => deliver(href, body)).catch(() => {});
                    }
                } catch { /* ignore */ }
            });
        } catch { /* never break the page's own request */ }
        return originalOpen.apply(this, arguments);
    };

    // Shadow roots, open and closed: some players draw their captions inside one, out of reach of the page's DOM.
    const shadowRoots = new Set(); // WeakRefs
    const shadowListeners = new Set();
    const originalAttachShadow = Element.prototype.attachShadow;
    if (originalAttachShadow) {
        Element.prototype.attachShadow = function () {
            const root = originalAttachShadow.apply(this, arguments);
            try {
                shadowRoots.add(new WeakRef(root));
                for (const listener of shadowListeners) listener(root);
            } catch { /* never break the page's own elements */ }
            return root;
        };
    }

    function liveShadowRoots() {
        const roots = [];
        for (const ref of shadowRoots) {
            const root = ref.deref();
            if (root) roots.push(root); else shadowRoots.delete(ref);
        }
        return roots;
    }

    CG.hooks = {
        // The page's own fetch, unwatched, for the extension's requests. It must be called on the window.
        originalFetch: (...args) => originalFetch.apply(window, args),
        recent,
        listen: listener => listeners.add(listener),
        shadowRoots: liveShadowRoots,
        onShadowRoot: listener => shadowListeners.add(listener),
    };
})();
