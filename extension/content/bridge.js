// Runs beside the page (isolated from it): hands settings to the engine in the page, and passes the engine's state,
// statistics and setting changes to the background worker.
// It loads no shared files: the browser injects a file once per frame, and those already run in the page.
(() => {
    const TAG = '__cinemaGuard';

    const toPage = (type, payload) => window.postMessage({ [TAG]: 'toPage', type, payload }, '*');

    async function sendSettings() {
        try {
            const { settings, allowChannels } = await chrome.storage.sync.get(['settings', 'allowChannels']);
            toPage('settings', { settings: settings || null, allowChannels: allowChannels || [] });
        } catch { /* the extension was reloaded; this page keeps its last settings */ }
    }

    const send = message => {
        try {
            chrome.runtime.sendMessage(message).catch(() => {});
        } catch { /* the extension was reloaded */ }
    };

    window.addEventListener('message', event => {
        if (event.source !== window || event.data?.[TAG] !== 'toBridge') return;
        const { type, payload } = event.data;

        if (type === 'hello') sendSettings();
        else if (type === 'state') send({ type: 'state', state: payload });
        else if (type === 'muted') send({ type: 'muted', ...payload });
        // The page may only change what YouTube's CC button controls, so no website can change the filter.
        else if (type === 'saveSettings' && typeof payload?.showCaptions === 'boolean') send({ type: 'showCaptions', value: payload.showCaptions });
    });

    chrome.storage.onChanged.addListener((changes, area) => {
        if (area === 'sync' && (changes.settings || changes.allowChannels)) sendSettings();
    });

    chrome.runtime.onMessage.addListener(message => {
        if (message?.type === 'command') toPage('command', message.command);
    });

    sendSettings();
})();
