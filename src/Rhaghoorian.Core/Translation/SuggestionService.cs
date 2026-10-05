using Rhaghoorian.Core.Abstractions;
using Rhaghoorian.Core.Linguistics;
using Rhaghoorian.Core.Models;
using Rhaghoorian.Core.Text;

namespace Rhaghoorian.Core.Translation;

public interface ISuggestionService
{
    Task<IReadOnlyList<WordSuggestion>> SuggestAsync(string word, TranslationDirection direction, int max = 5, CancellationToken ct = default);
}

/// <summary>Sugestões por distância de edição, prefixo e similaridade sobre chaves normalizadas (inclui formas alternativas).</summary>
public sealed class SuggestionService : ISuggestionService
{
    private readonly IDictionaryLookup _lookup;
    private readonly IRhaghoLinguisticService _ling;

    public SuggestionService(IDictionaryLookup lookup, IRhaghoLinguisticService ling)
    {
        _lookup = lookup;
        _ling = ling;
    }

    public async Task<IReadOnlyList<WordSuggestion>> SuggestAsync(string word, TranslationDirection direction, int max = 5, CancellationToken ct = default)
    {
        var norm = TextNormalizer.Normalize(word);
        if (norm.Length == 0) return Array.Empty<WordSuggestion>();

        // Em Rhagho'oriano -> Português, uma forma com sufixo verbal desconhecida é sugerida pela base e o sufixo é reanexado.
        var suffix = string.Empty;
        var probe = norm;
        if (direction == TranslationDirection.RhaghoToPortuguese && _ling.StripInflectionSuffix(norm) is { } stripped)
        {
            probe = TextNormalizer.Normalize(stripped.Base);
            suffix = _ling.Inflect("x", stripped.Tense)[1..];
        }

        var candidates = await _lookup.GetCandidateKeysAsync(direction, ct);
        var scored = new List<(SuggestionCandidate Cand, double Score)>();

        foreach (var c in candidates)
        {
            if (c.Key == probe) continue;
            var score = Score(probe, c.Key);
            if (score > 0) scored.Add((c, score));
        }

        return scored
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.Cand.Display.Length)
            .GroupBy(s => (s.Cand.Display, s.Cand.Meaning)).Select(g => g.First())
            .Take(max)
            .Select(s => new WordSuggestion(s.Cand.Display + suffix, s.Cand.Meaning, s.Cand.EntryId, Math.Round(s.Score, 3)))
            .ToList();
    }

    internal static double Score(string probe, string key)
    {
        var maxLen = Math.Max(probe.Length, key.Length);
        if (maxLen == 0) return 0;

        var distance = Levenshtein(probe, key, Math.Max(3, maxLen / 3 + 1));
        var prefix = CommonPrefix(probe, key);
        var similarity = 1.0 - (double)distance / maxLen;

        var closeEdit = distance <= Math.Max(1, Math.Min(3, maxLen / 3));
        var sharedPrefix = prefix >= 3 && prefix * 2 >= Math.Min(probe.Length, key.Length);
        var contains = probe.Length >= 3 && (key.StartsWith(probe, StringComparison.Ordinal) || probe.StartsWith(key, StringComparison.Ordinal));

        if (!closeEdit && !sharedPrefix && !contains) return 0;
        return similarity + 0.05 * Math.Min(prefix, 4) + (contains ? 0.1 : 0);
    }

    public static int CommonPrefix(string a, string b)
    {
        var n = Math.Min(a.Length, b.Length);
        var i = 0;
        while (i < n && a[i] == b[i]) i++;
        return i;
    }

    /// <summary>Distância de Levenshtein com corte: devolve cap+1 quando excede o limite.</summary>
    public static int Levenshtein(string a, string b, int cap = int.MaxValue)
    {
        if (a == b) return 0;
        if (a.Length == 0) return Math.Min(b.Length, cap + 1);
        if (b.Length == 0) return Math.Min(a.Length, cap + 1);
        if (Math.Abs(a.Length - b.Length) > cap) return cap + 1;

        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            var rowMin = curr[0];
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                if (curr[j] < rowMin) rowMin = curr[j];
            }
            if (rowMin > cap) return cap + 1;
            (prev, curr) = (curr, prev);
        }
        return prev[b.Length];
    }
}
