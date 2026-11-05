// ==UserScript==
// @name         Cinema Guard for YouTube
// @namespace    http://tampermonkey.net/
// @version      2026-11-05.1
// @description  Mutes YouTube videos while swearing is spoken, using the video's captions and Cinema Guard's word lists.
// @author       nieuwoudtza
// @match        https://www.youtube.com/*
// @icon         https://www.google.com/s2/favicons?sz=64&domain=youtube.com
// @run-at       document-start
// @grant        none
// ==/UserScript==

(function () {
    'use strict';

    // ---- Settings ----
    // Defaults copied from Cinema Guard (%LOCALAPPDATA%\CinemaGuard\settings.json). Changes made in the panel are
    // saved in this browser's storage for youtube.com.

    const DEFAULTS = {
        enabled: true,

        // "Mute the whole sentence"
        wordsFull: [
            'JEESUZ', 'JEEZUS', 'JEEZUSS', 'JEEZUZ', 'JESUS', "JESUS'S", 'JESUSES',
            'CHRIST', 'CHRISTS', "CHRIST'S", 'CHRISSAKE', 'CHRISSAKES', 'CHRISTSAKE', 'CHRISTSAKES',
        ],

        // "Mute the word only"
        wordsSingle: [
            'BUMFUCK', 'CLUSTERFUCK', 'CLUSTERFUCKS', 'DUMBFUCK', 'FUCK', 'FUCKABLE', 'FUCKBOI', 'FUCKBOIS',
            'FUCKBOY', 'FUCKBOYS', 'FUCKED', 'FUCKEDUP', 'FUCKED-UP', 'FUCKER', 'FUCKERS', 'FUCKERY', 'FUCKFACE',
            'FUCKFACES', 'FUCKFEST', 'FUCKFESTS', 'FUCKGIRL', 'FUCKGIRLS', 'FUCKHEAD', 'FUCKHEADS', 'FUCKHOLE',
            'FUCKHOLES', 'FUCKIN', 'FUCKING', 'FUCKITY', 'FUCKNUT', 'FUCKNUTS', 'FUCKNUTTED', 'FUCKNUTTING',
            'FUCKOFF', 'FUCKOFFS', 'FUCKS', 'FUCKSAKE', 'FUCKSSAKE', 'FUCKSTICK', 'FUCKSTICKS', 'FUCKTARD',
            'FUCKTARDS', 'FUCKUP', 'FUCKUPS', 'FUCKWAD', 'FUCKWIT', 'FUCKWITS', 'SKULLFUCKED', 'SKULLFUCKING',
            'UNFUCK', 'UNFUCKED', 'UNFUCKING', 'UNFUCKS',
            'DUMBFUCKS', 'FUCKA', 'FUCKALL', 'FUCKAS', 'FUCKTON', 'FUCKTONS', 'FUCKWADS', 'MINDFUCK', 'MINDFUCKED',
            'MINDFUCKING', 'MINDFUCKS',
            'CUNT', 'CUNTS', 'CUNTISH', 'CUNTFACE',
            'MOTHERFUCKER', 'MOTHERFUCKERIN', 'MOTHERFUCKERING', 'MOTHERFUCKERS', 'MOTHERFUCKING', 'MOTHERFUCKIN',
            'MOTHERFUCK', 'MOTHERFUCKA', 'MOTHERFUCKAS', 'MOTHERFUCKED', 'MOTHERFUCKS', 'MOTHAFUCKA', 'MOTHAFUCKAS',
            'MOTHAFUCKER', 'MOTHAFUCKERS', 'MUTHAFUCKA', 'MUTHAFUCKAS', 'MUTHAFUCKER', 'MUTHAFUCKERS', 'MUTHERFUCKER',
            'MUTHERFUCKERS',
            'PUSSY', 'PUSSIES',
            'GOD', 'HOLY SHIT',
        ],

        // Milliseconds added around each mute.
        pads: {
            estimatedBefore: 500, estimatedAfter: 500, // a word whose time is guessed from its place in the caption line
            wordBefore: 150, wordAfter: 250,           // a word timed by YouTube's auto-generated captions
            lineBefore: 250, lineAfter: 500,           // a whole sentence
        },

        // How long a word is muted when it is caught live as it appears in auto-generated captions.
        liveWordMs: 800,

        // Blanks listed words in the captions on screen: one symbol or a space; '' leaves the captions alone.
        censorCharacter: ' ',

        // Words the captions already hide (f***, [bleep], YouTube's [ __ ]) are muted too, as Cinema Guard does.
        muteMaskedWords: true,

        // Caption languages to use, best first. Other languages are used only when none of these exist.
        languages: ['en'],

        // Captions are kept on so the words can be read; this is whether the viewer sees them. Until it is set,
        // it is taken from whether captions were on the first time a video plays.
        showCaptions: null,
    };

    // The same limits as Cinema Guard's settings.
    const PAD_LIMITS = { estimatedBefore: 5000, estimatedAfter: 5000, wordBefore: 2000, wordAfter: 2000, lineBefore: 5000, lineAfter: 5000 };
    const STORAGE_KEY = 'cinema-guard-youtube';

    // ---- Matching (port of SwearMatcher and SubtitleFormatter) ----

    const SNAP_WINDOW = 2.0;
    const MERGE_GAP = 0.05;
    const MINIMUM_RANGE = 0.02;
    const MASKED_LABEL = 'MASKED';
    const LOG = '[Cinema Guard]';

    // Upper-cases one character at a time so match positions still line up with the original text.
    function upper(text) {
        let result = '';
        for (const character of text) {
            const upperCase = character.toUpperCase();
            result += upperCase.length === character.length ? upperCase : character;
        }
        return result;
    }

    function cleanEntries(list) {
        const entries = new Set();
        for (const word of list) {
            const cleaned = upper(String(word).split(/\s+/).filter(Boolean).join(' '));
            if (cleaned) entries.add(cleaned);
        }
        return [...entries];
    }

    // Cinema Guard's rule: one symbol, or a space. '' turns blanking off here.
    const isValidCensorCharacter = value => value === '' || value === ' ' ||
        (typeof value === 'string' && value.length === 1 && !/[\p{L}\p{N}\s\p{C}]/u.test(value));

    function normalise(raw) {
        const milliseconds = (value, fallback, max) => {
            const number = Number(value);
            return value !== null && value !== '' && Number.isFinite(number) ? Math.min(max, Math.max(0, Math.round(number))) : fallback;
        };

        const pads = {};
        for (const [key, max] of Object.entries(PAD_LIMITS)) pads[key] = milliseconds(raw.pads?.[key], DEFAULTS.pads[key], max);

        // A word on both lists mutes the whole sentence.
        const wordsFull = cleanEntries(Array.isArray(raw.wordsFull) ? raw.wordsFull : DEFAULTS.wordsFull);
        const wordsSingle = cleanEntries(Array.isArray(raw.wordsSingle) ? raw.wordsSingle : DEFAULTS.wordsSingle).filter(word => !wordsFull.includes(word));

        return {
            enabled: raw.enabled !== false,
            wordsFull,
            wordsSingle,
            pads,
            liveWordMs: Math.max(100, milliseconds(raw.liveWordMs, DEFAULTS.liveWordMs, 5000)),
            censorCharacter: isValidCensorCharacter(raw.censorCharacter) ? raw.censorCharacter : DEFAULTS.censorCharacter,
            muteMaskedWords: raw.muteMaskedWords !== false,
            languages: (Array.isArray(raw.languages) ? raw.languages : DEFAULTS.languages).map(code => String(code).trim().toLowerCase()).filter(Boolean),
            showCaptions: typeof raw.showCaptions === 'boolean' ? raw.showCaptions : null,
        };
    }

    function loadSettings() {
        let saved = null;
        try { saved = JSON.parse(localStorage.getItem(STORAGE_KEY) || 'null'); } catch { /* unreadable: defaults */ }
        return normalise({ ...DEFAULTS, ...(saved || {}) });
    }

    // Accepts this script's own export, or Cinema Guard's settings.json.
    function importSettings(text) {
        const data = JSON.parse(text);
        if (!data || typeof data !== 'object') throw new Error('That is not a settings file.');

        let incoming = data;
        if ('WordsFull' in data || 'WordsSingle' in data) {
            incoming = {
                wordsFull: data.WordsFull,
                wordsSingle: data.WordsSingle,
                censorCharacter: data.CensorCharacter,
                pads: {
                    estimatedBefore: data.EstimatedPadBeforeMs, estimatedAfter: data.EstimatedPadAfterMs,
                    wordBefore: data.WordPadBeforeMs, wordAfter: data.WordPadAfterMs,
                    lineBefore: data.LinePadBeforeMs, lineAfter: data.LinePadAfterMs,
                },
            };
        }

        const defined = object => Object.fromEntries(Object.entries(object || {}).filter(([, value]) => value !== undefined));
        return normalise({ ...settings, ...defined(incoming), pads: { ...settings.pads, ...defined(incoming.pads) } });
    }

    let settings = loadSettings();
    let FULL = [];
    let SINGLE = [];
    let ALL = [];
    let PADS = {};
    let MASK = '';

    // Turns the saved settings into what matching and muting use.
    function applySettings() {
        FULL = settings.wordsFull;
        SINGLE = settings.wordsSingle;
        ALL = [...FULL, ...SINGLE];
        PADS = Object.fromEntries(Object.entries(settings.pads).map(([key, ms]) => [key, ms / 1000]));
        MASK = settings.censorCharacter === ' ' ? '\u00A0' : settings.censorCharacter;
    }
    applySettings();

    const LETTER = /\p{L}/u;
    const isLetter = character => character !== undefined && LETTER.test(character);
    const escapeRegex = value => value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

    const phraseRegexes = new Map();
    function phraseRegex(phrase) {
        let regex = phraseRegexes.get(phrase);
        if (!regex) {
            // A phrase matches across any punctuation between its words: "Holy... shit!", "holy-shit".
            regex = new RegExp('(?<!\\p{L})' + phrase.split(' ').map(escapeRegex).join('[^\\p{L}\\p{N}]+') + '(?!\\p{L})', 'gu');
            phraseRegexes.set(phrase, regex);
        }
        return regex;
    }

    // Whole-word matching: the character either side of an entry must not be a letter,
    // so FUCK'S matches and SCUNTHORPE never matches CUNT.
    function find(text, entries) {
        const matches = [];
        if (!text || entries.length === 0) return matches;

        const upperText = upper(text);

        for (const entry of entries) {
            if (entry.includes(' ')) {
                for (const match of upperText.matchAll(phraseRegex(entry)))
                    matches.push({ index: match.index, length: match[0].length, entry });
                continue;
            }

            let searchFrom = 0;
            while (searchFrom <= upperText.length - entry.length) {
                const found = upperText.indexOf(entry, searchFrom);
                if (found < 0) break;

                if (!isLetter(upperText[found - 1]) && !isLetter(upperText[found + entry.length]))
                    matches.push({ index: found, length: entry.length, entry });

                searchFrom = found + 1;
            }
        }

        return matches.sort((a, b) => a.index - b.index);
    }

    const MASKED_TOKEN = /(?<![A-Za-z])[A-Za-z][A-Za-z']*(?:[*#@]{2,}[A-Za-z'*#@]*)/g;
    // [ __ ] is how YouTube's auto-generated captions hide a swear word.
    const BLEEP_MARKER = /\[(?:bleep|censored|expletive)\]|\((?:bleep|censored|expletive)\)|\[\s*_{2,}\s*\]/gi;

    function findMasked(text) {
        if (!settings.muteMaskedWords || !text) return [];
        return [...text.matchAll(MASKED_TOKEN), ...text.matchAll(BLEEP_MARKER)]
            .map(match => ({ index: match.index, length: match[0].length, entry: MASKED_LABEL }))
            .sort((a, b) => a.index - b.index);
    }

    function formatLine(line) {
        let value = line.replace(/[\u00A0\u200B\uFEFF]/g, ' ')
            .replace(/<[^>]*>/g, '')
            .replace(/\{[^}]*\}/g, '')
            .replace(/\[[^\]]*\]/g, '');

        // Unbalanced brackets: keep what a viewer would see on screen.
        const open = value.indexOf('[');
        if (open >= 0 && value.indexOf(']', open) < 0) value = value.slice(0, open);

        const close = value.indexOf(']');
        if (close >= 0) value = value.slice(close + 1);

        value = value.replace(/[\u2018\u2019]/g, "'").replace(/[\u201C\u201D]/g, '"');
        value = value.replace(/[^\p{L}\p{Nd} \t,.?'"!$%-]/gu, '');
        return value.replace(/\s+/g, ' ').trim();
    }

    const formatCue = text => text.replace(/\r\n/g, '\n').split('\n').map(formatLine).join(' ').trim();

    // ---- Captions ----

    // Cues are { start, end, text, words }. words is null unless YouTube timed each word (auto-generated captions).
    function parseJson3(data) {
        const events = (data.events || []).filter(event => event.segs && event.tStartMs !== undefined);
        const timed = events.some(event => event.segs.some(seg => seg.tOffsetMs !== undefined));
        const cues = [];

        for (const event of events) {
            const start = event.tStartMs / 1000;
            const end = start + (event.dDurationMs || 0) / 1000;
            const words = [];
            let text = '';

            for (const seg of event.segs) {
                const piece = seg.utf8 || '';
                if (piece.trim()) words.push({ from: text.length, to: text.length + piece.length, start: start + (seg.tOffsetMs || 0) / 1000, cueEnd: end });
                text += piece;
            }

            if (text.trim()) cues.push({ start, end, text, words: timed ? words : null });
        }

        if (timed) {
            // A word lasts until the next word starts, but no longer than its line or a second.
            const all = cues.flatMap(cue => cue.words);
            all.forEach((word, i) => {
                const next = all[i + 1];
                let end = Math.min(word.cueEnd, word.start + 1);
                if (next && next.start > word.start) end = Math.min(end, next.start);
                word.end = Math.max(end, word.start + 0.1);
            });
        }

        return cues;
    }

    const decodeEntities = value => value.replace(/&(#x[0-9a-f]+|#\d+|amp|lt|gt|quot|apos);/gi, (_, entity) => {
        const name = entity.toLowerCase();
        if (name.startsWith('#x')) return String.fromCodePoint(parseInt(name.slice(2), 16));
        if (name.startsWith('#')) return String.fromCodePoint(parseInt(name.slice(1), 10));
        return { amp: '&', lt: '<', gt: '>', quot: '"', apos: "'" }[name];
    });

    const attribute = (attributes, name) => {
        const match = new RegExp('\\b' + name + '="([^"]*)"').exec(attributes);
        return match ? Number(match[1]) : undefined;
    };

    // XML captions (srv3 and the older format), read without DOMParser because of YouTube's Trusted Types.
    function parseXml(xml) {
        const events = [];
        const text = value => decodeEntities(value.replace(/<br\s*\/?>/gi, '\n').replace(/<[^>]*>/g, ''));

        for (const p of xml.matchAll(/<p\b([^>]*)>([\s\S]*?)<\/p>/g)) {
            const segs = [...p[2].matchAll(/<s\b([^>]*)>([\s\S]*?)<\/s>/g)]
                .map(s => ({ utf8: text(s[2]), tOffsetMs: attribute(s[1], 't') }));
            events.push({ tStartMs: attribute(p[1], 't'), dDurationMs: attribute(p[1], 'd'), segs: segs.length ? segs : [{ utf8: text(p[2]) }] });
        }

        for (const line of xml.matchAll(/<text\b([^>]*)>([\s\S]*?)<\/text>/g)) {
            events.push({
                tStartMs: (attribute(line[1], 'start') || 0) * 1000,
                dDurationMs: (attribute(line[1], 'dur') || 0) * 1000,
                segs: [{ utf8: decodeEntities(text(line[2])) }],
            });
        }

        return parseJson3({ events });
    }

    const vttTime = value => value.split(':').reduce((total, part) => total * 60 + Number(part.replace(',', '.')), 0);

    function parseVtt(vtt) {
        const events = [];
        for (const block of vtt.replace(/\r/g, '').split(/\n{2,}/)) {
            const lines = block.split('\n');
            const timing = lines.findIndex(line => line.includes('-->'));
            if (timing < 0) continue;

            const [from, to] = lines[timing].split('-->').map(part => vttTime(part.trim().split(/\s+/)[0]));
            const text = decodeEntities(lines.slice(timing + 1).join('\n').replace(/<[^>]*>/g, ''));
            events.push({ tStartMs: from * 1000, dDurationMs: (to - from) * 1000, segs: [{ utf8: text }] });
        }
        return parseJson3({ events });
    }

    function parseCaptions(body) {
        const trimmed = (body || '').replace(/^\)\]\}'/, '').trim();
        try {
            if (trimmed.startsWith('{')) return parseJson3(JSON.parse(trimmed));
            if (trimmed.startsWith('<')) return parseXml(trimmed);
            if (trimmed.startsWith('WEBVTT')) return parseVtt(trimmed);
        } catch (error) {
            console.warn(LOG, 'Could not read captions', error);
        }
        return [];
    }

    // ---- Mute windows (port of HitCollector) ----

    // A hit is one detection: { word, mute, at, start, end, line, index, length, dropped }.
    // at is when the word is said; start and end are the padded mute; line, index and length show it in context.
    function collectHits(tracks) {
        const hits = [];
        const timedHits = [];

        // Auto-generated captions time every word, like Cinema Guard's speech model.
        for (const cues of tracks.filter(cues => cues.some(cue => cue.words))) {
            for (const cue of cues) {
                if (!cue.words || cue.words.length === 0) continue;

                const span = (from, to) => {
                    const words = cue.words.filter(word => word.from < to && word.to > from);
                    return words.length ? { start: words[0].start, end: words[words.length - 1].end } : null;
                };
                const lineStart = cue.words[0].start;
                const lineEnd = cue.words[cue.words.length - 1].end;

                for (const match of find(cue.text, FULL)) {
                    hits.push({
                        word: match.entry, mute: 'Sentence', at: span(match.index, match.index + match.length)?.start ?? lineStart,
                        start: lineStart - PADS.lineBefore, end: lineEnd + PADS.lineAfter, line: cue.text, index: match.index, length: match.length,
                    });
                }

                for (const match of [...find(cue.text, SINGLE), ...findMasked(cue.text)]) {
                    const word = span(match.index, match.index + match.length);
                    if (!word) continue;
                    const hit = {
                        word: match.entry, mute: 'Word, timed', at: word.start,
                        start: word.start - PADS.wordBefore, end: word.end + PADS.wordAfter, line: cue.text, index: match.index, length: match.length,
                    };
                    timedHits.push(hit);
                    hits.push(hit);
                }
            }
        }

        // A guess from a caption line is dropped when a timed word with the same label starts nearby.
        const addEstimated = (from, to, match, line) => {
            const dropped = timedHits.some(hit => Math.abs(hit.at - from) <= SNAP_WINDOW && (hit.word === match.entry || match.entry === MASKED_LABEL));
            hits.push({
                word: match.entry, mute: 'Word, guessed', at: from,
                start: from - PADS.estimatedBefore, end: to + PADS.estimatedAfter, line, index: match.index, length: match.length, dropped,
            });
        };

        for (const cues of tracks.filter(cues => !cues.some(cue => cue.words))) {
            for (const cue of cues) {
                const duration = cue.end - cue.start;
                if (duration <= 0) continue;

                const formatted = formatCue(cue.text);
                if (formatted.length === 0) continue;

                // The whole line, widened: captions are often shown a little before or after the line is spoken.
                for (const match of find(formatted, FULL)) {
                    hits.push({
                        word: match.entry, mute: 'Sentence', at: cue.start,
                        start: cue.start - PADS.lineBefore, end: cue.end + PADS.lineAfter, line: formatted, index: match.index, length: match.length,
                    });
                }

                // A word's time is guessed from where it sits in the line.
                const at = index => cue.start + index / formatted.length * duration;
                for (const match of find(formatted, SINGLE)) {
                    if (match.entry.includes(' ')) {
                        addEstimated(at(match.index), at(match.index + match.length), match, formatted);
                    } else {
                        const middle = at(match.index + match.length / 2);
                        addEstimated(middle, middle, match, formatted);
                    }
                }

                const raw = cue.text.replace(/\r\n|\n/g, ' ');
                for (const masked of findMasked(raw)) {
                    const estimate = cue.start + (masked.index + masked.length / 2) / raw.length * duration;
                    addEstimated(estimate, estimate, masked, raw);
                }
            }
        }

        return hits.sort((a, b) => a.at - b.at);
    }

    function buildRanges(hits, duration) {
        const ranges = hits
            .map(hit => ({ start: Math.max(0, hit.start), end: duration > 0 ? Math.min(duration, hit.end) : hit.end }))
            .filter(range => range.end > range.start)
            .sort((a, b) => a.start - b.start);

        const merged = [];
        for (const range of ranges) {
            const last = merged[merged.length - 1];
            if (last && range.start <= last.end + MERGE_GAP) last.end = Math.max(last.end, range.end);
            else merged.push({ ...range });
        }

        return merged.filter(range => range.end - range.start >= MINIMUM_RANGE);
    }

    function rangeAt(ranges, time) {
        let low = 0;
        let high = ranges.length - 1;
        while (low <= high) {
            const middle = (low + high) >> 1;
            if (time < ranges[middle].start) high = middle - 1;
            else if (time >= ranges[middle].end) low = middle + 1;
            else return true;
        }
        return false;
    }

    // ---- Caption requests ----
    // YouTube often answers a caption request with nothing unless it carries the player's proof-of-origin token,
    // so the player's own caption requests are watched and their token reused.

    const originalFetch = window.fetch;
    const captured = new Map(); // video id -> the best caption request the player made: { params, body, status, cues }
    const waiting = new Map();  // video id -> callback for each caption request while a capture is in progress

    function onTimedText(href, body, status) {
        const url = new URL(href, location.origin);
        const videoId = url.searchParams.get('v');
        if (!videoId) return;

        const capture = { params: url.searchParams, body, status, cues: parseCaptions(body) };
        // A request that came back empty never replaces one that held captions.
        if (capture.cues.length || !captured.get(videoId)?.cues.length) captured.set(videoId, capture);
        waiting.get(videoId)?.(capture);
    }

    window.fetch = function (input) {
        const promise = originalFetch.apply(this, arguments);
        try {
            const href = input instanceof Request ? input.url : String(input);
            if (href.includes('/api/timedtext')) {
                promise.then(response => response.clone().text().then(body => onTimedText(href, body, response.status))).catch(() => {});
            }
        } catch { /* never break the page's own request */ }
        return promise;
    };

    const originalOpen = XMLHttpRequest.prototype.open;
    XMLHttpRequest.prototype.open = function (method, url) {
        try {
            const href = String(url);
            if (href.includes('/api/timedtext')) {
                this.addEventListener('load', () => {
                    try {
                        const body = this.responseType === 'json' ? JSON.stringify(this.response)
                            : this.responseType === 'arraybuffer' ? new TextDecoder().decode(this.response)
                            : this.responseType === '' || this.responseType === 'text' ? this.responseText : '';
                        onTimedText(href, body, this.status);
                    } catch { /* ignore */ }
                });
            }
        } catch { /* never break the page's own request */ }
        return originalOpen.apply(this, arguments);
    };

    const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));

    function trackName(track) {
        const name = track.name?.simpleText || track.name?.runs?.map(run => run.text).join('') || track.languageCode || 'Unknown';
        return track.kind === 'asr' && !/auto/i.test(name) ? `${name} (auto-generated)` : name;
    }

    function pickTracks(response) {
        const tracks = response?.captions?.playerCaptionsTracklistRenderer?.captionTracks || [];
        const preferred = track => {
            const code = (track.languageCode || '').toLowerCase();
            return settings.languages.some(language => code === language || code.startsWith(language + '-'));
        };

        const manual = tracks.filter(track => track.kind !== 'asr');
        const auto = tracks.filter(track => track.kind === 'asr');

        // Written captions have the right words; auto-generated ones have the right times. Both are used when present.
        const picked = [manual.find(preferred), auto.find(preferred)].filter(Boolean);
        return picked.length ? picked : [manual[0] || auto[0]].filter(Boolean);
    }

    async function fetchTrack(track, token) {
        const url = new URL(track.baseUrl, location.origin);
        if (token) {
            for (const [key, value] of token) {
                if (key !== 'tlang' && !url.searchParams.has(key)) url.searchParams.set(key, value);
            }
        }
        url.searchParams.set('fmt', 'json3');

        const response = await originalFetch(url.href, { credentials: 'include' });
        const body = await response.text();
        return { cues: response.ok ? parseCaptions(body) : [], status: response.status, body };
    }

    const isSameTrack = (params, track) => !params.get('tlang') &&
        params.get('lang') === track.languageCode && (params.get('kind') || '') === (track.kind || '');

    // What came back, for the load log: enough to tell an empty answer from one in a format that is not understood.
    function describeAnswer(status, body) {
        const text = (body || '').trim();
        return `HTTP ${status}, ${text.length} characters${text.length ? `, starting "${text.slice(0, 40).replace(/\s+/g, ' ')}"` : ''}`;
    }

    const describeRequest = params => [
        `${params.get('lang') || '?'}${params.get('kind') === 'asr' ? ' auto' : ''}${params.get('tlang') ? ` translated to ${params.get('tlang')}` : ''}`,
        `fmt=${params.get('fmt') || 'default'}`,
        params.has('pot') ? 'with token' : 'no token',
    ].join(', ');

    const subtitlesButton = player => player.querySelector('.ytp-subtitles-button');
    const captionsOn = player => subtitlesButton(player)?.getAttribute('aria-pressed') === 'true';

    // The script's own presses of the CC button, timed so they are never mistaken for the viewer's.
    let lastCaptionClick = -Infinity;
    function clickCaptions(player) {
        lastCaptionClick = performance.now();
        subtitlesButton(player)?.click();
    }

    // Makes the player request captions itself: turns them on out of sight, or off and on again when they are
    // already on. Requests that come back empty are reported and waited past.
    function captureFromPlayer(player, videoId, report) {
        if (captured.get(videoId)?.cues.length) return Promise.resolve(captured.get(videoId));

        return new Promise(resolve => {
            const button = subtitlesButton(player);
            const wasOn = captionsOn(player);
            let finished = false;

            const finish = capture => {
                if (finished) return;
                finished = true;
                waiting.delete(videoId);
                if (captionsOn(player) !== (wasOn || settings.enabled)) clickCaptions(player);
                setTimeout(() => player.classList.remove('cg-capturing'), 300);
                resolve(capture || null);
            };

            waiting.set(videoId, capture => {
                report(capture);
                if (capture.cues.length) finish(capture);
            });
            if (button && !wasOn) {
                player.classList.add('cg-capturing');
                clickCaptions(player);
            } else if (button) {
                clickCaptions(player);
                setTimeout(() => { if (!finished && !captionsOn(player)) clickCaptions(player); }, 300);
            }
            setTimeout(() => finish(captured.get(videoId)), 8000);
        });
    }

    // ---- Video tracking ----

    // The video being filtered: state is loading, ready, none (no captions) or failed.
    let current = null;
    let mutedByUs = false;
    let observedPlayer = null;

    const getPlayer = () => document.getElementById('movie_player');

    function note(session, text) {
        session.log.push({ at: (performance.now() - session.startedAt) / 1000, text });
        console.info(LOG, text);
        if (session === current) refreshPanel();
    }

    function settle(session, player, state, message) {
        session.state = state;
        session.message = message;
        note(session, message);
        status(player, message);
    }

    const plural = (count, word) => `${count} ${word}${count === 1 ? '' : 's'}`;
    const readyMessage = session => session.ranges.length ? `${plural(session.ranges.length, 'mute')} ready` : 'No swearing found in the captions';

    // Works the detections out again from the loaded captions, after a word or a padding changes.
    function recompute(session) {
        if (!session?.cueTracks) return;
        session.hits = collectHits(session.cueTracks);
        session.ranges = buildRanges(session.hits.filter(hit => !hit.dropped), getPlayer()?.querySelector('video')?.duration);
        if (session.state === 'ready') session.message = readyMessage(session);
    }

    async function load(player, videoId) {
        const session = { videoId, state: 'loading', message: 'Loading captions…', startedAt: performance.now(), sources: [], hits: [], liveHits: [], ranges: [], log: [] };
        current = session;
        release();
        resetLive();

        note(session, `Video ${videoId}`);

        let response = null;
        for (let attempt = 0; attempt < 40 && current === session; attempt++) {
            response = player.getPlayerResponse?.();
            if (response?.videoDetails?.videoId === videoId) break;
            await sleep(250);
        }
        if (current !== session) return;
        if (response?.videoDetails?.videoId !== videoId) note(session, 'The player did not give the caption list for this video within 10 seconds');

        const available = response?.captions?.playerCaptionsTracklistRenderer?.captionTracks || [];
        session.autoOnly = available.length > 0 && available.every(track => track.kind === 'asr');
        note(session, available.length ? `Caption tracks: ${available.map(trackName).join(', ')}` : 'The video lists no caption tracks');

        const tracks = pickTracks(response);
        if (tracks.length === 0) {
            settle(session, player, 'none', 'No captions on this video, so it is not filtered');
            return;
        }

        // The first time ever, whether the viewer had captions on decides whether they see them from now on.
        // The player is given a moment to apply its own caption setting first.
        if (settings.showCaptions === null) {
            await sleep(1000);
            if (current !== session) return;
            settings.showCaptions = captionsOn(player);
            saveSettings();
            note(session, settings.showCaptions ? 'Captions were on, so they stay in view' : 'Captions were off, so they are kept on out of sight');
        }
        syncCaptions(player);

        const loaded = new Map(); // track -> { cues, via }
        const tryFetch = async (track, token, via) => {
            try {
                const result = await fetchTrack(track, token);
                if (result.cues.length) {
                    loaded.set(track, { cues: result.cues, via });
                    note(session, `${trackName(track)}: ${result.cues.length} lines (${via.toLowerCase()})`);
                } else {
                    note(session, `${trackName(track)}: ${via.toLowerCase()} gave no captions (${describeAnswer(result.status, result.body)})`);
                }
            } catch (error) {
                note(session, `${trackName(track)}: ${via.toLowerCase()} failed (${error.message})`);
            }
        };

        await Promise.all(tracks.map(track => tryFetch(track, null, 'Direct request')));

        if (loaded.size < tracks.length && current === session) {
            note(session, captured.get(videoId)?.cues.length ? 'Using the caption request the player already made'
                : captionsOn(player) ? 'Turning captions off and on to catch the player\'s own request'
                : 'Turning captions on out of sight to catch the player\'s own request');

            const capture = await captureFromPlayer(player, videoId, request => note(session,
                `Player requested ${describeRequest(request.params)}: ${request.cues.length ? `${request.cues.length} lines` : describeAnswer(request.status, request.body)}`));
            if (current !== session) return;
            if (!capture) note(session, 'The player made no caption request that could be caught');

            for (const track of tracks.filter(track => capture && !loaded.has(track))) {
                if (isSameTrack(capture.params, track) && capture.cues.length) {
                    loaded.set(track, { cues: capture.cues, via: 'Player\'s request' });
                    note(session, `${trackName(track)}: ${capture.cues.length} lines (player's request)`);
                } else {
                    await tryFetch(track, capture.params, 'Player\'s token');
                }
            }

            // Whatever the player loaded itself is better than nothing, even when it is not a track picked above.
            if (loaded.size === 0 && capture?.cues.length) {
                const track = { languageCode: capture.params.get('lang') || '?', kind: capture.params.get('kind') || '' };
                loaded.set(track, { cues: capture.cues, via: 'Player\'s request' });
                note(session, `${trackName(track)}: ${capture.cues.length} lines (player's request)`);
            }
        }
        if (current !== session) return;

        if (loaded.size === 0) {
            settle(session, player, 'failed', 'Could not load the captions, so muting follows the captions on screen');
            return;
        }

        session.sources = [...loaded].map(([track, { cues, via }]) => ({ name: trackName(track), via, lines: cues.length }));
        session.cueTracks = [...loaded.values()].map(value => value.cues);
        recompute(session);

        note(session, `${plural(session.hits.filter(hit => !hit.dropped).length, 'detection')}, ${plural(session.ranges.length, 'mute range')}`);
        settle(session, player, 'ready', readyMessage(session));
    }

    function checkVideo() {
        const player = getPlayer();
        if (!player) return;
        ensureButton(player);
        if (player.classList.contains('ad-showing')) return;

        if (player !== observedPlayer) {
            observedPlayer = player;
            new MutationObserver(() => onCaptionsChanged(player)).observe(player, { childList: true, subtree: true, characterData: true });
        }

        const videoId = player.getVideoData?.()?.video_id;
        if (videoId && videoId !== current?.videoId) load(player, videoId);
        else syncCaptions(player);
    }

    // ---- Captions kept on ----
    // While filtering, captions stay on so the words on screen can always be read. Whether the viewer sees them is
    // settings.showCaptions, which the CC button switches instead of turning captions off.

    const captionWatch = { videoId: null, wasOn: false };

    function captionsAvailable(player) {
        const button = subtitlesButton(player);
        return !!button && !button.disabled && button.getAttribute('aria-disabled') !== 'true' &&
            getComputedStyle(button).display !== 'none' && current?.state !== 'none';
    }

    function syncCaptions(player) {
        player.classList.toggle('cg-hidden-captions', settings.enabled && settings.showCaptions === false);
        if (!captionsAvailable(player) || waiting.size > 0 || settings.showCaptions === null) return;

        const on = captionsOn(player);
        const videoId = current?.videoId;
        const settled = performance.now() - lastCaptionClick > 2000;

        // Turned off some other way while showing (YouTube's C key or its settings menu): kept on, but hidden.
        if (settings.enabled && settings.showCaptions && !on && settled && captionWatch.videoId === videoId && captionWatch.wasOn) {
            settings.showCaptions = false;
            saveSettings();
            player.classList.add('cg-hidden-captions');
            if (current) note(current, 'Captions were turned off, so they are kept on out of sight');
        }

        const wantOn = settings.enabled || settings.showCaptions;
        if (on !== wantOn && settled) clickCaptions(player);

        captionWatch.videoId = videoId;
        captionWatch.wasOn = captionsOn(player);
    }

    // The viewer's own CC presses only show or hide the captions while filtering; captions stay on underneath.
    function watchCaptionButton() {
        window.addEventListener('click', event => {
            if (!event.isTrusted || !event.target.closest?.('.ytp-subtitles-button')) return;
            const player = getPlayer();
            if (!player || settings.showCaptions === null) return;

            if (settings.enabled) {
                event.preventDefault();
                event.stopImmediatePropagation();
                settings.showCaptions = !settings.showCaptions;
                saveSettings();
                syncCaptions(player);
                if (current) note(current, settings.showCaptions ? 'Captions shown' : 'Captions hidden; they stay on out of sight');
                if (panel && panelTab === 'settings') refreshPanel(true);
            } else {
                // Not filtering: the button works as usual, and its new state is remembered.
                settings.showCaptions = !captionsOn(player);
                saveSettings();
            }
        }, true);
    }

    // ---- Muting ----

    function setMuted(player, video, muted) {
        if (muted) player.mute?.();
        else player.unMute?.();
        video.muted = muted;
    }

    function release() {
        const player = getPlayer();
        const video = player?.querySelector('video');
        if (mutedByUs && video) setMuted(player, video, false);
        mutedByUs = false;
    }

    function shouldMute(player, video) {
        if (!settings.enabled || !current || player.classList.contains('ad-showing')) return false;
        if (rangeAt(current.ranges, video.currentTime)) return true;
        return liveActive() && (live.hold || video.currentTime < live.until);
    }

    function tick() {
        const player = getPlayer();
        const video = player?.querySelector('video');
        if (!video) return;

        if (shouldMute(player, video)) {
            // Already muted by the viewer: leave it to them. Unmuted while we hold it: mute again.
            if (!mutedByUs && !video.muted) {
                setMuted(player, video, true);
                mutedByUs = true;
            } else if (mutedByUs && !video.muted) {
                setMuted(player, video, true);
            }
        } else if (mutedByUs) {
            setMuted(player, video, false);
            mutedByUs = false;
        }
    }

    // ---- Captions on screen ----
    // Listed words are blanked as captions appear. Until the caption file is loaded, the same check also mutes:
    // a word in auto-generated captions appears as it is said, so it is muted from then on;
    // a written caption line appears whole, so it is muted for as long as it is on screen.

    const maskedSegments = new WeakMap(); // segment -> { original, masked }, so blanked text is still matched
    // seen: each occurrence on screen, keyed by its word and the words before it -> the video time it was last seen.
    const live = { seen: new Map(), hold: false, until: 0 };
    const LIVE_FORGET_SECONDS = 6;
    // YouTube shows this hint inside the caption box when captions are turned on.
    const CAPTION_HINT = /click for settings/i;

    const liveActive = () => current && current.state !== 'ready';

    function resetLive() {
        live.seen = new Map();
        live.hold = false;
        live.until = 0;
    }

    function maskText(text) {
        const matches = find(text, ALL);
        if (matches.length === 0) return text;

        const mask = new Array(text.length).fill(false);
        for (const match of matches) {
            for (let i = match.index; i < Math.min(match.index + match.length, text.length); i++) mask[i] = true;
        }

        let result = '';
        for (let i = 0; i < text.length; i++) result += mask[i] ? MASK : text[i];
        return result;
    }

    function onCaptionsChanged(player) {
        if (!settings.enabled) return;
        const lines = [];

        for (const segment of player.querySelectorAll('.ytp-caption-segment')) {
            const text = segment.textContent;
            const known = maskedSegments.get(segment);
            const original = known && known.masked === text ? known.original : text;
            if (!CAPTION_HINT.test(original)) lines.push(original);

            if (MASK) {
                const masked = maskText(original);
                if (masked !== text) {
                    maskedSegments.set(segment, { original, masked });
                    segment.textContent = masked;
                }
            }
        }

        if (liveActive()) checkLive(player, lines);
    }

    // Auto-generated captions show each word as it is said; written ones show a whole line at once.
    function showingAutoCaptions(player) {
        try {
            const track = player.getOption?.('captions', 'track');
            if (track?.languageCode) return track.kind === 'asr';
        } catch { /* older players have no such option */ }

        if (player.querySelector('.ytp-caption-window-rollup')) return true;
        const request = captured.get(current?.videoId);
        if (request) return request.params.get('kind') === 'asr';
        return !!current?.autoOnly;
    }

    function checkLive(player, lines) {
        const video = player.querySelector('video');
        if (!video) return;

        const rollUp = showingAutoCaptions(player);
        const now = video.currentTime;
        const liveWord = settings.liveWordMs / 1000;
        let hold = false;

        for (const line of lines) {
            for (const match of [...find(line, ALL), ...findMasked(line)]) {
                const wholeLine = !rollUp || FULL.includes(match.entry);
                if (wholeLine) hold = true;

                // The same word with the same words before it is the same occurrence, however often YouTube redraws it.
                const before = upper(line.slice(Math.max(0, match.index - 24), match.index)).replace(/[^\p{L}\p{N}]/gu, '');
                const key = `${match.entry}|${before}`;
                const lastSeen = live.seen.get(key);
                live.seen.set(key, now);
                if (lastSeen !== undefined && Math.abs(now - lastSeen) < LIVE_FORGET_SECONDS) continue;

                if (!wholeLine) live.until = Math.max(live.until, now + liveWord);
                current.liveHits.push({
                    word: match.entry, mute: wholeLine ? 'Live, while on screen' : 'Live, word', at: now,
                    start: now, end: wholeLine ? null : now + liveWord, line, index: match.index, length: match.length,
                });
                refreshPanel();
            }
        }

        for (const [key, time] of live.seen) {
            if (Math.abs(now - time) > LIVE_FORGET_SECONDS * 5) live.seen.delete(key);
        }

        if (live.hold && !hold) live.until = Math.max(live.until, now + PADS.lineAfter);
        live.hold = hold;
        tick();
    }

    // ---- Panel ----

    let button = null;
    let panel = null;
    let panelTab = 'detections';
    let focusAfterRender = null;
    const openSections = new Set();

    function el(tag, props, ...children) {
        const node = document.createElement(tag);
        for (const [key, value] of Object.entries(props || {})) {
            if (key === 'class') node.className = value;
            else if (key === 'value') node.value = value;
            else if (key === 'checked') node.checked = value;
            else if (key.startsWith('on')) node.addEventListener(key.slice(2), value);
            else if (value === true) node.setAttribute(key, '');
            else if (value !== false && value !== null && value !== undefined) node.setAttribute(key, value);
        }
        for (const child of children.flat()) {
            if (child !== null && child !== undefined && child !== false) node.append(child instanceof Node ? child : String(child));
        }
        return node;
    }

    function clock(seconds) {
        const total = Math.max(0, seconds || 0);
        const hours = Math.floor(total / 3600);
        const minutes = Math.floor(total % 3600 / 60);
        const rest = (total % 60).toFixed(1).padStart(4, '0');
        return hours ? `${hours}:${String(minutes).padStart(2, '0')}:${rest}` : `${minutes}:${rest}`;
    }

    const displayWord = word => word === MASKED_LABEL ? '[censored]' : word.toLowerCase();

    function seek(seconds) {
        const player = getPlayer();
        if (player?.seekTo) player.seekTo(Math.max(0, seconds), true);
        else if (player?.querySelector('video')) player.querySelector('video').currentTime = Math.max(0, seconds);
    }

    function copyText(text, buttonNode) {
        navigator.clipboard.writeText(text).then(() => {
            const label = buttonNode.textContent;
            buttonNode.textContent = 'Copied';
            setTimeout(() => { buttonNode.textContent = label; }, 1500);
        }, () => { buttonNode.textContent = 'Copy failed'; });
    }

    function ensureButton(player) {
        const controls = player.querySelector('.ytp-right-controls');
        if (!controls || (button && controls.contains(button))) return;

        const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
        svg.setAttribute('viewBox', '0 0 24 24');
        const path = document.createElementNS('http://www.w3.org/2000/svg', 'path');
        path.setAttribute('d', 'M12 2 4 5v6c0 5.2 3.4 10 8 11.3 4.6-1.3 8-6.1 8-11.3V5l-8-3Zm-1 14.2-4-4 1.4-1.4 2.6 2.6 5.6-5.6L18 9.2l-7 7Z');
        svg.append(path);

        button = el('button', {
            class: 'ytp-button cg-button', title: 'Cinema Guard', 'aria-label': 'Cinema Guard',
            onclick: event => { event.stopPropagation(); togglePanel(); },
        }, svg, el('span', { class: 'cg-count' }));
        controls.prepend(button);
        updateLive();
    }

    function togglePanel() {
        if (panel) {
            panel.remove();
            panel = null;
            return;
        }

        const player = getPlayer();
        if (!player) return;
        panel = el('div', { class: 'cg-panel' });
        // Clicks and keys inside the panel must not reach the player (play, pause, shortcuts).
        for (const type of ['click', 'dblclick', 'mousedown', 'mouseup', 'pointerdown', 'pointerup', 'keydown', 'keyup', 'keypress', 'contextmenu', 'touchstart', 'touchend', 'wheel'])
            panel.addEventListener(type, event => event.stopPropagation());
        player.appendChild(panel);
        refreshPanel(true);
    }

    function section(key, title, ...children) {
        return el('details', {
            open: openSections.has(key),
            ontoggle: event => event.target.open ? openSections.add(key) : openSections.delete(key),
        }, el('summary', null, title), ...children);
    }

    // Redraws the panel. Status updates only redraw the Detections tab, so typing in the other tabs is never lost.
    function refreshPanel(force) {
        if (!panel || (!force && panelTab !== 'detections')) return;
        const scroll = panel.scrollTop;

        const tab = (key, label) => el('button', {
            class: `cg-tab${panelTab === key ? ' cg-tab-on' : ''}`,
            onclick: () => { panelTab = key; refreshPanel(true); panel.scrollTop = 0; },
        }, label);

        const header = el('div', { class: 'cg-head' },
            el('strong', null, 'Cinema Guard'),
            el('span', { class: 'cg-chip cg-live' }, 'Muting now'),
            settings.enabled ? null : el('span', { class: 'cg-chip cg-paused' }, 'Paused'),
            el('button', { class: 'cg-close', title: 'Close', onclick: togglePanel }, '×'));

        const tabs = el('div', { class: 'cg-tabs' }, tab('detections', 'Detections'), tab('words', 'Words'), tab('settings', 'Settings'));
        const body = panelTab === 'words' ? wordsTab() : panelTab === 'settings' ? settingsTab() : detectionsTab();

        panel.replaceChildren(header, tabs, ...[body].flat().filter(Boolean));
        panel.scrollTop = scroll;

        if (focusAfterRender) {
            panel.querySelector(`[data-focus="${focusAfterRender}"]`)?.focus();
            focusAfterRender = null;
        }
        updateLive();
    }

    function detectionsTab() {
        const session = current;
        if (!session) return el('p', { class: 'cg-muted' }, 'Waiting for a video.');

        const hits = [...session.hits, ...session.liveHits].sort((a, b) => a.at - b.at);
        const kept = hits.filter(hit => !hit.dropped);
        const mutedSeconds = session.ranges.reduce((sum, range) => sum + range.end - range.start, 0);
        panel.cgHits = hits;

        const lineWithMark = hit => !hit.line ? '' :
            [hit.line.slice(0, hit.index), el('mark', null, hit.line.slice(hit.index, hit.index + hit.length)), hit.line.slice(hit.index + hit.length)];

        const rows = hits.map((hit, i) => el('tr', {
            'data-i': i,
            class: hit.dropped ? 'cg-dropped' : '',
            title: hit.dropped ? 'Not muted separately: the auto-generated captions already timed this word' : 'Jump to a second before this',
            onclick: () => seek(hit.at - 1),
        },
            el('td', { class: 'cg-num' }, clock(hit.at)),
            el('td', { class: 'cg-word' }, displayWord(hit.word)),
            el('td', null, hit.dropped ? `${hit.mute} (skipped)` : hit.mute),
            el('td', { class: 'cg-num' }, hit.end === null ? `${clock(hit.start)}–…` : `${clock(hit.start)}–${clock(hit.end)}`),
            el('td', { class: 'cg-line' }, lineWithMark(hit))));

        const logText = () => session.log.map(entry => `${entry.at.toFixed(1)} s  ${entry.text}`).join('\n');

        return [
            el('div', { class: `cg-state cg-${session.state}` }, session.message),
            el('div', { class: 'cg-stats' },
                el('div', null, el('b', null, kept.length), el('span', null, 'detections')),
                el('div', null, el('b', null, session.ranges.length), el('span', null, 'mute ranges')),
                el('div', null, el('b', null, `${mutedSeconds.toFixed(1)} s`), el('span', null, 'muted in total'))),
            session.sources.length
                ? el('div', { class: 'cg-sources' }, session.sources.map(source => el('div', null, `${source.name}: ${source.lines} lines · ${source.via}`)))
                : null,
            hits.length
                ? el('div', { class: 'cg-table' }, el('table', null,
                    el('thead', null, el('tr', null, ['Said at', 'Word', 'Mute', 'Muted', 'Caption'].map(name => el('th', null, name)))),
                    el('tbody', null, rows)))
                : el('p', { class: 'cg-muted' }, session.state === 'loading' ? 'Nothing detected yet.' : 'Nothing detected.'),
            section('ranges', `Mute ranges (${session.ranges.length})`,
                el('div', { class: 'cg-ranges' }, session.ranges.map(range =>
                    el('button', { onclick: () => seek(range.start - 1), title: 'Jump to a second before this' }, `${clock(range.start)}–${clock(range.end)}`)))),
            section('log', 'Load log',
                el('button', { class: 'cg-btn cg-small', onclick: event => copyText(logText(), event.currentTarget) }, 'Copy log'),
                el('ol', { class: 'cg-log' }, session.log.map(entry =>
                    el('li', null, el('span', { class: 'cg-num' }, `${entry.at.toFixed(1)} s`), entry.text)))),
        ];
    }

    function saveSettings() {
        try {
            localStorage.setItem(STORAGE_KEY, JSON.stringify(settings));
        } catch (error) {
            console.warn(LOG, 'Could not save the settings', error);
        }
    }

    // Saves the settings and applies them to the video playing now.
    function settingsChanged() {
        saveSettings();
        applySettings();
        recompute(current);
        resetLive();
        if (!settings.enabled) release();
        const player = getPlayer();
        if (player) syncCaptions(player);
        tick();
    }

    function addWords(key, text) {
        const added = cleanEntries(text.split(/[,\n]/));
        if (added.length === 0) return;

        // A word lives on one list only, so adding it here takes it off the other.
        const other = key === 'wordsFull' ? 'wordsSingle' : 'wordsFull';
        settings[key] = cleanEntries([...settings[key], ...added]);
        settings[other] = settings[other].filter(word => !added.includes(word));
        settingsChanged();
        focusAfterRender = key;
        refreshPanel(true);
    }

    function removeWord(key, word) {
        settings[key] = settings[key].filter(entry => entry !== word);
        settingsChanged();
        refreshPanel(true);
    }

    function moveWord(key, word) {
        const other = key === 'wordsFull' ? 'wordsSingle' : 'wordsFull';
        settings[key] = settings[key].filter(entry => entry !== word);
        settings[other] = cleanEntries([...settings[other], word]);
        settingsChanged();
        refreshPanel(true);
    }

    function wordsTab() {
        const list = (key, title, hint, moveLabel) => {
            const words = [...settings[key]].sort();
            const input = el('input', {
                class: 'cg-input', 'data-focus': key, placeholder: 'Add a word or phrase (commas for several)',
                onkeydown: event => { if (event.key === 'Enter') addWords(key, input.value); },
            });

            return el('div', { class: 'cg-list' },
                el('div', { class: 'cg-list-head' }, el('b', null, title), el('span', { class: 'cg-muted' }, plural(words.length, 'word'))),
                el('div', { class: 'cg-muted cg-hint' }, hint),
                el('div', { class: 'cg-add' }, input, el('button', { class: 'cg-btn', onclick: () => addWords(key, input.value) }, 'Add')),
                el('div', { class: 'cg-chips' }, words.length
                    ? words.map(word => el('span', { class: 'cg-word-chip' },
                        word.toLowerCase(),
                        el('button', { title: moveLabel, onclick: () => moveWord(key, word) }, '⇄'),
                        el('button', { title: `Remove "${word.toLowerCase()}"`, onclick: () => removeWord(key, word) }, '×')))
                    : el('span', { class: 'cg-muted' }, 'No words yet.')));
        };

        return [
            list('wordsSingle', 'Mute the word only', 'The word is muted, with the padding from Settings around it.', 'Move to "Mute the whole sentence"'),
            list('wordsFull', 'Mute the whole sentence', 'The whole caption line the word is in is muted.', 'Move to "Mute the word only"'),
            el('div', { class: 'cg-muted cg-hint' },
                'Whole words only: "fuck" also catches "fuck\'s" but never a longer word, so add each form you want caught. ',
                'Phrases such as "holy shit" match across punctuation.'),
        ];
    }

    function settingsTab() {
        const padInput = key => el('input', {
            class: 'cg-input cg-number', type: 'number', min: 0, max: PAD_LIMITS[key], step: 50, value: settings.pads[key],
            onchange: event => {
                settings = normalise({ ...settings, pads: { ...settings.pads, [key]: event.target.value } });
                event.target.value = settings.pads[key];
                settingsChanged();
            },
        });

        const padRow = (label, hint, before, after) => el('tr', null,
            el('td', null, label, el('div', { class: 'cg-muted cg-hint' }, hint)),
            el('td', null, padInput(before)),
            el('td', null, padInput(after)));

        const toggle = (key, label, hint) => el('label', { class: 'cg-field cg-check' },
            el('input', {
                type: 'checkbox', checked: settings[key],
                onchange: event => { settings[key] = event.target.checked; settingsChanged(); if (key === 'enabled') refreshPanel(true); },
            }),
            el('span', null, label, hint ? el('div', { class: 'cg-muted cg-hint' }, hint) : null));

        const message = el('div', { class: 'cg-muted cg-hint' });
        const importBox = el('textarea', { class: 'cg-input cg-textarea', placeholder: 'Paste exported settings, or the contents of Cinema Guard\'s settings.json' });

        return [
            toggle('enabled', 'Filtering on', 'Turn off to watch without muting or blanking.'),

            el('div', { class: 'cg-group' }, 'Padding around a mute'),
            el('table', { class: 'cg-pads' },
                el('thead', null, el('tr', null, el('th', null, ''), el('th', null, 'Before (ms)'), el('th', null, 'After (ms)'))),
                el('tbody', null,
                    padRow('Word, timed', 'Auto-generated captions give each word\'s time.', 'wordBefore', 'wordAfter'),
                    padRow('Word, guessed', 'Written captions: the time is guessed from where the word sits in the line.', 'estimatedBefore', 'estimatedAfter'),
                    padRow('Whole sentence', 'Words on the "Mute the whole sentence" list.', 'lineBefore', 'lineAfter'))),

            el('label', { class: 'cg-field' },
                el('span', null, 'Live word mute (ms)', el('div', { class: 'cg-muted cg-hint' }, 'Used only when the captions cannot be loaded: how long a word is muted from the moment it appears on screen.')),
                el('input', {
                    class: 'cg-input cg-number', type: 'number', min: 100, max: 5000, step: 50, value: settings.liveWordMs,
                    onchange: event => {
                        settings = normalise({ ...settings, liveWordMs: event.target.value });
                        event.target.value = settings.liveWordMs;
                        settingsChanged();
                    },
                })),

            el('div', { class: 'cg-group' }, 'Captions'),
            toggle('showCaptions', 'Show captions', 'While filtering, captions stay on so swearing can be caught; this only shows or hides them. The CC button does the same.'),
            el('label', { class: 'cg-field' },
                el('span', null, 'Censor character', el('div', { class: 'cg-muted cg-hint' }, 'Replaces each letter of a listed word in the captions: one symbol, or a space. Leave empty to leave the captions as they are.')),
                el('input', {
                    class: 'cg-input cg-char', maxlength: 1, value: settings.censorCharacter,
                    onchange: event => {
                        if (isValidCensorCharacter(event.target.value)) {
                            settings.censorCharacter = event.target.value;
                            settingsChanged();
                        } else {
                            event.target.value = settings.censorCharacter;
                        }
                    },
                })),
            toggle('muteMaskedWords', 'Mute words the captions already hide', 'Such as f***, [bleep] and YouTube\'s [ __ ].'),
            el('label', { class: 'cg-field' },
                el('span', null, 'Caption languages', el('div', { class: 'cg-muted cg-hint' }, 'Language codes, best first, separated by commas. Takes effect on the next video.')),
                el('input', {
                    class: 'cg-input cg-langs', value: settings.languages.join(', '),
                    onchange: event => {
                        settings = normalise({ ...settings, languages: event.target.value.split(',') });
                        event.target.value = settings.languages.join(', ');
                        settingsChanged();
                    },
                })),

            el('div', { class: 'cg-group' }, 'Back up and restore'),
            el('div', { class: 'cg-actions' },
                el('button', { class: 'cg-btn', onclick: event => copyText(JSON.stringify(settings, null, 2), event.currentTarget) }, 'Copy settings'),
                el('button', {
                    class: 'cg-btn cg-danger',
                    onclick: () => {
                        if (!window.confirm('Put every setting and both word lists back to Cinema Guard\'s defaults?')) return;
                        settings = normalise(DEFAULTS);
                        settingsChanged();
                        refreshPanel(true);
                    },
                }, 'Reset to defaults')),
            importBox,
            el('div', { class: 'cg-actions' },
                el('button', {
                    class: 'cg-btn',
                    onclick: () => {
                        try {
                            settings = importSettings(importBox.value);
                            settingsChanged();
                            refreshPanel(true);
                        } catch (error) {
                            message.textContent = `Could not import: ${error.message}`;
                        }
                    },
                }, 'Import'),
                message),
        ];
    }

    // Marks what is being muted now, on the button and in the panel.
    function updateLive() {
        const kept = current ? current.hits.filter(hit => !hit.dropped).length + current.liveHits.length : 0;
        if (button) {
            button.querySelector('.cg-count').textContent = kept && settings.enabled ? String(kept) : '';
            button.classList.toggle('cg-muting', mutedByUs);
            button.classList.toggle('cg-off', !settings.enabled || current?.state === 'none' || current?.state === 'failed');
        }

        if (!panel) return;
        panel.classList.toggle('cg-is-muting', mutedByUs);
        if (panelTab !== 'detections' || !panel.cgHits) return;
        const time = getPlayer()?.querySelector('video')?.currentTime ?? -1;
        for (const row of panel.querySelectorAll('tr[data-i]')) {
            const hit = panel.cgHits[Number(row.dataset.i)];
            row.classList.toggle('cg-active', !hit.dropped && time >= hit.start && time < (hit.end ?? hit.start + 1));
        }
    }

    function status(player, message) {
        let badge = player.querySelector('.cg-status');
        if (!badge) {
            badge = el('div', { class: 'cg-status' });
            player.appendChild(badge);
        }
        badge.textContent = `Cinema Guard: ${message}`;
        badge.style.opacity = '1';
        clearTimeout(badge.hideTimer);
        badge.hideTimer = setTimeout(() => { badge.style.opacity = '0'; }, 4000);
    }

    function addStyles() {
        const style = document.createElement('style');
        style.textContent = `
            .cg-capturing .ytp-caption-window-container,
            .cg-hidden-captions .ytp-caption-window-container { visibility: hidden !important; }
            .cg-hidden-captions .ytp-subtitles-button[aria-pressed="true"]::after { display: none !important; }
            .cg-hidden-captions .ytp-subtitles-button { opacity: 0.6; }
            .cg-status {
                position: absolute; top: 12px; left: 12px; z-index: 70; pointer-events: none;
                padding: 6px 10px; border-radius: 4px; background: rgba(0, 0, 0, 0.75); color: #fff;
                font: 500 13px/1.3 Roboto, Arial, sans-serif; opacity: 0; transition: opacity 0.4s;
            }
            .cg-button { position: relative; display: inline-flex !important; align-items: center; justify-content: center; vertical-align: top; }
            .cg-button svg { width: 24px; height: 24px; fill: currentColor; }
            .cg-button.cg-muting { color: #ff4e45; }
            .cg-button.cg-off { opacity: 0.5; }
            .cg-count {
                position: absolute; top: 6px; right: 2px; min-width: 14px; height: 14px; padding: 0 3px; box-sizing: border-box;
                border-radius: 7px; background: #ff4e45; color: #fff; font: 700 9px/14px Roboto, Arial, sans-serif; text-align: center;
            }
            .cg-count:empty { display: none; }

            #movie_player .cg-panel {
                position: absolute; top: 12px; right: 12px; z-index: 80; box-sizing: border-box;
                width: min(580px, calc(100% - 24px)); max-height: calc(100% - 84px); overflow: auto;
                padding: 12px 14px; border-radius: 8px; background: rgba(18, 18, 18, 0.95); color: #f1f1f1;
                font: 400 13px/1.4 Roboto, Arial, sans-serif; text-align: left; cursor: default;
            }
            .cg-panel * { box-sizing: border-box; }
            .cg-panel .cg-head { display: flex; align-items: center; gap: 8px; margin-bottom: 8px; font-size: 15px; }
            .cg-panel .cg-close {
                margin-left: auto; width: 28px; height: 28px; border: 0; border-radius: 50%;
                background: transparent; color: inherit; font-size: 20px; line-height: 28px; cursor: pointer;
            }
            .cg-panel .cg-close:hover { background: rgba(255, 255, 255, 0.12); }
            .cg-panel .cg-chip { padding: 2px 8px; border-radius: 10px; font-size: 11px; font-weight: 500; }
            .cg-panel .cg-live { display: none; background: #ff4e45; color: #fff; }
            .cg-panel.cg-is-muting .cg-live { display: inline-block; }
            .cg-panel .cg-paused { background: rgba(255, 166, 0, 0.3); }
            .cg-panel .cg-tabs { display: flex; gap: 4px; margin-bottom: 10px; padding: 3px; border-radius: 8px; background: rgba(255, 255, 255, 0.06); }
            .cg-panel .cg-tab {
                flex: 1; padding: 5px 8px; border: 0; border-radius: 6px; background: transparent;
                color: #aaa; font: inherit; font-weight: 500; cursor: pointer;
            }
            .cg-panel .cg-tab:hover { color: #fff; }
            .cg-panel .cg-tab-on { background: rgba(255, 255, 255, 0.14); color: #fff; }
            .cg-panel .cg-state { padding: 6px 10px; border-radius: 6px; background: rgba(255, 255, 255, 0.08); }
            .cg-panel .cg-state.cg-ready { background: rgba(43, 166, 64, 0.25); }
            .cg-panel .cg-state.cg-failed, .cg-panel .cg-state.cg-none { background: rgba(255, 166, 0, 0.22); }
            .cg-panel .cg-stats { display: flex; gap: 8px; margin: 10px 0; }
            .cg-panel .cg-stats > div { flex: 1; padding: 6px 10px; border-radius: 6px; background: rgba(255, 255, 255, 0.06); }
            .cg-panel .cg-stats b { display: block; font-size: 18px; font-weight: 500; }
            .cg-panel .cg-stats span, .cg-panel .cg-muted, .cg-panel .cg-sources { color: #aaa; font-size: 12px; }
            .cg-panel .cg-hint { margin-top: 2px; font-size: 11px; line-height: 1.35; }
            .cg-panel .cg-sources { margin-bottom: 8px; }
            .cg-panel .cg-table { overflow-x: auto; margin-bottom: 8px; }
            .cg-panel table { width: 100%; border-collapse: collapse; font-size: 12px; }
            .cg-panel th { padding: 4px 6px; color: #aaa; font-weight: 500; text-align: left; white-space: nowrap; border-bottom: 1px solid rgba(255, 255, 255, 0.15); }
            .cg-panel td { padding: 4px 6px; vertical-align: top; border-bottom: 1px solid rgba(255, 255, 255, 0.06); }
            .cg-panel .cg-table tbody tr { cursor: pointer; }
            .cg-panel .cg-table tbody tr:hover { background: rgba(255, 255, 255, 0.08); }
            .cg-panel tr.cg-active { background: rgba(255, 78, 69, 0.3) !important; }
            .cg-panel tr.cg-dropped { opacity: 0.45; }
            .cg-panel .cg-num { font-variant-numeric: tabular-nums; white-space: nowrap; }
            .cg-panel .cg-word { font-weight: 500; white-space: nowrap; }
            .cg-panel .cg-line { min-width: 160px; color: #ccc; }
            .cg-panel mark { padding: 0 1px; border-radius: 2px; background: #ff4e45; color: #fff; }
            .cg-panel details { margin-top: 6px; }
            .cg-panel summary { cursor: pointer; color: #aaa; }
            .cg-panel .cg-ranges { display: flex; flex-wrap: wrap; gap: 4px; margin-top: 6px; }
            .cg-panel .cg-ranges button {
                padding: 2px 6px; border: 0; border-radius: 4px; background: rgba(255, 255, 255, 0.08);
                color: inherit; font: inherit; font-size: 12px; font-variant-numeric: tabular-nums; cursor: pointer;
            }
            .cg-panel .cg-log { margin: 6px 0 0; padding-left: 0; list-style: none; font-size: 12px; }
            .cg-panel .cg-log li { display: flex; gap: 8px; }
            .cg-panel .cg-log .cg-num { color: #888; min-width: 42px; }

            .cg-panel .cg-btn {
                padding: 6px 12px; border: 0; border-radius: 6px; background: rgba(255, 255, 255, 0.12);
                color: #fff; font: inherit; font-weight: 500; white-space: nowrap; cursor: pointer;
            }
            .cg-panel .cg-btn:hover { background: rgba(255, 255, 255, 0.2); }
            .cg-panel .cg-btn.cg-small { margin: 6px 0 2px; padding: 3px 10px; font-size: 12px; }
            .cg-panel .cg-btn.cg-danger { color: #ff8a80; }
            .cg-panel .cg-input {
                width: 100%; padding: 6px 8px; border: 1px solid rgba(255, 255, 255, 0.18); border-radius: 6px;
                background: rgba(0, 0, 0, 0.35); color: #fff; font: inherit; outline: none;
            }
            .cg-panel .cg-input:focus { border-color: #3ea6ff; }
            .cg-panel .cg-number { width: 90px; text-align: right; font-variant-numeric: tabular-nums; }
            .cg-panel .cg-char { width: 48px; text-align: center; }
            .cg-panel .cg-langs { width: 140px; }
            .cg-panel .cg-textarea { min-height: 64px; margin-top: 8px; resize: vertical; font: 12px/1.4 Consolas, monospace; }
            .cg-panel .cg-list { margin-bottom: 14px; }
            .cg-panel .cg-list-head { display: flex; align-items: baseline; justify-content: space-between; }
            .cg-panel .cg-add { display: flex; gap: 6px; margin: 6px 0 8px; }
            .cg-panel .cg-chips { display: flex; flex-wrap: wrap; gap: 4px; max-height: 150px; overflow: auto; }
            .cg-panel .cg-word-chip {
                display: inline-flex; align-items: center; gap: 2px; padding: 2px 4px 2px 9px;
                border-radius: 12px; background: rgba(255, 255, 255, 0.1); font-size: 12px;
            }
            .cg-panel .cg-word-chip button {
                width: 18px; height: 18px; padding: 0; border: 0; border-radius: 50%;
                background: transparent; color: #aaa; font-size: 12px; line-height: 18px; cursor: pointer;
            }
            .cg-panel .cg-word-chip button:hover { background: rgba(255, 255, 255, 0.15); color: #fff; }
            .cg-panel .cg-group { margin: 14px 0 6px; color: #aaa; font-size: 11px; font-weight: 500; letter-spacing: 0.04em; text-transform: uppercase; }
            .cg-panel .cg-field { display: flex; align-items: center; justify-content: space-between; gap: 12px; margin: 8px 0; }
            .cg-panel .cg-check { justify-content: flex-start; align-items: flex-start; cursor: pointer; }
            .cg-panel .cg-check input { margin: 3px 0 0; accent-color: #3ea6ff; }
            .cg-panel .cg-pads td { border: 0; padding: 4px 6px 4px 0; }
            .cg-panel .cg-pads td:not(:first-child) { width: 96px; }
            .cg-panel .cg-pads th { border: 0; }
            .cg-panel .cg-actions { display: flex; align-items: center; gap: 8px; margin-top: 8px; }`;
        document.head.appendChild(style);
    }

    // ---- Start ----

    function start() {
        addStyles();
        watchCaptionButton();

        // Timers alone run late, so the time is also checked every frame and on every playback event.
        setInterval(tick, 50);
        const frame = () => { tick(); requestAnimationFrame(frame); };
        requestAnimationFrame(frame);
        for (const type of ['timeupdate', 'seeked', 'play', 'playing', 'ratechange', 'volumechange'])
            document.addEventListener(type, tick, true);
        document.addEventListener('seeking', () => { resetLive(); tick(); }, true);

        for (const type of ['loadedmetadata', 'loadstart'])
            document.addEventListener(type, checkVideo, true);
        document.addEventListener('yt-navigate-finish', checkVideo);
        setInterval(checkVideo, 1000);
        setInterval(updateLive, 250);
        checkVideo();

        window.addEventListener('pagehide', release);
    }

    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start, { once: true });
    else start();
})();
