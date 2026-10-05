using Rhaghoorian.Core.Abstractions;
using Rhaghoorian.Core.Ai;
using Rhaghoorian.Core.Errors;
using Rhaghoorian.Core.Linguistics;
using Rhaghoorian.Core.Models;
using Rhaghoorian.Core.Translation;

namespace Rhaghoorian.Core.Services;

public interface ITranslationWorkflow
{
    /// <summary>Traduz localmente e grava no histórico local.</summary>
    Task<TranslationResult> TranslateAsync(string text, TranslationDirection direction, CancellationToken ct = default);
    /// <summary>Ação explícita do usuário: pede à IA apenas para completar lacunas/ambiguidades do resultado local.</summary>
    Task<TranslationResult> RefineWithAiAsync(TranslationResult local, CancellationToken ct = default);

    Task<IReadOnlyList<TranslationHistoryItem>> GetHistoryAsync(int take = 200, CancellationToken ct = default);
    Task DeleteHistoryItemAsync(long id, CancellationToken ct = default);
    Task ClearHistoryAsync(CancellationToken ct = default);
}

public sealed class TranslationWorkflow : ITranslationWorkflow
{
    private readonly ITranslationService _translator;
    private readonly ITranslationHistoryRepository _history;
    private readonly IAiClient _ai;
    private readonly AiContextBuilder _contextBuilder;
    private readonly IDictionaryLookup _lookup;
    private readonly IRhaghoLinguisticService _ling;
    private readonly IConnectivityService _connectivity;

    public TranslationWorkflow(ITranslationService translator, ITranslationHistoryRepository history, IAiClient ai,
        AiContextBuilder contextBuilder, IDictionaryLookup lookup, IRhaghoLinguisticService ling, IConnectivityService connectivity)
    {
        _translator = translator; _history = history; _ai = ai; _contextBuilder = contextBuilder;
        _lookup = lookup; _ling = ling; _connectivity = connectivity;
    }

    public async Task<TranslationResult> TranslateAsync(string text, TranslationDirection direction, CancellationToken ct = default)
    {
        var result = await _translator.TranslateAsync(text, direction, ct);
        await SaveAsync(result.SourceText, direction, result.Text, ct);
        return result;
    }

    public async Task<TranslationResult> RefineWithAiAsync(TranslationResult local, CancellationToken ct = default)
    {
        if (!local.CanBenefitFromAi) return local;
        if (!_connectivity.IsOnline) throw new OfflineException("A IA precisa de conexão com a internet.");

        var request = await _contextBuilder.BuildTranslationAsync(local, ct);
        var raw = await _ai.TranslateAsync(request, ct);
        var text = AiResponseSanitizer.CleanTranslation(raw);

        if (local.Direction == TranslationDirection.PortugueseToRhagho)
            await AiOutputValidator.EnsureOnlyKnownRhaghoWordsAsync(text, _lookup, _ling, ct);

        var refined = new TranslationResult
        {
            Direction = local.Direction,
            SourceText = local.SourceText,
            Text = text,
            Segments = local.Segments,
            UnknownWords = local.UnknownWords,
            UsedAi = true
        };
        await SaveAsync(local.SourceText, local.Direction, text, ct);
        return refined;
    }

    public Task<IReadOnlyList<TranslationHistoryItem>> GetHistoryAsync(int take = 200, CancellationToken ct = default) => _history.ListAsync(take, 0, ct);
    public Task DeleteHistoryItemAsync(long id, CancellationToken ct = default) => _history.DeleteAsync(id, ct);
    public Task ClearHistoryAsync(CancellationToken ct = default) => _history.ClearAsync(ct);

    private Task SaveAsync(string source, TranslationDirection direction, string result, CancellationToken ct) =>
        _history.AddAsync(new TranslationHistoryItem
        {
            SourceText = source, Direction = direction, ResultText = result, CreatedAt = DateTimeOffset.UtcNow
        }, ct);
}
