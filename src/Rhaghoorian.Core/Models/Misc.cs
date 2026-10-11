namespace Rhaghoorian.Core.Models;

public sealed record VerbConjugation(string Present, string Past, string Future, string Imperative)
{
    public string Get(VerbTense tense) => tense switch
    {
        VerbTense.Present => Present,
        VerbTense.Past => Past,
        VerbTense.Future => Future,
        VerbTense.Imperative => Imperative,
        _ => throw new ArgumentOutOfRangeException(nameof(tense))
    };
}

public sealed class TranslationHistoryItem
{
    public long Id { get; set; }
    public string SourceText { get; set; } = string.Empty;
    public TranslationDirection Direction { get; set; }
    public string ResultText { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class DictionaryQuery
{
    public string? Text { get; set; }
    public string? PartOfSpeech { get; set; }
    public DictionarySort Sort { get; set; } = DictionarySort.AlphabeticalPortuguese;
    public bool Descending { get; set; }
    /// <summary>Quando falso, formas verbais flexionadas importadas individualmente ficam ocultas na listagem (continuam pesquisáveis pelo tradutor).</summary>
    public bool IncludeInflectedForms { get; set; }
    public int Take { get; set; } = 500;
    public int Skip { get; set; }
}

/// <param name="Cursor">Maior carimbo de servidor (syncStamp) já aplicado; ponto de partida da sincronização incremental.</param>
/// <param name="LastFullSyncAt">Última carga completa; uma carga completa é refeita periodicamente como rede de segurança.</param>
public sealed record SyncState(long? Revision, DateTimeOffset? LastSyncAt, int LocalCount, DateTimeOffset? Cursor = null, DateTimeOffset? LastFullSyncAt = null);

public enum SyncOutcome { UpToDate, Updated, Offline, RemoteNotInitialized, Failed }

public sealed record SyncResult(SyncOutcome Outcome, int Count = 0, string? Message = null);
