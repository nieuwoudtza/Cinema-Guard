// The filter running in the page: finds the video and its captions, works out when to mute, mutes, and blanks
// listed words in captions on screen. Settings come from the bridge script; state goes back to it for the popup.
(() => {
    const CG = (globalThis.__cinemaGuard ??= {});
    if (CG.engine) return;

    const { matcher, hits: H, settings: S, hooks, sites } = CG;
    const TAG = '__cinemaGuard';
    const adapter = sites.pick();
    const host = location.hostname.replace(/^www\./, '');

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
        settings = S.normalise(payload?.settings);
        allowChannels = Array.isArray(payload?.allowChannels) ? payload.allowChannels : [];
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
        if (settings.pausedSites.includes(host)) return 'site';
        if (current?.channel && allowChannels.some(channel => channel.id === current.channel.id)) return 'channel';
        return null;
    }

    const filtering = () => !!settings && settings.enabled && !session?.allowed && !settings.pausedSites.includes(host);

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
                    setState(current, 'live', 'No caption file yet: muting follows the captions on screen');
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
        const languages = settings.languages;
        if (capture.language && !languages.some(language => capture.language.toLowerCase().startsWith(language))) {
            if (!current.skippedLanguages?.has(capture.language)) {
                (current.skippedLanguages ??= new Set()).add(capture.language);
                note(current, `Skipped a ${capture.language} subtitle file (not one of your caption languages)`);
            }
            return;
        }
        // Segments of one track share their folder; a whole file is its own source.
        const url = new URL(capture.url, location.href);
        const folder = url.origin + url.pathname.replace(/[^/]*$/, '');
        const id = capture.format === 'vtt' && /seg|\d+\.vtt$/i.test(url.pathname) ? `dl:${folder}` : `dl:${url.origin}${url.pathname}`;
        const name = `Subtitles (${capture.format}${capture.language ? `, ${capture.language}` : ''})`;
        addSource(current, { id, name, via: 'Downloaded by the player', cues: capture.cues, alignable: adapter.alignToScreen });
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

        const languages = settings.languages;
        const preferred = tracks.filter(track => languages.some(language => (track.language || '').toLowerCase().startsWith(language)));
        const usable = preferred.length ? preferred : tracks;
        // A track that is off has no cues loaded; one is turned on out of sight.
        if (usable.every(track => track.mode === 'disabled') && tracks.every(track => track.mode === 'disabled')) {
            usable[0].mode = 'hidden';
            note(current, `Turned on the "${usable[0].label || usable[0].language || 'subtitles'}" text track out of sight so it can be read`);
        }

        tracks.forEach((track, index) => {
            if (track.mode === 'disabled' || !track.cues?.length) return;
            if (track.mode === 'showing') maskCues(track);
            const id = `track:${index}:${track.language}:${track.label}`;
            if (current.trackCounts.get(id) === track.cues.length) return;
            current.trackCounts.set(id, track.cues.length);
            const cues = [...track.cues].map(cue => ({ start: cue.startTime, end: cue.endTime, text: cueOriginals.get(cue) ?? cue.text ?? '', words: null }));
            addSource(current, { id, name: track.label || track.language || 'Subtitles', via: 'Text track', cues, replace: true, alignable: false });
        });
    }

    function maskCues(track) {
        const blank = filtering() && lists.mask;
        for (const cue of track.cues || []) {
            if (typeof cue.text !== 'string') continue;
            const original = cueOriginals.get(cue) ?? cue.text;
            cueOriginals.set(cue, original);
            const wanted = blank ? matcher.maskText(original, lists.all, lists.mask) : original;
            if (cue.text !== wanted) cue.text = wanted;
        }
    }

    // ---- Captions on screen ----

    const CAPTION_HINT = /click for settings/i;
    const textOriginals = new WeakMap(); // text node -> { original, masked }
    const maskedNodes = new Set();       // WeakRefs to blanked text nodes, to put back when filtering stops
    let observer = null;
    let observedRoot = null;
    let scanQueued = false;

    function observeCaptions() {
        const root = safe(() => adapter.observeRoot(), null);
        if (!root || root === observedRoot) return;
        observer?.disconnect();
        observedRoot = root;
        observer = new MutationObserver(queueScan);
        observer.observe(root, { childList: true, subtree: true, characterData: true });
        queueScan();
    }

    function queueScan() {
        if (scanQueued) return;
        scanQueued = true;
        queueMicrotask(() => {
            scanQueued = false;
            scanCaptions();
        });
    }

    function rescanCaptions() {
        // Put every blanked word back, then blank again with the current settings.
        for (const ref of maskedNodes) {
            const node = ref.deref();
            const known = node && textOriginals.get(node);
            if (known && node.nodeValue === known.masked) node.nodeValue = known.original;
            maskedNodes.delete(ref);
        }
        if (adapter.readsTextTracks) {
            for (const track of adapter.video()?.textTracks || []) if (track.mode === 'showing') maskCues(track);
        }
        queueScan();
    }

    function scanCaptions() {
        if (!observedRoot || !settings) return;
        const blank = filtering() && lists.mask;
        const lines = [];

        for (const container of observedRoot.querySelectorAll(adapter.captionSelector)) {
            if (container.parentElement?.closest(adapter.captionSelector)) continue;
            const parts = [];
            const walker = document.createTreeWalker(container, NodeFilter.SHOW_TEXT);
            for (let node = walker.nextNode(); node; node = walker.nextNode()) {
                const current = node.nodeValue;
                const known = textOriginals.get(node);
                const original = known && known.masked === current ? known.original : current;
                parts.push(original);

                const wanted = blank ? matcher.maskText(original, lists.all, lists.mask) : original;
                if (wanted !== current) {
                    node.nodeValue = wanted;
                    if (wanted !== original) {
                        textOriginals.set(node, { original, masked: wanted });
                        maskedNodes.add(new WeakRef(node));
                    }
                }
            }
            const line = parts.join(' ').replace(/\s+/g, ' ').trim();
            if (line && !CAPTION_HINT.test(line)) lines.push(line);
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
        if (!session || !filtering() || safe(() => adapter.isAd(), false)) return false;
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
        post();
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
        post();
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
        if (!session || session.counted.has(countKey(hit)) || !filtering()) return;
        session.counted.add(countKey(hit));
        toBridge('muted', { word: hit.word, site: adapter.name, key: session.key, title: session.title, url: location.href });
    }

    // ---- Marks on the progress bar ----

    let markerSignature = '';

    function drawMarkers(force) {
        const bar = settings?.markers && filtering() && session ? safe(() => adapter.markers(), null) : null;
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

    function post() {
        if (postTimer) return;
        postTimer = setTimeout(() => {
            postTimer = null;
            const video = adapter.video();
            if (!session) {
                toBridge('state', { hasMedia: false, site: adapter.name, siteId: adapter.id, host, filtering: filtering() });
                return;
            }

            const state = {
                hasMedia: true, site: adapter.name, siteId: adapter.id, host, key: session.key, url: location.href,
                title: session.title, channel: session.channel, allowed: session.allowed,
                state: session.state, message: session.message, filtering: filtering(), muting: mutedByUs,
                time: video?.currentTime ?? null, duration: Number.isFinite(video?.duration) ? video.duration : null,
                ranges: session.ranges.length, mutedSeconds: session.ranges.reduce((sum, range) => sum + range.end - range.start, 0),
                siteCaptionsHidden: document.documentElement.classList.contains('cg-hide-site-captions'),
                version: session.version,
            };

            // The detections and log only go when they changed, and now and then in case the popup missed them.
            if (session.version !== postedVersion || Date.now() - lastFullPost > 10000) {
                postedVersion = session.version;
                lastFullPost = Date.now();
                const all = [...session.hits, ...session.liveHits].sort((a, b) => a.at - b.at);
                state.detections = all.filter(hit => !hit.dropped).length;
                state.hits = all.slice(0, 600).map(compactHit);
                state.sources = [...session.sources.values()].map(source => ({ name: source.name, via: source.via, lines: source.cues.length }));
                state.log = session.log.slice(-100);
                state.offset = session.offset;
            }
            toBridge('state', state);
        }, 250);
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
        .cg-hidden-captions .ytp-subtitles-button[aria-pressed="true"]::after { display: none !important; }
        .cg-hidden-captions .ytp-subtitles-button { opacity: 0.6; }
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

    function start() {
        started = true;
        addStyles();

        // Timers alone run late, so the time is also checked every frame and on every playback event.
        setInterval(tick, 50);
        const frame = () => { tick(); requestAnimationFrame(frame); };
        requestAnimationFrame(frame);
        for (const type of ['timeupdate', 'seeked', 'play', 'playing', 'pause', 'ratechange', 'volumechange'])
            document.addEventListener(type, tick, true);
        document.addEventListener('seeking', () => { resetLive(); tick(); }, true);

        for (const type of ['loadedmetadata', 'loadstart', 'durationchange'])
            document.addEventListener(type, checkMedia, true);
        document.addEventListener('yt-navigate-finish', checkMedia);
        setInterval(checkMedia, 1000);
        checkMedia();

        window.addEventListener('pagehide', release);
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
