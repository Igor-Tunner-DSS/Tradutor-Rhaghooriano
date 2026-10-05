using Rhaghoorian.Core.Abstractions;
using Rhaghoorian.Core.Linguistics;
using Rhaghoorian.Core.Models;
using Rhaghoorian.Core.Text;
using Rhaghoorian.Core.Translation;

namespace Rhaghoorian.Core.Ai;

public sealed class AiContextBuilder
{
    private const int MaxEntries = 80;
    private const int MaxPerWord = 6;

    private readonly IDictionaryLookup _lookup;
    private readonly IRhaghoLinguisticService _ling;

    public AiContextBuilder(IDictionaryLookup lookup, IRhaghoLinguisticService ling)
    {
        _lookup = lookup; _ling = ling;
    }

    public async Task<AiTranslationRequest> BuildTranslationAsync(TranslationResult local, CancellationToken ct = default)
    {
        var direction = local.Direction;
        var entries = new List<AiContextEntry>();
        var seen = new HashSet<string>();

        async Task AddAsync(IReadOnlyList<WordEntry> found, string? tense = null)
        {
            foreach (var e in found.Take(MaxPerWord))
            {
                if (entries.Count >= MaxEntries) return;
                if (seen.Add(e.Id)) entries.Add(new AiContextEntry(e.Rhagho, e.Portuguese, e.PartOfSpeech, tense ?? e.Verb?.Tense.ToString()));
            }
            await Task.CompletedTask;
        }

        foreach (var word in Tokenizer.WordsOf(local.SourceText).Distinct())
        {
            ct.ThrowIfCancellationRequested();
            if (direction == TranslationDirection.PortugueseToRhagho)
            {
                await AddAsync(await _lookup.FindByPortugueseAsync(word, ct));
                foreach (var c in _ling.AnalyzePortugueseForm(word))
                    await AddAsync((await _lookup.FindByPortugueseAsync(c.Infinitive, ct)).Where(e => e.IsVerb).ToList(), c.Tense.ToString());
            }
            else
            {
                await AddAsync(await _lookup.FindByRhaghoAsync(word, ct));
                if (_ling.StripInflectionSuffix(word) is { } s)
                    await AddAsync((await _lookup.FindByRhaghoAsync(TextNormalizer.Normalize(s.Base), ct)).Where(e => e.IsVerb).ToList(), s.Tense.ToString());
            }
        }

        var unknown = local.UnknownWords.Select(u => u.Word).ToList();
        return new AiTranslationRequest(local.SourceText, direction, new AiContext(entries, unknown), local.Text);
    }
}
