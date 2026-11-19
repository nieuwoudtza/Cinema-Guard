// The filter running in the page: finds the video and its captions, works out when to mute, mutes, and blanks
// listed words in captions on screen. Settings come from the bridge script; state goes back to it for the popup.
(() => {
    const CG = (globalThis.__cinemaGuard ??= {});
    if (CG.engine) return;

    const { matcher, hits: H, settings: S, hooks, sites } = CG;
    const TAG = '__cinemaGuard';
    const adapter = sites.pick();
    const host = location.hostname.replace(/^www\./, '');

    // The site of the tab. A player embedded from another site (most film sites) is filtered as the tab's site says.
    const topHost = (() => {
        try {
            const origins = location.ancestorOrigins;
            const top = origins?.length ? origins[origins.length - 1] : null;
            if (top && top !== 'null') return new URL(top).hostname.replace(/^www\./, '');
        } catch { /* not known */ }
        return host;
    })();

    let settings = null;
    let allowChannels = [];
    let lists = { full: [], single: [], all: [], pads: {}, mask: '', maskedWords: true };
    let session = null;
    let mutedByUs = false;
    let mutedVideo = null;
    let started = false;

    // ---- Talking to the bridge ----

    const toBridge = (type, payload) => window.postMessage({ [TAG]: 'toBridge', type, payload }, '*');

    window.addEventListener('message', event => {
        if (event.source !== window || event.data?.[TAG] !== 'toPage') return;
        const { type, payload } = event.data;
        if (type === 'settings') receiveSettings(payload);
        else if (type === 'command') command(payload || {});
    });

    function receiveSettings(payload) {
        const shownBefore = settings?.showCaptions;
        settings = S.normalise(payload?.settings);
        allowChannels = Array.isArray(payload?.allowChannels) ? payload.allowChannels : [];
        // Changes made on this page are already applied, so a change here came from somewhere else.
        if (session && typeof shownBefore === 'boolean' && typeof settings.showCaptions === 'boolean' && settings.showCaptions !== shownBefore) {
            note(session, settings.showCaptions
                ? 'Captions shown from the popup, the options page or another tab'
                : 'Captions hidden from the popup, the options page or another tab; they stay on out of sight');
        }
        applySettings();
        if (!started) start();
    }

    function applySettings() {
        lists = {
            full: settings.wordsFull,
            // A word on both lists mutes the whole sentence, as in the app.
            single: settings.wordsSingle.filter(word => !settings.wordsFull.includes(word)),
            all: [...settings.wordsFull, ...settings.wordsSingle],
            pads: Object.fromEntries(Object.entries(settings.pads).map(([key, ms]) => [key, ms / 1000])),
            mask: settings.censorCharacter === ' ' ? '\u00A0' : settings.censorCharacter,
            maskedWords: settings.muteMaskedWords,
        };
        if (session) {
            session.allowed = allowedReason(session);
            recompute(session);
        }
        resetLive();
        rescanCaptions();
        tick();
    }

    // Changes made from the page (YouTube's CC button) are applied at once and saved through the bridge.
    function saveSettings(changes) {
        settings = S.normalise({ ...settings, ...changes });
        applySettings();
        toBridge('saveSettings', changes);
    }

    function command({ name, time }) {
        const video = adapter.video();
        if (name === 'seek' && video && Number.isFinite(time)) adapter.seek(video, time);
        if (name === 'reload' && session) {
            session = null;
            checkMedia();
        }
        if (name === 'repost') {
            postedVersion = -1;
            idlePosted = '';
            post();
        }
        if (name === 'showSiteCaptions') {
            document.documentElement.classList.remove('cg-hide-site-captions');
            if (session) note(session, 'Subtitles shown');
        }
    }

    // ---- Whether to filter ----

    function allowedReason(current) {
        if (!settings) return null;
        if (!S.siteOn(settings, topHost, host)) return 'site';
        if (current?.channel && allowChannels.some(channel => channel.id === current.channel.id)) return 'channel';
        return null;
    }

    // Subtitles are read whatever the lists say; this decides only whether to mute and blank.
    const filtering = () => !!settings && settings.enabled && !session?.allowed && S.siteOn(settings, topHost, host);

    // ---- Sessions: one per video ----

    const isCurrent = current => current === session;

    function note(current, text) {
        current.log.push({ t: (performance.now() - current.startedAt) / 1000, text });
        if (current.log.length > 200) current.log.splice(0, current.log.length - 200);
        current.version++;
        post();
    }

    function setState(current, state, message) {
        current.state = state;
        current.message = message;
        current.version++;
        post();
    }

    function checkMedia() {
        if (!settings) return;
        let key = null;
        try { key = adapter.mediaKey(); } catch { /* the player is not ready */ }

        // Most pages have no video: nothing more runs on them until one turns up.
        if (!key && !session && !safe(() => adapter.video(), null)) {
            post();
            return;
        }
        run();

        if (key && key !== session?.key) startSession(key);
        observeCaptions();

        if (session) {
            const info = safe(() => adapter.info(), {});
            if (info.title && info.title !== session.title) { session.title = info.title; session.version++; }
            if (info.channel && !session.channel) session.channel = info.channel;
            const allowed = allowedReason(session);
            if (allowed !== session.allowed) {
                session.allowed = allowed;
                session.version++;
                rescanCaptions();
            }
            safe(() => adapter.tick(api));
            if (adapter.readsTextTracks) readTextTracks(session);
            drawMarkers();
        }
        post();
    }

    function startSession(key) {
        release();
        resetLive();
        const info = safe(() => adapter.info(), {});
        session = {
            key, version: 1, startedAt: performance.now(), startedWall: Date.now(), url: location.href,
            title: info.title || '', channel: info.channel || null, allowed: null,
            state: 'loading', message: 'Looking for captions…', log: [],
            sources: new Map(), trackCounts: new Map(), hits: [], liveHits: [], ranges: [], counted: new Set(),
            offset: 0, alignment: [], alignSeen: new Set(),
            foreign: new Map(), shown: null, // subtitles in other languages, and which language is on screen
        };
        session.allowed = allowedReason(session);
        const current = session;
        note(current, `${adapter.name}: ${current.title || key}`);

        // Subtitle files the player downloaded just before this video was recognised.
        if (adapter.acceptsDownloads) {
            for (const capture of hooks.recent) if (capture.at > Date.now() - 30000) addDownload(current, capture);
        }

        Promise.resolve()
            .then(() => adapter.load(current, api))
            .catch(error => note(current, `Loading captions failed: ${error.message}`))
            .finally(() => {
                if (isCurrent(current) && current.state === 'loading') {
                    // Most film players download subtitles only once a language is chosen in them.
                    setState(current, 'live', adapter.id === 'youtube'
                        ? 'No caption file yet: muting follows the captions on screen'
                        : 'No subtitles yet. Turn on subtitles in the player, in your caption language, so Cinema Guard can read them; until then it follows any captions on screen');
                }
            });
    }

    // ---- Caption sources ----

    function addSource(current, source) {
        if (!isCurrent(current) || !source.cues?.length) return;
        const existing = current.sources.get(source.id);

        if (existing && !source.replace) {
            // More of the same track (HLS players download subtitles in segments).
            const seen = new Set(existing.cues.map(cue => `${cue.start.toFixed(2)}|${cue.text}`));
            const added = source.cues.filter(cue => !seen.has(`${cue.start.toFixed(2)}|${cue.text}`));
            if (added.length === 0) return;
            existing.cues = existing.cues.concat(added).sort((a, b) => a.start - b.start);
        } else {
            current.sources.set(source.id, { ...source, cues: [...source.cues].sort((a, b) => a.start - b.start) });
            if (!existing) note(current, `${source.name}: ${source.cues.length} lines (${source.via.toLowerCase()})`);
        }
        scheduleRecompute(current);
    }

    function addDownload(current, capture) {
        // Segments of one track share their folder; a whole file is its own source.
        const url = new URL(capture.url, location.href);
        const folder = url.origin + url.pathname.replace(/[^/]*$/, '');
        const id = capture.format === 'vtt' && /seg|\d+\.vtt$/i.test(url.pathname) ? `dl:${folder}` : `dl:${url.origin}${url.pathname}`;

        const languages = settings.languages;
        if (capture.language && !languages.some(language => capture.language.toLowerCase().startsWith(language))) {
            keepForeign(current, id, capture.cues);
            if (!current.skippedLanguages?.has(capture.language)) {
                (current.skippedLanguages ??= new Set()).add(capture.language);
                note(current, `Skipped a ${capture.language} subtitle file (not one of your caption languages)`);
            }
            return;
        }
        // A file that doesn't say its language is looked at: one in another language is skipped, so the player's
        // first pick (often Arabic or Spanish) doesn't stand in for the English the viewer switches to.
        if (!capture.language && otherLanguage(capture.cues)) {
            keepForeign(current, id, capture.cues);
            skippedLanguage(current, 'subtitle file');
            return;
        }
        const name = `Subtitles (${capture.format}${capture.language ? `, ${capture.language}` : ''})`;
        addSource(current, { id, name, via: 'Downloaded by the player', cues: capture.cues, alignable: adapter.alignToScreen });
    }

    // Caption languages written in the Latin alphabet; a file mostly in another one is not in them.
    const LATIN_LANGUAGES = new Set(['af', 'da', 'nl', 'en', 'fi', 'fr', 'de', 'id', 'it', 'no', 'pl', 'pt', 'es', 'sv', 'tr', 'vi', 'xh', 'zu']);

    // Words found all through English dialogue and seldom in other languages (so not "a", "no" or "me"). English
    // subtitles are about a quarter these words; Spanish, French or German ones hardly any.
    const COMMON_WORDS = {
        en: new Set(['the', 'you', 'to', 'and', 'it', 'is', 'that', 'what', 'of', 'this', 'we', 'my', 'your', "don't", "i'm", "it's",
            "you're", 'have', 'are', 'not', 'for', 'just', 'know', 'he', 'she', 'with', 'get', 'all', 'there', 'can', 'like', 'out',
            'they', 'him', 'her', 'if', 'how', 'right', 'okay', 'yes', 'want', 'got', 'think', 'well', 'now', 'about', 'gonna', 'did']),
    };

    // Whether subtitles are in none of the viewer's caption languages. Only said when it is clear.
    function otherLanguage(cues) {
        const sample = cues.slice(0, 120).map(cue => cue.text.replace(/<[^>]*>/g, '')).join(' ').replace(/[‘’]/g, "'");
        const letters = sample.match(/\p{L}/gu)?.length || 0;
        if (letters < 50) return false;
        if (settings.languages.every(language => LATIN_LANGUAGES.has(language))) {
            const latin = sample.match(/\p{Script=Latin}/gu)?.length || 0;
            if (latin / letters < 0.3) return true;
        }
        const lists = settings.languages.map(language => COMMON_WORDS[language]);
        if (lists.some(list => !list)) return false; // a language without a word list: no way to tell
        const words = sample.toLowerCase().match(/\p{L}+(?:'\p{L}+)?/gu) || [];
        if (words.length < 60) return false;
        const common = words.filter(word => lists.some(list => list.has(word))).length;
        return common / words.length < 0.08;
    }

    // Subtitles in other languages are kept aside (not used for muting), to tell which language is on screen.
    function keepForeign(current, id, cues) {
        const existing = current.foreign.get(id);
        const all = existing ? existing.concat(cues) : [...cues];
        current.foreign.set(id, all.sort((a, b) => a.start - b.start));
    }

    // ---- Subtitles on screen in another language ----
    // With the setting on, muting pauses while the player shows subtitles in another language (for a dub, where the
    // sound changes too). Told from the player's showing text track, or from which file the text on screen comes from.

    const pausedForSubtitles = () => !!settings?.pauseForOtherSubtitles && session?.shown === 'other';

    function setShown(current, shown) {
        if (!shown || current.shown === shown) return;
        const before = current.shown;
        current.shown = shown;
        current.version++;
        if (before || shown === 'other') {
            const on = settings.pauseForOtherSubtitles;
            note(current, shown === 'other'
                ? `The subtitles on screen are in another language, so ${on ? 'muting is paused' : 'muting carries on: the setting to stop muting for other subtitles is off'}`
                : `The subtitles on screen are in your language again${on ? ', so muting is back on' : ''}`);
        }
        drawMarkers(true);
        tick();
        post();
    }

    // Text on screen is a subtitle line when it is a fair part of one (a line the player splits in two), or the line
    // with little more around it. Names shared by both languages ("Staten Island", "Spider-Man") match neither way: a
    // short line is not found inside a longer text, and a text that matches both languages decides nothing.
    const partOf = (key, line) => (key.length >= 8 && line.includes(key)) || (line.length >= 8 && key.includes(line) && line.length >= key.length * 0.6);

    function watchShownLanguage(texts) {
        if (!session?.sources.size || !session.foreign.size) return;
        const keys = texts.map(text => text && lineKey(text)).filter(key => key && key.length >= 8);
        if (keys.length === 0) return;
        const ours = spokenKeys();
        const foreign = keysAt([...session.foreign.values()].map(cues => ({ cues, offset: 0 })));
        let votesOurs = 0;
        let votesOther = 0;
        for (const key of keys) {
            const inOurs = ours.some(line => partOf(key, line));
            const inOther = foreign.some(line => partOf(key, line));
            if (inOurs && !inOther) votesOurs++;
            if (inOther && !inOurs) votesOther++;
        }
        if (votesOurs > votesOther) setShown(session, 'ours');
        else if (votesOther > votesOurs) setShown(session, 'other');
    }

    function skippedLanguage(current, what) {
        if (current.skippedOther) return;
        current.skippedOther = true;
        note(current, `Skipped a ${what} in another language than your caption languages; it is read once you choose one of yours in the player`);
    }

    hooks.listen(capture => {
        if (session && settings && adapter.acceptsDownloads) addDownload(session, capture);
    });

    let recomputeTimer = null;
    function scheduleRecompute(current) {
        clearTimeout(recomputeTimer);
        recomputeTimer = setTimeout(() => { if (isCurrent(current)) recompute(current); }, 150);
    }

    const shifted = (cues, offset) => offset ? cues.map(cue => ({
        ...cue, start: cue.start + offset, end: cue.end + offset,
        words: cue.words && cue.words.map(word => ({ ...word, start: word.start + offset, end: word.end + offset })),
    })) : cues;

    function recompute(current) {
        if (!current || current.sources.size === 0) return;
        const tracks = [...current.sources.values()].map(source => source.alignable ? shifted(source.cues, current.offset) : source.cues);
        current.hits = H.collectHits(tracks, { full: lists.full, single: lists.single, maskedWords: lists.maskedWords, pads: lists.pads });
        current.ranges = H.buildRanges(current.hits, adapter.video()?.duration);
        const count = current.ranges.length;
        setState(current, 'ready', count ? `${count} mute${count === 1 ? '' : 's'} ready` : 'No swearing found in the captions');
        drawMarkers(true);
        tick();
    }

    // Native text tracks (<track> elements and players that use them): their cues are all there in advance.
    const cueOriginals = new WeakMap();

    function readTextTracks(current) {
        const video = adapter.video();
        if (!video?.textTracks?.length) return;
        const tracks = [...video.textTracks].filter(track => track.kind === 'subtitles' || track.kind === 'captions');
        if (tracks.length === 0) return;

        const preferred = tracks.filter(trackInLanguage);
        const usable = preferred.length ? preferred : tracks;
        // A track that is off has no cues loaded; one is turned on out of sight. Once only: a player that turns it off
        // again (it loads its subtitles itself) is left to it.
        current.tracksTurnedOn ??= new WeakSet();
        if (tracks.every(track => track.mode === 'disabled') && !current.tracksTurnedOn.has(usable[0])) {
            current.tracksTurnedOn.add(usable[0]);
            usable[0].mode = 'hidden';
            note(current, `Turned on the "${usable[0].label || usable[0].language || 'subtitles'}" text track out of sight so it can be read`);
        }

        tracks.forEach((track, index) => {
            if (track.mode === 'disabled' || !track.cues?.length) return;
            // Players load several languages: only the viewer's are read, when the video has them.
            const inLanguage = !preferred.length || preferred.includes(track);
            if (inLanguage) {
                // Hidden tracks too: many players draw their captions themselves from the track's cues.
                maskCues(track);
                if (!watchedTracks.has(track)) {
                    watchedTracks.add(track);
                    // Cues an HLS player adds are blanked as they come on screen, without waiting for the next check.
                    track.addEventListener('cuechange', () => { if (track.mode !== 'disabled') maskCues(track, track.activeCues); });
                }
            }
            const id = `track:${index}:${track.language}:${track.label}`;
            // Read again when the lines change: more of them, or another language loaded into the same track.
            const signature = `${track.cues.length}|${cueOriginals.get(track.cues[0]) ?? track.cues[0].text}|${cueOriginals.get(track.cues[track.cues.length - 1]) ?? track.cues[track.cues.length - 1].text}`;
            if (current.trackCounts.get(id) === signature) return;
            current.trackCounts.set(id, signature);
            const cues = [...track.cues].map(cue => ({ start: cue.startTime, end: cue.endTime, text: cueOriginals.get(cue) ?? cue.text ?? '', words: null }));
            // A track in another language is set aside, also one labelled with the viewer's language whose lines plainly
            // aren't (some players load the language chosen into the one track they have).
            if (!inLanguage || otherLanguage(cues)) {
                current.foreign.set(id, cues);
                if (current.sources.delete(id)) scheduleRecompute(current);
                if (inLanguage) skippedLanguage(current, 'subtitle track');
                return;
            }
            current.foreign.delete(id);
            addSource(current, { id, name: track.label || track.language || 'Subtitles', via: 'Text track', cues, replace: true, alignable: false });
        });

        // Which language the player has on: the tracks it has turned on, when that changes (cinesrc loads the language
        // chosen into its one track; others turn another track on). A track the browser draws itself is on screen too.
        const idOf = track => `track:${tracks.indexOf(track)}:${track.language}:${track.label}`;
        const on = tracks.filter(track => track.mode !== 'disabled' && track.cues?.length);
        const onSignature = on.map(idOf).join('|');
        const showing = on.filter(track => track.mode === 'showing');
        if (onSignature !== current.tracksOn) {
            const first = current.tracksOn === undefined;
            current.tracksOn = onSignature;
            const ours = on.some(track => current.sources.has(idOf(track)));
            const foreign = on.filter(track => current.foreign.has(idOf(track)));
            if (!first && !ours && foreign.length) note(current, `The player switched its subtitles to ${foreign[0].label || foreign[0].language || 'another language'}`);
            // A change of the tracks turned on says what the player shows; with every track kept on, it says nothing.
            if (!first && !(tracks.length > 1 && on.length === tracks.length)) {
                if (ours) setShown(current, 'ours');
                else if (foreign.length) setShown(current, 'other');
            }
        }
        if (showing.length) setShown(current, showing.some(track => current.sources.has(idOf(track))) ? 'ours' : 'other');
    }

    // Whether a text track is in one of the viewer's caption languages, by its code or by its label (players often
    // give made-up codes such as x2 with a label that says English).
    const languageNames = new Map();
    function languageName(code) {
        if (!languageNames.has(code)) {
            let name = '';
            try { name = new Intl.DisplayNames(['en'], { type: 'language' }).of(code) || ''; } catch { /* not a code */ }
            languageNames.set(code, name.toLowerCase());
        }
        return languageNames.get(code);
    }

    // The viewer's first caption language by name, for messages ("English").
    function languageLabel() {
        const name = languageName(settings.languages[0] || 'en');
        return name ? name[0].toUpperCase() + name.slice(1) : settings.languages[0];
    }

    function trackInLanguage(track) {
        const code = (track.language || '').toLowerCase();
        const label = (track.label || '').toLowerCase();
        return settings.languages.some(language => code.startsWith(language) || (languageName(language) && label.includes(languageName(language))));
    }

    const watchedTracks = new WeakSet();
    const cueMasks = new WeakMap(); // cue -> { text, version }: what it was last set to, under which settings
    let maskVersion = 0;

    function maskCues(track, cues = track.cues) {
        const blank = filtering() && lists.mask;
        const version = `${maskVersion}|${!!blank}`;
        for (const cue of cues || []) {
            if (typeof cue.text !== 'string') continue;
            const done = cueMasks.get(cue);
            if (done && done.version === version && done.text === cue.text) continue;
            const original = cueOriginals.get(cue) ?? cue.text;
            cueOriginals.set(cue, original);
            const wanted = blank ? matcher.maskText(original, lists.all, lists.mask) : original;
            if (cue.text !== wanted) cue.text = wanted;
            cueMasks.set(cue, { text: wanted, version });
        }
    }

    // ---- Captions on screen ----
    // Captions on screen are found three ways: the player's own caption elements (known to the adapter), elements
    // named like captions that lie over the video, and, once there is a subtitle file, any text on the page that is
    // part of the line being spoken, whatever draws it. Only what changes is looked at, so busy pages stay quick.

    const CAPTION_HINT = /click for settings/i;
    const OBSERVE = { childList: true, subtree: true, characterData: true };
    const LOOSE_LIMIT = 400;
    const textOriginals = new WeakMap(); // text node -> { original, masked }
    const maskedNodes = new Set();       // WeakRefs to blanked text nodes, to put back when filtering stops
    const containers = new Set();        // caption elements on the page now
    const plausibleElements = new WeakSet();
    let observer = null;
    let observedRoot = null;
    let observedShadows = new WeakSet();
    let pending = [];                    // changes waiting for the next scan
    let relook = [];                     // text put back, to blank again with new settings
    let scanQueued = false;

    const captionSelector = () => adapter.likelyCaptions ? `${adapter.captionSelector}, ${adapter.likelyCaptions}` : adapter.captionSelector;

    function observeCaptions() {
        const root = safe(() => adapter.observeRoot(), null);
        if (root && root !== observedRoot) {
            observer?.disconnect();
            containers.clear();
            observedShadows = new WeakSet();
            observedRoot = root;
            observer = new MutationObserver(records => {
                pending.push(...records);
                queueScan();
            });
            observer.observe(root, OBSERVE);
            discover(root);
        }
        if (observer && adapter.likelyCaptions) {
            for (const shadow of hooks.shadowRoots()) {
                if (observedShadows.has(shadow)) continue;
                observedShadows.add(shadow);
                observer.observe(shadow, OBSERVE);
                discover(shadow);
            }
        }
    }

    function discover(root) {
        for (const element of root.querySelectorAll(captionSelector())) {
            const container = containerOf(element);
            if (container) addContainer(container);
        }
        queueScan();
    }

    function queueScan() {
        if (scanQueued) return;
        scanQueued = true;
        // Before the next paint, so a word is never drawn before it is blanked.
        queueMicrotask(() => {
            scanQueued = false;
            scanCaptions();
        });
    }

    // Whether an element is captions. The player's own caption elements are; one only named like captions must lie
    // over the video (or just below it) and hold nothing but text, so headings, buttons and menus are left alone.
    function plausible(element) {
        if (plausibleElements.has(element)) return true;
        if (element.matches(adapter.captionSelector)) {
            plausibleElements.add(element);
            return true;
        }
        if (!adapter.likelyCaptions || /^(H[1-6]|BUTTON|A|LABEL|svg|SVG|path|use|TITLE|OPTION)$/.test(element.tagName)) return false;
        if (element.closest('button, a[href], label, [role="button"], [role="menu"], [role="menuitem"], [role="dialog"], [role="listbox"], [role="slider"]')) return false;
        if (element.querySelector('video, audio, iframe, button, input, select, textarea, a[href], img, svg, canvas')) return false;
        if ((element.textContent || '').length > 400) return false;

        const video = adapter.video();
        if (!video) return false;
        const box = element.getBoundingClientRect();
        const area = video.getBoundingClientRect();
        if (box.width === 0 && box.height === 0) return false; // empty or hidden for now: looked at again when it changes
        const over = box.right > area.left && box.left < area.right && box.bottom > area.top && box.top < area.bottom + area.height * 0.25;
        if (over) plausibleElements.add(element);
        return over;
    }

    // The outermost caption element around a node, or null.
    function containerOf(element) {
        const selector = captionSelector();
        let found = null;
        for (let current = element?.closest?.(selector); current; current = current.parentElement?.closest(selector)) {
            if (plausible(current)) found = current;
        }
        return found;
    }

    function addContainer(container) {
        if (containers.has(container)) return;
        for (const other of containers) {
            if (other.contains(container)) return;
            if (container.contains(other)) containers.delete(other);
        }
        containers.add(container);
    }

    function rescanCaptions() {
        // Put every blanked word back, then blank again with the current settings.
        maskVersion++;
        for (const ref of maskedNodes) {
            const node = ref.deref();
            const known = node && textOriginals.get(node);
            if (known && node.nodeValue === known.masked) {
                node.nodeValue = known.original;
                relook.push(node);
            }
            maskedNodes.delete(ref);
        }
        if (adapter.readsTextTracks) {
            for (const track of adapter.video()?.textTracks || []) if (track.mode !== 'disabled') maskCues(track);
        }
        queueScan();
    }

    // Blanks listed words in one text node, remembering what it said. Returns what it says unblanked.
    function maskNode(node, blank) {
        const current = node.nodeValue;
        const known = textOriginals.get(node);
        const original = known && known.masked === current ? known.original : current;
        const wanted = blank ? matcher.maskText(original, lists.all, lists.mask) : original;
        if (wanted !== current) {
            node.nodeValue = wanted;
            if (wanted !== original) {
                textOriginals.set(node, { original, masked: wanted });
                maskedNodes.add(new WeakRef(node));
            }
        }
        return original;
    }

    // Text nodes that changed, and caption elements among them.
    function sortChanges(records, loose) {
        const selector = captionSelector();
        const take = node => { if (loose.length < LOOSE_LIMIT) loose.push(node); };

        for (const record of records) {
            const target = record.target;
            const element = target.nodeType === Node.ELEMENT_NODE ? target : target.parentElement;
            const container = element && containerOf(element);
            if (container) {
                addContainer(container);
                continue;
            }
            if (record.type === 'characterData') {
                take(target);
                continue;
            }
            for (const added of record.addedNodes) {
                if (added.nodeType === Node.TEXT_NODE) {
                    take(added);
                } else if (added.nodeType === Node.ELEMENT_NODE) {
                    let found = false;
                    for (const candidate of [added, ...added.querySelectorAll(selector)]) {
                        if (candidate !== added || added.matches(selector)) {
                            const inner = containerOf(candidate);
                            if (inner) {
                                addContainer(inner);
                                found = true;
                            }
                        }
                    }
                    if (!found && adapter.matchesCueText && loose.length < LOOSE_LIMIT) {
                        const walker = document.createTreeWalker(added, NodeFilter.SHOW_TEXT);
                        for (let node = walker.nextNode(); node && loose.length < LOOSE_LIMIT; node = walker.nextNode()) loose.push(node);
                    }
                }
            }
        }
    }

    // The lines being spoken now (and just before and after), as letters only, from the subtitle files.
    const cueKeys = new WeakMap();
    const spokenKeys = () => session
        ? keysAt([...session.sources.values()].map(source => ({ cues: source.cues, offset: source.alignable ? session.offset : 0 })))
        : [];

    // The lines of these cue lists ({ cues, offset }) due now, give or take two seconds.
    function keysAt(lists) {
        const time = adapter.video()?.currentTime;
        if (!session || !Number.isFinite(time)) return [];
        const keys = [];
        for (const { cues, offset } of lists) {
            for (const cue of cues) {
                if (cue.start + offset > time + 2) break;
                if (cue.end + offset < time - 2) continue;
                let key = cueKeys.get(cue);
                if (key === undefined) cueKeys.set(cue, key = lineKey(cue.text));
                if (key) keys.push(key);
            }
        }
        return keys;
    }

    // Text outside any caption element is blanked when it is part of the line being spoken: captions drawn by a
    // player nobody knows the names of.
    function maskLoose(nodes, blank) {
        if (!adapter.matchesCueText || !session?.sources.size) return;
        const keys = blank ? spokenKeys() : [];
        for (const node of nodes) {
            if (node.nodeType !== Node.TEXT_NODE || !node.isConnected) continue;
            const known = textOriginals.get(node);
            if (!blank) {
                if (known) maskNode(node, false);
                continue;
            }
            if (keys.length === 0) return;
            const parent = node.parentElement;
            if (!parent || /^(SCRIPT|STYLE|NOSCRIPT|TEXTAREA|TITLE|OPTION)$/.test(parent.tagName) || parent.isContentEditable) continue;
            const original = known && known.masked === node.nodeValue ? known.original : node.nodeValue;
            if (!original || original.length > 400 || matcher.find(original, lists.all).length === 0) continue;
            const key = lineKey(original);
            if (key.length >= 2 && keys.some(spoken => spoken.includes(key))) maskNode(node, blank);
        }
    }

    function scanCaptions() {
        const records = pending;
        const again = relook;
        pending = [];
        relook = [];
        if (!observedRoot || !settings) return;
        const blank = filtering() && lists.mask;
        const loose = [...again];
        sortChanges(records, loose);

        const lines = [];
        for (const container of containers) {
            if (!container.isConnected) {
                containers.delete(container);
                continue;
            }
            const parts = [];
            const walker = document.createTreeWalker(container, NodeFilter.SHOW_TEXT);
            for (let node = walker.nextNode(); node; node = walker.nextNode()) parts.push(maskNode(node, blank));
            const line = parts.join(' ').replace(/\s+/g, ' ').trim();
            if (line && !CAPTION_HINT.test(line)) lines.push(line);
        }
        maskLoose(loose, blank);
        if (settings.pauseForOtherSubtitles) {
            const original = node => { const known = textOriginals.get(node); return known && known.masked === node.nodeValue ? known.original : node.nodeValue; };
            watchShownLanguage([...lines, ...loose.filter(node => node.nodeType === Node.TEXT_NODE && node.isConnected).map(original)]);
        }

        if (session && filtering()) {
            if (liveActive()) checkLive(lines);
            if (adapter.alignToScreen) alignToScreen(lines);
        }
    }

    // ---- Following captions as they appear (when no caption file could be read) ----
    // A word in captions shown word by word (YouTube's auto-generated ones) appears as it is said, so it is muted
    // from then on; a caption line shown whole is muted for as long as it is on screen.

    const live = { seen: new Map(), hold: false, until: 0 };
    const LIVE_FORGET = 6;

    const liveActive = () => !!session && session.sources.size === 0 && session.state !== 'none';

    function resetLive() {
        live.seen = new Map();
        live.hold = false;
        live.until = 0;
    }

    function checkLive(lines) {
        const video = adapter.video();
        if (!video) return;
        const wordByWord = safe(() => adapter.wordByWord?.(), false);
        const now = video.currentTime;
        const liveWord = settings.liveWordMs / 1000;
        let hold = false;

        for (const line of lines) {
            const found = [...matcher.find(line, lists.all), ...(lists.maskedWords ? matcher.findMasked(line) : [])];
            for (const match of found) {
                const wholeLine = !wordByWord || lists.full.includes(match.entry);
                if (wholeLine) hold = true;

                // The same word after the same words is the same occurrence, however often the player redraws it.
                const before = matcher.upper(line.slice(Math.max(0, match.index - 24), match.index)).replace(/[^\p{L}\p{N}]/gu, '');
                const key = `${match.entry}|${before}`;
                const lastSeen = live.seen.get(key);
                live.seen.set(key, now);
                if (lastSeen !== undefined && Math.abs(now - lastSeen) < LIVE_FORGET) continue;

                if (!wholeLine) live.until = Math.max(live.until, now + liveWord);
                const hit = {
                    word: match.entry, mute: wholeLine ? 'live-line' : 'live-word', at: now,
                    start: now, end: wholeLine ? null : now + liveWord, line, index: match.index, length: match.length,
                };
                session.liveHits.push(hit);
                session.version++;
                count(hit);
            }
        }

        for (const [key, time] of live.seen) if (Math.abs(now - time) > LIVE_FORGET * 5) live.seen.delete(key);
        if (live.hold && !hold) live.until = Math.max(live.until, now + lists.pads.lineAfter);
        live.hold = hold;
        tick();
    }

    // ---- Lining a downloaded caption file up with the screen ----
    // Some players (HLS on Disney+, Max) give subtitle files on a different clock. When the same line shows on screen,
    // the difference is measured; once a few agree, the file is shifted to match.

    const lineKey = text => matcher.upper(matcher.formatCue(text)).replace(/[^\p{L}\p{N}]/gu, '');

    function alignToScreen(lines) {
        const sources = [...session.sources.values()].filter(source => source.alignable);
        if (sources.length === 0) return;
        const now = adapter.video()?.currentTime;
        if (!Number.isFinite(now)) return;

        for (const line of lines) {
            const key = lineKey(line);
            if (key.length < 10 || session.alignSeen.has(key)) continue;
            session.alignSeen.add(key);
            const matches = sources.flatMap(source => source.cues.filter(cue => lineKey(cue.text) === key));
            if (matches.length !== 1) continue;
            session.alignment.push(now - matches[0].start);
        }

        const recent = session.alignment.slice(-7);
        if (recent.length < 3) return;
        const sorted = [...recent].sort((a, b) => a - b);
        const median = sorted[Math.floor(sorted.length / 2)];
        const agreeing = recent.filter(value => Math.abs(value - median) < 0.4).length;
        if (agreeing >= 3 && Math.abs(median - session.offset) > 0.3) {
            session.offset = median;
            note(session, `Caption file lined up with the screen (${median >= 0 ? '+' : ''}${median.toFixed(2)} s)`);
            recompute(session);
        }
    }

    // ---- Muting ----
    // The sound fades out so it reaches silence as a mute range starts, and fades back in after it ends.
    // The fade moves the video's volume; once silent the video is muted and its volume put back, so the
    // viewer's own volume is never changed.

    const fade = { timer: null, base: 1 };

    // 'now' inside a mute, 'soon' within the fade before one, otherwise false.
    function shouldMute(video) {
        if (!session || !filtering() || pausedForSubtitles() || safe(() => adapter.isAd(), false)) return false;
        const time = video.currentTime;
        if (H.rangeAt(session.ranges, time)) return 'now';
        if (liveActive() && (live.hold || time < live.until)) return 'now';
        const lead = fadeLead(video);
        return lead > 0 && H.rangeAt(session.ranges, time + lead) ? 'soon' : false;
    }

    // How far ahead of a mute its fade starts, in video time: the fade, plus 10 ms for timers that run late.
    const fadeLead = video => settings.fadeMs > 0 ? (settings.fadeMs + 10) / 1000 * (video.playbackRate || 1) : 0;

    function stopFade() {
        clearInterval(fade.timer);
        fade.timer = null;
    }

    // Equal-power curve: it sounds even, where a straight line seems to drop late.
    function ramp(video, from, to, done) {
        stopFade();
        const started = performance.now();
        const step = () => {
            const progress = Math.min(1, (performance.now() - started) / settings.fadeMs);
            const curve = to < from ? Math.cos(progress * Math.PI / 2) : Math.sin(progress * Math.PI / 2);
            const level = to < from ? to + (from - to) * curve : from + (to - from) * curve;
            video.volume = Math.min(1, Math.max(0, level));
            if (progress >= 1) {
                stopFade();
                done();
            }
        };
        fade.timer = setInterval(step, 4);
        step();
    }

    function muteVideo(video, instant) {
        mutedByUs = true;
        mutedVideo = video;
        fade.base = video.volume;
        if (instant || settings.fadeMs <= 0 || video.paused) {
            stopFade();
            adapter.mute(video, true);
        } else {
            ramp(video, fade.base, 0, () => {
                adapter.mute(video, true);
                video.volume = fade.base;
            });
        }
        post(true);
    }

    function unmuteVideo(video) {
        mutedByUs = false;
        mutedVideo = null;
        const base = fade.base;
        const from = video.muted ? 0 : video.volume;
        stopFade();
        adapter.mute(video, false);
        if (settings?.fadeMs > 0 && !video.paused) {
            // YouTube puts its own volume back on unmute; the fade starts from silence regardless.
            video.volume = from;
            ramp(video, from, base, () => { video.volume = base; });
        } else {
            video.volume = base;
        }
        post(true);
    }

    // The checks every frame and every 50 ms can come too late for a 30 ms fade, so a timer is set for the moment the
    // next fade must start (or the current mute ends).
    let boundaryTimer = null;

    function scheduleBoundary(video) {
        clearTimeout(boundaryTimer);
        if (!session?.ranges.length || video.paused || !filtering()) return;
        const rate = video.playbackRate || 1;
        const lead = fadeLead(video);
        const time = video.currentTime;
        const inside = H.rangeAt(session.ranges, time);
        const next = inside ? inside.end : session.ranges.find(range => range.start - lead > time)?.start - lead;
        if (!Number.isFinite(next)) return;
        const wait = (next - time) / rate * 1000;
        if (wait < 1000) boundaryTimer = setTimeout(tick, Math.max(0, wait));
    }

    function tick() {
        const video = adapter.video();
        if (mutedVideo && mutedVideo !== video) release();
        if (!video || !settings) return;
        scheduleBoundary(video);

        const wanted = shouldMute(video);
        if (wanted) {
            // Already muted by the viewer: left to them. Unmuted by someone while held: muted again.
            if (!mutedByUs && !video.muted) {
                muteVideo(video, wanted === 'now');
            } else if (mutedByUs && !video.muted && !fade.timer) {
                fade.base = video.volume;
                adapter.mute(video, true);
            }
            if (wanted === 'now') countPassing(video.currentTime);
        } else if (mutedByUs) {
            unmuteVideo(video);
        }
    }

    function release() {
        stopFade();
        if (mutedByUs && mutedVideo) {
            const video = mutedVideo;
            safe(() => adapter.mute(video, false));
            safe(() => { video.volume = fade.base; });
        }
        mutedByUs = false;
        mutedVideo = null;
    }

    // Statistics: each detection counts once per video, when playback mutes it, however often it is worked out again.
    const countKey = hit => `${hit.word}|${Math.round(hit.at)}`;

    function countPassing(time) {
        for (const hit of session.hits) {
            if (hit.dropped || time < hit.start || time >= hit.end) continue;
            count(hit);
        }
    }

    function count(hit) {
        if (!session || session.counted.has(countKey(hit)) || !filtering() || pausedForSubtitles()) return;
        session.counted.add(countKey(hit));
        toBridge('muted', { word: hit.word, site: adapter.name, key: session.key, title: session.title, url: location.href });
    }

    // ---- Marks on the progress bar ----

    let markerSignature = '';

    function drawMarkers(force) {
        const bar = settings?.markers && filtering() && !pausedForSubtitles() && session ? safe(() => adapter.markers(), null) : null;
        const duration = adapter.video()?.duration;
        const layer = document.querySelector('.cg-markers');

        if (!bar || !(duration > 0) || session.ranges.length === 0) {
            layer?.remove();
            markerSignature = '';
            return;
        }

        const signature = `${session.key}|${duration}|${session.ranges.length}|${session.ranges[0].start}|${session.ranges.at(-1).end}`;
        if (!force && signature === markerSignature && layer && bar.contains(layer)) return;
        markerSignature = signature;

        const target = layer && bar.contains(layer) ? layer : document.createElement('div');
        target.className = 'cg-markers';
        target.replaceChildren(...session.ranges.map(range => {
            const mark = document.createElement('div');
            mark.className = 'cg-marker';
            mark.style.left = `${(range.start / duration) * 100}%`;
            mark.style.width = `max(2px, ${((range.end - range.start) / duration) * 100}%)`;
            return mark;
        }));
        if (target !== layer) {
            layer?.remove();
            bar.appendChild(target);
        }
    }

    // ---- State for the popup ----

    let postTimer = null;
    let postedVersion = -1;
    let lastFullPost = 0;
    let idlePosted = '';

    // Changes are gathered for a quarter of a second; a mute starting or ending goes at once, so the popup keeps up.
    function post(now = false) {
        if (now) {
            clearTimeout(postTimer);
            postTimer = null;
            sendState();
            return;
        }
        if (postTimer) return;
        postTimer = setTimeout(() => {
            postTimer = null;
            sendState();
        }, 250);
    }

    function sendState() {
        const video = adapter.video();
        if (!session) {
            // Without a video only changes go, so the many pages and frames with none stay quiet.
            const idle = JSON.stringify({ hasMedia: false, site: adapter.name, siteId: adapter.id, host, topHost, filtering: filtering() });
            if (idle !== idlePosted) toBridge('state', JSON.parse(idlePosted = idle));
            return;
        }

        // Subtitles in another language on screen (with the setting for dubbed sound): the detections are those of
        // these subtitles, so none. The file in the viewer's language is kept for when they switch back.
        const otherSubtitles = pausedForSubtitles();
        const ranges = otherSubtitles ? [] : session.ranges;
        const state = {
            hasMedia: true, site: adapter.name, siteId: adapter.id, host, topHost, key: session.key, url: location.href,
            title: session.title, channel: session.channel, allowed: session.allowed,
            state: session.state, filtering: filtering(), muting: mutedByUs, pausedForSubtitles: otherSubtitles,
            message: otherSubtitles
                ? `The subtitles on screen are in another language, so nothing is muted. Switch back to ${languageLabel()} subtitles to filter again.`
                : session.message,
            time: video?.currentTime ?? null, duration: Number.isFinite(video?.duration) ? video.duration : null,
            ranges: ranges.length, mutedSeconds: ranges.reduce((sum, range) => sum + range.end - range.start, 0),
            siteCaptionsHidden: document.documentElement.classList.contains('cg-hide-site-captions'),
            version: session.version,
        };

        // The detections and log only go when they changed, and now and then in case the popup missed them.
        if (session.version !== postedVersion || Date.now() - lastFullPost > 10000) {
            postedVersion = session.version;
            lastFullPost = Date.now();
            const all = otherSubtitles ? [] : [...session.hits, ...session.liveHits].sort((a, b) => a.at - b.at);
            state.detections = all.filter(hit => !hit.dropped).length;
            state.hits = all.slice(0, 600).map(compactHit);
            state.sources = [...session.sources.values()].map(source => ({ name: source.name, via: source.via, lines: source.cues.length }));
            state.log = session.log.slice(-100);
            state.offset = session.offset;
        }
        toBridge('state', state);
    }

    // A detection for the popup: the caption line is cut down to the words around the match.
    function compactHit(hit) {
        let line = hit.line || '';
        let index = hit.index ?? 0;
        if (line.length > 90) {
            const from = Math.max(0, index - 40);
            const to = Math.min(line.length, index + (hit.length || 0) + 40);
            line = (from > 0 ? '…' : '') + line.slice(from, to) + (to < line.length ? '…' : '');
            index = index - from + (from > 0 ? 1 : 0);
        }
        return { word: hit.word, mute: hit.mute, at: hit.at, start: hit.start, end: hit.end, dropped: !!hit.dropped, line, index, length: hit.length || 0 };
    }

    // ---- Start ----

    const safe = (work, fallback) => {
        try { return work(); } catch { return fallback; }
    };

    const STYLES = `
        .cg-hidden-captions .ytp-caption-window-container { visibility: hidden !important; }
        /* Hidden captions: CC is dimmed and struck through, in place of its red "on" line. The player's own rules are
           more specific, hence !important. */
        .cg-hidden-captions .ytp-chrome-controls .ytp-subtitles-button { opacity: 0.6 !important; position: relative !important; }
        .cg-hidden-captions .ytp-chrome-controls .ytp-subtitles-button::after {
            content: '' !important; display: block !important; position: absolute !important; inset: auto !important;
            left: 50% !important; top: 50% !important; width: 26px !important; height: 2px !important; margin: 0 !important;
            border-radius: 1px !important; background: #fff !important; box-shadow: 0 0 0 1px rgba(0, 0, 0, 0.5) !important;
            transform: translate(-50%, -50%) rotate(-45deg) !important; pointer-events: none; z-index: 1;
        }
        html.cg-hide-site-captions .player-timedtext { visibility: hidden !important; }
        .cg-markers { position: absolute; inset: 0; pointer-events: none; z-index: 33; }
        .cg-marker { position: absolute; top: 0; bottom: 0; background: #f5d367; opacity: 0.9; border-radius: 1px; }`;

    function addStyles() {
        try {
            const sheet = new CSSStyleSheet();
            sheet.replaceSync(STYLES);
            document.adoptedStyleSheets = [...document.adoptedStyleSheets, sheet];
        } catch {
            const style = document.createElement('style');
            style.textContent = STYLES;
            (document.head || document.documentElement).appendChild(style);
        }
    }

    // The scripts run on every page, so at first they only watch for a video.
    function start() {
        started = true;
        for (const type of ['loadedmetadata', 'loadstart', 'durationchange', 'play'])
            document.addEventListener(type, checkMedia, true);
        document.addEventListener('yt-navigate-finish', checkMedia);
        setInterval(checkMedia, 1000);
        checkMedia();

        window.addEventListener('pagehide', release);
    }

    // The rest starts with the first video.
    let running = false;

    function run() {
        if (running) return;
        running = true;
        addStyles();

        // Timers alone run late, so the time is also checked every frame and on every playback event.
        setInterval(tick, 50);
        const frame = () => { tick(); requestAnimationFrame(frame); };
        requestAnimationFrame(frame);
        for (const type of ['timeupdate', 'seeked', 'play', 'playing', 'pause', 'ratechange', 'volumechange'])
            document.addEventListener(type, tick, true);
        document.addEventListener('seeking', () => { resetLive(); tick(); }, true);
    }

    const api = {
        settings: () => settings,
        session: () => session,
        filtering,
        isCurrent,
        note,
        addSource,
        saveSettings,
        setNoCaptions(current) {
            if (isCurrent(current)) setState(current, 'none', 'This video has no captions, so it is not filtered');
        },
    };

    CG.engine = { api };
    toBridge('hello');
})();
