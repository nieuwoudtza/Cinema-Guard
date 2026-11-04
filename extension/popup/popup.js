// The toolbar popup: what Cinema Guard is doing on this tab.
(() => {
    const CG = globalThis.__cinemaGuard;
    const { el, icon, clock, duration, displayWord, MUTE_LABELS, plural, loadSettings, saveSettings, saveAllowChannels, coverage, untestedSite, reportLink } = CG.ui;

    const main = document.getElementById('main');
    const enabledSwitch = document.getElementById('enabled');
    const params = new URLSearchParams(location.search);

    let tab = null;
    let settings = null;
    let allowChannels = [];
    let cover = { kind: 'unsupported' };
    let frame = null;
    let signature = '';

    async function init() {
        tab = params.has('tab')
            ? await chrome.tabs.get(Number(params.get('tab')))
            : (await chrome.tabs.query({ active: true, currentWindow: true }))[0];
        ({ settings, allowChannels } = await loadSettings());
        cover = await coverage(tab?.url);
        showSwitch();

        if (tab) chrome.runtime.sendMessage({ type: 'command', tabId: tab.id, command: { name: 'repost' } }).catch(() => {});
        await refresh();
        setInterval(refresh, 700);

        chrome.storage.onChanged.addListener(async (changes, area) => {
            if (area !== 'sync') return;
            ({ settings, allowChannels } = await loadSettings());
            showSwitch();
            signature = '';
            refresh();
        });
    }

    function showSwitch() {
        enabledSwitch.checked = settings.enabled;
        document.getElementById('enabled-label').textContent = settings.enabled ? 'On' : 'Off';
    }

    enabledSwitch.addEventListener('change', () => saveSettings({ ...settings, enabled: enabledSwitch.checked }));
    document.getElementById('open-options').addEventListener('click', () => chrome.runtime.openOptionsPage());
    document.getElementById('open-stats').addEventListener('click', () => chrome.tabs.create({ url: chrome.runtime.getURL('options/options.html#statistics') }));

    const command = (name, extra = {}) => chrome.runtime.sendMessage({ type: 'command', tabId: tab.id, frameId: frame?.frameId, command: { name, ...extra } }).catch(() => {});

    async function refresh() {
        if (!tab) return render();
        const { frames = [] } = await chrome.runtime.sendMessage({ type: 'getTab', tabId: tab.id }).catch(() => ({}));
        frame = frames.filter(item => item.hasMedia).sort((a, b) => a.frameId - b.frameId)[0] || frames.find(item => item.frameId === 0) || null;

        const next = JSON.stringify([cover.kind, settings.enabled, settings.showCaptions, settings.pausedSites, allowChannels.length,
            frame && [frame.key, frame.version, frame.state, frame.filtering, frame.allowed, frame.siteCaptionsHidden, frame.title, frame.hits?.length]]);
        if (next !== signature) {
            signature = next;
            render();
        } else {
            showActive();
        }
    }

    // ---- Pages without a video ----

    function notice(title, text, ...buttons) {
        return el('section', { class: 'card notice' }, el('h2', { text: title }), el('p', { class: 'muted', text }), ...buttons);
    }

    async function turnOnSite() {
        const granted = await chrome.permissions.request({ origins: [`${cover.origin}/*`] });
        if (!granted) return;
        await chrome.runtime.sendMessage({ type: 'syncSites' });
        await chrome.tabs.reload(tab.id);
        window.close();
    }

    async function turnOffSite() {
        await chrome.permissions.remove({ origins: [`${cover.origin}/*`] });
        await chrome.runtime.sendMessage({ type: 'syncSites' });
        await chrome.tabs.reload(tab.id);
        window.close();
    }

    function render() {
        // A video the extension already follows settles it, whatever the address says.
        if (frame?.hasMedia) {
            main.replaceChildren(mediaCard(), ...hitList(), logSection());
            showActive();
            return;
        }
        if (cover.kind === 'unsupported') {
            main.replaceChildren(notice('Nothing to filter here', 'Cinema Guard works on web pages with videos, such as YouTube.'));
            return;
        }
        const untested = untestedSite(cover.host);
        if (cover.kind === 'off' && untested) {
            main.replaceChildren(notice(`${untested.name} hasn't been tested yet`,
                `Cinema Guard should work here, but nobody has been able to try it on ${untested.name} yet. You can turn it on and let us know how it went. The page reloads.`,
                el('div', { class: 'notice-actions' },
                    el('button', { class: 'button accent', onclick: turnOnSite, text: 'Try it anyway' }),
                    el('a', { class: 'button', href: reportLink(untested.name), target: '_blank', rel: 'noreferrer', text: 'Report how it went' }))));
            return;
        }
        if (cover.kind === 'off') {
            main.replaceChildren(notice(`Cinema Guard is off on ${cover.host}`,
                'Turn it on to mute swearing in videos on this site. The page reloads.',
                el('button', { class: 'button accent', onclick: turnOnSite, text: `Turn on for ${cover.host}` })));
            return;
        }
        main.replaceChildren(notice('No video yet', 'Start a video with captions or subtitles, and Cinema Guard picks it up.',
            cover.kind === 'granted' ? el('button', { class: 'button', onclick: turnOffSite, text: `Turn off for ${cover.host}` }) : null));
    }

    // ---- The video ----

    function status() {
        if (!settings.enabled) return ['caution', 'Off'];
        if (frame.allowed === 'channel') return ['plain', 'Channel not filtered'];
        if (frame.allowed === 'site') return ['plain', 'Paused on this site'];
        return {
            ready: ['success', 'Filtering'],
            live: ['accent', 'Following captions'],
            loading: ['plain', 'Loading'],
            none: ['caution', 'No captions'],
        }[frame.state] || ['plain', frame.state];
    }

    function mediaCard() {
        const [tone, label] = status();
        const host = frame.host;
        const paused = settings.pausedSites.includes(host);
        const channelAllowed = frame.channel && allowChannels.some(channel => channel.id === frame.channel.id);
        const actions = [];

        if (frame.channel) {
            actions.push(el('button', {
                class: 'button',
                onclick: () => saveAllowChannels(channelAllowed
                    ? allowChannels.filter(channel => channel.id !== frame.channel.id)
                    : [...allowChannels, { id: frame.channel.id, name: frame.channel.name, site: frame.site }]),
                text: channelAllowed ? 'Filter this channel again' : 'Don\'t filter this channel',
            }));
        }
        actions.push(el('button', {
            class: 'button',
            onclick: () => saveSettings({ ...settings, pausedSites: paused ? settings.pausedSites.filter(item => item !== host) : [...settings.pausedSites, host] }),
            text: paused ? `Resume on ${host}` : `Pause on ${host}`,
        }));
        if (frame.siteId === 'youtube' && settings.keepCaptionsOn && settings.showCaptions !== null && frame.filtering) {
            actions.push(el('button', {
                class: 'button',
                onclick: () => saveSettings({ ...settings, showCaptions: !settings.showCaptions }),
                text: settings.showCaptions ? 'Hide captions' : 'Show captions',
            }));
        }
        if (frame.siteCaptionsHidden) actions.push(el('button', { class: 'button', onclick: () => command('showSiteCaptions'), text: 'Show subtitles' }));

        const untested = untestedSite(host);
        return el('section', { class: 'card media' },
            untested ? el('div', { class: 'infobar caution untested' },
                el('div', null,
                    el('b', { text: `${untested.name} is still untested` }),
                    el('div', { class: 'muted', text: 'Did it mute the right moments? Copy the log below and tell us how it went.' }),
                    el('a', { href: reportLink(untested.name), target: '_blank', rel: 'noreferrer', text: 'Report how it went' }))) : null,
            el('div', { class: 'media-head' },
                el('span', { class: 'site', text: frame.site }),
                el('span', { class: `pill ${tone}`, text: label })),
            el('div', { class: 'title', text: frame.title || 'Untitled video' }),
            frame.channel?.name ? el('div', { class: 'channel muted', text: frame.channel.name }) : null,
            el('p', { class: 'message muted', text: frame.message }),
            el('div', { class: 'stats' },
                el('div', { class: 'stat' }, el('b', { text: (frame.detections ?? 0).toLocaleString() }), el('span', { class: 'muted', text: 'detections' })),
                el('div', { class: 'stat' }, el('b', { text: (frame.ranges ?? 0).toLocaleString() }), el('span', { class: 'muted', text: 'mutes' })),
                el('div', { class: 'stat' }, el('b', { text: duration(frame.mutedSeconds || 0) }), el('span', { class: 'muted', text: 'muted in all' }))),
            el('div', { class: 'muting', id: 'muting', hidden: !frame.muting }, icon('speaker'), 'Muting now'),
            el('div', { class: 'actions' }, actions));
    }

    function lineWithMark(hit) {
        if (!hit.line) return '';
        const said = hit.line.slice(hit.index, hit.index + hit.length);
        const shown = hit.word === CG.matcher.MASKED_LABEL ? said : CG.catalog.displayName(CG.matcher.upper(said)).toLowerCase();
        return [hit.line.slice(0, hit.index), el('span', { class: 'mark', text: shown }), hit.line.slice(hit.index + hit.length)];
    }

    function hitList() {
        const hits = frame.hits || [];
        const head = el('div', { class: 'list-head' },
            el('h2', { text: 'Detections' }),
            el('span', { class: 'muted', text: hits.length ? 'Select one to jump to it' : '' }));

        if (hits.length === 0) {
            const text = frame.state === 'loading' ? 'Reading the captions…' : frame.state === 'none' ? 'This video has no captions.' : 'Nothing found so far.';
            return [head, el('section', { class: 'card empty muted', text })];
        }

        return [head, el('section', { class: 'card hits' }, hits.map((hit, index) => el('button', {
            class: `hit${hit.dropped ? ' dropped' : ''}`,
            'data-index': index,
            title: hit.dropped ? 'Not muted separately: the same word has an exact time from another caption track' : 'Jump to a second before this',
            onclick: () => command('seek', { time: hit.at - 1 }),
        },
            el('span', { class: 'hit-time', text: clock(hit.at) }),
            el('span', { class: 'hit-word' }, el('b', { text: displayWord(hit.word) }),
                el('span', { class: 'muted', text: hit.dropped ? `${MUTE_LABELS[hit.mute]} · skipped` : MUTE_LABELS[hit.mute] || hit.mute })),
            el('span', { class: 'hit-line' }, lineWithMark(hit)))))];
    }

    function logSection() {
        const log = frame.log || [];
        const text = () => log.map(entry => `${entry.t.toFixed(1)} s  ${entry.text}`).join('\n');
        const copy = el('button', {
            class: 'button subtle',
            onclick: async () => {
                await navigator.clipboard.writeText(`${frame.site} · ${frame.title}\n${frame.url}\n\n${text()}`);
                copy.lastChild.textContent = 'Copied';
            },
        }, icon('copy'), el('span', { text: 'Copy log' }));

        return el('details', { class: 'log' },
            el('summary', { text: 'How the captions were found' }),
            el('ul', { class: 'log-lines' }, log.map(entry => el('li', null, el('span', { class: 'num', text: `${entry.t.toFixed(1)} s` }), el('span', { text: entry.text })))),
            frame.sources?.length ? el('p', { class: 'muted', text: `Sources: ${frame.sources.map(source => `${source.name} (${source.lines} lines)`).join(', ')}` }) : null,
            el('div', { class: 'log-actions' }, copy));
    }

    // The detection playing now, and the muting banner, between full redraws.
    function showActive() {
        if (!frame?.hasMedia) return;
        const time = frame.time ?? -1;
        const hits = frame.hits || [];
        for (const row of main.querySelectorAll('.hit')) {
            const hit = hits[Number(row.dataset.index)];
            row.classList.toggle('active', !!hit && !hit.dropped && time >= hit.start && time < (hit.end ?? hit.start + 1));
        }
        const muting = document.getElementById('muting');
        if (muting) muting.hidden = !frame.muting;
    }

    init();
})();
