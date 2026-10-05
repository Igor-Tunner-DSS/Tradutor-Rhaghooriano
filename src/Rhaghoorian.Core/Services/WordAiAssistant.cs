using Rhaghoorian.Core.Abstractions;
using Rhaghoorian.Core.Ai;
using Rhaghoorian.Core.Errors;
using Rhaghoorian.Core.Linguistics;
using Rhaghoorian.Core.Models;
using Rhaghoorian.Core.Text;

namespace Rhaghoorian.Core.Services;

public interface IWordAiAssistant
{
    /// <summary>Sugestões de sinônimos. NUNCA persiste nada: quem aceita e salva é o usuário autenticado no formulário.</summary>
    Task<IReadOnlyList<string>> SuggestSynonymsAsync(WordEntry draft, CancellationToken ct = default);
    /// <summary>Pede à IA as quatro formas e as confronta com as regras oficiais. O resultado oficial prevalece sempre.</summary>
    Task<VerbAiCheck> CheckConjugationAsync(string infinitive, string baseForm, CancellationToken ct = default);
}

public sealed class WordAiAssistant : IWordAiAssistant
{
    private readonly IAiClient _ai;
    private readonly IAuthService _auth;
    private readonly IConnectivityService _net;
    private readonly IRhaghoLinguisticService _ling;

    public WordAiAssistant(IAiClient ai, IAuthService auth, IConnectivityService net, IRhaghoLinguisticService ling)
    {
        _ai = ai; _auth = auth; _net = net; _ling = ling;
    }

    public async Task<IReadOnlyList<string>> SuggestSynonymsAsync(WordEntry draft, CancellationToken ct = default)
    {
        Guard();
        if (string.IsNullOrWhiteSpace(draft.Portuguese)) throw new InvalidInputException("Informe o significado em português antes de pedir sinônimos.");

        var existing = draft.Synonyms.Select(s => s.Text).ToList();
        var raw = await _ai.SuggestSynonymsAsync(new AiSynonymRequest(
            draft.Portuguese.Trim(), string.IsNullOrWhiteSpace(draft.Rhagho) ? null : draft.Rhagho.Trim(),
            string.IsNullOrWhiteSpace(draft.PartOfSpeech) ? null : draft.PartOfSpeech.Trim(), existing), ct);

        var skip = existing.Select(TextNormalizer.Normalize).Append(TextNormalizer.Normalize(draft.Portuguese)).ToHashSet();
        return raw.Where(s => !skip.Contains(TextNormalizer.Normalize(s))).ToList();
    }

    public async Task<VerbAiCheck> CheckConjugationAsync(string infinitive, string baseForm, CancellationToken ct = default)
    {
        Guard();
        if (string.IsNullOrWhiteSpace(baseForm)) throw new InvalidInputException("Informe a forma base do verbo.");
        if (!_ling.IsBaseForm(baseForm)) throw new InvalidInputException("A forma base não pode terminar em 'ta, 'vil ou 'nux.");

        var ai = await _ai.ConjugateAsync(new AiConjugationRequest(infinitive.Trim(), baseForm.Trim()), ct);
        return AiOutputValidator.CheckConjugation(_ling, baseForm.Trim(), ai);
    }

    private void Guard()
    {
        if (!_auth.IsAuthenticated) throw new NotAuthenticatedException("Entre com sua conta para usar a IA no cadastro.");
        if (!_net.IsOnline) throw new OfflineException("A IA precisa de conexão com a internet.");
    }
}
