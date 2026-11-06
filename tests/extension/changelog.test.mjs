// Run with: node --test tests/extension
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { readFileSync } from 'node:fs';

const require = createRequire(import.meta.url);
require('../../extension/shared/changelog.js');
const { changelog } = globalThis.__cinemaGuard;

const sample = `# Changelog

Intro text, not a change.

## Unreleased

- Coming next.

## 1.0.10 — 7 November 2026

- Two-digit part.
- A change written
  over two lines.

## 1.0.2 — 6 November 2026

- Spellings.

## 1.0.0 — 4 November 2026

- The first version.
`;

test('reads each version with its date and changes', () => {
    const entries = changelog.parse(sample);
    assert.deepEqual(entries.map(entry => entry.title), ['Unreleased', '1.0.10', '1.0.2', '1.0.0']);
    assert.equal(entries[0].version, null);
    assert.equal(entries[0].date, null);
    assert.equal(entries[1].version, '1.0.10');
    assert.equal(entries[1].date, '7 November 2026');
    assert.deepEqual(entries[1].changes, ['Two-digit part.', 'A change written over two lines.']);
    assert.deepEqual(changelog.parse(sample.replace(/\n/g, '\r\n')), entries);
});

test('compares versions part by part', () => {
    assert.equal(changelog.compare('1.0.10', '1.0.9'), 1);
    assert.equal(changelog.compare('1.0.2', '1.0.2'), 0);
    assert.equal(changelog.compare('1.0', '1.0.1'), -1);
});

test('lists the versions after the previous one, up to the current one', () => {
    const entries = changelog.parse(sample);
    const titles = (previous, current) => changelog.since(entries, previous, current).map(entry => entry.title);
    assert.deepEqual(titles('1.0.2', '1.0.10'), ['1.0.10']);
    assert.deepEqual(titles('1.0.0', '1.0.10'), ['1.0.10', '1.0.2']);
    assert.deepEqual(titles('1.0.0', '1.0.2'), ['1.0.2']);
    assert.deepEqual(titles('1.0.10', '1.0.10'), []);
});

test('the shipped changelog is well formed', () => {
    const entries = changelog.parse(readFileSync(new URL('../../extension/CHANGELOG.md', import.meta.url), 'utf8'));
    const manifest = JSON.parse(readFileSync(new URL('../../extension/manifest.json', import.meta.url), 'utf8'));

    // Only the top one may be unreleased; the rest are versions, newest first, each with a date and a change.
    const released = entries[0]?.title === 'Unreleased' ? entries.slice(1) : entries;
    assert.ok(released.length > 0);
    for (const entry of released) {
        assert.ok(entry.version, `${entry.title} is not a version`);
        assert.ok(entry.date, `${entry.title} has no date`);
        assert.ok(entry.changes.length > 0, `${entry.title} lists no changes`);
    }
    for (let i = 1; i < released.length; i++) assert.equal(changelog.compare(released[i - 1].version, released[i].version), 1);
    assert.ok(changelog.compare(released[0].version, manifest.version) <= 0, 'the newest entry is newer than the manifest');
});
