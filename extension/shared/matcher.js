// Word matching: a port of the Windows app's SwearMatcher and SubtitleFormatter.
(() => {
    const CG = (globalThis.__cinemaGuard ??= {});

    const MASKED_LABEL = 'MASKED';

    // Upper-cases one character at a time so match positions still line up with the original text ("ß" stays put).
    function upper(text) {
        let result = '';
        for (const character of text) {
            const upperCase = character.toUpperCase();
            result += upperCase.length === character.length ? upperCase : character;
        }
        return result;
    }

    // Upper case, single spaces, no blanks or duplicates: how word lists are kept.
    function cleanEntries(list) {
        const entries = new Set();
        for (const word of list || []) {
            const cleaned = upper(String(word).split(/\s+/).filter(Boolean).join(' '));
            if (cleaned) entries.add(cleaned);
        }
        return [...entries];
    }

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
        if (!text || !entries || entries.length === 0) return matches;

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

    // Words the captions already hide, which need no list entry.
    function findMasked(text) {
        if (!text) return [];
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

    // A caption cue as a viewer reads it: tags and [sound descriptions] gone, lines joined.
    const formatCue = text => text.replace(/\r\n/g, '\n').split('\n').map(formatLine).join(' ').trim();

    // Each letter of a listed word becomes the mask character, so the line keeps its length.
    function maskText(text, entries, mask) {
        const matches = find(text, entries);
        if (matches.length === 0 || !mask) return text;

        const masked = new Array(text.length).fill(false);
        for (const match of matches) {
            for (let i = match.index; i < Math.min(match.index + match.length, text.length); i++) masked[i] = true;
        }

        let result = '';
        for (let i = 0; i < text.length; i++) result += masked[i] && !/\s/.test(text[i]) ? mask : text[i];
        return result;
    }

    CG.matcher = { MASKED_LABEL, upper, cleanEntries, find, findMasked, formatLine, formatCue, maskText };
})();
