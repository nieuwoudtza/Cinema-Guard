// The word families and first-run filter of the Windows app (FilterCatalog.cs), so both filter the same way.
(() => {
    const CG = (globalThis.__cinemaGuard ??= {});

    // Root words in the order the filter page shows them.
    const ROOTS = [
        'JESUS', 'CHRIST', 'GOD', 'BITCH', 'SHIT', 'BASTARD', 'GODDAMN', 'DAMN', 'FUCK',
        'CUNT', 'DICKHEAD', 'COCK', 'DICK', 'BULLSHIT', 'MOTHERFUCKER', 'PUSSY', 'ASSHOLE', 'ASS',
    ];

    const FAMILIES = {
        ASS: ['ASS', 'ASSES', 'ARSE', 'ARSES'],
        ASSHOLE: ['ASSHOLE', 'ASSHOLES'],
        BASTARD: ['BASTARD', 'BASTARDS'],
        BITCH: ['BITCH', 'BITCHASS', 'BITCHES', 'BITCHFACE', 'BITCHIN', 'BITCHING', 'BITCHY'],
        COCK: ['COCK', 'COCKS', 'COCKSUCKER', 'COCKSUCKERS'],
        CUNT: ['CUNT', 'CUNTS', 'CUNTISH', 'CUNTFACE'],
        DICK: ['DICK', 'DICKS', 'DICKING', 'DICKISH', 'DICKLESS'],
        DICKHEAD: ['DICKHEAD', 'DICKHEADS'],
        FUCK: [
            'BUMFUCK', 'CLUSTERFUCK', 'CLUSTERFUCKS', 'DUMBFUCK', 'FUCK', 'FUCKABLE', 'FUCKBOI',
            'FUCKBOIS', 'FUCKBOY', 'FUCKBOYS', 'FUCKED', 'FUCKEDUP', 'FUCKED-UP', 'FUCKER',
            'FUCKERS', 'FUCKERY', 'FUCKFACE', 'FUCKFACES', 'FUCKFEST', 'FUCKFESTS', 'FUCKGIRL',
            'FUCKGIRLS', 'FUCKHEAD', 'FUCKHEADS', 'FUCKHOLE', 'FUCKHOLES', 'FUCKIN', 'FUCKING',
            'FUCKITY', 'FUCKNUT', 'FUCKNUTS', 'FUCKNUTTED', 'FUCKNUTTING', 'FUCKOFF', 'FUCKOFFS',
            'FUCKS', 'FUCKSAKE', 'FUCKSSAKE', 'FUCKSTICK', 'FUCKSTICKS', 'FUCKTARD', 'FUCKTARDS',
            'FUCKUP', 'FUCKUPS', 'FUCKWAD', 'FUCKWIT', 'FUCKWITS', 'SKULLFUCKED', 'SKULLFUCKING',
            'UNFUCK', 'UNFUCKED', 'UNFUCKING', 'UNFUCKS',
        ],
        MOTHERFUCKER: ['MOTHERFUCKER', 'MOTHERFUCKERIN', 'MOTHERFUCKERING', 'MOTHERFUCKERS', 'MOTHERFUCKING', 'MOTHERFUCKIN'],
        PUSSY: ['PUSSY', 'PUSSIES'],
        SHIT: [
            'SHIT', 'SHIT-EATER', 'SHIT-EATERS', 'SHIT-EATING', 'SHITBAG', 'SHITBAGS', 'SHITCAN',
            'SHITCANNED', 'SHITEATER', 'SHITEATERS', 'SHITEATING', 'SHITFACE', 'SHITFACED',
            'SHITFACING', 'SHITHEAD', 'SHITHEADS', 'SHITHOLE', 'SHITHOLES', 'SHITLOAD', 'SHITLOADS',
            'SHITPILE', 'SHITPILES', 'SHITPOST', 'SHITPOSTER', 'SHITPOSTERS', 'SHITPOSTING', 'SHITS',
            'SHITSHOW', 'SHITSHOWS', 'SHITSTAIN', 'SHITSTAINS', 'SHITSTORM', 'SHITSTORMS', 'SHITTIER',
            'SHITTIEST', 'SHITTY',
        ],
        JESUS: ['JEESUZ', 'JEEZUS', 'JEEZUSS', 'JEEZUZ', 'JESUS', "JESUS'S", 'JESUSES'],
        CHRIST: ['CHRIST', 'CHRISTS', "CHRIST'S", 'CHRISSAKE', 'CHRISSAKES', 'CHRISTSAKE', 'CHRISTSAKES'],
    };

    // Spellings the families gained after the app's first lists, by the catalog version that added them.
    // Saved settings from before a version get its spellings on every list with the root word.
    const ADDED = {
        // The app's settings version 4: "Motherfuck!", "Goddammit" and the like.
        2: {
            ASS: ['BADASS', 'BADASSES', 'DUMBASS', 'DUMBASSES', 'FATASS', 'HARDASS', 'JACKASS', 'JACKASSES', 'SMARTASS', 'SMARTASSES'],
            BITCH: ['BITCHED', 'SONOFABITCH', 'SONUVABITCH'],
            BULLSHIT: ['BULLSHITS', 'BULLSHITTED', 'BULLSHITTER', 'BULLSHITTERS', 'BULLSHITTING'],
            COCK: ['COCKSUCKING'],
            DAMN: ['DAMMIT', 'DAMNED', 'DAMNIT'],
            DICK: ['DICKFACE', 'DICKWAD', 'DICKWADS'],
            FUCK: [
                'DUMBFUCKS', 'FUCKA', 'FUCKALL', 'FUCKAS', 'FUCKTON', 'FUCKTONS', 'FUCKWADS', 'MINDFUCK',
                'MINDFUCKED', 'MINDFUCKING', 'MINDFUCKS',
            ],
            GODDAMN: ['GODDAM', 'GODDAMMIT', 'GODDAMNED', 'GODDAMNIT'],
            MOTHERFUCKER: [
                'MOTHERFUCK', 'MOTHERFUCKA', 'MOTHERFUCKAS', 'MOTHERFUCKED', 'MOTHERFUCKS', 'MOTHAFUCKA',
                'MOTHAFUCKAS', 'MOTHAFUCKER', 'MOTHAFUCKERS', 'MUTHAFUCKA', 'MUTHAFUCKAS', 'MUTHAFUCKER',
                'MUTHAFUCKERS', 'MUTHERFUCKER', 'MUTHERFUCKERS',
            ],
            SHIT: [
                'APESHIT', 'BATSHIT', 'CHICKENSHIT', 'CHICKENSHITS', 'DIPSHIT', 'DIPSHITS', 'HORSESHIT', 'SHITE',
                'SHITES', 'SHITLESS', 'SHITTED', 'SHITTER', 'SHITTERS', 'SHITTIN', 'SHITTING',
            ],
        },
        // The app's settings version 5: misspellings captions use. Punctuation stuck to a word (F*CK-UP, F*CK'D,
        // CHRIST') needs no entry, as find() already matches across it.
        3: {
            FUCK: ['FUCIN', 'FUCKEN', 'FUKIN'],
            JESUS: ['JEEZ'],
            SHIT: ['SHITTEST'],
        },
    };

    // The catalog version saved with the settings: the newest in ADDED.
    const VERSION = 3;

    const family = root => [...(FAMILIES[root] || [root]), ...Object.values(ADDED).flatMap(added => added[root] || [])];

    // Settings saved before a version in ADDED: a list with a family's root word gets the spellings added since,
    // unless on a list already.
    function addNewSpellings(wordsFull, wordsSingle, savedVersion = 1) {
        const full = [...wordsFull];
        const single = [...wordsSingle];
        for (const [version, spellings] of Object.entries(ADDED)) {
            if (Number(version) <= savedVersion) continue;
            for (const [root, added] of Object.entries(spellings)) {
                for (const words of [full, single]) {
                    if (!words.includes(root)) continue;
                    for (const word of added) if (!full.includes(word) && !single.includes(word)) words.push(word);
                }
            }
        }
        return { wordsFull: full, wordsSingle: single };
    }
    const unique = list => [...new Set(list)];

    // The app's rule for showing a word: one vowel replaced with '*', the first after the first letter.
    function starOneVowel(word) {
        const vowels = 'AEIOU';
        for (let i = 1; i < word.length; i++) {
            if (vowels.includes(word[i])) return word.slice(0, i) + '*' + word.slice(i + 1);
        }
        for (let i = 0; i < word.length; i++) {
            if (vowels.includes(word[i])) return word.slice(0, i) + '*' + word.slice(i + 1);
        }
        return word;
    }

    // Swear words that also appear inside longer ones (CLUSTERFUCK, BULLSHIT, BITCHASS).
    const EMBEDDED = /FUCK|SHIT|CUNT|COCK|DICK|PUSS|BITCH|BASTARD|DAMN|ARSE|ASS|JESUS|CHRIST|GOD/g;

    // How a word is shown: each swear word inside it gets a starred vowel, so a compound never shows one in full
    // (MOTHERF*CKER, CL*STERF*CK would show FUCK). Other words follow the app's rule.
    function displayName(word) {
        const starred = word.replace(EMBEDDED, starOneVowel);
        return starred !== word ? starred : starOneVowel(word);
    }

    // Every spelling of the family on (true), none (false), or only some of them (null).
    function familyState(words, root) {
        const spellings = family(root);
        const on = spellings.filter(word => words.includes(word)).length;
        return on === spellings.length ? true : on === 0 ? false : null;
    }

    // Words that belong to no family: the custom words box.
    const allFamilyWords = new Set(ROOTS.flatMap(family));
    const customWords = words => words.filter(word => !allFamilyWords.has(word));

    CG.catalog = {
        ROOTS,
        VERSION,
        family,
        addNewSpellings,
        familyState,
        customWords,
        displayName,
        defaultFull: () => unique([...family('JESUS'), ...family('CHRIST')]),
        defaultSingle: () => unique([...family('FUCK'), ...family('CUNT'), ...family('MOTHERFUCKER'), ...family('PUSSY')]),
    };
})();
