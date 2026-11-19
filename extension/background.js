// Background worker: keeps each tab's state for the popup, the toolbar badge, statistics and the keyboard shortcut.
importScripts('shared/catalog.js', 'shared/matcher.js', 'shared/settings.js');
const CG = globalThis.__cinemaGuard;

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
    for (const port of popups) if (port.tabId === tabId) port.postMessage({ type: 'frames', frames: Object.values(frames) });
}

// An open popup gets each change as it comes, so "Muting now" shows the moment the sound goes.
const popups = new Set();
chrome.runtime.onConnect.addListener(port => {
    if (!port.name.startsWith('popup:')) return;
    port.tabId = Number(port.name.slice('popup:'.length));
    popups.add(port);
    port.onDisconnect.addListener(() => popups.delete(port));
});

// The frame that matters for a tab: the one playing the film rather than an advert beside it. A video with captions
// first, then the longest, then the top frame. The popup picks the same way.
function mainFrame(frames) {
    const list = Object.values(frames || {});
    const rank = frame => (frame.state === 'ready' ? 4 : 0) + (frame.duration > 300 ? 2 : 0) + (frame.state === 'live' ? 1 : 0);
    return list.filter(frame => frame.hasMedia).sort((a, b) => rank(b) - rank(a) || (b.duration || 0) - (a.duration || 0) || a.frameId - b.frameId)[0]
        || list.find(frame => frame.frameId === 0) || null;
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

// ---- Sites turned on one by one, before 1.0.5 ----
// The scripts now run on every site, so the ones registered for those sites would run twice. The sites go on the
// whitelist, for anyone who switches to it.

async function migrateSites() {
    const { sitesMigrated } = await chrome.storage.local.get('sitesMigrated');
    if (sitesMigrated) return;
    const registered = await chrome.scripting.getRegisteredContentScripts();
    if (registered.length) await chrome.scripting.unregisterContentScripts({ ids: registered.map(script => script.id) });

    const { origins = [] } = await chrome.permissions.getAll();
    const hosts = origins.filter(origin => /^https?:\/\/[^*]/.test(origin)).map(origin => CG.settings.siteHost(origin.replace(/\/\*$/, ''))).filter(Boolean);
    if (hosts.length) await updateSettings(current => ({ whitelist: [...current.whitelist, ...hosts] }));
    await chrome.storage.local.set({ sitesMigrated: true });
}

chrome.runtime.onInstalled.addListener(details => {
    migrateSites().catch(() => {});
    if (details.reason === 'install') chrome.tabs.create({ url: chrome.runtime.getURL('options/options.html#welcome') });

    // The popup says the extension was updated until What's new is seen. Reloading an unpacked copy also arrives as
    // an update, with the same version. After several updates, the oldest version is kept, so all of it shows as new.
    if (details.reason === 'update' && details.previousVersion && details.previousVersion !== chrome.runtime.getManifest().version) {
        chrome.storage.local.get('updatedFrom').then(({ updatedFrom }) => {
            if (!updatedFrom) return chrome.storage.local.set({ updatedFrom: details.previousVersion });
        }).catch(() => {});
    }
});
