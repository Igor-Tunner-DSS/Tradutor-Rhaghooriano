using Rhaghoorian.Core.Abstractions;
using Rhaghoorian.Core.Models;
using Rhaghoorian.Core.Text;

namespace Rhaghoorian.Core.Services;

/// <summary>Implementação em memória de <see cref="IDictionaryRepository"/> usada SOMENTE nos testes. O SQLite segue o mesmo contrato.</summary>
public sealed class InMemoryDictionaryRepository : IDictionaryRepository
{
    private readonly Dictionary<string, WordEntry> _entries = new();
    private long? _revision;
    private DateTimeOffset? _lastSync;
    private DateTimeOffset? _cursor;
    private DateTimeOffset? _lastFull;
    private readonly object _gate = new();

    private List<WordEntry> Snapshot() { lock (_gate) return _entries.Values.Select(e => e.Clone()).OrderBy(e => e.SortOrder).ToList(); }

    private InMemoryDictionaryLookup Lookup() => new(Snapshot());

    public Task<IReadOnlyList<WordEntry>> FindByPortugueseAsync(string k, CancellationToken ct = default) => Lookup().FindByPortugueseAsync(k, ct);
    public Task<IReadOnlyList<WordEntry>> FindByRhaghoAsync(string k, CancellationToken ct = default) => Lookup().FindByRhaghoAsync(k, ct);
    public Task<IReadOnlyList<SuggestionCandidate>> GetCandidateKeysAsync(TranslationDirection d, CancellationToken ct = default) => Lookup().GetCandidateKeysAsync(d, ct);
    public Task<int> GetMaxExpressionWordsAsync(TranslationDirection d, CancellationToken ct = default) => Lookup().GetMaxExpressionWordsAsync(d, ct);

    private IEnumerable<WordEntry> Filter(DictionaryQuery q)
    {
        var text = TextNormalizer.Normalize(q.Text);
        var pos = TextNormalizer.Normalize(q.PartOfSpeech);
        IEnumerable<WordEntry> result = Snapshot();

        if (text.Length > 0)
            result = result.Where(e => DictionaryKeys.Portuguese(e).Contains(text, StringComparison.Ordinal)
                                       || DictionaryKeys.RhaghoKeysWithAlternatives(e).Any(k => k.Contains(text, StringComparison.Ordinal)));
        else if (!q.IncludeInflectedForms)
            result = result.Where(e => !e.IsInflectedVerbForm);

        if (pos.Length > 0 && pos != "todas")
            result = result.Where(e => TextNormalizer.Normalize(e.PartOfSpeech) == pos);
        return result;
    }

    public Task<IReadOnlyList<WordEntry>> SearchAsync(DictionaryQuery q, CancellationToken ct = default)
    {
        var filtered = Filter(q);
        IOrderedEnumerable<WordEntry> ordered = q.Sort switch
        {
            DictionarySort.AlphabeticalRhagho => OrderBy(filtered, e => DictionaryKeys.Rhagho(e), StringComparer.Ordinal, q.Descending),
            DictionarySort.LetterCount => OrderBy(filtered, e => TextNormalizer.CountLetters(e.Rhagho), Comparer<int>.Default, q.Descending)
                .ThenBy(e => DictionaryKeys.Rhagho(e), StringComparer.Ordinal),
            _ => OrderBy(filtered, e => DictionaryKeys.Portuguese(e), StringComparer.Ordinal, q.Descending)
        };
        var page = ordered.ThenBy(e => e.SortOrder).Skip(q.Skip).Take(q.Take).ToList();
        return Task.FromResult<IReadOnlyList<WordEntry>>(page);
    }

    private static IOrderedEnumerable<WordEntry> OrderBy<TKey>(IEnumerable<WordEntry> src, Func<WordEntry, TKey> key, IComparer<TKey> cmp, bool desc) =>
        desc ? src.OrderByDescending(key, cmp) : src.OrderBy(key, cmp);

    public Task<int> CountAsync(DictionaryQuery q, CancellationToken ct = default) => Task.FromResult(Filter(q).Count());

    public Task<WordEntry?> GetAsync(string id, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_entries.TryGetValue(id, out var e) ? e.Clone() : null);
    }

    public Task UpsertAsync(WordEntry entry, CancellationToken ct = default)
    {
        lock (_gate) _entries[entry.Id] = entry.Clone();
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string id, CancellationToken ct = default)
    {
        lock (_gate) _entries.Remove(id);
        return Task.CompletedTask;
    }

    public Task ReplaceAllAsync(IReadOnlyCollection<WordEntry> entries, long remoteRevision, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _entries.Clear();
            foreach (var e in entries) _entries[e.Id] = e.Clone();
            _revision = remoteRevision;
            _lastSync = DateTimeOffset.UtcNow;
            _cursor = null; _lastFull = null;
        }
        return Task.CompletedTask;
    }

    public Task ApplyChangesAsync(RemoteChanges changes, long remoteRevision, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (changes.IsFull) { _entries.Clear(); _lastFull = DateTimeOffset.UtcNow; }
            foreach (var id in changes.DeletedIds) _entries.Remove(id);              // exclusões primeiro
            foreach (var e in changes.Upserts) _entries[e.Id] = e.Clone();
            _revision = remoteRevision;
            _lastSync = DateTimeOffset.UtcNow;
            if (changes.Cursor is { } c && (_cursor is null || c > _cursor)) _cursor = c;
        }
        return Task.CompletedTask;
    }

    /// <summary>Somente testes: recua a data da última carga completa.</summary>
    public void SetLastFullSync(DateTimeOffset? at) { lock (_gate) _lastFull = at; }

    public Task<IReadOnlyList<string>> GetPartsOfSpeechAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>>(Snapshot().Select(e => e.PartOfSpeech).Where(p => p.Length > 0)
            .GroupBy(TextNormalizer.Normalize).Select(g => g.First()).OrderBy(TextNormalizer.Normalize).ToList());

    public Task<SyncState> GetSyncStateAsync(CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(new SyncState(_revision, _lastSync, _entries.Count, _cursor, _lastFull));
    }

    public Task<int> GetMaxSortOrderAsync(CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_entries.Count == 0 ? 0 : _entries.Values.Max(e => e.SortOrder));
    }

    public Task<VerbConjugation?> GetGeneratedFormsAsync(string entryId, CancellationToken ct = default) => Task.FromResult<VerbConjugation?>(null);
}
