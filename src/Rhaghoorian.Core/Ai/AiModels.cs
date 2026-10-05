using Rhaghoorian.Core.Models;

namespace Rhaghoorian.Core.Ai;

public sealed record AiContextEntry(string Rhagho, string Portuguese, string PartOfSpeech, string? Tense);

/// <summary>Contexto compacto: somente os registros relevantes ao texto (nunca o dicionário inteiro).</summary>
public sealed record AiContext(IReadOnlyList<AiContextEntry> Entries, IReadOnlyList<string> UnknownWords);

public sealed record AiTranslationRequest(string Text, TranslationDirection Direction, AiContext Context, string? LocalDraft);
public sealed record AiConjugationRequest(string Infinitive, string BaseForm);
public sealed record AiSynonymRequest(string Portuguese, string? Rhagho, string? PartOfSpeech, IReadOnlyList<string> Existing);

public interface IAiClient
{
    /// <summary>Devolve SOMENTE o texto traduzido.</summary>
    Task<string> TranslateAsync(AiTranslationRequest request, CancellationToken ct = default);
    /// <summary>Devolve somente as quatro formas (presente, passado, futuro, imperativo).</summary>
    Task<VerbConjugation> ConjugateAsync(AiConjugationRequest request, CancellationToken ct = default);
    /// <summary>Devolve somente uma lista de sinônimos em português.</summary>
    Task<IReadOnlyList<string>> SuggestSynonymsAsync(AiSynonymRequest request, CancellationToken ct = default);
}
