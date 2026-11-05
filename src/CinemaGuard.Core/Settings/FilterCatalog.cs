namespace CinemaGuard.Core.Settings;

/// <summary>
/// The word families from the old app (Info.cs, AssociatedSwearWords) and the filter page
/// checkboxes (CfrmFilter.Designer.cs tags). Ported verbatim so the user's filter behaves the same.
/// </summary>
public static class FilterCatalog
{
    /// <summary>Root words shown as checkboxes on the filter page, in the old order.</summary>
    public static IReadOnlyList<string> Roots { get; } =
    [
        "JESUS", "CHRIST", "GOD", "BITCH", "SHIT", "BASTARD", "GODDAMN", "DAMN", "FUCK",
        "CUNT", "DICKHEAD", "COCK", "DICK", "BULLSHIT", "MOTHERFUCKER", "PUSSY", "ASSHOLE", "ASS",
    ];

    public static IReadOnlyList<string> Associated(string word)
    {
        var root = word.ToUpperInvariant();
        IReadOnlyList<string> family = root switch
        {
            "ASS" => ["ASS", "ASSES", "ARSE", "ARSES"],
            "ASSHOLE" => ["ASSHOLE", "ASSHOLES"],
            "BASTARD" => ["BASTARD", "BASTARDS"],
            "BITCH" =>
            [
                "BITCH", "BITCHASS", "BITCHES", "BITCHFACE", "BITCHIN", "BITCHING", "BITCHY",
            ],
            "COCK" => ["COCK", "COCKS", "COCKSUCKER", "COCKSUCKERS"],
            "CUNT" => ["CUNT", "CUNTS", "CUNTISH", "CUNTFACE"],
            "DICK" => ["DICK", "DICKS", "DICKING", "DICKISH", "DICKLESS"],
            "DICKHEAD" => ["DICKHEAD", "DICKHEADS"],
            "FUCK" =>
            [
                "BUMFUCK", "CLUSTERFUCK", "CLUSTERFUCKS", "DUMBFUCK", "FUCK", "FUCKABLE", "FUCKBOI",
                "FUCKBOIS", "FUCKBOY", "FUCKBOYS", "FUCKED", "FUCKEDUP", "FUCKED-UP", "FUCKER",
                "FUCKERS", "FUCKERY", "FUCKFACE", "FUCKFACES", "FUCKFEST", "FUCKFESTS", "FUCKGIRL",
                "FUCKGIRLS", "FUCKHEAD", "FUCKHEADS", "FUCKHOLE", "FUCKHOLES", "FUCKIN", "FUCKING",
                "FUCKITY", "FUCKNUT", "FUCKNUTS", "FUCKNUTTED", "FUCKNUTTING", "FUCKOFF", "FUCKOFFS",
                "FUCKS", "FUCKSAKE", "FUCKSSAKE", "FUCKSTICK", "FUCKSTICKS", "FUCKTARD", "FUCKTARDS",
                "FUCKUP", "FUCKUPS", "FUCKWAD", "FUCKWIT", "FUCKWITS", "SKULLFUCKED", "SKULLFUCKING",
                "UNFUCK", "UNFUCKED", "UNFUCKING", "UNFUCKS",
            ],
            "MOTHERFUCKER" =>
            [
                "MOTHERFUCKER", "MOTHERFUCKERIN", "MOTHERFUCKERING", "MOTHERFUCKERS", "MOTHERFUCKING",
                "MOTHERFUCKIN",
            ],
            "PUSSY" => ["PUSSY", "PUSSIES"],
            "SHIT" =>
            [
                "SHIT", "SHIT-EATER", "SHIT-EATERS", "SHIT-EATING", "SHITBAG", "SHITBAGS", "SHITCAN",
                "SHITCANNED", "SHITEATER", "SHITEATERS", "SHITEATING", "SHITFACE", "SHITFACED",
                "SHITFACING", "SHITHEAD", "SHITHEADS", "SHITHOLE", "SHITHOLES", "SHITLOAD", "SHITLOADS",
                "SHITPILE", "SHITPILES", "SHITPOST", "SHITPOSTER", "SHITPOSTERS", "SHITPOSTING", "SHITS",
                "SHITSHOW", "SHITSHOWS", "SHITSTAIN", "SHITSTAINS", "SHITSTORM", "SHITSTORMS", "SHITTIER",
                "SHITTIEST", "SHITTY",
            ],
            "JESUS" => ["JEESUZ", "JEEZUS", "JEEZUSS", "JEEZUZ", "JESUS", "JESUS'S", "JESUSES"],
            "CHRIST" => ["CHRIST", "CHRISTS", "CHRIST'S", "CHRISSAKE", "CHRISSAKES", "CHRISTSAKE", "CHRISTSAKES"],
            _ => [root],
        };

        return AddedInVersion4.TryGetValue(root, out var added) ? [.. family, .. added] : family;
    }

    /// <summary>
    /// Spellings a family gained after the old app's lists (settings version 4): forms subtitles use that the
    /// lists missed, such as "Motherfuck!" and "Goddammit". Settings saved before then get them on every list
    /// that has the family's root word (see <see cref="AddNewSpellings"/>).
    /// </summary>
    internal static IReadOnlyDictionary<string, string[]> AddedInVersion4 { get; } = new Dictionary<string, string[]>
    {
        ["ASS"] =
        [
            "BADASS", "BADASSES", "DUMBASS", "DUMBASSES", "FATASS", "HARDASS", "JACKASS", "JACKASSES",
            "SMARTASS", "SMARTASSES",
        ],
        ["BITCH"] = ["BITCHED", "SONOFABITCH", "SONUVABITCH"],
        ["BULLSHIT"] = ["BULLSHITS", "BULLSHITTED", "BULLSHITTER", "BULLSHITTERS", "BULLSHITTING"],
        ["COCK"] = ["COCKSUCKING"],
        ["DAMN"] = ["DAMMIT", "DAMNED", "DAMNIT"],
        ["DICK"] = ["DICKFACE", "DICKWAD", "DICKWADS"],
        ["FUCK"] =
        [
            "DUMBFUCKS", "FUCKA", "FUCKALL", "FUCKAS", "FUCKTON", "FUCKTONS", "FUCKWADS", "MINDFUCK",
            "MINDFUCKED", "MINDFUCKING", "MINDFUCKS",
        ],
        ["GODDAMN"] = ["GODDAM", "GODDAMMIT", "GODDAMNED", "GODDAMNIT"],
        ["MOTHERFUCKER"] =
        [
            "MOTHERFUCK", "MOTHERFUCKA", "MOTHERFUCKAS", "MOTHERFUCKED", "MOTHERFUCKS", "MOTHAFUCKA",
            "MOTHAFUCKAS", "MOTHAFUCKER", "MOTHAFUCKERS", "MUTHAFUCKA", "MUTHAFUCKAS", "MUTHAFUCKER",
            "MUTHAFUCKERS", "MUTHERFUCKER", "MUTHERFUCKERS",
        ],
        ["SHIT"] =
        [
            "APESHIT", "BATSHIT", "CHICKENSHIT", "CHICKENSHITS", "DIPSHIT", "DIPSHITS", "HORSESHIT", "SHITE",
            "SHITES", "SHITLESS", "SHITTED", "SHITTER", "SHITTERS", "SHITTIN", "SHITTING",
        ],
    };

    /// <summary>
    /// Settings from before version 4: a list that has a family's root word gets the spellings added since.
    /// A spelling already on either list stays where it is.
    /// </summary>
    internal static void AddNewSpellings(List<string> wordsFull, List<string> wordsSingle)
    {
        foreach (var (root, added) in AddedInVersion4)
        {
            foreach (var words in new[] { wordsFull, wordsSingle })
            {
                if (!words.Contains(root)) continue;

                foreach (var word in added)
                    if (!wordsFull.Contains(word) && !wordsSingle.Contains(word)) words.Add(word);
            }
        }
    }

    /// <summary>The old app's first-run defaults: JESUS/CHRIST mute the whole sentence.</summary>
    public static List<string> DefaultWordsFull() =>
        Associated("JESUS").Concat(Associated("CHRIST")).Distinct().ToList();

    /// <summary>The old app's first-run defaults: these mute only the word itself.</summary>
    public static List<string> DefaultWordsSingle() =>
        Associated("FUCK").Concat(Associated("CUNT")).Concat(Associated("MOTHERFUCKER")).Concat(Associated("PUSSY"))
            .Distinct().ToList();

    /// <summary>Words of a family that are currently enabled (list contains any of them).</summary>
    public static bool IsFamilyEnabled(IReadOnlyCollection<string> words, string root)
    {
        var family = Associated(root);
        return family.Any(words.Contains);
    }

    /// <summary>Every spelling of the family on (true), none (false), or only some of them (null).</summary>
    public static bool? FamilyState(IReadOnlyCollection<string> words, string root)
    {
        var family = Associated(root);
        var enabled = family.Count(words.Contains);
        return enabled == family.Count ? true : enabled == 0 ? false : null;
    }

    /// <summary>One spelling on or off, leaving the rest of its family as it is.</summary>
    public static void EnableWord(ICollection<string> words, string word, bool enable)
    {
        if (enable)
        {
            if (!words.Contains(word)) words.Add(word);
        }
        else
        {
            words.Remove(word);
        }
    }

    /// <summary>How the old filter page showed a word: one vowel replaced with '*'.</summary>
    public static string DisplayName(string word)
    {
        const string vowels = "AEIOU";

        for (var i = 1; i < word.Length; i++)
        {
            if (!vowels.Contains(word[i])) continue;
            return word[..i] + "*" + word[(i + 1)..];
        }

        for (var i = 0; i < word.Length; i++)
        {
            if (!vowels.Contains(word[i])) continue;
            return word[..i] + "*" + word[(i + 1)..];
        }

        return word;
    }

    public static void EnableFamily(ICollection<string> words, string root, bool enable)
    {
        var family = Associated(root);
        if (enable)
        {
            foreach (var word in family)
                if (!words.Contains(word)) words.Add(word);
        }
        else
        {
            foreach (var word in family) words.Remove(word);
        }
    }
}
