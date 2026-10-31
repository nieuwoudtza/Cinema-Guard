using System.Text;

namespace CinemaGuard.Core.Phonon;

public sealed record LanguageGuess(string Code, string Name, double Confidence, bool IsKnown)
{
    public static LanguageGuess Unknown { get; } = new("und", "Unknown", 0, false);
    public bool IsEnglish => Code == "en";
}

/// <summary>
/// Small dependency-free language identifier for the foreign-dialogue report: script detection
/// first (Cyrillic, Arabic, CJK, …), then function-word scoring for Latin-script languages.
/// It only needs to say "this stretch is not English, probably Spanish", not translate.
/// </summary>
public static class LanguageIdentifier
{
    private static readonly Dictionary<string, (string Name, HashSet<string> Words)> Profiles = Build();

    public static LanguageGuess Identify(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return LanguageGuess.Unknown;

        var scripted = DetectScript(text);
        if (scripted is not null) return scripted;

        var tokens = Tokenize(text);
        if (tokens.Count < 4) return LanguageGuess.Unknown;

        LanguageGuess? best = null;

        foreach (var (code, profile) in Profiles)
        {
            var hits = tokens.Count(profile.Words.Contains);
            if (hits < 3) continue;

            var ratio = (double)hits / tokens.Count;
            if (ratio < 0.08) continue;

            if (best is null || ratio > best.Confidence) best = new LanguageGuess(code, profile.Name, ratio, true);
        }

        return best ?? LanguageGuess.Unknown;
    }

    public static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var builder = new StringBuilder();

        foreach (var character in text)
        {
            if (char.IsLetter(character) || character == '\'')
            {
                builder.Append(character);
            }
            else if (builder.Length > 0)
            {
                tokens.Add(Fold(builder.ToString()));
                builder.Clear();
            }
        }

        if (builder.Length > 0) tokens.Add(Fold(builder.ToString()));
        return tokens;
    }

    /// <summary>Remove accents so "QUÉ" and "QUE" score the same ("SÉ"/"SE", "ÉL"/"EL").</summary>
    private static string Fold(string token)
    {
        var decomposed = token.Normalize(System.Text.NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);

        foreach (var character in decomposed)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(character) != System.Globalization.UnicodeCategory.NonSpacingMark)
                builder.Append(character);
        }

        return builder.ToString().ToUpperInvariant();
    }

    private static LanguageGuess? DetectScript(string text)
    {
        int cyrillic = 0, greek = 0, arabic = 0, devanagari = 0, kana = 0, hangul = 0, han = 0, total = 0;

        foreach (var character in text)
        {
            if (!char.IsLetter(character)) continue;
            total++;

            switch (character)
            {
                case >= '\u0400' and <= '\u04FF': cyrillic++; break;
                case >= '\u0370' and <= '\u03FF': greek++; break;
                case >= '\u0600' and <= '\u06FF': arabic++; break;
                case >= '\u0750' and <= '\u077F': arabic++; break;
                case >= '\u0900' and <= '\u097F': devanagari++; break;
                case >= '\u3040' and <= '\u30FF': kana++; break;
                case >= '\uAC00' and <= '\uD7AF': hangul++; break;
                case >= '\u4E00' and <= '\u9FFF': han++; break;
            }
        }

        if (total == 0) return null;

        double Share(int count) => (double)count / total;

        if (Share(hangul) > 0.3) return new LanguageGuess("ko", "Korean", Share(hangul), true);
        if (Share(kana) > 0.1) return new LanguageGuess("ja", "Japanese", Share(kana), true);
        if (Share(han) > 0.3) return new LanguageGuess("zh", "Chinese", Share(han), true);
        if (Share(cyrillic) > 0.3) return new LanguageGuess("ru", "Russian", Share(cyrillic), true);
        if (Share(greek) > 0.3) return new LanguageGuess("el", "Greek", Share(greek), true);
        if (Share(arabic) > 0.3) return new LanguageGuess("ar", "Arabic", Share(arabic), true);
        if (Share(devanagari) > 0.3) return new LanguageGuess("hi", "Hindi", Share(devanagari), true);

        return null;
    }

    private static Dictionary<string, (string, HashSet<string>)> Build()
    {
        var profiles = new Dictionary<string, (string, HashSet<string>)>(StringComparer.Ordinal)
        {
            ["en"] = ("English", Words("THE AND TO OF YOU THAT IT IS WHAT IN WE ME HE SHE THEY DON'T I'M A FOR ON KNOW LIKE NOT BUT HAVE THIS WITH WAS ARE")),
            ["es"] = ("Spanish", Words("QUE EL LA DE NO SE LO TE ES Y UN UNA POR CON PARA ESTO COMO PERO MI TU AQUI TODO BIEN MUY CUANDO HAY ESTA")),
            ["fr"] = ("French", Words("QUE JE PAS IL ELLE ON NOUS VOUS LES DES UN UNE EST ET EN POUR DANS AVEC MAIS SUR CE QUI TRES BIEN NE")),
            ["de"] = ("German", Words("NICHT UND DER DIE DAS IST ICH DU WIR SIE EIN EINE MIT FÜR AUF ZU DEN DEM ABER WENN WAS WIE MICH")),
            ["it"] = ("Italian", Words("CHE NON IL LA DI E UN UNA PER CON SONO MI TI LO LE GLI DELLA COSA BENE ANCHE MOLTO COME")),
            ["pt"] = ("Portuguese", Words("QUE NÃO NAO E O A DE DO DA UM UMA PARA COM ISSO VOCÊ VOCE ELE ELA MAS SE MUITO BEM COMO")),
            ["nl"] = ("Dutch", Words("DE HET EEN EN VAN IK JE NIET IS DAT DIT OP MET VOOR MAAR WAT WE ZE HIJ ZIJ OOK")),
            ["pl"] = ("Polish", Words("NIE I W NA ZE SIE JEST TO CO JAK ALE TAK DO OD PO MÓJ MOJ TWÓJ TWOJ CZY")),
            ["sv"] = ("Swedish", Words("OCH ATT DET SOM JAG DU HAN HON VI ÄR AR INTE MED FÖR FOR PÅ PA DEN DETTA")),
            ["tr"] = ("Turkish", Words("VE BIR BU NE BEN SEN O DA DE GIBI IÇIN ICIN AMA ÇOK COK MI MÜ")),
            ["da"] = ("Danish", Words("OG ATT DET SOM JEG DU HAN HUN VI ER IKKE MED FOR PÅ PA DEN DETTE")),
            ["no"] = ("Norwegian", Words("OG AT DET SOM JEG DU HAN HUN VI ER IKKE MED FOR PÅ PA DEN DETTE")),
        };

        return profiles;
    }

    private static HashSet<string> Words(string words) =>
        new(words.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
}
