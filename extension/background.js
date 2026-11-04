// Background worker: keeps each tab's state for the popup, the toolbar badge, statistics, the keyboard shortcut,
// and the content scripts for sites the user turned on themselves.
importScripts('shared/catalog.js', 'shared/matcher.js', 'shared/settings.js');
const CG = globalThis.__cinemaGuard;

const PAGE_SCRIPTS = ['shared/catalog.js', 'shared/matcher.js', 'shared/settings.js', 'shared/captions.js', 'shared/hits.js', 'page/hooks.js', 'page/sites.js', 'page/engine.js'];
const BRIDGE_SCRIPTS = ['content/bridge.js'];
const BUILT_IN = chrome.runtime.getManifest().content_scripts[0].matches;

// ---- Tab state ----
// Each frame's latest state. The detections and log come only when they change, so they are kept between updates.

const tabs = new Map(); // tab id -> { frameId: state }
const restored = chrome.storage.session.get('tabs').then(({ tabs: saved }) => {
    for (const [id, frames] of Object.entries(saved || {})) if (!tabs.has(Number(id))) tabs.set(Number(id), frames);
}).catch(() => {});

let persistTimer = null;
function persistTabs() {
    clearTimeout(persistTimer);
    persistTimer = setTimeout(() => chrome.storage.session.set({ tabs: Object.fromEntries(tabs) }).catch(() => {}), 1000);
}

const HEAVY = ['hits', 'log', 'sources', 'detections', 'offset'];

function storeState(tabId, frameId, state) {
    const frames = tabs.get(tabId) || {};
    const previous = frames[frameId];
    const merged = { ...state, frameId, updated: Date.now() };
    if (previous && previous.key === state.key && state.hits === undefined) {
        for (const key of HEAVY) if (previous[key] !== undefined) merged[key] = previous[key];
    }
    frames[frameId] = merged;
    tabs.set(tabId, frames);
    persistTabs();
    updateBadge(tabId);
}

// The frame that matters for a tab: one with a video, the top frame first.
function mainFrame(frames) {
    const list = Object.values(frames || {});
    return list.filter(frame => frame.hasMedia).sort((a, b) => a.frameId - b.frameId)[0] || list.find(frame => frame.frameId === 0) || null;
}

async function updateBadge(tabId) {
    const frame = mainFrame(tabs.get(tabId));
    let text = '';
    let color = '#C42B1C';
    if (frame?.hasMedia && !frame.filtering) {
        text = 'off';
        color = '#8A8A8E';
    } else if (frame?.hasMedia && frame.detections > 0) {
        text = frame.detections > 999 ? '999+' : String(frame.detections);
    }
    try {
        await chrome.action.setBadgeText({ tabId, text });
        await chrome.action.setBadgeBackgroundColor({ tabId, color });
    } catch { /* the tab is gone */ }
}

chrome.tabs.onRemoved.addListener(tabId => {
    tabs.delete(tabId);
    persistTabs();
});

chrome.tabs.onUpdated.addListener((tabId, change) => {
    if (change.status === 'loading' && change.url) {
        tabs.delete(tabId);
        persistTabs();
        updateBadge(tabId);
    }
});

// ---- Statistics ----

let statsChain = Promise.resolve();

function addStat({ word, site, key, title, url }) {
    statsChain = statsChain.then(async () => {
        const { stats } = await chrome.storage.local.get('stats');
        const next = stats || { since: new Date().toISOString(), total: 0, words: {}, days: {}, videos: [] };
        const now = new Date();
        const day = `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}-${String(now.getDate()).padStart(2, '0')}`;

        next.total += 1;
        next.words[word] = (next.words[word] || 0) + 1;
        next.days[day] = (next.days[day] || 0) + 1;
        for (const old of Object.keys(next.days).sort().slice(0, -120)) delete next.days[old];

        const existing = next.videos.find(video => video.key === key && video.site === site);
        if (existing) {
            existing.count += 1;
            existing.last = now.toISOString();
            if (title) existing.title = title;
            next.videos.splice(next.videos.indexOf(existing), 1);
            next.videos.unshift(existing);
        } else {
            next.videos.unshift({ key, site, title: title || key, url, count: 1, last: now.toISOString() });
            next.videos.length = Math.min(next.videos.length, 200);
        }
        await chrome.storage.local.set({ stats: next });
    }).catch(() => {});
}

// ---- Messages ----

chrome.runtime.onMessage.addListener((message, sender, reply) => {
    if (message?.type === 'state' && sender.tab) {
        storeState(sender.tab.id, sender.frameId ?? 0, message.state);
    } else if (message?.type === 'muted' && sender.tab) {
        addStat(message);
    } else if (message?.type === 'getTab') {
        restored.then(() => reply({ frames: Object.values(tabs.get(message.tabId) || {}) }));
        return true;
    } else if (message?.type === 'command') {
        const options = Number.isInteger(message.frameId) ? { frameId: message.frameId } : undefined;
        chrome.tabs.sendMessage(message.tabId, { type: 'command', command: message.command }, options).catch(() => {});
    } else if (message?.type === 'showCaptions' && sender.tab && typeof message.value === 'boolean') {
        updateSettings({ showCaptions: message.value });
    } else if (message?.type === 'syncSites') {
        syncSiteScripts().then(() => reply({ ok: true }), error => reply({ ok: false, error: String(error) }));
        return true;
    }
    return false;
});

// ---- Settings changed outside the options page ----

let settingsChain = Promise.resolve();

function updateSettings(changes) {
    settingsChain = settingsChain.then(async () => {
        const { settings } = await chrome.storage.sync.get('settings');
        const current = CG.settings.normalise(settings);
        const next = typeof changes === 'function' ? changes(current) : changes;
        await chrome.storage.sync.set({ settings: CG.settings.normalise({ ...current, ...next }) });
    }).catch(() => {});
    return settingsChain;
}

// ---- Keyboard shortcut ----

chrome.commands.onCommand.addListener(name => {
    if (name === 'toggle-filtering') updateSettings(current => ({ enabled: !current.enabled }));
});

// ---- Sites the user turned on ----
// Built-in sites are in the manifest; any other site gets the same scripts once the user grants it.

const isBuiltIn = origin => BUILT_IN.some(pattern => pattern === origin || pattern.replace(/\/\*$/, '') === origin.replace(/\/\*$/, ''));

async function syncSiteScripts() {
    const { origins = [] } = await chrome.permissions.getAll();
    const extra = origins.filter(origin => !isBuiltIn(origin) && /^https?:\/\//.test(origin));
    const registered = (await chrome.scripting.getRegisteredContentScripts()).map(script => script.id);
    const scripts = [
        { id: 'cg-sites-page', js: PAGE_SCRIPTS, matches: extra, runAt: 'document_start', allFrames: true, world: 'MAIN' },
        { id: 'cg-sites-bridge', js: BRIDGE_SCRIPTS, matches: extra, runAt: 'document_start', allFrames: true },
    ];

    if (extra.length === 0) {
        const ids = scripts.map(script => script.id).filter(id => registered.includes(id));
        if (ids.length) await chrome.scripting.unregisterContentScripts({ ids });
        return;
    }
    for (const script of scripts) {
        if (registered.includes(script.id)) await chrome.scripting.updateContentScripts([script]);
        else await chrome.scripting.registerContentScripts([script]);
    }
}

chrome.permissions.onAdded.addListener(() => syncSiteScripts().catch(() => {}));
chrome.permissions.onRemoved.addListener(() => syncSiteScripts().catch(() => {}));
chrome.runtime.onStartup.addListener(() => syncSiteScripts().catch(() => {}));

chrome.runtime.onInstalled.addListener(details => {
    syncSiteScripts().catch(() => {});
    if (details.reason === 'install') chrome.tabs.create({ url: chrome.runtime.getURL('options/options.html#welcome') });
});
