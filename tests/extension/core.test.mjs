// Run with: node --test tests/extension
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { readFileSync, existsSync } from 'node:fs';

const require = createRequire(import.meta.url);
for (const file of ['catalog', 'matcher', 'settings', 'captions', 'hits']) require(`../../extension/shared/${file}.js`);
const { catalog, matcher, settings, captions, hits } = globalThis.__cinemaGuard;

const defaults = settings.defaults();
const options = (overrides = {}) => ({
    full: defaults.wordsFull,
    single: [...defaults.wordsSingle, 'GOD', 'HOLY SHIT'],
    maskedWords: true,
    pads: { estimatedBefore: 0.5, estimatedAfter: 0.5, wordBefore: 0.15, wordAfter: 0.25, lineBefore: 0.25, lineAfter: 0.5 },
    ...overrides,
});
const entries = list => list.map(match => match.entry);

test('whole words only, as the app matches', () => {
    const single = options().single;
    assert.deepEqual(entries(matcher.find("What the fuck's that?", single)), ['FUCK']);
    assert.deepEqual(entries(matcher.find('Scunthorpe', single)), []);
    assert.deepEqual(entries(matcher.find('motherfucking hell', single)), ['MOTHERFUCKING']);
    assert.deepEqual(entries(matcher.find('Holy... shit!', single)), ['HOLY SHIT']);
    assert.deepEqual(entries(matcher.find('holy-shit', single)), ['HOLY SHIT']);
    assert.deepEqual(entries(matcher.find('Oh my God', single)), ['GOD']);
    assert.deepEqual(entries(matcher.find('Goddamn', single)), []);
    assert.deepEqual(entries(matcher.find('Jesus Christ!', defaults.wordsFull)), ['JESUS', 'CHRIST']);
    assert.equal(matcher.find('Straße fuck', single)[0].index, 7);
});

test('words the captions already hide', () => {
    assert.deepEqual(matcher.findMasked('what the [ __ ] is f*** this [bleep]').map(m => m.length), [6, 4, 7]);
});

test('a guessed word is muted around the middle of its place in the line', () => {
    const cues = [{ start: 10, end: 12, text: 'FUCK THIS', words: null }];
    const [range] = hits.buildRanges(hits.collectHits([cues], options()), NaN);
    assert.ok(Math.abs(range.start - 9.9444) < 0.001 && Math.abs(range.end - 10.9444) < 0.001);
});

test('a whole sentence is muted with the line padding', () => {
    const cues = [{ start: 20, end: 23, text: '<i>Jesus,</i>\nwhat now?', words: null }];
    assert.deepEqual(hits.buildRanges(hits.collectHits([cues], options()), NaN), [{ start: 19.75, end: 23.5 }]);
});

test('timed words win over guesses nearby', () => {
    const written = [{ start: 10, end: 12, text: 'FUCK THIS', words: null }];
    const auto = captions.parseJson3({ events: [
        { tStartMs: 10000, dDurationMs: 4000, segs: [{ utf8: 'fuck' }, { utf8: ' this', tOffsetMs: 400 }, { utf8: ' [ __ ]', tOffsetMs: 900 }] },
        { tStartMs: 12000, dDurationMs: 3000, segs: [{ utf8: 'next' }] },
    ] });
    const all = hits.collectHits([written, auto], options());
    assert.equal(all.length, 3);
    assert.ok(all[1].dropped && all[1].mute === 'guessed');
    const kept = all.filter(hit => !hit.dropped);
    assert.ok(Math.abs(kept[0].start - 9.85) < 1e-9 && Math.abs(kept[0].end - 10.65) < 1e-9);
    assert.equal(kept[1].line.slice(kept[1].index, kept[1].index + kept[1].length), '[ __ ]');
});

test('ranges merge and stay inside the video', () => {
    assert.deepEqual(hits.buildRanges([{ start: 1, end: 2 }, { start: 2.04, end: 3 }, { start: -1, end: 0.5 }], 2.5), [{ start: 0, end: 0.5 }, { start: 1, end: 2.5 }]);
    assert.deepEqual(hits.rangeAt([{ start: 1, end: 2 }], 1.5), { start: 1, end: 2 });
    assert.equal(hits.rangeAt([{ start: 1, end: 2 }], 2), null);
});

test('caption formats', () => {
    const vtt = captions.parse('WEBVTT\nX-TIMESTAMP-MAP=MPEGTS:900000,LOCAL:00:00:00.000\n\n00:00:01.000 --> 00:00:03.500\n<c>Oh my God</c>');
    assert.equal(vtt.format, 'vtt');
    assert.deepEqual(vtt.cues.map(c => [c.start, c.end, c.text]), [[11, 13.5, 'Oh my God']]);

    const srt = captions.parse('1\r\n00:00:01,000 --> 00:00:02,000\r\nHello\r\n\r\n2\r\n00:01:02,500 --> 00:01:04,000\r\nThere');
    assert.deepEqual(srt.cues.map(c => [c.start, c.text]), [[1, 'Hello'], [62.5, 'There']]);

    const ttml = captions.parse('<?xml version="1.0"?><tt xmlns="http://www.w3.org/ns/ttml" xml:lang="en" ttp:tickRate="10000000"><body><div>' +
        '<p begin="10000000t" end="25000000t">What the <span>hell</span><br/>is this?</p><p begin="00:00:05.500" dur="1.5s">Two &amp; three</p></div></body></tt>');
    assert.equal(ttml.format, 'ttml');
    assert.equal(ttml.language, 'en');
    assert.deepEqual(ttml.cues.map(c => [c.start, c.end, c.text]), [[1, 2.5, 'What the hell\nis this?'], [5.5, 7, 'Two & three']]);

    const ass = captions.parse('[Script Info]\nTitle: x\n\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n' +
        'Dialogue: 0,0:00:01.50,0:00:03.00,Default,,0,0,0,,{\\i1}Well, damn{\\i0}\\Nthat hurts');
    assert.deepEqual(ass.cues.map(c => [c.start, c.end, c.text]), [[1.5, 3, 'Well, damn\nthat hurts']]);

    assert.equal(captions.parse(")]}'\n" + JSON.stringify({ events: [{ tStartMs: 0, dDurationMs: 1000, segs: [{ utf8: 'x' }] }] })).cues.length, 1);
    assert.equal(captions.parse('<MPD xmlns="urn:mpeg:dash:schema:mpd:2011"></MPD>').format, null);
    assert.ok(captions.looksLikeCaptions('WEBVTT\n') && captions.looksLikeCaptions('<?xml version') && !captions.looksLikeCaptions('\u0000\u0000\u0000 ftyp'));
});

test('the filter defaults match the Windows app', () => {
    assert.equal(catalog.ROOTS.length, 18);
    assert.ok(defaults.wordsFull.includes('JESUS') && defaults.wordsFull.includes('CHRISTSAKES'));
    assert.ok(defaults.wordsSingle.includes('FUCK') && defaults.wordsSingle.includes('PUSSIES') && !defaults.wordsSingle.includes('SHIT'));
    assert.equal(catalog.familyState(defaults.wordsSingle, 'FUCK'), true);
    assert.equal(catalog.familyState(['FUCK'], 'FUCK'), null);
    assert.equal(catalog.familyState([], 'SHIT'), false);
    assert.deepEqual(catalog.customWords(['FUCK', 'HOLY SHIT']), ['HOLY SHIT']);
    assert.equal(catalog.displayName('FUCK'), 'F*CK');
    assert.equal(catalog.displayName('ASS'), '*SS');
    // No compound shows a swear word in full.
    assert.equal(catalog.displayName('CLUSTERFUCK'), 'CLUSTERF*CK');
    assert.equal(catalog.displayName('MOTHERFUCKER'), 'MOTHERF*CKER');
    assert.equal(catalog.displayName('BITCHASS'), 'B*TCH*SS');
    assert.equal(catalog.displayName('HOLY SHIT'), 'HOLY SH*T');
    assert.equal(catalog.displayName('GODDAMN'), 'G*DD*MN');
    const swears = ['FUCK', 'SHIT', 'CUNT', 'COCK', 'DICK', 'PUSSY', 'BITCH', 'BASTARD'];
    for (const root of catalog.ROOTS) {
        for (const word of catalog.family(root)) {
            const shown = catalog.displayName(word);
            assert.ok(!swears.some(swear => shown.includes(swear)), `${word} is shown as ${shown}`);
        }
    }
});

test('settings are checked and imported', () => {
    const odd = settings.normalise({ wordsFull: ['god'], wordsSingle: ['God', ' holy   shit ', 'x'], pads: { wordBefore: 99999, lineAfter: -5, estimatedBefore: 'abc' }, censorCharacter: 'a', languages: [' EN '] });
    assert.deepEqual(odd.wordsFull, ['GOD']);
    assert.deepEqual(odd.wordsSingle, ['GOD', 'HOLY SHIT', 'X']);
    assert.deepEqual([odd.pads.wordBefore, odd.pads.lineAfter, odd.pads.estimatedBefore], [2000, 0, 500]);
    assert.equal(odd.censorCharacter, '*');
    assert.deepEqual(odd.languages, ['en']);

    const app = settings.importSettings({ WordsFull: ['JESUS'], WordsSingle: ['GOD'], CensorCharacter: ' ', WordPadBeforeMs: 100, MuteFadeMs: 60 }, defaults);
    assert.deepEqual([app.wordsFull, app.wordsSingle, app.censorCharacter, app.pads.wordBefore, app.pads.lineAfter, app.fadeMs], [['JESUS'], ['GOD'], ' ', 100, 500, 60]);
    assert.equal(defaults.fadeMs, 30);
    assert.equal(settings.normalise({ fadeMs: 9000 }).fadeMs, 500);
    assert.equal(settings.normalise({ fadeMs: 0 }).fadeMs, 0);
    assert.throws(() => settings.importSettings('{"hello": 1}', defaults), /No Cinema Guard settings/);
    assert.throws(() => settings.importSettings('nope', defaults), /not valid JSON/);

    const settingsFile = `${process.env.LOCALAPPDATA}\\CinemaGuard\\settings.json`;
    if (existsSync(settingsFile)) {
        const mine = settings.importSettings(readFileSync(settingsFile, 'utf8'), defaults);
        assert.ok(mine.wordsSingle.includes('GOD') && mine.wordsSingle.includes('HOLY SHIT'));
    }
});

test('blanking keeps the line length', () => {
    assert.equal(matcher.maskText('Shit. Fuck you, Jesus!', ['FUCK', 'JESUS'], '*'), 'Shit. **** you, *****!');
    assert.equal(matcher.maskText('holy shit', ['HOLY SHIT'], '#'), '#### ####');
});
