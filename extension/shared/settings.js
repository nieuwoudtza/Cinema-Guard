// Settings: defaults from the Windows app, checking, and importing from the app's settings.json.
(() => {
    const CG = (globalThis.__cinemaGuard ??= {});
    const { catalog, matcher } = CG;

    const DEFAULTS = {
        enabled: true,

        // "Mute the whole sentence" and "Mute the word only".
        wordsFull: catalog.defaultFull(),
        wordsSingle: catalog.defaultSingle(),

        // Which catalog the lists were saved with: older lists get the spellings added since.
        catalogVersion: catalog.VERSION,

        // Milliseconds added around each mute.
        pads: {
            estimatedBefore: 500, estimatedAfter: 500, // a word whose time is guessed from its place in the caption line
            wordBefore: 150, wordAfter: 250,           // a word with its own time (YouTube's auto-generated captions)
            lineBefore: 250, lineAfter: 500,           // a whole sentence
        },

        // How long the sound fades out before a mute and back in after it, so a mute never clicks. 0 cuts straight to silence.
        fadeMs: 30,

        // Replaces each letter of a listed word in captions on screen: one symbol or a space; '' leaves captions alone.
        censorCharacter: '*',

        // Words the captions already hide (f***, [bleep], YouTube's [ __ ]) are muted too.
        muteMaskedWords: true,

        // For dubbed sound: muting pauses while the player shows subtitles in another language than these.
        pauseForOtherSubtitles: false,

        // Caption languages to use, best first.
        languages: ['en'],

        // When no caption file can be read, how long a word is muted from the moment it appears on screen.
        liveWordMs: 800,

        // YouTube: keep captions on underneath so they can always be read, and whether the viewer sees them.
        // showCaptions is null until the first video, which takes it from whether captions were on.
        keepCaptionsOn: true,
        showCaptions: null,

        // Marks on YouTube's progress bar where it will mute.
        markers: true,

        // Where to filter: 'blacklist' filters every site except those on the blacklist, 'whitelist' only the sites on
        // the whitelist. Subtitles are read on every site either way, so a site starts filtering the moment it is allowed.
        // Both lists hold host names; a name covers its subdomains too.
        siteMode: 'blacklist',
        blacklist: [],
        whitelist: ['youtube.com'],
    };

    // The same limits as the Windows app.
    const PAD_LIMITS = { estimatedBefore: 5000, estimatedAfter: 5000, wordBefore: 2000, wordAfter: 2000, lineBefore: 5000, lineAfter: 5000 };
    const FADE_LIMIT = 500;

    // The app's rule: one symbol, or a space. '' turns blanking off here.
    const isValidCensorCharacter = value => value === '' || value === ' ' ||
        (typeof value === 'string' && value.length === 1 && !/[\p{L}\p{N}\s\p{C}]/u.test(value));

    function milliseconds(value, fallback, max, min = 0) {
        const number = Number(value);
        return value !== null && value !== '' && value !== undefined && Number.isFinite(number)
            ? Math.min(max, Math.max(min, Math.round(number)))
            : fallback;
    }

    // A site as the lists keep it: the host name alone, without www. Accepts an address or a bare name ('' if neither).
    function siteHost(value) {
        let text = String(value ?? '').trim().toLowerCase().replace(/^\*\./, '');
        if (!text) return '';
        try {
            text = new URL(/^[a-z][a-z0-9+.-]*:\/\//.test(text) ? text : `http://${text}`).hostname;
        } catch {
            return '';
        }
        text = text.replace(/^www\./, '').replace(/\.$/, '');
        return text.includes('.') || text === 'localhost' ? text : '';
    }

    // Whether a list names this host, or a site it belongs to (m.example.com is on example.com).
    const listed = (list, host) => !!host && list.some(entry => host === entry || host.endsWith(`.${entry}`));

    // Whether to filter, given the site of the tab and the site of the frame playing the video (a player embedded
    // from another site). Either one on the blacklist leaves it alone; either one on the whitelist filters it.
    function siteOn(settings, ...hosts) {
        const known = hosts.filter(Boolean);
        return settings.siteMode === 'whitelist'
            ? known.some(host => listed(settings.whitelist, host))
            : !known.some(host => listed(settings.blacklist, host));
    }

    function normalise(raw) {
        raw = raw || {};
        const pads = {};
        for (const [key, max] of Object.entries(PAD_LIMITS)) pads[key] = milliseconds(raw.pads?.[key], DEFAULTS.pads[key], max);

        // The two lists are independent, as the app's two columns are; a word on both mutes the whole sentence.
        let wordsFull = matcher.cleanEntries(Array.isArray(raw.wordsFull) ? raw.wordsFull : DEFAULTS.wordsFull);
        let wordsSingle = matcher.cleanEntries(Array.isArray(raw.wordsSingle) ? raw.wordsSingle : DEFAULTS.wordsSingle);

        if (!(Number(raw.catalogVersion) >= catalog.VERSION))
            ({ wordsFull, wordsSingle } = catalog.addNewSpellings(wordsFull, wordsSingle, Number(raw.catalogVersion) || 1));

        const hosts = list => [...new Set((Array.isArray(list) ? list : []).map(siteHost).filter(Boolean))];

        return {
            enabled: raw.enabled !== false,
            wordsFull,
            wordsSingle,
            catalogVersion: catalog.VERSION,
            pads,
            fadeMs: milliseconds(raw.fadeMs, DEFAULTS.fadeMs, FADE_LIMIT),
            censorCharacter: isValidCensorCharacter(raw.censorCharacter) ? raw.censorCharacter : DEFAULTS.censorCharacter,
            muteMaskedWords: raw.muteMaskedWords !== false,
            pauseForOtherSubtitles: raw.pauseForOtherSubtitles === true,
            languages: (Array.isArray(raw.languages) && raw.languages.length ? raw.languages : DEFAULTS.languages)
                .map(code => String(code).trim().toLowerCase()).filter(Boolean),
            liveWordMs: milliseconds(raw.liveWordMs, DEFAULTS.liveWordMs, 5000, 100),
            keepCaptionsOn: raw.keepCaptionsOn !== false,
            showCaptions: typeof raw.showCaptions === 'boolean' ? raw.showCaptions : null,
            markers: raw.markers !== false,
            siteMode: raw.siteMode === 'whitelist' ? 'whitelist' : 'blacklist',
            // Sites paused before there were lists are the blacklist.
            blacklist: hosts(Array.isArray(raw.blacklist) ? raw.blacklist : raw.pausedSites),
            whitelist: hosts(Array.isArray(raw.whitelist) ? raw.whitelist : DEFAULTS.whitelist),
        };
    }

    // Accepts this extension's own export, the old userscript's, or the Windows app's settings.json.
    // Only what the file holds changes; everything else stays as it is.
    function importSettings(text, current) {
        let data;
        try {
            data = typeof text === 'string' ? JSON.parse(text) : text;
        } catch {
            throw new Error('That is not valid JSON.');
        }
        if (!data || typeof data !== 'object' || Array.isArray(data)) throw new Error('That is not a settings file.');

        let incoming = data.settings && typeof data.settings === 'object' ? data.settings : data;
        if ('WordsFull' in incoming || 'WordsSingle' in incoming) {
            incoming = {
                wordsFull: incoming.WordsFull,
                wordsSingle: incoming.WordsSingle,
                // The app's settings versions 4 and 5 brought catalogs 2 and 3.
                catalogVersion: incoming.SchemaVersion >= 5 ? 3 : incoming.SchemaVersion >= 4 ? 2 : 1,
                censorCharacter: incoming.CensorCharacter,
                fadeMs: incoming.MuteFadeMs,
                pads: {
                    estimatedBefore: incoming.EstimatedPadBeforeMs, estimatedAfter: incoming.EstimatedPadAfterMs,
                    wordBefore: incoming.WordPadBeforeMs, wordAfter: incoming.WordPadAfterMs,
                    lineBefore: incoming.LinePadBeforeMs, lineAfter: incoming.LinePadAfterMs,
                },
            };
        }

        const known = Object.keys(DEFAULTS);
        const defined = object => Object.fromEntries(Object.entries(object || {}).filter(([key, value]) => value !== undefined && value !== null || key === 'showCaptions'));
        const picked = Object.fromEntries(Object.entries(defined(incoming)).filter(([key]) => known.includes(key)));
        if (Array.isArray(incoming.pausedSites) && !('blacklist' in picked)) picked.blacklist = incoming.pausedSites;
        if (Object.keys(picked).length === 0) throw new Error('No Cinema Guard settings were found in it.');

        // Lists exported before the catalog had a version get the spellings added since.
        if (('wordsFull' in picked || 'wordsSingle' in picked) && !('catalogVersion' in picked)) picked.catalogVersion = 1;

        const base = normalise(current);
        return normalise({ ...base, ...picked, pads: { ...base.pads, ...defined(incoming.pads) } });
    }

    CG.settings = { DEFAULTS, PAD_LIMITS, FADE_LIMIT, isValidCensorCharacter, siteHost, listed, siteOn, normalise, importSettings, defaults: () => normalise(DEFAULTS) };
})();
