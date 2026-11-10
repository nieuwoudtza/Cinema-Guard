// Site adapters: where each site keeps its video, its captions on screen and its caption files.
// The engine asks the adapter for the current site; anything not listed uses the generic adapter.
(() => {
    const CG = (globalThis.__cinemaGuard ??= {});
    const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));

    // Caption elements of common players, for blanking and for reading captions as they appear.
    const COMMON_CAPTIONS = [
        '.player-timedtext-text-container',          // Netflix
        '.atvwebplayersdk-captions-text',             // Prime Video
        '.dss-subtitle-renderer-cue',                 // Disney+
        '[data-testid="CueBoxContainer"]',            // Max
        '.ClosedCaption__outband', '.closed-caption', // Hulu
        '.vjs-text-track-display',                    // Video.js
        '.jw-text-track-container',                   // JW Player
        '.shaka-text-container',                      // Shaka Player
        '.plyr__captions',                            // Plyr
        '.bmpui-ui-subtitle-overlay',                 // Bitmovin
        '.mejs__captions-layer', '.fp-captions',      // MediaElement, Flowplayer
    ].join(', ');

    // The video being watched: the one playing, else the biggest.
    function mainVideo(root = document) {
        const videos = [...root.querySelectorAll('video')];
        if (videos.length <= 1) return videos[0] || null;
        const area = video => { const box = video.getBoundingClientRect(); return box.width * box.height; };
        const playing = videos.filter(video => !video.paused && video.readyState > 2);
        return (playing.length ? playing : videos).sort((a, b) => area(b) - area(a))[0];
    }

    const videoIds = new WeakMap();
    let nextVideoId = 1;
    const videoId = video => {
        if (!videoIds.has(video)) videoIds.set(video, nextVideoId++);
        return videoIds.get(video);
    };

    // ---- Generic ----

    const generic = {
        id: 'generic',
        name: location.hostname.replace(/^www\./, ''),
        matches: () => true,
        captionSelector: COMMON_CAPTIONS,
        readsTextTracks: true,
        acceptsDownloads: true,
        alignToScreen: true,
        observeRoot: () => document.documentElement,
        video: () => mainVideo(),
        mediaKey() {
            const video = this.video();
            if (!video || !(video.duration > 0 || video.readyState > 0)) return null;
            return `${location.origin}${location.pathname}#${videoId(video)}`;
        },
        info: () => ({ title: document.title.replace(/\s*[-|]\s*[^-|]*$/, '') || document.title, channel: null }),
        isAd: () => false,
        mute(video, on) { video.muted = on; },
        seek(video, seconds) { video.currentTime = Math.max(0, seconds); },
        async load() { },
        tick() { },
        markers: () => null,
    };

    // ---- YouTube ----

    // YouTube's web player needs a proof-of-origin token for caption files; its app clients do not.
    const YOUTUBE_CLIENTS = [
        { clientName: 'ANDROID_VR', clientVersion: '1.65.10', androidSdkVersion: 32, osName: 'Android', osVersion: '12L' },
        { clientName: 'IOS', clientVersion: '20.10.4', deviceMake: 'Apple', deviceModel: 'iPhone16,2', osName: 'iPhone', osVersion: '18.3.2.22D82' },
        { clientName: 'ANDROID', clientVersion: '20.10.38', androidSdkVersion: 30, osName: 'Android', osVersion: '11' },
    ];

    const youtube = {
        ...generic,
        id: 'youtube',
        name: 'YouTube',
        matches: () => /(^|\.)youtube(-nocookie)?\.com$/.test(location.hostname),
        captionSelector: '.ytp-caption-segment',
        readsTextTracks: false,
        acceptsDownloads: false,
        alignToScreen: false,
        shorts: () => location.pathname.startsWith('/shorts/'),
        player() {
            if (this.shorts()) return document.querySelector('#shorts-player') || document.getElementById('movie_player');
            return document.getElementById('movie_player');
        },
        observeRoot() { return this.player(); },
        video() {
            const player = this.player();
            return player?.querySelector('video.html5-main-video') || player?.querySelector('video') || null;
        },
        mediaKey() {
            const player = this.player();
            if (!player || this.isAd()) return null;
            return player.getVideoData?.()?.video_id || null;
        },
        info() {
            const player = this.player();
            const details = player?.getPlayerResponse?.()?.videoDetails;
            const data = player?.getVideoData?.();
            return {
                title: details?.title || data?.title || document.title.replace(/ - YouTube$/, ''),
                channel: details?.channelId ? { id: details.channelId, name: details.author || data?.author || '' } : null,
            };
        },
        isAd() { return !!this.player()?.classList.contains('ad-showing'); },
        // Auto-generated captions show each word as it is said.
        wordByWord() {
            try { return this.player()?.getOption?.('captions', 'track')?.kind === 'asr'; } catch { return false; }
        },
        mute(video, on) {
            const player = this.player();
            if (on) player?.mute?.(); else player?.unMute?.();
            video.muted = on;
        },
        seek(video, seconds) {
            const player = this.player();
            if (player?.seekTo) player.seekTo(Math.max(0, seconds), true);
            else video.currentTime = Math.max(0, seconds);
        },

        async load(session, engine) {
            const key = session.key;
            const config = window.ytcfg?.get?.('INNERTUBE_CONTEXT');
            const visitorData = config?.client?.visitorData || '';
            const languages = engine.settings().languages;
            let tracks = null;

            for (const client of YOUTUBE_CLIENTS) {
                try {
                    const response = await CG.hooks.originalFetch('/youtubei/v1/player?prettyPrint=false', {
                        method: 'POST',
                        credentials: 'omit',
                        headers: { 'Content-Type': 'application/json', 'X-Goog-Visitor-Id': visitorData },
                        body: JSON.stringify({ context: { client: { ...client, hl: 'en', gl: 'US', visitorData } }, videoId: key, contentCheckOk: true, racyCheckOk: true }),
                    });
                    const data = await response.json();
                    if (data?.videoDetails?.channelId && !session.channel) session.channel = { id: data.videoDetails.channelId, name: data.videoDetails.author || '' };
                    if (data?.playabilityStatus?.status !== 'OK') {
                        engine.note(session, `${client.clientName}: ${data?.playabilityStatus?.status || `HTTP ${response.status}`}`);
                        continue;
                    }
                    tracks = data?.captions?.playerCaptionsTracklistRenderer?.captionTracks || [];
                    engine.note(session, `Caption list from YouTube's ${client.clientName} client: ${tracks.length ? tracks.map(trackName).join(', ') : 'none'}`);
                    break;
                } catch (error) {
                    engine.note(session, `${client.clientName}: ${error.message}`);
                }
                if (!engine.isCurrent(session)) return;
            }
            if (!engine.isCurrent(session)) return;

            if (!tracks) {
                engine.note(session, 'YouTube gave no caption list; following the captions on screen instead');
                return;
            }
            if (tracks.length === 0) {
                engine.setNoCaptions(session);
                return;
            }

            await Promise.all(pickTracks(tracks, languages).map(async track => {
                try {
                    const url = new URL(track.baseUrl, location.origin);
                    url.searchParams.set('fmt', 'json3');
                    const response = await CG.hooks.originalFetch(url.href, { credentials: 'omit' });
                    const parsed = CG.captions.parse(await response.text());
                    if (!engine.isCurrent(session)) return;
                    if (parsed.cues.length === 0) {
                        engine.note(session, `${trackName(track)}: no captions came back (HTTP ${response.status})`);
                        return;
                    }
                    engine.addSource(session, { id: `yt:${track.vssId || track.languageCode + track.kind}`, name: trackName(track), via: 'Caption file', cues: parsed.cues });
                } catch (error) {
                    engine.note(session, `${trackName(track)}: ${error.message}`);
                }
            }));
        },

        tick(engine) {
            const player = this.player();
            if (player && !this.shorts()) syncCaptions(player, engine, this.isAd());
        },

        markers() {
            if (this.shorts()) return null;
            return this.player()?.querySelector('.ytp-progress-bar') || null;
        },
    };

    function trackName(track) {
        const name = track.name?.simpleText || track.name?.runs?.map(run => run.text).join('') || track.languageCode || 'Unknown';
        return track.kind === 'asr' && !/auto/i.test(name) ? `${name} (auto-generated)` : name;
    }

    // Written captions have the right words; auto-generated ones have the right times. Both are used when present.
    function pickTracks(tracks, languages) {
        const preferred = track => {
            const code = (track.languageCode || '').toLowerCase();
            return languages.some(language => code === language || code.startsWith(language + '-'));
        };
        const manual = tracks.filter(track => track.kind !== 'asr');
        const auto = tracks.filter(track => track.kind === 'asr');
        const picked = [manual.find(preferred), auto.find(preferred)].filter(Boolean);
        return picked.length ? picked : [manual[0] || auto[0]].filter(Boolean);
    }

    // ---- YouTube captions kept on ----
    // While filtering, captions stay on so the words on screen can always be read. Whether the viewer sees them is
    // settings.showCaptions, which the CC button switches instead of turning captions off.

    const subtitlesButton = player => player.querySelector('.ytp-subtitles-button');
    const captionsOn = player => subtitlesButton(player)?.getAttribute('aria-pressed') === 'true';
    const captionWatch = { key: null, wasOn: false, lastClick: -Infinity, lastMenuClick: -Infinity, buttonWatched: false };

    function clickCaptions(player) {
        captionWatch.lastClick = performance.now();
        subtitlesButton(player)?.click();
    }

    function captionsAvailable(player) {
        const button = subtitlesButton(player);
        return !!button && !button.disabled && button.getAttribute('aria-disabled') !== 'true' && getComputedStyle(button).display !== 'none';
    }

    function syncCaptions(player, engine, ad) {
        const settings = engine.settings();
        const session = engine.session();
        const filtering = engine.filtering();
        const keepOn = filtering && settings.keepCaptionsOn;
        watchCaptionButton(engine);

        player.classList.toggle('cg-hidden-captions', keepOn && settings.showCaptions === false);
        // An ad has its own caption state, which says nothing about the viewer's choice for the video.
        if (ad || !session || session.state === 'none' || !captionsAvailable(player)) return;

        const on = captionsOn(player);
        const settled = performance.now() - captionWatch.lastClick > 2000;

        // The first time ever: whether the viewer had captions on decides whether they see them from now on.
        if (settings.showCaptions === null) {
            if (performance.now() - session.startedAt < 1500) return;
            engine.saveSettings({ showCaptions: on });
            engine.note(session, on ? 'Captions were on, so they stay in view' : 'Captions were off, so they are kept on out of sight');
            return;
        }

        // Turned off in YouTube's settings menu while showing: kept on, but hidden. YouTube also turns captions off by
        // itself as a video loads; then they are only turned back on, and stay in view.
        const fromMenu = performance.now() - captionWatch.lastMenuClick < 3000;
        if (keepOn && settings.showCaptions && !on && settled && fromMenu && captionWatch.key === session.key && captionWatch.wasOn) {
            engine.saveSettings({ showCaptions: false });
            engine.note(session, 'Captions were turned off, so they are kept on out of sight');
        }

        const wantOn = keepOn || settings.showCaptions;
        if (on !== wantOn && settled) clickCaptions(player);

        captionWatch.key = session.key;
        captionWatch.wasOn = captionsOn(player);
    }

    // The viewer's own CC presses (the button or the C key) only show or hide the captions while they are kept on.
    function watchCaptionButton(engine) {
        if (captionWatch.buttonWatched) return;
        captionWatch.buttonWatched = true;

        const keptOn = settings => engine.filtering() && settings.keepCaptionsOn;

        function toggleShown(event) {
            const settings = engine.settings();
            event.preventDefault();
            event.stopImmediatePropagation();
            engine.saveSettings({ showCaptions: !settings.showCaptions });
            const session = engine.session();
            if (session) engine.note(session, settings.showCaptions ? 'Captions hidden; they stay on out of sight' : 'Captions shown');
        }

        window.addEventListener('click', event => {
            if (!event.isTrusted) return;
            if (event.target.closest?.('.ytp-settings-menu')) captionWatch.lastMenuClick = performance.now();
            if (!event.target.closest?.('.ytp-subtitles-button')) return;
            const settings = engine.settings();
            if (!settings || settings.showCaptions === null) return;

            if (keptOn(settings)) {
                toggleShown(event);
            } else {
                // The button works as usual; its new state is remembered.
                const shown = !captionsOn(youtube.player());
                engine.saveSettings({ showCaptions: shown });
                const session = engine.session();
                if (session) engine.note(session, shown ? 'Captions turned on' : 'Captions turned off');
            }
        }, true);

        // YouTube's C key would turn the captions off underneath, and with them hidden could never show them.
        window.addEventListener('keydown', event => {
            if (!event.isTrusted || event.key?.toLowerCase() !== 'c' || event.ctrlKey || event.altKey || event.metaKey) return;
            const target = event.target;
            if (target?.isContentEditable || target?.closest?.('input, textarea, select')) return;
            const settings = engine.settings();
            const player = youtube.player();
            if (!settings || settings.showCaptions === null || !keptOn(settings) || youtube.shorts() || !player || !captionsAvailable(player)) return;
            toggleShown(event);
        }, true);
    }

    // ---- Netflix ----

    const netflixPlayer = () => {
        try {
            const videoPlayer = window.netflix?.appContext?.state?.playerApp?.getAPI?.()?.videoPlayer;
            const ids = videoPlayer?.getAllPlayerSessionIds?.() || [];
            const id = ids.find(session => session.startsWith('watch')) || ids[0];
            return id ? videoPlayer.getVideoPlayerBySessionId(id) : null;
        } catch {
            return null;
        }
    };

    const netflix = {
        ...generic,
        id: 'netflix',
        name: 'Netflix',
        matches: () => /(^|\.)netflix\.com$/.test(location.hostname),
        captionSelector: '.player-timedtext-text-container',
        readsTextTracks: false,
        mediaKey() {
            const id = /\/watch\/(\d+)/.exec(location.pathname)?.[1];
            return id && this.video() ? `netflix:${id}` : null;
        },
        info: () => ({ title: document.querySelector('[data-uia="video-title"]')?.innerText.replace(/\n+/g, ' · ') || document.title.replace(/ - Netflix$/, ''), channel: null }),
        seek(video, seconds) {
            const player = netflixPlayer();
            if (player?.seek) player.seek(Math.max(0, seconds) * 1000);
        },

        // Netflix downloads a subtitle file only for the track chosen. With none chosen, one is turned on out of sight.
        async load(session, engine) {
            await sleep(4000);
            if (!engine.isCurrent(session) || session.sources.size > 0) return;

            const player = netflixPlayer();
            if (!player?.getTextTrackList || !player.setTextTrack) return;
            const current = player.getTextTrack?.();
            const off = !current || current.isNoneTrack || /^off$/i.test(current.displayName || '');
            if (!off) return;

            const languages = engine.settings().languages;
            const tracks = player.getTextTrackList().filter(track => !track.isNoneTrack && !/off/i.test(track.displayName || ''));
            const track = tracks.find(t => languages.some(l => (t.bcp47 || '').toLowerCase().startsWith(l))) || tracks[0];
            if (!track) return;

            document.documentElement.classList.add('cg-hide-site-captions');
            player.setTextTrack(track);
            engine.note(session, `Turned on ${track.displayName || track.bcp47} subtitles out of sight so they can be read`);
        },
    };

    // ---- Other streaming sites: their own caption elements; the rest is generic ----

    const named = (id, name, host, captionSelector) => ({
        ...generic,
        id,
        name,
        matches: () => host.test(location.hostname),
        captionSelector: `${captionSelector}, ${COMMON_CAPTIONS}`,
    });

    const SITES = [
        youtube,
        netflix,
        named('prime', 'Prime Video', /(^|\.)(primevideo\.com|amazon\.[a-z.]+)$/, '.atvwebplayersdk-captions-text'),
        named('disney', 'Disney+', /(^|\.)disneyplus\.com$/, '.dss-subtitle-renderer-cue'),
        named('max', 'Max', /(^|\.)max\.com$/, '[data-testid="CueBoxContainer"]'),
        named('hulu', 'Hulu', /(^|\.)hulu\.com$/, '.ClosedCaption__outband'),
    ];

    CG.sites = {
        pick: () => SITES.find(site => site.matches()) || generic,
        COMMON_CAPTIONS,
    };
})();
