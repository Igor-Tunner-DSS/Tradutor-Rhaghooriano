using Rhaghoorian.Core.Abstractions;
using Rhaghoorian.Core.Errors;
using Rhaghoorian.Core.Linguistics;
using Rhaghoorian.Core.Models;

namespace Rhaghoorian.Core.Services;

public interface IDictionaryService
{
    Task<IReadOnlyList<WordEntry>> SearchAsync(DictionaryQuery query, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetPartsOfSpeechAsync(CancellationToken ct = default);
    Task<WordEntry?> GetAsync(string id, CancellationToken ct = default);
    Task<WordEntry> CreateAsync(WordEntry entry, CancellationToken ct = default);
    Task<WordEntry> UpdateAsync(WordEntry entry, CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);
    /// <summary>Busca a versão atual do servidor e a grava no cache (usado após um conflito de edição).</summary>
    Task<WordEntry> ReloadFromRemoteAsync(string id, CancellationToken ct = default);
    VerbConjugation? GenerateConjugation(WordEntry entry);
}

/// <summary>
/// Leitura sempre pelo cache local (SQLite). Escrita: exige usuário autenticado e conectividade, grava primeiro no
/// Firestore (fonte da verdade) e só então atualiza o cache — nunca informa sucesso sem persistência real.
/// </summary>
public sealed class DictionaryService : IDictionaryService
{
    private readonly IDictionaryRepository _local;
    private readonly IRemoteDictionaryStore _remote;
    private readonly IAuthService _auth;
    private readonly IConnectivityService _connectivity;
    private readonly IRhaghoLinguisticService _ling;

    public DictionaryService(IDictionaryRepository local, IRemoteDictionaryStore remote, IAuthService auth,
        IConnectivityService connectivity, IRhaghoLinguisticService ling)
    {
        _local = local; _remote = remote; _auth = auth; _connectivity = connectivity; _ling = ling;
    }

    public Task<IReadOnlyList<WordEntry>> SearchAsync(DictionaryQuery query, CancellationToken ct = default) => _local.SearchAsync(query, ct);
    public Task<IReadOnlyList<string>> GetPartsOfSpeechAsync(CancellationToken ct = default) => _local.GetPartsOfSpeechAsync(ct);
    public Task<WordEntry?> GetAsync(string id, CancellationToken ct = default) => _local.GetAsync(id, ct);

    public VerbConjugation? GenerateConjugation(WordEntry entry) =>
        entry.IsVerb && entry.Verb is { IsInfinitive: true, Base.Length: > 0 } ? _ling.Conjugate(entry.Verb.Base) : null;

    private void RequireAdmin()
    {
        if (!_auth.IsAuthenticated) throw new NotAuthenticatedException();
    }

    private void RequireOnline()
    {
        if (!_connectivity.IsOnline) throw new OfflineException();
    }

    private WordEntry Prepare(WordEntry input)
    {
        var entry = input.Clone();
        entry.Rhagho = entry.Rhagho.Trim();
        entry.Portuguese = entry.Portuguese.Trim();
        entry.PartOfSpeech = entry.PartOfSpeech.Trim();
        entry.AlternativeForms = entry.AlternativeForms.Where(a => !string.IsNullOrWhiteSpace(a.Text)).ToList();
        entry.Synonyms = entry.Synonyms.Where(s => !string.IsNullOrWhiteSpace(s.Text)).ToList();
        entry.Abbreviations = entry.Abbreviations.Where(a => !string.IsNullOrWhiteSpace(a)).ToList();
        EntryValidator.NormalizeVerb(entry);

        var errors = EntryValidator.Validate(entry, _ling);
        if (errors.Count > 0) throw new InvalidInputException(string.Join(" ", errors));
        return entry;
    }

    public async Task<WordEntry> CreateAsync(WordEntry input, CancellationToken ct = default)
    {
        RequireAdmin();
        var entry = Prepare(input);
        RequireOnline();

        var now = DateTimeOffset.UtcNow;
        if (string.IsNullOrWhiteSpace(entry.Id)) entry.Id = Guid.NewGuid().ToString("N");
        entry.CreatedAt = entry.UpdatedAt = now;
        entry.CreatedBy = entry.UpdatedBy = _auth.UserId;
        entry.Version = 1;
        entry.RemoteUpdateTime = null;
        entry.SortOrder = await _local.GetMaxSortOrderAsync(ct) + 1;

        var saved = await _remote.CreateAsync(entry, ct);
        await _local.UpsertAsync(saved, ct);
        return saved;
    }

    public async Task<WordEntry> UpdateAsync(WordEntry input, CancellationToken ct = default)
    {
        RequireAdmin();
        var entry = Prepare(input);
        RequireOnline();

        var existing = await _local.GetAsync(entry.Id, ct) ?? throw new InvalidInputException("O registro não existe mais no dicionário local. Atualize a lista.");
        entry.CreatedAt = existing.CreatedAt;
        entry.CreatedBy = existing.CreatedBy;
        entry.SortOrder = existing.SortOrder;
        entry.SourceLine = existing.SourceLine;
        entry.UpdatedAt = DateTimeOffset.UtcNow;
        entry.UpdatedBy = _auth.UserId;
        entry.Version = existing.Version + 1;
        // A pré-condição é a versão que o usuário estava vendo ao abrir o formulário.
        entry.RemoteUpdateTime = input.RemoteUpdateTime;

        var saved = await _remote.UpdateAsync(entry, ct);
        await _local.UpsertAsync(saved, ct);
        return saved;
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        RequireAdmin();
        RequireOnline();
        var existing = await _local.GetAsync(id, ct) ?? throw new InvalidInputException("O registro não existe mais no dicionário local.");
        await _remote.DeleteAsync(id, existing.RemoteUpdateTime, ct);
        await _local.DeleteAsync(id, ct);
    }

    public async Task<WordEntry> ReloadFromRemoteAsync(string id, CancellationToken ct = default)
    {
        RequireOnline();
        var remote = await _remote.GetAsync(id, ct);
        if (remote is null)
        {
            await _local.DeleteAsync(id, ct);
            throw new InvalidInputException("Este registro foi excluído por outra pessoa.");
        }
        await _local.UpsertAsync(remote, ct);
        return remote;
    }
}
