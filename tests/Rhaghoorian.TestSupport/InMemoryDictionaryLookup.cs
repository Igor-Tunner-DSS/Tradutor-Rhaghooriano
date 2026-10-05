using Rhaghoorian.Core.Abstractions;
using Rhaghoorian.Core.Models;
using Rhaghoorian.Core.Text;

namespace Rhaghoorian.Core.Services;

/// <summary>Implementação em memória de <see cref="IDictionaryLookup"/> usada SOMENTE nos testes; mesma semântica do SQLite.</summary>
public sealed class InMemoryDictionaryLookup : IDictionaryLookup
{
    private readonly List<WordEntry> _entries;

    public InMemoryDictionaryLookup(IEnumerable<WordEntry> entries) =>
        _entries = entries.OrderBy(e => e.SortOrder).ToList();

    public Task<IReadOnlyList<WordEntry>> FindByPortugueseAsync(string normalizedKey, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<WordEntry>>(_entries.Where(e => DictionaryKeys.Portuguese(e) == normalizedKey).ToList());

    public Task<IReadOnlyList<WordEntry>> FindByRhaghoAsync(string normalizedKey, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<WordEntry>>(_entries.Where(e => DictionaryKeys.RhaghoKeysWithAlternatives(e).Contains(normalizedKey)).ToList());

    public Task<IReadOnlyList<SuggestionCandidate>> GetCandidateKeysAsync(TranslationDirection direction, CancellationToken ct = default)
    {
        var list = new List<SuggestionCandidate>();
        foreach (var e in _entries)
        {
            if (direction == TranslationDirection.PortugueseToRhagho)
                list.Add(new SuggestionCandidate(DictionaryKeys.Portuguese(e), e.Portuguese, e.Id, e.Rhagho));
            else
                foreach (var k in DictionaryKeys.RhaghoKeysWithAlternatives(e))
                    list.Add(new SuggestionCandidate(k, e.Rhagho, e.Id, e.Portuguese));
        }
        return Task.FromResult<IReadOnlyList<SuggestionCandidate>>(list);
    }

    public Task<int> GetMaxExpressionWordsAsync(TranslationDirection direction, CancellationToken ct = default)
    {
        var max = _entries.Count == 0 ? 1 : _entries.Max(e => direction == TranslationDirection.PortugueseToRhagho
            ? DictionaryKeys.WordCount(DictionaryKeys.Portuguese(e))
            : DictionaryKeys.RhaghoKeysWithAlternatives(e).Max(DictionaryKeys.WordCount));
        return Task.FromResult(Math.Max(1, max));
    }
}
