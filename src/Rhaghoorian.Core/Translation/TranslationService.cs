using Rhaghoorian.Core.Abstractions;
using Rhaghoorian.Core.Errors;
using Rhaghoorian.Core.Linguistics;
using Rhaghoorian.Core.Models;
using Rhaghoorian.Core.Text;

namespace Rhaghoorian.Core.Translation;

public interface ITranslationService
{
    /// <summary>Tradução 100% determinística e local. Nunca chama IA.</summary>
    Task<TranslationResult> TranslateAsync(string text, TranslationDirection direction, CancellationToken ct = default);
}

/// <summary>
/// Motor determinístico: normalização -> tokenização -> expressões (maior primeiro) -> consulta ao dicionário ->
/// regras verbais -> resolução de ambiguidades. Palavras ausentes NUNCA são inventadas: são mantidas como estão,
/// marcadas como desconhecidas e acompanhadas de sugestões.
/// </summary>
public sealed class TranslationService : ITranslationService
{
    private static readonly Dictionary<string, int> SubjectSlots = new()
    {
        ["eu"] = 0, ["tu"] = 1, ["voce"] = 2, ["ele"] = 2, ["ela"] = 2,
        ["nos"] = 3, ["vos"] = 4, ["voces"] = 5, ["eles"] = 5, ["elas"] = 5
    };

    private readonly IDictionaryLookup _lookup;
    private readonly IRhaghoLinguisticService _ling;
    private readonly ISuggestionService _suggestions;

    public TranslationService(IDictionaryLookup lookup, IRhaghoLinguisticService ling, ISuggestionService suggestions)
    {
        _lookup = lookup;
        _ling = ling;
        _suggestions = suggestions;
    }

    private sealed class ClauseState { public int? SubjectSlot; }

    private sealed record Resolution(string Output, SegmentKind Kind, string? EntryId, IReadOnlyList<string> Alternatives);

    public async Task<TranslationResult> TranslateAsync(string text, TranslationDirection direction, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidInputException("Digite um texto para traduzir.");

        var tokens = Tokenizer.Tokenize(text);
        var maxExpression = Math.Max(1, await _lookup.GetMaxExpressionWordsAsync(direction, ct));
        var segments = new List<TranslationSegment>();
        var unknownTokens = new List<Token>();
        var state = new ClauseState();

        for (var i = 0; i < tokens.Count;)
        {
            ct.ThrowIfCancellationRequested();
            var tok = tokens[i];

            if (!tok.IsWordLike)
            {
                segments.Add(new TranslationSegment(tok.Text, tok.Text, SegmentKind.Text));
                if (tok.Kind == TokenKind.NewLine || (tok.Kind == TokenKind.Punctuation && ".!?;,:".Contains(tok.Text)))
                    state.SubjectSlot = null;
                i++;
                continue;
            }

            Resolution? res = null;
            var consumed = 1;

            if (maxExpression > 1)
            {
                var span = CollectWordSpan(tokens, i, maxExpression);
                for (var n = span.Count; n >= 2 && res is null; n--)
                {
                    var key = string.Join(" ", span.Take(n).Select(ix => tokens[ix].Normalized));
                    res = await ResolveAsync(key, tok.Text, direction, state, allowMorphology: false, ct);
                    if (res is not null)
                    {
                        consumed = span[n - 1] - i + 1;
                        res = res with { Kind = SegmentKind.Expression };
                    }
                }
            }

            if (res is null) res = await ResolveAsync(tok.Normalized, tok.Text, direction, state, allowMorphology: true, ct);

            var source = string.Concat(tokens.Skip(i).Take(consumed).Select(t => t.Text));
            if (res is null)
            {
                segments.Add(new TranslationSegment(source, source, SegmentKind.Unknown));
                unknownTokens.Add(tok);
            }
            else
            {
                var kind = res.Kind == SegmentKind.Word && tok.Kind == TokenKind.Number ? SegmentKind.Number : res.Kind;
                segments.Add(new TranslationSegment(source, res.Output, kind, res.EntryId, res.Alternatives));
            }
            i += consumed;
        }

        if (direction == TranslationDirection.RhaghoToPortuguese) segments = ApplySentenceCase(segments);
        var output = string.Concat(segments.Select(s => s.Output));

        var unknown = new List<UnknownWord>();
        foreach (var group in unknownTokens.GroupBy(t => t.Normalized))
        {
            var first = group.First();
            var suggestions = await _suggestions.SuggestAsync(first.Text, direction, 5, ct);
            unknown.Add(new UnknownWord(first.Text, first.Position, suggestions));
        }

        return new TranslationResult
        {
            Direction = direction,
            SourceText = text,
            Text = output,
            Segments = segments,
            UnknownWords = unknown
        };
    }

    /// <summary>Maiúscula inicial por frase apenas nos trechos traduzidos; palavras desconhecidas permanecem como digitadas.</summary>
    private static List<TranslationSegment> ApplySentenceCase(List<TranslationSegment> segments)
    {
        var result = new List<TranslationSegment>(segments.Count);
        var capitalizeNext = true;
        foreach (var seg in segments)
        {
            switch (seg.Kind)
            {
                case SegmentKind.Word or SegmentKind.Expression:
                    result.Add(capitalizeNext ? seg with { Output = CaseHelper.Capitalize(seg.Output) } : seg);
                    capitalizeNext = false;
                    break;
                case SegmentKind.Number or SegmentKind.Unknown:
                    result.Add(seg);
                    capitalizeNext = false;
                    break;
                default:
                    result.Add(seg);
                    if (seg.Source.Any(c => c is '.' or '!' or '?' or '\n' or '\r')) capitalizeNext = true;
                    break;
            }
        }
        return result;
    }

    private static List<int> CollectWordSpan(IReadOnlyList<Token> tokens, int start, int max)
    {
        var indices = new List<int> { start };
        var j = start;
        while (indices.Count < max && j + 2 < tokens.Count && tokens[j + 1].Kind == TokenKind.Whitespace && tokens[j + 2].IsWordLike)
        {
            indices.Add(j + 2);
            j += 2;
        }
        return indices;
    }

    private Task<Resolution?> ResolveAsync(string key, string source, TranslationDirection direction, ClauseState state, bool allowMorphology, CancellationToken ct) =>
        direction == TranslationDirection.PortugueseToRhagho
            ? ResolvePortugueseAsync(key, source, allowMorphology, ct)
            : ResolveRhaghoAsync(key, allowMorphology, state, ct);

    // ------------------------------------------------------------ Português -> Rhagho'oriano

    private async Task<Resolution?> ResolvePortugueseAsync(string key, string source, bool allowMorphology, CancellationToken ct)
    {
        var entries = await _lookup.FindByPortugueseAsync(key, ct);
        if (entries.Count > 0)
            return BuildPt(source, entries.Select(e => (e.Id, e.Rhagho)).ToList());

        if (!allowMorphology) return null;

        var candidates = new List<(string Id, string Output)>();
        foreach (var c in _ling.AnalyzePortugueseForm(key))
        {
            var infinitives = await _lookup.FindByPortugueseAsync(c.Infinitive, ct);
            foreach (var e in infinitives.Where(IsInfinitiveVerb))
            {
                var baseForm = !string.IsNullOrWhiteSpace(e.Verb?.Base) ? e.Verb!.Base : e.Rhagho;
                candidates.Add((e.Id, _ling.Inflect(baseForm, c.Tense)));
            }
        }
        return candidates.Count == 0 ? null : BuildPt(source, candidates);
    }

    private static Resolution BuildPt(string source, List<(string Id, string Output)> candidates)
    {
        var distinct = candidates.DistinctBy(c => c.Output).ToList();
        var chosen = distinct[0];
        var alternatives = distinct.Skip(1).Select(c => CaseHelper.MatchCase(source, c.Output)).ToList();
        return new Resolution(CaseHelper.MatchCase(source, chosen.Output), SegmentKind.Word, chosen.Id, alternatives);
    }

    // ------------------------------------------------------------ Rhagho'oriano -> Português

    private async Task<Resolution?> ResolveRhaghoAsync(string key, bool allowMorphology, ClauseState state, CancellationToken ct)
    {
        var entries = await _lookup.FindByRhaghoAsync(key, ct);
        if (entries.Count > 0)
        {
            var chosen = ChooseEntry(entries, state);
            UpdateState(chosen, state);
            var chosenLemma = LemmaOf(chosen);
            var alternatives = entries
                .Where(e => LemmaOf(e) != chosenLemma)
                .Select(e => e.Portuguese)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return new Resolution(chosen.Portuguese, SegmentKind.Word, chosen.Id, alternatives);
        }

        if (!allowMorphology) return null;

        // Forma flexionada não cadastrada: remove 'ta / 'vil / 'nux e procura a base (infinitivo).
        foreach (var analysis in _ling.AnalyzeRhaghoForm(key).Where(a => a.Tense != VerbTense.Present))
        {
            var baseEntries = (await _lookup.FindByRhaghoAsync(TextNormalizer.Normalize(analysis.Base), ct))
                .Where(IsInfinitiveVerb).ToList();
            if (baseEntries.Count == 0) continue;

            string Conjugated(WordEntry e)
            {
                var inf = !string.IsNullOrWhiteSpace(e.Verb?.Infinitive) ? e.Verb!.Infinitive : e.Portuguese;
                return _ling.ConjugatePortuguese(inf, analysis.Tense, state.SubjectSlot) ?? inf;
            }

            var forms = baseEntries.Select(Conjugated).Distinct().ToList();
            state.SubjectSlot = null;
            return new Resolution(forms[0], SegmentKind.Word, baseEntries[0].Id, forms.Skip(1).ToList());
        }
        return null;
    }

    /// <summary>Formas do mesmo verbo (amo, amas, amamos...) compartilham o lema e não contam como leituras alternativas.</summary>
    private static string LemmaOf(WordEntry e) =>
        TextNormalizer.Normalize(!string.IsNullOrWhiteSpace(e.Verb?.Infinitive) ? e.Verb!.Infinitive : e.Portuguese);

    private static bool IsInfinitiveVerb(WordEntry e) => e.IsVerb && (e.Verb is null || e.Verb.IsInfinitive);

    private static WordEntry ChooseEntry(IReadOnlyList<WordEntry> entries, ClauseState state)
    {
        if (entries.Count == 1) return entries[0];

        var verbs = entries.Where(e => e.Verb is not null).ToList();
        if (verbs.Count > 0)
        {
            if (state.SubjectSlot is { } slot)
            {
                var bySlot = verbs.FirstOrDefault(e => e.Verb!.PersonSlot == slot);
                if (bySlot is not null) return bySlot;
            }
            return verbs.FirstOrDefault(e => e.Verb!.IsInfinitive) ?? verbs[0];
        }
        return entries[0];
    }

    private static void UpdateState(WordEntry chosen, ClauseState state)
    {
        if (chosen.Verb is not null)
        {
            state.SubjectSlot = null; // verbos seguintes na mesma oração ficam no infinitivo ("irei caçar")
        }
        else if (TextNormalizer.Normalize(chosen.PartOfSpeech) == "pronome"
                 && SubjectSlots.TryGetValue(TextNormalizer.Normalize(chosen.Portuguese), out var slot))
        {
            state.SubjectSlot = slot;
        }
    }
}
