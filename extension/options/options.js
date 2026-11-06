// The options page: the filter (as in the Windows app), settings, sites and channels, statistics and backup.
(() => {
    const CG = globalThis.__cinemaGuard;
    const { catalog, matcher } = CG;
    const { el, icon, plural, displayWord, loadSettings, saveSettings, saveAllowChannels, UNTESTED_SITES, reportLink } = CG.ui;

    const PAGES = [
        { id: 'filter', title: 'Filter', icon: 'filter', render: filterPage },
        { id: 'settings', title: 'Settings', icon: 'settings', render: settingsPage },
        { id: 'sites', title: 'Sites and channels', icon: 'globe', render: sitesPage },
        { id: 'statistics', title: 'Statistics', icon: 'chart', render: statisticsPage },
        { id: 'backup', title: 'Backup', icon: 'archive', render: backupPage },
        { id: 'whats-new', title: 'What\'s new', icon: 'sparkle', render: whatsNewPage },
    ];

    const BUILT_IN_SITES = [
        { name: 'YouTube', host: 'youtube.com', note: 'Videos, Shorts, and YouTube videos embedded on other sites' },
    ];

    const DESKTOP_APP = 'https://wavebit.dev/software/cinemaguard';

    // Caption languages offered; the code is matched against the start of a track's language (en matches en-GB).
    const LANGUAGES = [
        ['af', 'Afrikaans'], ['ar', 'Arabic'], ['zh', 'Chinese'], ['da', 'Danish'], ['nl', 'Dutch'], ['en', 'English'],
        ['fi', 'Finnish'], ['fr', 'French'], ['de', 'German'], ['el', 'Greek'], ['he', 'Hebrew'], ['hi', 'Hindi'],
        ['id', 'Indonesian'], ['it', 'Italian'], ['ja', 'Japanese'], ['ko', 'Korean'], ['no', 'Norwegian'], ['pl', 'Polish'],
        ['pt', 'Portuguese'], ['ru', 'Russian'], ['es', 'Spanish'], ['sv', 'Swedish'], ['th', 'Thai'], ['tr', 'Turkish'],
        ['uk', 'Ukrainian'], ['vi', 'Vietnamese'], ['xh', 'Xhosa'], ['zu', 'Zulu'],
    ];

    const page = document.getElementById('page');
    let settings = null;
    let allowChannels = [];
    let current = 'filter';
    let welcome = false;
    let ownWrites = 0;
    const expanded = new Set();

    // ---- Saving ----

    let toastTimer = null;
    function toast(text) {
        const box = document.getElementById('toast');
        box.textContent = text;
        box.classList.add('show');
        clearTimeout(toastTimer);
        toastTimer = setTimeout(() => box.classList.remove('show'), 1600);
    }

    async function save(next, message = 'Saved') {
        settings = CG.settings.normalise(next);
        ownWrites++;
        try {
            await saveSettings(settings);
            toast(message);
        } catch (error) {
            toast(`Could not save: ${error.message}`);
        } finally {
            setTimeout(() => ownWrites--, 400);
        }
    }

    async function saveChannels(list) {
        allowChannels = list;
        ownWrites++;
        await saveAllowChannels(list);
        setTimeout(() => ownWrites--, 400);
        toast('Saved');
    }

    // ---- Navigation ----

    function nav() {
        document.getElementById('nav-items').replaceChildren(...PAGES.map(item => el('a', {
            class: `nav-item${item.id === current ? ' selected' : ''}`,
            href: `#${item.id}`,
            'aria-current': item.id === current ? 'page' : null,
        }, icon(item.icon), el('span', { text: item.title }))));

        document.getElementById('nav-footer').replaceChildren(el('a', {
            class: 'nav-item', href: DESKTOP_APP, target: '_blank', rel: 'noreferrer',
            title: 'Cinema Guard for Windows cleans your own video files: muted words and clean subtitles in a new copy.',
        }, icon('desktop'), el('span', null, 'Cinema Guard for Windows', el('span', { class: 'muted', text: 'For your own video files' }))));
    }

    async function show() {
        const hash = location.hash.slice(1);
        welcome = hash === 'welcome';
        current = PAGES.some(item => item.id === hash) ? hash : 'filter';
        nav();
        await render();
    }

    async function render() {
        const scroll = window.scrollY;
        const item = PAGES.find(entry => entry.id === current);
        document.title = `${item.title} · Cinema Guard`;
        page.replaceChildren(...[await item.render()].flat(Infinity).filter(Boolean));
        window.scrollTo(0, scroll);
    }

    const title = text => el('h1', { class: 'page-title', text });
    const intro = text => el('p', { class: 'page-intro', text });
    const section = (text, ...children) => [el('h2', { class: 'section-header', text }), ...children];
    const card = (...rows) => el('div', { class: 'card' }, rows);
    const setting = (header, description, ...controls) => el('div', { class: 'setting' },
        el('div', { class: 'setting-text' }, el('div', { text: header }), description ? el('div', { class: 'muted', text: description }) : null),
        el('div', { class: 'setting-control' }, controls));

    // ---- Filter ----

    function filterPage() {
        return [
            title('Filter'),
            intro('Choose the words to mute. Each word family covers its different spellings.'),
            welcome ? welcomeBar() : null,
            el('div', { class: 'columns' },
                column('wordsFull', 'Mute the whole sentence', 'Blasphemy and insults: the entire line is silenced.'),
                column('wordsSingle', 'Mute the word only', 'Everyday swearing: only the spoken word is silenced.')),
            el('p', { class: 'filter-summary muted', id: 'filter-summary', text: summary() }),
        ];
    }

    function welcomeBar() {
        return el('div', { class: 'infobar', style: { marginBottom: '16px' } },
            el('div', null,
                el('div', { style: { fontWeight: 600 }, text: 'Welcome to Cinema Guard' }),
                el('div', { class: 'muted', text: 'The filter starts with the Windows app\'s defaults. If you use Cinema Guard on Windows, you can bring its words and settings across.' }),
                el('div', { style: { marginTop: '10px', display: 'flex', gap: '8px' } },
                    el('a', { class: 'button accent', href: '#backup', text: 'Import from the Windows app' }),
                    el('a', { class: 'button', href: DESKTOP_APP, target: '_blank', rel: 'noreferrer', text: 'Get the Windows app' }))));
    }

    function summary() {
        const active = new Set([...settings.wordsFull, ...settings.wordsSingle]).size;
        return active ? `${plural(active, 'word')} active` : 'No words are selected, so nothing will be muted.';
    }

    function column(key, heading, description) {
        const words = settings[key];
        const allOn = catalog.ROOTS.every(root => catalog.familyState(words, root) === true);

        const custom = el('textarea', {
            class: 'input',
            id: `custom-${key}`,
            spellcheck: 'false',
            placeholder: 'HOLY SHIT',
            value: catalog.customWords(words).join('\n'),
        });
        let timer = null;
        custom.addEventListener('input', () => {
            clearTimeout(timer);
            timer = setTimeout(() => setCustom(key, custom.value), 500);
        });

        return el('section', { class: 'card column' },
            el('div', { class: 'column-head' },
                el('div', null, el('h2', { text: heading }), el('div', { class: 'muted', text: description })),
                el('button', { class: 'button', onclick: () => setFamilies(key, !allOn), text: allOn ? 'Select none' : 'Select all' })),
            catalog.ROOTS.map(root => familyRow(key, root)),
            el('div', { class: 'custom' },
                el('label', { for: `custom-${key}`, text: 'Additional words or phrases (one per line)' }),
                custom));
    }

    function familyRow(key, root) {
        const words = settings[key];
        const spellings = catalog.family(root);
        const state = catalog.familyState(words, root);
        const on = spellings.filter(word => words.includes(word)).length;
        const open = expanded.has(`${key}:${root}`);

        const row = el('div', { class: 'family', title: `Includes ${plural(spellings.length, 'word')}: ${spellings.map(catalog.displayName).join(', ')}` },
            el('label', { class: 'family-main' },
                el('input', {
                    type: 'checkbox', checked: state === true, indeterminate: state === null,
                    'aria-label': catalog.displayName(root),
                    // A half-ticked family turns fully on when clicked, as in the app.
                    onchange: () => setFamily(key, root, state !== true),
                }),
                el('span', { class: 'family-name', text: catalog.displayName(root) })),
            spellings.length > 1 ? el('button', {
                class: 'button subtle count',
                'aria-expanded': String(open),
                onclick: () => {
                    if (open) expanded.delete(`${key}:${root}`); else expanded.add(`${key}:${root}`);
                    render();
                },
            }, `${on} of ${spellings.length}`, icon('chevron')) : null);

        if (!open) return row;
        return [row, el('div', { class: 'spellings' }, spellings.map(word => el('label', { class: 'spelling' },
            el('input', { type: 'checkbox', checked: words.includes(word), onchange: event => setWord(key, word, event.target.checked) }),
            el('span', { text: catalog.displayName(word) }))))];
    }

    function setList(key, words) {
        save({ ...settings, [key]: words });
        render();
    }

    function setFamily(key, root, enable) {
        const spellings = catalog.family(root);
        const rest = settings[key].filter(word => !spellings.includes(word));
        setList(key, enable ? [...rest, ...spellings] : rest);
    }

    function setFamilies(key, enable) {
        const custom = catalog.customWords(settings[key]);
        setList(key, enable ? [...catalog.ROOTS.flatMap(catalog.family), ...custom] : custom);
    }

    function setWord(key, word, enable) {
        setList(key, enable ? [...settings[key], word] : settings[key].filter(entry => entry !== word));
    }

    function setCustom(key, text) {
        const custom = catalog.customWords(settings[key]);
        const families = settings[key].filter(word => !custom.includes(word));
        save({ ...settings, [key]: [...families, ...matcher.cleanEntries(text.split('\n'))] });
        const line = document.getElementById('filter-summary');
        if (line) line.textContent = summary();
    }

    // ---- Settings ----

    function switchControl(key, onChange) {
        return el('input', {
            type: 'checkbox', class: 'switch', checked: settings[key] === true, 'aria-label': key,
            onchange: event => {
                save({ ...settings, [key]: event.target.checked });
                onChange?.();
            },
        });
    }

    function numberControl(read, write, min, max, label, step = 50) {
        return el('input', {
            class: 'input number', type: 'number', min, max, step, value: read(), 'aria-label': label,
            onchange: event => {
                const next = CG.settings.normalise(write(event.target.value));
                event.target.value = read.call(null, next);
                save(next);
            },
        });
    }

    function padSetting(header, description, before, after) {
        const max = CG.settings.PAD_LIMITS[before];
        const pad = (key, label) => el('label', { class: 'pad' },
            el('span', { text: label }),
            numberControl(
                (from = settings) => from.pads[key],
                value => ({ ...settings, pads: { ...settings.pads, [key]: value } }),
                0, max, `${header}: ${label}`));
        return setting(header, description, el('div', { class: 'pads' }, pad(before, 'Before (ms)'), pad(after, 'After (ms)')));
    }

    function settingsPage() {
        const shortcut = el('kbd', { text: '…' });
        chrome.commands.getAll().then(commands => {
            shortcut.textContent = commands.find(item => item.name === 'toggle-filtering')?.shortcut || 'Not set';
        });

        const censor = el('input', {
            class: 'input char', maxlength: 1, value: settings.censorCharacter, 'aria-label': 'Censor character',
            onchange: event => {
                if (CG.settings.isValidCensorCharacter(event.target.value)) save({ ...settings, censorCharacter: event.target.value });
                else {
                    event.target.value = settings.censorCharacter;
                    toast('Use one symbol, a space, or nothing. Letters and digits are not allowed.');
                }
            },
        });

        const current = settings.languages[0];
        const known = LANGUAGES.some(([code]) => code === current);
        const languages = el('select', {
            class: 'input', 'aria-label': 'Caption language',
            onchange: event => save({ ...settings, languages: [event.target.value] }),
        }, known ? null : el('option', { value: current, text: current }),
        LANGUAGES.map(([code, name]) => el('option', { value: code, text: name, selected: code === current })));

        return [
            title('Settings'),
            intro('How and when Cinema Guard mutes. Changes apply straight away, also to videos already playing.'),
            ...section('Filtering', card(
                setting('Filtering on', 'Turn off to watch without muting or blanking anything.', switchControl('enabled')),
                setting('Keyboard shortcut', 'Turns filtering on or off from any tab.', shortcut,
                    el('button', { class: 'button', onclick: () => chrome.tabs.create({ url: 'chrome://extensions/shortcuts' }), text: 'Change' })))),
            ...section('Extra mute', card(
                padSetting('Single word with an exact time', 'YouTube\'s auto-generated captions give each word its own time.', 'wordBefore', 'wordAfter'),
                padSetting('Single word, time guessed', 'Written captions: the time is guessed from where the word sits in the line.', 'estimatedBefore', 'estimatedAfter'),
                padSetting('Whole sentence', 'Raise After if captions appear early, Before if they appear late.', 'lineBefore', 'lineAfter'),
                setting('Word caught live', 'Only when no caption file can be read: how long a word is muted from when it appears on screen (ms).',
                    numberControl((from = settings) => from.liveWordMs, value => ({ ...settings, liveWordMs: value }), 100, 5000, 'Word caught live')),
                setting('Fade in and out', 'How long the sound takes to fade to silence before a mute, and back after it, so a mute never clicks. 0 cuts straight to silence (ms).',
                    numberControl((from = settings) => from.fadeMs, value => ({ ...settings, fadeMs: value }), 0, CG.settings.FADE_LIMIT, 'Fade in and out', 10)))),
            ...section('Captions', card(
                setting('Censor character', 'Replaces each letter of a filtered word in captions on screen: one symbol, or a space to blank it. Leave it empty to leave captions as they are.', censor),
                setting('Mute words the captions already hide', 'Such as f***, [bleep] and YouTube\'s [ __ ].', switchControl('muteMaskedWords')),
                setting('Caption language', 'The captions to read when a video has several. The word lists are English, so English suits most people. Used from the next video.', languages))),
            ...section('YouTube', card(
                setting('Keep captions on underneath', 'So swearing can always be caught. The CC button then shows or hides captions instead of turning them off.', switchControl('keepCaptionsOn')),
                setting('Show captions', 'The same as pressing CC on YouTube.', switchControl('showCaptions')),
                setting('Marks on the progress bar', 'Shows where a video will be muted.', switchControl('markers')))),
        ];
    }

    // ---- Sites and channels ----

    async function sitesPage() {
        const { origins = [] } = await chrome.permissions.getAll();
        const builtInPatterns = chrome.runtime.getManifest().content_scripts[0].matches;
        const extra = origins.filter(origin => /^https?:/.test(origin) && !builtInPatterns.includes(origin));
        const paused = settings.pausedSites;

        const pauseButton = host => el('button', {
            class: 'button',
            onclick: () => save({ ...settings, pausedSites: paused.includes(host) ? paused.filter(item => item !== host) : [...paused, host] }).then(render),
            text: paused.includes(host) ? 'Resume' : 'Pause',
        });
        const pill = host => paused.includes(host) ? el('span', { class: 'pill caution', text: 'Paused' }) : el('span', { class: 'pill success', text: 'On' });

        const extraRows = extra.map(origin => {
            const host = new URL(origin.replace('/*', '/')).hostname.replace(/^www\./, '');
            return setting(host, origin.replace('/*', ''), pill(host), pauseButton(host), el('button', {
                class: 'button',
                onclick: async () => {
                    await chrome.permissions.remove({ origins: [origin] });
                    await chrome.runtime.sendMessage({ type: 'syncSites' });
                    toast(`Turned off for ${host}`);
                    render();
                },
                text: 'Remove',
            }));
        });

        const knownHosts = new Set([...BUILT_IN_SITES.map(site => site.host), ...extra.map(origin => new URL(origin.replace('/*', '/')).hostname.replace(/^www\./, ''))]);
        const otherPaused = paused.filter(host => !knownHosts.has(host));

        return [
            title('Sites and channels'),
            intro('Where Cinema Guard filters, and what it leaves alone.'),
            ...section('Built-in sites', card(...BUILT_IN_SITES.map(site => setting(site.name, `${site.host} · ${site.note}`, pill(site.host), pauseButton(site.host))))),
            ...section('Other sites you turned on', el('div', { class: 'card row-list' }, extraRows.length ? extraRows
                : el('div', { class: 'empty-row', text: 'To add a site, open a page with a video there, select Cinema Guard in the toolbar and choose Turn on. Captions or subtitles must be available on the video.' }))),
            ...section('Help bring Cinema Guard to more sites', el('div', { class: 'card row-list' },
                el('div', { class: 'empty-row', text: 'Cinema Guard knows how these sites deliver their subtitles, but nobody has been able to test it on them yet: they need a paid account. If you have one, open a video there, select Cinema Guard in the toolbar and choose Try it anyway. Then tell us how it went, and copy the log from the popup into your report. Once a site is confirmed, it becomes built in.' }),
                UNTESTED_SITES.map(site => setting(site.name, site.host, el('span', { class: 'pill caution', text: 'Not tested yet' }),
                    el('a', { class: 'button', href: reportLink(site.name), target: '_blank', rel: 'noreferrer', text: 'Report how it went' }))))),
            otherPaused.length ? section('Other paused sites', card(...otherPaused.map(host => setting(host, null, pauseButton(host))))) : null,
            ...section('Channels that are not filtered', el('div', { class: 'card row-list' }, allowChannels.length
                ? allowChannels.map(channel => setting(channel.name || channel.id, channel.site || 'YouTube', el('button', {
                    class: 'button',
                    onclick: () => saveChannels(allowChannels.filter(item => item.id !== channel.id)).then(render),
                    text: 'Filter again',
                })))
                : el('div', { class: 'empty-row', text: 'To leave a channel unfiltered, open one of its videos, select Cinema Guard in the toolbar and choose Don\'t filter this channel.' }))),
        ];
    }

    // ---- Statistics ----

    async function statisticsPage() {
        const { stats } = await chrome.storage.local.get('stats');
        const total = stats?.total || 0;

        const days = [];
        const today = new Date();
        for (let i = 29; i >= 0; i--) {
            const day = new Date(today.getFullYear(), today.getMonth(), today.getDate() - i);
            const key = `${day.getFullYear()}-${String(day.getMonth() + 1).padStart(2, '0')}-${String(day.getDate()).padStart(2, '0')}`;
            days.push({ day, count: stats?.days?.[key] || 0 });
        }
        const peak = Math.max(1, ...days.map(day => day.count));
        const words = Object.entries(stats?.words || {}).sort((a, b) => b[1] - a[1]).slice(0, 10);
        const topWord = words[0]?.[1] || 1;
        const dateText = value => new Date(value).toLocaleDateString(undefined, { day: 'numeric', month: 'short', year: 'numeric' });

        return [
            title('Statistics'),
            intro('What Cinema Guard has muted in this browser.'),
            el('div', { class: 'summary' },
                el('div', { class: 'card' }, el('b', { text: total.toLocaleString() }), el('span', { class: 'muted', text: 'words muted' })),
                el('div', { class: 'card' }, el('b', { text: (stats?.videos?.length || 0).toLocaleString() }), el('span', { class: 'muted', text: 'videos with mutes' })),
                el('div', { class: 'card' }, el('b', { text: stats?.since ? dateText(stats.since) : '–' }), el('span', { class: 'muted', text: 'counting since' }))),
            ...section('Last 30 days', el('div', { class: 'card' },
                el('div', { class: 'chart' }, days.map(({ day, count }) => el('div', {
                    class: `chart-bar${count ? '' : ' zero'}`,
                    style: { height: `${Math.max(2, (count / peak) * 100)}%` },
                    title: `${day.toLocaleDateString(undefined, { weekday: 'short', day: 'numeric', month: 'short' })}: ${plural(count, 'word')}`,
                }))),
                el('div', { class: 'chart-axis muted' }, el('span', { text: dateText(days[0].day) }), el('span', { text: 'Today' })))),
            ...section('Most muted words', el('div', { class: 'card bars' }, words.length
                ? words.map(([word, count]) => el('div', { class: 'setting' },
                    el('span', { class: 'bar-word', text: displayWord(word) }),
                    el('div', { class: 'bar-track' }, el('div', { class: 'bar-fill', style: { width: `${(count / topWord) * 100}%` } })),
                    el('span', { class: 'bar-count', text: count.toLocaleString() })))
                : el('div', { class: 'empty-row', text: 'Nothing muted yet.' }))),
            ...section('Recent videos', el('div', { class: 'card row-list' }, stats?.videos?.length
                ? stats.videos.slice(0, 25).map(video => el('div', { class: 'setting' },
                    el('div', { class: 'setting-text' },
                        el('a', { class: 'video-title', href: video.url, target: '_blank', rel: 'noreferrer', text: video.title || video.key }),
                        el('div', { class: 'muted', text: `${video.site} · ${dateText(video.last)}` })),
                    el('span', { class: 'pill plain', text: plural(video.count, 'mute') })))
                : el('div', { class: 'empty-row', text: 'Videos you watch with mutes show up here.' }))),
            el('div', { style: { marginTop: '16px' } }, el('button', {
                class: 'button danger',
                onclick: async () => {
                    if (!confirm('Clear all statistics?')) return;
                    await chrome.storage.local.remove('stats');
                    toast('Statistics cleared');
                    render();
                },
                text: 'Clear statistics',
            })),
        ];
    }

    // ---- Backup ----

    function backupPage() {
        const result = el('div', { id: 'import-result' });
        const showResult = (tone, text) => result.replaceChildren(el('div', { class: `infobar ${tone}`, style: { marginTop: '12px' }, text }));

        async function importText(text, source) {
            try {
                const next = CG.settings.importSettings(text, settings);
                await save(next, 'Imported');
                const data = JSON.parse(text);
                if (Array.isArray(data.allowChannels)) await saveChannels(data.allowChannels);
                showResult('', `Imported ${source}: ${plural(new Set([...next.wordsFull, ...next.wordsSingle]).size, 'word')} active.`);
            } catch (error) {
                showResult('caution', `Could not import ${source}: ${error.message}`);
            }
        }

        const file = el('input', {
            type: 'file', accept: '.json,application/json', hidden: true,
            onchange: async event => {
                const chosen = event.target.files[0];
                if (chosen) await importText(await chosen.text(), chosen.name);
                event.target.value = '';
            },
        });
        const paste = el('textarea', { class: 'input', placeholder: 'Or paste the contents of a settings file here' });

        return [
            title('Backup'),
            intro('Bring settings across from the Windows app, or keep a copy of your own.'),
            ...section('Import', card(
                setting('From the Windows app or a backup',
                    'Choose Cinema Guard\'s settings.json (in %LOCALAPPDATA%\\CinemaGuard) or a backup saved here. Words, padding and the censor character come across.',
                    el('button', { class: 'button accent', onclick: () => file.click(), text: 'Choose file' }), file),
                el('div', { class: 'setting', style: { display: 'block' } },
                    paste,
                    el('div', { style: { marginTop: '8px' } }, el('button', { class: 'button', onclick: () => importText(paste.value, 'the pasted settings'), text: 'Import pasted text' }))))),
            result,
            ...section('Export', card(
                setting('Save a backup', 'Your filter, settings and unfiltered channels, as a file.', el('button', {
                    class: 'button',
                    onclick: () => {
                        const blob = new Blob([JSON.stringify({ app: 'Cinema Guard', exported: new Date().toISOString(), settings, allowChannels }, null, 2)], { type: 'application/json' });
                        const link = el('a', { href: URL.createObjectURL(blob), download: `cinema-guard-${new Date().toISOString().slice(0, 10)}.json` });
                        link.click();
                        setTimeout(() => URL.revokeObjectURL(link.href), 1000);
                    },
                    text: 'Save backup',
                })))),
            ...section('Reset', card(
                setting('Reset everything', 'Puts the filter and every setting back to the Windows app\'s defaults. Statistics and unfiltered channels stay.', el('button', {
                    class: 'button danger',
                    onclick: async () => {
                        if (!confirm('Reset the filter and every setting to the defaults?')) return;
                        await save(CG.settings.defaults(), 'Reset to defaults');
                        render();
                    },
                    text: 'Reset',
                })))),
        ];
    }

    // ---- What's new ----

    // The version the extension was updated from (kept by the background worker until this page is seen): the
    // versions after it are marked New. Held here too, so a redraw keeps the marks while the page is open.
    let updatedFrom = null;

    async function whatsNewPage() {
        const version = chrome.runtime.getManifest().version;
        const stored = (await chrome.storage.local.get('updatedFrom')).updatedFrom;
        if (stored) updatedFrom = stored;
        await chrome.storage.local.remove('updatedFrom');

        let entries = [];
        try {
            entries = await CG.changelog.load();
        } catch { /* shown below */ }
        const fresh = new Set(updatedFrom ? CG.changelog.since(entries, updatedFrom, version) : []);

        return [
            title('What\'s new'),
            intro(`What changed in each version of Cinema Guard, newest first. You have version ${version}.`),
            entries.length ? entries.map(entry => [
                el('h2', { class: 'section-header release-header' },
                    el('span', { text: entry.title }),
                    entry.date ? el('span', { class: 'muted', text: entry.date }) : null,
                    fresh.has(entry) ? el('span', { class: 'pill accent', text: 'New' }) : null),
                el('div', { class: 'card' }, el('ul', { class: 'changes' }, entry.changes.map(change => el('li', { text: change })))),
            ]) : el('div', { class: 'card' }, el('div', { class: 'empty-row', text: 'The list of changes could not be read.' })),
        ];
    }

    // ---- Start ----

    chrome.storage.onChanged.addListener(async (changes, area) => {
        if (area !== 'sync' || ownWrites > 0) return;
        ({ settings, allowChannels } = await loadSettings());
        // Never redraw under someone typing.
        if (!document.activeElement?.matches('input, textarea')) render();
    });

    window.addEventListener('hashchange', show);

    loadSettings().then(loaded => {
        ({ settings, allowChannels } = loaded);
        show();
    });
})();
