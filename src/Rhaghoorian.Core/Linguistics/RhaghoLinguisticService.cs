using Rhaghoorian.Core.Models;
using Rhaghoorian.Core.Text;

namespace Rhaghoorian.Core.Linguistics;

public sealed class RhaghoLinguisticService : IRhaghoLinguisticService
{
    public string PastSuffix => "'ta";
    public string FutureSuffix => "'vil";
    public string ImperativeSuffix => "'nux";

    private static readonly (string Suffix, VerbTense Tense)[] Suffixes =
    {
        ("'ta", VerbTense.Past),
        ("'vil", VerbTense.Future),
        ("'nux", VerbTense.Imperative)
    };

    // ---------------------------------------------------------------- Rhagho'oriano (regras oficiais)

    public string Inflect(string baseForm, VerbTense tense)
    {
        if (string.IsNullOrWhiteSpace(baseForm)) throw new ArgumentException("Forma base vazia.", nameof(baseForm));
        var b = baseForm.Trim();
        return tense switch
        {
            VerbTense.Present => b,
            VerbTense.Past => b + PastSuffix,
            VerbTense.Future => b + FutureSuffix,
            VerbTense.Imperative => b + ImperativeSuffix,
            _ => throw new ArgumentOutOfRangeException(nameof(tense))
        };
    }

    public VerbConjugation Conjugate(string baseForm) => new(
        Inflect(baseForm, VerbTense.Present),
        Inflect(baseForm, VerbTense.Past),
        Inflect(baseForm, VerbTense.Future),
        Inflect(baseForm, VerbTense.Imperative));

    public bool IsBaseForm(string form) => StripInflectionSuffix(form) is null;

    public RhaghoVerbAnalysis? StripInflectionSuffix(string form)
    {
        if (string.IsNullOrWhiteSpace(form)) return null;
        var unified = TextNormalizer.UnifyApostrophes(form.Trim());
        foreach (var (suffix, tense) in Suffixes)
        {
            if (unified.Length > suffix.Length && unified.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return new RhaghoVerbAnalysis(unified[..^suffix.Length], tense);
        }
        return null;
    }

    public IReadOnlyList<RhaghoVerbAnalysis> AnalyzeRhaghoForm(string form)
    {
        var result = new List<RhaghoVerbAnalysis>();
        var stripped = StripInflectionSuffix(form);
        if (stripped is not null) result.Add(stripped);
        result.Add(new RhaghoVerbAnalysis(TextNormalizer.UnifyApostrophes(form.Trim()), VerbTense.Present));
        return result;
    }

    // ---------------------------------------------------------------- Português (verbos regulares)

    // Terminações em forma normalizada (sem acento). Ordem das pessoas: eu, tu, ele, nós, vós, eles.
    private static readonly Dictionary<char, string[]> PresentEndings = new()
    {
        ['a'] = new[] { "o", "as", "a", "amos", "ais", "am" },
        ['e'] = new[] { "o", "es", "e", "emos", "eis", "em" },
        ['i'] = new[] { "o", "es", "e", "imos", "is", "em" }
    };

    private static readonly Dictionary<char, string[]> PastEndings = new()
    {
        ['a'] = new[] { "ei", "aste", "ou", "amos", "astes", "aram" },
        ['e'] = new[] { "i", "este", "eu", "emos", "estes", "eram" },
        ['i'] = new[] { "i", "iste", "iu", "imos", "istes", "iram" }
    };

    // Futuro: infinitivo completo + terminação (ei, ás, á, emos, eis, ão).
    private static readonly string[] FutureEndingsNormalized = { "ei", "as", "a", "emos", "eis", "ao" };
    private static readonly string[] FutureEndingsAccented = { "ei", "ás", "á", "emos", "eis", "ão" };

    // Imperativo (formas de você, nós, vós, vocês) -> slots 2,3,4,5.
    private static readonly Dictionary<char, string[]> ImperativeEndings = new()
    {
        ['a'] = new[] { "e", "emos", "eis", "em" },
        ['e'] = new[] { "a", "amos", "ais", "am" },
        ['i'] = new[] { "a", "amos", "ais", "am" }
    };

    public bool LooksLikePortugueseInfinitive(string word)
    {
        var n = TextNormalizer.Normalize(word);
        if (n == "ir") return true;   // único infinitivo de duas letras
        return n.Length >= 3 && !n.Contains(' ') && (n.EndsWith("ar") || n.EndsWith("er") || n.EndsWith("ir") || n.EndsWith("or"));
    }

    public IReadOnlyList<PortugueseVerbCandidate> AnalyzePortugueseForm(string surface)
    {
        var s = TextNormalizer.Normalize(surface);
        var result = new List<PortugueseVerbCandidate>();
        if (s.Length < 2 || s.Contains(' ')) return result;

        void Add(string infinitive, VerbTense tense, int? slot)
        {
            if (!result.Any(r => r.Infinitive == infinitive && r.Tense == tense && r.PersonSlot == slot))
                result.Add(new PortugueseVerbCandidate(infinitive, tense, slot));
        }

        // Infinitivo
        if (LooksLikePortugueseInfinitive(s) && !s.EndsWith("or")) Add(s, VerbTense.Present, null);

        foreach (var conj in new[] { 'a', 'e', 'i' })
        {
            var themeEnding = conj + "r";

            ScanParadigm(PresentEndings[conj], 0, VerbTense.Present);
            ScanParadigm(PastEndings[conj], 0, VerbTense.Past);
            ScanParadigm(ImperativeEndings[conj], 2, VerbTense.Imperative);

            void ScanParadigm(string[] endings, int firstSlot, VerbTense tense)
            {
                for (var i = 0; i < endings.Length; i++)
                {
                    var e = endings[i];
                    if (s.Length <= e.Length || !s.EndsWith(e, StringComparison.Ordinal)) continue;
                    var stem = s[..^e.Length];
                    Add(stem + themeEnding, tense, firstSlot + i);
                    if (conj == 'a' && (e[0] is 'e' or 'i'))
                    {
                        if (stem.EndsWith("qu")) Add(stem[..^2] + "c" + themeEnding, tense, firstSlot + i);
                        else if (stem.EndsWith("gu")) Add(stem[..^1] + themeEnding, tense, firstSlot + i);
                    }
                }
            }
        }

        // Futuro: stem já contém o infinitivo (ex.: amar + ei).
        for (var i = 0; i < FutureEndingsNormalized.Length; i++)
        {
            var e = FutureEndingsNormalized[i];
            if (s.Length <= e.Length || !s.EndsWith(e, StringComparison.Ordinal)) continue;
            var inf = s[..^e.Length];
            if (LooksLikePortugueseInfinitive(inf) && !inf.EndsWith("or")) Add(inf, VerbTense.Future, i);
        }

        // Ordem determinística: presente, passado, futuro, imperativo.
        return result.OrderBy(r => (int)r.Tense).ThenBy(r => r.PersonSlot ?? -1).ToList();
    }

    public string? ConjugatePortuguese(string infinitive, VerbTense tense, int? personSlot)
    {
        if (string.IsNullOrWhiteSpace(infinitive)) return null;
        var inf = infinitive.Trim();
        var norm = TextNormalizer.Normalize(inf);
        if (!LooksLikePortugueseInfinitive(norm) || norm.EndsWith("or")) return null;

        var theme = norm[^2]; // a, e, i
        var stem = inf[..^2];

        switch (tense)
        {
            case VerbTense.Present:
                if (personSlot is null) return inf;
                return Join(stem, PresentEndings[theme][Math.Clamp(personSlot.Value, 0, 5)], theme);
            case VerbTense.Past:
                return Join(stem, PastEndings[theme][Math.Clamp(personSlot ?? 0, 0, 5)], theme);
            case VerbTense.Future:
                return inf + FutureEndingsAccented[Math.Clamp(personSlot ?? 0, 0, 5)];
            case VerbTense.Imperative:
                var slot = personSlot is >= 2 and <= 5 ? personSlot.Value : 2;
                return Join(stem, ImperativeEndings[theme][slot - 2], theme);
            default:
                return null;
        }
    }

    /// <summary>Junta radical e terminação aplicando as alternâncias ortográficas regulares (dançar→dance, tocar→toque, pagar→pague, vencer→venço).</summary>
    private static string Join(string stem, string ending, char theme)
    {
        if (stem.Length == 0 || ending.Length == 0) return stem + ending;
        var soft = ending[0] is 'e' or 'i' or 'é' or 'ê' or 'í';
        var hard = ending[0] is 'a' or 'o' or 'á' or 'ó';
        var last = char.ToLowerInvariant(stem[^1]);

        if (theme == 'a' && soft)
        {
            if (last == 'ç') return stem[..^1] + (char.IsUpper(stem[^1]) ? "C" : "c") + ending;
            if (last == 'c') return stem + "qu" + ending;
            if (last == 'g') return stem + "u" + ending;
        }
        else if (theme != 'a' && hard)
        {
            if (last == 'g') return stem[..^1] + "j" + ending;
            if (last == 'c') return stem[..^1] + "ç" + ending;
        }
        return stem + ending;
    }
}
