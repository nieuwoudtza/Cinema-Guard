// Caption files: YouTube json3 and srv3, TTML/DFXP (Netflix, Prime Video), WebVTT, SubRip and ASS.
// Everything is read with plain text parsing: DOMParser is blocked by Trusted Types on some sites.
// A cue is { start, end, text, words }: seconds, and words only when each word has its own time.
(() => {
    const CG = (globalThis.__cinemaGuard ??= {});

    function decodeEntities(value) {
        return value.replace(/&(#x[0-9a-f]+|#\d+|amp|lt|gt|quot|apos|nbsp);/gi, (_, entity) => {
            const name = entity.toLowerCase();
            if (name.startsWith('#x')) return String.fromCodePoint(parseInt(name.slice(2), 16));
            if (name.startsWith('#')) return String.fromCodePoint(parseInt(name.slice(1), 10));
            return { amp: '&', lt: '<', gt: '>', quot: '"', apos: "'", nbsp: '\u00A0' }[name];
        });
    }

    const attribute = (attributes, name) => {
        const match = new RegExp('(?:^|\\s)' + name.replace(':', '\\:') + '\\s*=\\s*"([^"]*)"').exec(attributes);
        return match ? match[1] : undefined;
    };

    // ---- YouTube json3 ----

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
                delete word.cueEnd;
            });
        }

        return cues;
    }

    // ---- YouTube srv3 and the older timedtext format ----

    function parseYouTubeXml(xml) {
        const events = [];
        const text = value => decodeEntities(value.replace(/<br\s*\/?>/gi, '\n').replace(/<[^>]*>/g, ''));
        const number = value => value === undefined ? undefined : Number(value);

        for (const p of xml.matchAll(/<p\b([^>]*)>([\s\S]*?)<\/p>/g)) {
            const segs = [...p[2].matchAll(/<s\b([^>]*)>([\s\S]*?)<\/s>/g)]
                .map(s => ({ utf8: text(s[2]), tOffsetMs: number(attribute(s[1], 't')) }));
            events.push({ tStartMs: number(attribute(p[1], 't')), dDurationMs: number(attribute(p[1], 'd')), segs: segs.length ? segs : [{ utf8: text(p[2]) }] });
        }

        for (const line of xml.matchAll(/<text\b([^>]*)>([\s\S]*?)<\/text>/g)) {
            events.push({
                tStartMs: (number(attribute(line[1], 'start')) || 0) * 1000,
                dDurationMs: (number(attribute(line[1], 'dur')) || 0) * 1000,
                segs: [{ utf8: decodeEntities(text(line[2])) }],
            });
        }

        return parseJson3({ events });
    }

    // ---- TTML / DFXP ----

    function ttmlTime(value, rates) {
        if (!value) return undefined;
        value = value.trim();
        let match = /^([\d.]+)(h|m|s|ms|f|t)$/.exec(value);
        if (match) {
            const amount = Number(match[1]);
            return { h: amount * 3600, m: amount * 60, s: amount, ms: amount / 1000, f: amount / rates.frameRate, t: amount / rates.tickRate }[match[2]];
        }
        match = /^(\d+):(\d{2}):(\d{2})(?:([.:])(\d+))?$/.exec(value);
        if (match) {
            const seconds = Number(match[1]) * 3600 + Number(match[2]) * 60 + Number(match[3]);
            if (!match[4]) return seconds;
            return match[4] === '.' ? seconds + Number('0.' + match[5]) : seconds + Number(match[5]) / rates.frameRate;
        }
        return undefined;
    }

    function parseTtml(xml) {
        const root = /<tt\b([^>]*)>/.exec(xml)?.[1] || '';
        const frameRate = Number(attribute(root, 'ttp:frameRate')) || 30;
        const multiplier = (attribute(root, 'ttp:frameRateMultiplier') || '1 1').split(/\s+/).map(Number);
        const rates = {
            frameRate: frameRate * ((multiplier[0] || 1) / (multiplier[1] || 1)),
            tickRate: Number(attribute(root, 'ttp:tickRate')) || frameRate || 1,
        };

        const cues = [];
        for (const p of xml.matchAll(/<(?:\w+:)?p\b([^>]*?)(?:\/>|>([\s\S]*?)<\/(?:\w+:)?p>)/g)) {
            const start = ttmlTime(attribute(p[1], 'begin'), rates);
            let end = ttmlTime(attribute(p[1], 'end'), rates);
            const duration = ttmlTime(attribute(p[1], 'dur'), rates);
            if (start === undefined) continue;
            if (end === undefined && duration !== undefined) end = start + duration;
            if (end === undefined) continue;

            const text = decodeEntities((p[2] || '').replace(/<(?:\w+:)?br\s*\/?>/gi, '\n').replace(/<[^>]*>/g, ''))
                .replace(/[ \t]+/g, ' ').replace(/ *\n */g, '\n').trim();
            if (text) cues.push({ start, end, text, words: null });
        }
        return cues;
    }

    // ---- WebVTT and SubRip ----

    const clockTime = value => {
        const parts = value.trim().replace(',', '.').split(':').map(Number);
        return parts.reduce((total, part) => total * 60 + part, 0);
    };

    function parseVtt(vtt) {
        // HLS segments carry X-TIMESTAMP-MAP: their times are relative to the MPEG timestamp given.
        let offset = 0;
        const map = /X-TIMESTAMP-MAP=([^\r\n]*)/.exec(vtt);
        if (map) {
            const mpegts = Number(/MPEGTS:(\d+)/.exec(map[1])?.[1] || 0);
            const local = /LOCAL:([\d:.]+)/.exec(map[1])?.[1];
            offset = mpegts / 90000 - (local ? clockTime(local) : 0);
        }

        const cues = [];
        for (const block of vtt.replace(/\r/g, '').split(/\n{2,}/)) {
            const lines = block.split('\n');
            const timing = lines.findIndex(line => line.includes('-->'));
            if (timing < 0) continue;

            const [from, to] = lines[timing].split('-->').map(part => clockTime(part.trim().split(/\s+/)[0]));
            const text = decodeEntities(lines.slice(timing + 1).join('\n').replace(/<[^>]*>/g, '')).trim();
            if (text && Number.isFinite(from) && Number.isFinite(to)) cues.push({ start: from + offset, end: to + offset, text, words: null });
        }
        return cues;
    }

    // ---- ASS / SSA ----

    function parseAss(ass) {
        const cues = [];
        let format = null;
        let inEvents = false;

        for (const raw of ass.replace(/\r/g, '').split('\n')) {
            const line = raw.trim();
            if (/^\[.*\]$/.test(line)) {
                inEvents = /^\[events\]$/i.test(line);
                continue;
            }
            if (!inEvents) continue;

            if (/^format\s*:/i.test(line)) {
                format = line.slice(line.indexOf(':') + 1).split(',').map(field => field.trim().toLowerCase());
                continue;
            }
            if (!format || !/^dialogue\s*:/i.test(line)) continue;

            // The text is the last field and may itself contain commas.
            const values = line.slice(line.indexOf(':') + 1).split(',');
            const fields = values.slice(0, format.length - 1).concat(values.slice(format.length - 1).join(','));
            const get = name => fields[format.indexOf(name)]?.trim();

            const text = (get('text') || '').replace(/\{[^}]*\}/g, '').replace(/\\[Nn]/g, '\n').replace(/\\h/g, ' ').trim();
            const start = clockTime(get('start') || '');
            const end = clockTime(get('end') || '');
            if (text && Number.isFinite(start) && Number.isFinite(end)) cues.push({ start, end, text, words: null });
        }
        return cues.sort((a, b) => a.start - b.start);
    }

    // ---- Recognising a file ----

    // Returns { format, language, cues }; format is null when the text is not a caption file.
    function parse(body) {
        const text = (body || '').replace(/^\uFEFF/, '').replace(/^\)\]\}'/, '').trim();
        const result = (format, cues, language = null) => ({ format, language, cues: cues.filter(cue => cue.end >= cue.start) });

        try {
            if (text.startsWith('{') && text.includes('"events"')) return result('json3', parseJson3(JSON.parse(text)));
            if (text.startsWith('WEBVTT')) return result('vtt', parseVtt(text));
            if (/^\[Script Info\]/i.test(text)) return result('ass', parseAss(text));
            if (text.startsWith('<')) {
                if (/<(?:\w+:)?tt[\s>]/.test(text)) {
                    const root = /<(?:\w+:)?tt\b([^>]*)>/.exec(text)?.[1] || '';
                    return result('ttml', parseTtml(text), attribute(root, 'xml:lang') || null);
                }
                if (/<timedtext|<transcript/.test(text)) return result('srv3', parseYouTubeXml(text));
                return result(null, []);
            }
            if (/^\d+\s*\n\s*\d{1,2}:\d{2}:\d{2}[,.]\d{1,3}\s*-->/.test(text.replace(/\r/g, ''))) return result('srt', parseVtt(text));
        } catch (error) {
            return { format: null, language: null, cues: [], error: String(error) };
        }
        return result(null, []);
    }

    // A quick look at the start of a response, to decide whether it is worth reading in full.
    const looksLikeCaptions = start => /^\s*(?:\uFEFF)?(?:WEBVTT|\[Script Info\]|<\?xml|<tt[\s>]|<\w+:tt[\s>]|<timedtext|<transcript|\{\s*"wireMagic"|\)\]\}'|\d+\s*\r?\n\d{1,2}:\d{2}:\d{2}[,.])/i.test(start);

    CG.captions = { parse, looksLikeCaptions, parseJson3, parseTtml, parseVtt, parseAss, decodeEntities };
})();
