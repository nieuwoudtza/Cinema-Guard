// The release notes: CHANGELOG.md beside the manifest, read as the Windows app reads its own. A version is a
// "## 1.0.2 — 6 November 2026" heading followed by "- " lines; a line indented under one carries it on.
// "## Unreleased" collects changes until publish-extension.ps1 gives it the version it packs.
(() => {
    const CG = (globalThis.__cinemaGuard ??= {});

    const DATE_SEPARATOR = ' — ';

    // [{ title, version, date, changes }], newest first; version is null for Unreleased.
    function parse(text) {
        const entries = [];
        for (const line of text.split(/\r?\n/)) {
            const current = entries.at(-1);
            if (line.startsWith('## ')) {
                const heading = line.slice(3).trim();
                const separator = heading.indexOf(DATE_SEPARATOR);
                const title = separator < 0 ? heading : heading.slice(0, separator).trim();
                entries.push({
                    title,
                    version: /^\d+(\.\d+)*$/.test(title) ? title : null,
                    date: separator < 0 ? null : heading.slice(separator + DATE_SEPARATOR.length).trim(),
                    changes: [],
                });
            } else if (!current) {
                // The file's own title and introduction.
            } else if (line.startsWith('- ')) {
                current.changes.push(line.slice(2).trim());
            } else if (/^\s+\S/.test(line) && current.changes.length) {
                current.changes[current.changes.length - 1] += ` ${line.trim()}`;
            }
        }
        return entries;
    }

    // Numerically, part by part: 1.0.10 is newer than 1.0.9.
    function compare(a, b) {
        const left = a.split('.').map(Number);
        const right = b.split('.').map(Number);
        for (let i = 0; i < Math.max(left.length, right.length); i++) {
            const difference = (left[i] || 0) - (right[i] || 0);
            if (difference) return Math.sign(difference);
        }
        return 0;
    }

    // The versions after `previous` up to and including `current`: what is new to someone who had `previous`.
    const since = (entries, previous, current) =>
        entries.filter(entry => entry.version && compare(entry.version, previous) > 0 && compare(entry.version, current) <= 0);

    async function load() {
        const response = await fetch(chrome.runtime.getURL('CHANGELOG.md'));
        if (!response.ok) throw new Error(`CHANGELOG.md: ${response.status}`);
        return parse(await response.text());
    }

    CG.changelog = { parse, compare, since, load };
})();
