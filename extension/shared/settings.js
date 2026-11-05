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

        // Sites (host names) where filtering is paused.
        pausedSites: [],
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

    function normalise(raw) {
        raw = raw || {};
        const pads = {};
        for (const [key, max] of Object.entries(PAD_LIMITS)) pads[key] = milliseconds(raw.pads?.[key], DEFAULTS.pads[key], max);

        // The two lists are independent, as the app's two columns are; a word on both mutes the whole sentence.
        let wordsFull = matcher.cleanEntries(Array.isArray(raw.wordsFull) ? raw.wordsFull : DEFAULTS.wordsFull);
        let wordsSingle = matcher.cleanEntries(Array.isArray(raw.wordsSingle) ? raw.wordsSingle : DEFAULTS.wordsSingle);

        if (!(Number(raw.catalogVersion) >= catalog.VERSION))
            ({ wordsFull, wordsSingle } = catalog.addNewSpellings(wordsFull, wordsSingle));

        const hosts = list => [...new Set((Array.isArray(list) ? list : []).map(host => String(host).trim().toLowerCase()).filter(Boolean))];

        return {
            enabled: raw.enabled !== false,
            wordsFull,
            wordsSingle,
            catalogVersion: catalog.VERSION,
            pads,
            fadeMs: milliseconds(raw.fadeMs, DEFAULTS.fadeMs, FADE_LIMIT),
            censorCharacter: isValidCensorCharacter(raw.censorCharacter) ? raw.censorCharacter : DEFAULTS.censorCharacter,
            muteMaskedWords: raw.muteMaskedWords !== false,
            languages: (Array.isArray(raw.languages) && raw.languages.length ? raw.languages : DEFAULTS.languages)
                .map(code => String(code).trim().toLowerCase()).filter(Boolean),
            liveWordMs: milliseconds(raw.liveWordMs, DEFAULTS.liveWordMs, 5000, 100),
            keepCaptionsOn: raw.keepCaptionsOn !== false,
            showCaptions: typeof raw.showCaptions === 'boolean' ? raw.showCaptions : null,
            markers: raw.markers !== false,
            pausedSites: hosts(raw.pausedSites),
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
                catalogVersion: incoming.SchemaVersion >= 4 ? catalog.VERSION : 1,
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
        if (Object.keys(picked).length === 0) throw new Error('No Cinema Guard settings were found in it.');

        // Lists exported before the catalog had a version get the spellings added since.
        if (('wordsFull' in picked || 'wordsSingle' in picked) && !('catalogVersion' in picked)) picked.catalogVersion = 1;

        const base = normalise(current);
        return normalise({ ...base, ...picked, pads: { ...base.pads, ...defined(incoming.pads) } });
    }

    CG.settings = { DEFAULTS, PAD_LIMITS, FADE_LIMIT, isValidCensorCharacter, normalise, importSettings, defaults: () => normalise(DEFAULTS) };
})();
