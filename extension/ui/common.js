// Helpers shared by the popup and the options page.
(() => {
    const CG = globalThis.__cinemaGuard;

    function el(tag, props, ...children) {
        const node = tag === 'svg' || tag === 'path' ? document.createElementNS('http://www.w3.org/2000/svg', tag) : document.createElement(tag);
        for (const [key, value] of Object.entries(props || {})) {
            if (value === undefined || value === null || value === false) continue;
            if (key === 'class') node.setAttribute('class', value);
            else if (key === 'text') node.textContent = value;
            else if (key === 'value') node.value = value;
            else if (key === 'checked') node.checked = !!value;
            else if (key === 'indeterminate') node.indeterminate = !!value;
            else if (key === 'style' && typeof value === 'object') Object.assign(node.style, value);
            else if (key.startsWith('on') && typeof value === 'function') node.addEventListener(key.slice(2), value);
            else node.setAttribute(key, value === true ? '' : value);
        }
        for (const child of children.flat(Infinity)) {
            if (child === null || child === undefined || child === false) continue;
            node.append(child instanceof Node ? child : String(child));
        }
        return node;
    }

    // Fluent-style 16px icons.
    const ICONS = {
        filter: 'M2 3.5c0-.28.22-.5.5-.5h11a.5.5 0 0 1 .4.8L10 9.17v3.33a.5.5 0 0 1-.28.45l-3 1.5A.5.5 0 0 1 6 14V9.17L2.1 3.8A.5.5 0 0 1 2 3.5Zm1.48.5L6.9 8.7c.07.09.1.2.1.3v4.19l2-1V9c0-.1.03-.21.1-.3L12.52 4H3.48Z',
        settings: { stroke: 'M2 4.5h5.5M11 4.5h3M9.25 4.5m-1.75 0a1.75 1.75 0 1 0 3.5 0a1.75 1.75 0 1 0-3.5 0M2 11.5h2M8 11.5h6M6.25 11.5m-1.75 0a1.75 1.75 0 1 0 3.5 0a1.75 1.75 0 1 0-3.5 0' },
        desktop: { stroke: 'M2.5 3h11a1 1 0 0 1 1 1v6.5a1 1 0 0 1-1 1h-11a1 1 0 0 1-1-1V4a1 1 0 0 1 1-1ZM6 14h4M8 11.5V14' },
        globe: { stroke: 'M8 1.5a6.5 6.5 0 1 0 0 13a6.5 6.5 0 1 0 0-13ZM1.5 8h13M8 1.5C6.2 3.3 5.3 5.5 5.3 8s.9 4.7 2.7 6.5M8 1.5c1.8 1.8 2.7 4 2.7 6.5s-.9 4.7-2.7 6.5' },
        chart: 'M4.5 9a.5.5 0 0 1 .5.5v4a.5.5 0 0 1-1 0v-4a.5.5 0 0 1 .5-.5Zm3.5-3a.5.5 0 0 1 .5.5v7a.5.5 0 0 1-1 0v-7A.5.5 0 0 1 8 6Zm3.5-4a.5.5 0 0 1 .5.5v11a.5.5 0 0 1-1 0v-11a.5.5 0 0 1 .5-.5Z',
        archive: 'M2 3a1 1 0 0 1 1-1h10a1 1 0 0 1 1 1v1a1 1 0 0 1-.5.87V12a2 2 0 0 1-2 2h-7a2 2 0 0 1-2-2V4.87A1 1 0 0 1 2 4V3Zm1.5 2v7a1 1 0 0 0 1 1h7a1 1 0 0 0 1-1V5h-9ZM13 3H3v1h10V3ZM6 7.5c0-.28.22-.5.5-.5h3a.5.5 0 0 1 0 1h-3a.5.5 0 0 1-.5-.5Z',
        chevron: 'M4.65 6.15a.5.5 0 0 1 .7 0L8 8.79l2.65-2.64a.5.5 0 0 1 .7.7l-3 3a.5.5 0 0 1-.7 0l-3-3a.5.5 0 0 1 0-.7Z',
        open: 'M9 2.5c0-.28.22-.5.5-.5h4c.28 0 .5.22.5.5v4a.5.5 0 0 1-1 0V3.7L8.85 7.85a.5.5 0 1 1-.7-.7L12.29 3H9.5a.5.5 0 0 1-.5-.5ZM4 4a1 1 0 0 0-1 1v7a1 1 0 0 0 1 1h7a1 1 0 0 0 1-1V9.5a.5.5 0 0 1 1 0V12a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5c0-1.1.9-2 2-2h2.5a.5.5 0 0 1 0 1H4Z',
        copy: 'M5 3a2 2 0 0 1 2-2h5a2 2 0 0 1 2 2v7a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V3Zm2-1a1 1 0 0 0-1 1v7a1 1 0 0 0 1 1h5a1 1 0 0 0 1-1V3a1 1 0 0 0-1-1H7ZM3 5a1 1 0 0 1 1-1v6.5A2.5 2.5 0 0 0 6.5 13H11a1 1 0 0 1-1 1H6.5A3.5 3.5 0 0 1 3 10.5V5Z',
        dismiss: 'M3.15 3.15a.5.5 0 0 1 .7 0L8 7.29l4.15-4.14a.5.5 0 0 1 .7.7L8.71 8l4.14 4.15a.5.5 0 0 1-.7.7L8 8.71l-4.15 4.14a.5.5 0 0 1-.7-.7L7.29 8 3.15 3.85a.5.5 0 0 1 0-.7Z',
        speaker: 'M8.5 2.6a.5.5 0 0 0-.84-.37L4.8 5H2.5A1.5 1.5 0 0 0 1 6.5v3A1.5 1.5 0 0 0 2.5 11h2.3l2.86 2.77a.5.5 0 0 0 .84-.36V2.6ZM5.35 5.86 7.5 3.78v8.44L5.35 10.14A.5.5 0 0 0 5 10H2.5a.5.5 0 0 1-.5-.5v-3c0-.28.22-.5.5-.5H5a.5.5 0 0 0 .35-.14Zm5-.21a.5.5 0 0 1 .7 0L12.5 7.1l1.45-1.45a.5.5 0 0 1 .7.7L13.21 7.8l1.44 1.45a.5.5 0 0 1-.7.7L12.5 8.5l-1.45 1.45a.5.5 0 0 1-.7-.7l1.44-1.45-1.44-1.45a.5.5 0 0 1 0-.7Z',
    };

    // Most icons are filled shapes; a few are drawn as lines.
    const icon = name => {
        const shape = ICONS[name];
        const path = typeof shape === 'string'
            ? el('path', { d: shape, 'fill-rule': 'evenodd' })
            : el('path', { d: shape.stroke, fill: 'none', stroke: 'currentColor', 'stroke-width': '1.1', 'stroke-linecap': 'round' });
        return el('svg', { viewBox: '0 0 16 16', 'aria-hidden': 'true', class: `icon icon-${name}` }, path);
    };

    function clock(seconds) {
        if (!Number.isFinite(seconds)) return '–';
        const total = Math.max(0, seconds);
        const hours = Math.floor(total / 3600);
        const minutes = Math.floor(total % 3600 / 60);
        const rest = Math.floor(total % 60);
        return hours ? `${hours}:${String(minutes).padStart(2, '0')}:${String(rest).padStart(2, '0')}` : `${minutes}:${String(rest).padStart(2, '0')}`;
    }

    const duration = seconds => seconds < 60 ? `${seconds.toFixed(1)} s` : `${Math.floor(seconds / 60)} min ${Math.round(seconds % 60)} s`;

    // Words are shown the way the Windows app's filter page shows them: one vowel starred.
    const displayWord = word => word === CG.matcher.MASKED_LABEL ? '[censored]' : CG.catalog.displayName(word).toLowerCase();

    const MUTE_LABELS = {
        sentence: 'Whole sentence',
        timed: 'Word',
        guessed: 'Word, time guessed',
        'live-word': 'Word, caught live',
        'live-line': 'Line, caught live',
    };

    const plural = (count, word) => `${count.toLocaleString()} ${word}${count === 1 ? '' : 's'}`;

    async function loadSettings() {
        const { settings, allowChannels } = await chrome.storage.sync.get(['settings', 'allowChannels']);
        return { settings: CG.settings.normalise(settings), allowChannels: allowChannels || [] };
    }

    const saveSettings = settings => chrome.storage.sync.set({ settings: CG.settings.normalise(settings) });
    const saveAllowChannels = list => chrome.storage.sync.set({ allowChannels: list });

    // Streaming sites with an adapter nobody has been able to test (they need paid accounts). Off until someone turns
    // one on; people who can are asked to report how it went.
    const UNTESTED_SITES = [
        { name: 'Netflix', host: 'netflix.com' },
        { name: 'Prime Video', host: 'primevideo.com' },
        { name: 'Disney+', host: 'disneyplus.com' },
        { name: 'Max', host: 'max.com' },
        { name: 'Hulu', host: 'hulu.com' },
    ];
    const untestedSite = host => UNTESTED_SITES.find(site => host === site.host || host?.endsWith(`.${site.host}`)) || null;
    const reportLink = siteName => `https://github.com/nieuwoudtza/Cinema-Guard/issues/new?title=${encodeURIComponent(`${siteName}: how Cinema Guard did`)}`;

    // Whether the extension runs on a page: built into the manifest, or a site the user turned on.
    async function coverage(url) {
        if (!url) return { kind: 'unknown' };
        let parsed;
        try { parsed = new URL(url); } catch { return { kind: 'unsupported' }; }
        if (!/^https?:$/.test(parsed.protocol)) return { kind: 'unsupported' };

        const host = parsed.hostname.replace(/^www\./, '');
        const builtIn = chrome.runtime.getManifest().content_scripts[0].matches
            .some(pattern => new RegExp('^' + pattern.replace(/[.]/g, '\\.').replace(/\*/g, '.*') + '$').test(parsed.origin + parsed.pathname));
        if (builtIn) return { kind: 'built-in', host, origin: parsed.origin };

        const granted = await chrome.permissions.contains({ origins: [`${parsed.origin}/*`] });
        return { kind: granted ? 'granted' : 'off', host, origin: parsed.origin };
    }

    CG.ui = { el, icon, clock, duration, displayWord, MUTE_LABELS, plural, loadSettings, saveSettings, saveAllowChannels, coverage, UNTESTED_SITES, untestedSite, reportLink };
})();
