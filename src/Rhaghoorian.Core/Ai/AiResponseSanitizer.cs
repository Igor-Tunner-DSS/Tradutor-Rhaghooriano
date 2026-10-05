using System.Text.RegularExpressions;
using Rhaghoorian.Core.Errors;
using Rhaghoorian.Core.Models;

namespace Rhaghoorian.Core.Ai;

/// <summary>A saída da IA é tratada como dado não confiável: limpa, valida o formato e rejeita o que não for o modo pedido.</summary>
public static class AiResponseSanitizer
{
    private static readonly Regex Think = new(@"<think>.*?</think>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Fence = new(@"^```[a-zA-Z]*\s*|\s*```$", RegexOptions.Compiled);
    private static readonly Regex Label = new(
        @"^\s*(a\s+)?(tradu[cç][aã]o( aproximada)?( [eé])?|resposta|resultado|possivelmente significa|provavelmente significa|significa)\s*[:：\-–]\s*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Bullet = new(@"^\s*(?:[-*•]|\d+[.)])\s*", RegexOptions.Compiled);

    public static string CleanTranslation(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) throw new InvalidAiResponseException();
        var text = Think.Replace(raw, string.Empty).Trim();
        text = Fence.Replace(text, string.Empty).Trim();
        text = Label.Replace(text, string.Empty).Trim();
        text = text.Trim('"', '“', '”').Trim();

        if (text.Length == 0 || text.StartsWith('{') || text.StartsWith('[') || text.Length > 4000)
            throw new InvalidAiResponseException();
        return text;
    }

    public static IReadOnlyList<string> ParseSynonyms(string? raw, int max = 8)
    {
        if (string.IsNullOrWhiteSpace(raw)) throw new InvalidAiResponseException();
        var text = Fence.Replace(Think.Replace(raw, string.Empty).Trim(), string.Empty);
        var items = text.Split(new[] { '\n', '\r', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => Bullet.Replace(s, string.Empty).Trim().Trim('"', '“', '”', '.').Trim())
            .Where(s => s.Length is > 0 and <= 40 && s.Count(char.IsWhiteSpace) <= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .ToList();
        if (items.Count == 0) throw new InvalidAiResponseException();
        return items;
    }

    /// <summary>Aceita quatro linhas, com ou sem rótulo ("presente: x"). Qualquer outro formato é rejeitado.</summary>
    public static VerbConjugation ParseConjugation(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) throw new InvalidAiResponseException();
        var text = Fence.Replace(Think.Replace(raw, string.Empty).Trim(), string.Empty);
        var lines = text.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => Bullet.Replace(l, string.Empty).Trim())
            .Where(l => l.Length > 0).ToList();
        if (lines.Count != 4) throw new InvalidAiResponseException();

        string Value(string line)
        {
            var idx = line.IndexOf(':');
            var v = idx >= 0 ? line[(idx + 1)..] : line;
            return v.Trim().Trim('"', '“', '”');
        }
        var v4 = lines.Select(Value).ToList();
        if (v4.Any(v => v.Length == 0 || v.Contains(' '))) throw new InvalidAiResponseException();
        return new VerbConjugation(v4[0], v4[1], v4[2], v4[3]);
    }
}
