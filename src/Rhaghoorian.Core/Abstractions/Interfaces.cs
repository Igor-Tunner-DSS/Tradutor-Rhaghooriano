using Rhaghoorian.Core.Models;

namespace Rhaghoorian.Core.Abstractions;

public sealed record SuggestionCandidate(string Key, string Display, string EntryId, string Meaning);

/// <summary>
/// Alterações remotas desde um ponto. <paramref name="IsFull"/> = foto completa (substitui o cache); caso contrário, apenas o delta
/// (inclusões/alterações e exclusões). O custo em leituras do Firestore é proporcional ao delta, não ao tamanho do dicionário.
/// </summary>
public sealed record RemoteChanges(bool IsFull, IReadOnlyList<WordEntry> Upserts, IReadOnlyList<string> DeletedIds, DateTimeOffset? Cursor);

/// <summary>Consultas mínimas ao dicionário necessárias ao tradutor (implementadas por SQLite e por memória).</summary>
public interface IDictionaryLookup
{
    /// <param name="normalizedKey">Chave já normalizada (TextNormalizer.Normalize).</param>
    Task<IReadOnlyList<WordEntry>> FindByPortugueseAsync(string normalizedKey, CancellationToken ct = default);
    /// <summary>Procura pela grafia Rhagho'oriana e pelas formas alternativas. Ordenado por SortOrder.</summary>
    Task<IReadOnlyList<WordEntry>> FindByRhaghoAsync(string normalizedKey, CancellationToken ct = default);
    Task<IReadOnlyList<SuggestionCandidate>> GetCandidateKeysAsync(TranslationDirection direction, CancellationToken ct = default);
    Task<int> GetMaxExpressionWordsAsync(TranslationDirection direction, CancellationToken ct = default);
}

/// <summary>Cache local (SQLite) do dicionário.</summary>
public interface IDictionaryRepository : IDictionaryLookup
{
    Task<IReadOnlyList<WordEntry>> SearchAsync(DictionaryQuery query, CancellationToken ct = default);
    Task<int> CountAsync(DictionaryQuery query, CancellationToken ct = default);
    Task<WordEntry?> GetAsync(string id, CancellationToken ct = default);
    Task UpsertAsync(WordEntry entry, CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);
    /// <summary>Carga inicial local (revisão informada, sem cursor): a primeira sincronização real fará uma carga completa.</summary>
    Task ReplaceAllAsync(IReadOnlyCollection<WordEntry> entries, long remoteRevision, CancellationToken ct = default);
    /// <summary>Aplica uma foto completa (substitui tudo) ou um delta (exclusões primeiro, depois inclusões) e grava revisão + cursor.</summary>
    Task ApplyChangesAsync(RemoteChanges changes, long remoteRevision, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetPartsOfSpeechAsync(CancellationToken ct = default);
    Task<SyncState> GetSyncStateAsync(CancellationToken ct = default);
    Task<int> GetMaxSortOrderAsync(CancellationToken ct = default);
    /// <summary>Formas verbais geradas (presente/passado/futuro/imperativo) de um infinitivo.</summary>
    Task<VerbConjugation?> GetGeneratedFormsAsync(string entryId, CancellationToken ct = default);
}

public interface ITranslationHistoryRepository
{
    Task<TranslationHistoryItem> AddAsync(TranslationHistoryItem item, CancellationToken ct = default);
    Task<IReadOnlyList<TranslationHistoryItem>> ListAsync(int take = 200, int skip = 0, CancellationToken ct = default);
    Task DeleteAsync(long id, CancellationToken ct = default);
    Task ClearAsync(CancellationToken ct = default);
}

public interface ISettingsStore
{
    Task<string?> GetAsync(string key, CancellationToken ct = default);
    Task SetAsync(string key, string value, CancellationToken ct = default);
}

public sealed record ImportIssueRecord(
    long Id, string Severity, string Code, int? Line, string? Key, string Message, DateTimeOffset ImportedAt);

public interface IImportIssueRepository
{
    Task ReplaceAsync(IReadOnlyCollection<Importing.ImportIssue> issues, CancellationToken ct = default);
    Task<IReadOnlyList<ImportIssueRecord>> ListAsync(string? severity = null, CancellationToken ct = default);
}

/// <summary>Fonte compartilhada da verdade (Cloud Firestore).</summary>
public interface IRemoteDictionaryStore
{
    /// <summary>Contador de revisão do dicionário; nulo se ainda não inicializado no servidor.</summary>
    Task<long?> GetRevisionAsync(CancellationToken ct = default);
    /// <summary>
    /// <paramref name="since"/> nulo = tudo. Com valor, devolve só o que mudou desde então (carimbo de servidor), mais as exclusões.
    /// </summary>
    Task<RemoteChanges> ListChangesAsync(DateTimeOffset? since, CancellationToken ct = default);
    Task<WordEntry?> GetAsync(string id, CancellationToken ct = default);
    /// <summary>Cria o documento (falha com ConflictException se já existir). Devolve o registro com RemoteUpdateTime.</summary>
    Task<WordEntry> CreateAsync(WordEntry entry, CancellationToken ct = default);
    /// <summary>Atualiza com pré-condição RemoteUpdateTime (concorrência otimista).</summary>
    Task<WordEntry> UpdateAsync(WordEntry entry, CancellationToken ct = default);
    Task DeleteAsync(string id, string? expectedUpdateTime, CancellationToken ct = default);
    /// <summary>Cria somente documentos que ainda não existem. Devolve a quantidade criada.</summary>
    Task<int> CreateMissingAsync(IReadOnlyCollection<WordEntry> entries, IProgress<int>? progress = null, CancellationToken ct = default);
}

public interface IAuthService
{
    bool IsAuthenticated { get; }
    string? UserId { get; }
    string? Email { get; }
    event EventHandler? AuthStateChanged;
    Task<bool> TryRestoreSessionAsync(CancellationToken ct = default);
    Task SignInAsync(string email, string password, CancellationToken ct = default);
    Task SignOutAsync(CancellationToken ct = default);
    /// <summary>Token de ID válido (renova se necessário). Nulo quando não autenticado.</summary>
    Task<string?> GetIdTokenAsync(CancellationToken ct = default);
}

/// <summary>Armazenamento seguro de segredos do dispositivo (ex.: refresh token). Nunca guarda senhas.</summary>
public interface ISecureTokenStore
{
    Task<string?> GetAsync(string key);
    Task SetAsync(string key, string value);
    void Remove(string key);
}

public interface IConnectivityService
{
    bool IsOnline { get; }
}

public interface ISyncService
{
    Task<SyncResult> SyncAsync(CancellationToken ct = default);
}
