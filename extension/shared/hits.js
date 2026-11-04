// Mute windows: a port of the Windows app's HitCollector.
(() => {
    const CG = (globalThis.__cinemaGuard ??= {});
    const { find, findMasked, formatCue, MASKED_LABEL } = CG.matcher;

    const SNAP_WINDOW = 2.0;
    const MERGE_GAP = 0.05;
    const MINIMUM_RANGE = 0.02;

    // options: { full, single, maskedWords, pads } with pads in seconds.
    // A hit is one detection: { word, mute, at, start, end, line, index, length, dropped }. at is when the word is said;
    // start and end are the padded mute; line, index and length show it in context.
    function collectHits(tracks, options) {
        const { full, single, pads } = options;
        const masked = text => options.maskedWords ? findMasked(text) : [];
        const hits = [];
        const timedHits = [];

        // Tracks that time every word, like the app's speech model.
        for (const cues of tracks.filter(cues => cues.some(cue => cue.words))) {
            for (const cue of cues) {
                if (!cue.words || cue.words.length === 0) continue;

                const span = (from, to) => {
                    const words = cue.words.filter(word => word.from < to && word.to > from);
                    return words.length ? { start: words[0].start, end: words[words.length - 1].end } : null;
                };
                const lineStart = cue.words[0].start;
                const lineEnd = cue.words[cue.words.length - 1].end;

                for (const match of find(cue.text, full)) {
                    hits.push({
                        word: match.entry, mute: 'sentence', at: span(match.index, match.index + match.length)?.start ?? lineStart,
                        start: lineStart - pads.lineBefore, end: lineEnd + pads.lineAfter, line: cue.text, index: match.index, length: match.length,
                    });
                }

                for (const match of [...find(cue.text, single), ...masked(cue.text)]) {
                    const word = span(match.index, match.index + match.length);
                    if (!word) continue;
                    const hit = {
                        word: match.entry, mute: 'timed', at: word.start,
                        start: word.start - pads.wordBefore, end: word.end + pads.wordAfter, line: cue.text, index: match.index, length: match.length,
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
                word: match.entry, mute: 'guessed', at: from,
                start: from - pads.estimatedBefore, end: to + pads.estimatedAfter, line, index: match.index, length: match.length, dropped,
            });
        };

        for (const cues of tracks.filter(cues => !cues.some(cue => cue.words))) {
            for (const cue of cues) {
                const duration = cue.end - cue.start;
                if (duration <= 0) continue;

                const formatted = formatCue(cue.text);
                if (formatted.length === 0) continue;

                // The whole line, widened: captions are often shown a little before or after the line is spoken.
                for (const match of find(formatted, full)) {
                    hits.push({
                        word: match.entry, mute: 'sentence', at: cue.start,
                        start: cue.start - pads.lineBefore, end: cue.end + pads.lineAfter, line: formatted, index: match.index, length: match.length,
                    });
                }

                // A word's time is guessed from where it sits in the line.
                const at = index => cue.start + index / formatted.length * duration;
                for (const match of find(formatted, single)) {
                    if (match.entry.includes(' ')) {
                        addEstimated(at(match.index), at(match.index + match.length), match, formatted);
                    } else {
                        const middle = at(match.index + match.length / 2);
                        addEstimated(middle, middle, match, formatted);
                    }
                }

                const raw = cue.text.replace(/\r\n|\n/g, ' ');
                for (const hidden of masked(raw)) {
                    const estimate = cue.start + (hidden.index + hidden.length / 2) / raw.length * duration;
                    addEstimated(estimate, estimate, hidden, raw);
                }
            }
        }

        // The same word found twice (two copies of a track, or overlapping lines) is one detection.
        const seen = new Set();
        return hits
            .sort((a, b) => a.at - b.at || Number(!!a.dropped) - Number(!!b.dropped))
            .filter(hit => {
                const key = `${hit.word}|${hit.mute}|${hit.at.toFixed(2)}`;
                if (seen.has(key)) return false;
                seen.add(key);
                return true;
            });
    }

    function buildRanges(hits, duration) {
        const ranges = hits
            .filter(hit => !hit.dropped)
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

    // The range containing time, or null.
    function rangeAt(ranges, time) {
        let low = 0;
        let high = ranges.length - 1;
        while (low <= high) {
            const middle = (low + high) >> 1;
            if (time < ranges[middle].start) high = middle - 1;
            else if (time >= ranges[middle].end) low = middle + 1;
            else return ranges[middle];
        }
        return null;
    }

    CG.hits = { collectHits, buildRanges, rangeAt, SNAP_WINDOW };
})();
