using Rhaghoorian.Core.Abstractions;
using Rhaghoorian.Core.Errors;
using Rhaghoorian.Core.Linguistics;
using Rhaghoorian.Core.Models;
using Rhaghoorian.Core.Text;

namespace Rhaghoorian.Core.Ai;

public sealed record VerbAiCheck(VerbConjugation Official, VerbConjugation? FromAi, bool AiAgrees);

public static class AiOutputValidator
{
    /// <summary>
    /// A IA nunca decide sufixos verbais: o resultado oficial é sempre o determinístico.
    /// A sugestão da IA serve apenas como conferência (concorda / divergiu).
    /// </summary>
    public static VerbAiCheck CheckConjugation(IRhaghoLinguisticService ling, string baseForm, VerbConjugation? ai)
    {
        var official = ling.Conjugate(baseForm);
        var agrees = ai is not null
            && TextNormalizer.Normalize(ai.Present) == TextNormalizer.Normalize(official.Present)
            && TextNormalizer.Normalize(ai.Past) == TextNormalizer.Normalize(official.Past)
            && TextNormalizer.Normalize(ai.Future) == TextNormalizer.Normalize(official.Future)
            && TextNormalizer.Normalize(ai.Imperative) == TextNormalizer.Normalize(official.Imperative);
        return new VerbAiCheck(official, ai, agrees);
    }

    /// <summary>
    /// Português -> Rhagho'oriano via IA só é aceito se TODAS as palavras da resposta existirem no dicionário
    /// (diretamente ou como base + sufixo verbal). Impede a IA de inventar vocabulário.
    /// </summary>
    public static async Task EnsureOnlyKnownRhaghoWordsAsync(string output, IDictionaryLookup lookup, IRhaghoLinguisticService ling, CancellationToken ct = default)
    {
        foreach (var word in Tokenizer.Tokenize(output).Where(t => t.Kind == TokenKind.Word))
        {
            if ((await lookup.FindByRhaghoAsync(word.Normalized, ct)).Count > 0) continue;

            var stripped = ling.StripInflectionSuffix(word.Normalized);
            if (stripped is not null && (await lookup.FindByRhaghoAsync(TextNormalizer.Normalize(stripped.Base), ct)).Any(e => e.IsVerb)) continue;

            throw new InvalidAiResponseException(
                $"A IA propôs a palavra \"{word.Text}\", que não existe no dicionário. A resposta foi descartada para não inventar vocabulário.");
        }
    }
}
