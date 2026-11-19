// The toolbar popup: what Cinema Guard is doing on this tab.
(() => {
    const CG = globalThis.__cinemaGuard;
    const { el, icon, clock, duration, displayWord, MUTE_LABELS, plural, loadSettings, saveSettings, saveAllowChannels, coverage, withSite, untestedSite, reportLink } = CG.ui;

    const main = document.getElementById('main');
    const enabledSwitch = document.getElementById('enabled');
    const params = new URLSearchParams(location.search);
    const openedAt = Date.now();

    let tab = null;
    let settings = null;
    let allowChannels = [];
    let cover = { kind: 'unsupported' };
    let frame = null;
    let framesSeen = false;
    let signature = '';

    async function init() {
        tab = params.has('tab')
            ? await chrome.tabs.get(Number(params.get('tab')))
            : (await chrome.tabs.query({ active: true, currentWindow: true }))[0];
        ({ settings, allowChannels } = await loadSettings());
        cover = coverage(tab?.url);
        showSwitch();
        showUpdated();

        if (tab) {
            // Changes arrive as they happen; the check every 700 ms is for anything missed.
            chrome.runtime.connect({ name: `popup:${tab.id}` }).onMessage.addListener(message => {
                if (message?.type === 'frames') update(message.frames);
            });
            chrome.runtime.sendMessage({ type: 'command', tabId: tab.id, command: { name: 'repost' } }).catch(() => {});
        }
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

    // After an update, until What's new is opened or this is closed.
    async function showUpdated() {
        const box = document.getElementById('updated');
        const { updatedFrom } = await chrome.storage.local.get('updatedFrom');
        if (!updatedFrom) return;

        const dismiss = async () => {
            box.hidden = true;
            await chrome.storage.local.remove('updatedFrom');
        };

        box.replaceChildren(el('div', { class: 'infobar' },
            el('div', { class: 'updated-text' },
                el('b', { text: `Updated to version ${chrome.runtime.getManifest().version}` }),
                // The options page clears the notice once What's new is shown; the popup closes as the tab opens.
                el('a', {
                    href: '#',
                    text: 'See what\'s new',
                    onclick: event => {
                        event.preventDefault();
                        chrome.tabs.create({ url: chrome.runtime.getURL('options/options.html#whats-new') });
                    },
                })),
            el('button', { class: 'button subtle dismiss', title: 'Close', 'aria-label': 'Close', onclick: dismiss }, icon('dismiss'))));
        box.hidden = false;
    }

    enabledSwitch.addEventListener('change', () => saveSettings({ ...settings, enabled: enabledSwitch.checked }));
    document.getElementById('open-options').addEventListener('click', () => chrome.runtime.openOptionsPage());
    document.getElementById('open-stats').addEventListener('click', () => chrome.tabs.create({ url: chrome.runtime.getURL('options/options.html#statistics') }));

    const command = (name, extra = {}) => chrome.runtime.sendMessage({ type: 'command', tabId: tab.id, frameId: frame?.frameId, command: { name, ...extra } }).catch(() => {});

    async function refresh() {
        if (!tab) return render();
        const { frames = [] } = await chrome.runtime.sendMessage({ type: 'getTab', tabId: tab.id }).catch(() => ({}));
        update(frames);
    }

    function update(frames) {
        frame = mainFrame(frames);
        framesSeen = frames.length > 0;
        const waited = Date.now() - openedAt > 1500;

        const next = JSON.stringify([cover.kind, framesSeen, waited, settings.enabled, settings.showCaptions, settings.siteMode, settings.blacklist, settings.whitelist, allowChannels.length,
            frame && [frame.key, frame.version, frame.state, frame.filtering, frame.pausedForSubtitles, frame.allowed, frame.siteCaptionsHidden, frame.title, frame.hits?.length]]);
        if (next !== signature) {
            signature = next;
            render();
        } else {
            showActive();
        }
    }

    // The frame playing the film rather than an advert beside it, as the background worker picks it.
    function mainFrame(frames) {
        const rank = item => (item.state === 'ready' ? 4 : 0) + (item.duration > 300 ? 2 : 0) + (item.state === 'live' ? 1 : 0);
        return frames.filter(item => item.hasMedia).sort((a, b) => rank(b) - rank(a) || (b.duration || 0) - (a.duration || 0) || a.frameId - b.frameId)[0]
            || frames.find(item => item.frameId === 0) || null;
    }

    // ---- Where to filter ----
    // Decided by the tab's site, and by the site of the player when it is embedded from elsewhere.

    const whitelistMode = () => settings.siteMode === 'whitelist';
    const siteHosts = () => [cover.host, frame?.host].filter(Boolean);
    const siteOn = () => CG.settings.siteOn(settings, ...siteHosts());

    // Subtitles are read on every site whatever the lists say, so the change works at once, without a reload.
    function setSiteOn(on) {
        if (whitelistMode()) return saveSettings(withSite(settings, 'whitelist', cover.host, on));
        if (!on) return saveSettings(withSite(settings, 'blacklist', cover.host, true));
        let next = settings;
        for (const host of siteHosts()) next = withSite(next, 'blacklist', host, false);
        return saveSettings(next);
    }

    // ---- Pages without a video ----

    function notice(title, text, ...buttons) {
        return el('section', { class: 'card notice' }, el('h2', { text: title }), el('p', { class: 'muted', text }), ...buttons);
    }

    function render() {
        // A video the extension already follows settles it, whatever the address says.
        if (frame?.hasMedia) {
            main.replaceChildren(...[askToAdd(), mediaCard(), ...hitList(), logSection()].filter(Boolean));
            showActive();
            return;
        }
        if (cover.kind !== 'web') {
            main.replaceChildren(notice('Nothing to filter here', 'Cinema Guard works on web pages with videos, such as YouTube.'));
            return;
        }
        // Pages open from before Cinema Guard was installed or updated run without it.
        if (!framesSeen && Date.now() - openedAt > 1500) {
            main.replaceChildren(notice('Reload this page to start',
                `Cinema Guard can't see this page yet. It was probably open before Cinema Guard was installed or updated.`,
                el('button', { class: 'button accent', onclick: () => chrome.tabs.reload(tab.id).then(() => window.close()), text: 'Reload' })));
            return;
        }
        if (!siteOn()) {
            main.replaceChildren(whitelistMode()
                ? notice(`Cinema Guard is off on ${cover.host}`,
                    `${cover.host} is not on your whitelist. Add it and videos here are filtered straight away.`,
                    el('button', { class: 'button accent', onclick: () => setSiteOn(true), text: `Add ${cover.host} to the whitelist` }))
                : notice(`Cinema Guard is off on ${cover.host}`,
                    `${cover.host} is on your blacklist, so nothing is muted here.`,
                    el('button', { class: 'button accent', onclick: () => setSiteOn(true), text: `Take ${cover.host} off the blacklist` })));
            return;
        }
        main.replaceChildren(notice('No video yet', 'Start a video with captions or subtitles, and Cinema Guard picks it up.',
            el('button', { class: 'button', onclick: () => setSiteOn(false), text: whitelistMode() ? `Remove ${cover.host} from the whitelist` : `Don't filter ${cover.host}` })));
    }

    // Whitelist mode, on a site not on it: the one question asked. The subtitles are already being read.
    function askToAdd() {
        if (!whitelistMode() || siteOn() || !cover.host) return null;
        return el('section', { class: 'card notice' },
            el('h2', { text: `Filter videos on ${cover.host}?` }),
            el('p', { class: 'muted', text: 'It isn\'t on your whitelist, so nothing is muted or blanked here. Add it and this video is filtered straight away.' }),
            el('button', { class: 'button accent', onclick: () => setSiteOn(true), text: 'Add to the whitelist' }));
    }

    // ---- The video ----

    function status() {
        if (!settings.enabled) return ['caution', 'Off'];
        if (frame.allowed === 'channel') return ['plain', 'Channel not filtered'];
        if (frame.allowed === 'site' || !siteOn()) return ['plain', whitelistMode() ? 'Not on the whitelist' : 'On the blacklist'];
        if (frame.pausedForSubtitles) return ['caution', 'Paused: other subtitles'];
        return {
            ready: ['success', 'Filtering'],
            live: ['accent', 'Following captions'],
            loading: ['plain', 'Loading'],
            none: ['caution', 'No captions'],
        }[frame.state] || ['plain', frame.state];
    }

    // The status in the card's corner; while a mute plays, it says so there, so nothing below moves.
    function statusTag() {
        if (frame.muting) return el('span', { class: 'pill alert', id: 'status' }, icon('speaker'), 'Muting now');
        const [tone, label] = status();
        return el('span', { class: `pill ${tone}`, id: 'status', text: label });
    }

    function mediaCard() {
        const host = cover.host || frame.topHost || frame.host;
        const on = siteOn();
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
        // Off on a whitelist, the question above the card has the button.
        if (!(whitelistMode() && !on)) {
            actions.push(el('button', {
                class: 'button',
                onclick: () => setSiteOn(!on),
                text: whitelistMode() ? `Remove ${host} from the whitelist` : on ? `Don't filter ${host}` : `Filter ${host} again`,
            }));
        }
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
                statusTag()),
            el('div', { class: 'title', text: frame.title || 'Untitled video' }),
            frame.channel?.name ? el('div', { class: 'channel muted', text: frame.channel.name }) : null,
            el('p', { class: 'message muted', text: frame.message }),
            el('div', { class: 'stats' },
                el('div', { class: 'stat' }, el('b', { text: (frame.detections ?? 0).toLocaleString() }), el('span', { class: 'muted', text: 'detections' })),
                el('div', { class: 'stat' }, el('b', { text: (frame.ranges ?? 0).toLocaleString() }), el('span', { class: 'muted', text: 'mutes' })),
                el('div', { class: 'stat' }, el('b', { text: duration(frame.mutedSeconds || 0) }), el('span', { class: 'muted', text: 'muted in all' }))),
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
            const text = frame.pausedForSubtitles ? 'Nothing to mute in the subtitles on screen. Switch back to your language and the detections return.'
                : frame.state === 'loading' ? 'Reading the captions…' : frame.state === 'none' ? 'This video has no captions.' : 'Nothing found so far.';
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

    // The detection playing now, and the status (muting or not), between full redraws.
    function showActive() {
        if (!frame?.hasMedia) return;
        const time = frame.time ?? -1;
        const hits = frame.hits || [];
        for (const row of main.querySelectorAll('.hit')) {
            const hit = hits[Number(row.dataset.index)];
            row.classList.toggle('active', !!hit && !hit.dropped && time >= hit.start && time < (hit.end ?? hit.start + 1));
        }
        const tag = document.getElementById('status');
        if (tag && tag.classList.contains('alert') !== !!frame.muting) tag.replaceWith(statusTag());
    }

    init();
})();
