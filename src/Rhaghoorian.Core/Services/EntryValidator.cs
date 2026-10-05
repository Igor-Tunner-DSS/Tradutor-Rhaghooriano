using Rhaghoorian.Core.Linguistics;
using Rhaghoorian.Core.Models;
using Rhaghoorian.Core.Text;

namespace Rhaghoorian.Core.Services;

public static class EntryValidator
{
    /// <summary>Erros que impedem salvar. Lista vazia = válido.</summary>
    public static IReadOnlyList<string> Validate(WordEntry e, IRhaghoLinguisticService ling)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(e.Rhagho)) errors.Add("Informe a grafia em Rhagho'oriano.");
        if (string.IsNullOrWhiteSpace(e.Portuguese)) errors.Add("Informe o significado em português.");
        if (string.IsNullOrWhiteSpace(e.PartOfSpeech)) errors.Add("Escolha a classe gramatical.");

        if (e.AlternativeForms.Any(a => string.IsNullOrWhiteSpace(a.Text))) errors.Add("Há uma forma alternativa vazia.");
        if (e.Synonyms.Any(s => string.IsNullOrWhiteSpace(s.Text))) errors.Add("Há um sinônimo vazio.");

        if (e.IsVerb && e.Verb is { IsInfinitive: true })
        {
            if (string.IsNullOrWhiteSpace(e.Verb.Base)) errors.Add("Informe a forma base do verbo em Rhagho'oriano.");
            else if (!ling.IsBaseForm(e.Verb.Base))
                errors.Add("A forma base do verbo não pode terminar em 'ta, 'vil ou 'nux: esses sufixos são gerados automaticamente.");
            if (!string.IsNullOrWhiteSpace(e.Portuguese) && !ling.LooksLikePortugueseInfinitive(e.Portuguese))
                errors.Add("Para verbos, o significado deve ser o infinitivo em português (ex.: amar).");
        }
        return errors;
    }

    /// <summary>Garante que verbos novos tenham VerbInfo coerente com os campos principais (infinitivo + forma base).</summary>
    public static void NormalizeVerb(WordEntry e)
    {
        if (!e.IsVerb) { if (e.Verb is { IsInfinitive: true }) e.Verb = null; return; }
        if (e.Verb is null || e.Verb.IsInfinitive)
        {
            e.Verb = new VerbInfo
            {
                Infinitive = e.Portuguese.Trim(),
                Base = TextNormalizer.UnifyApostrophes(e.Rhagho.Trim()),
                Tense = VerbTense.Present,
                IsInfinitive = true
            };
        }
    }
}
