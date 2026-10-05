using System.Text.RegularExpressions;

namespace Rhaghoorian.Core.Text;

public enum TokenKind { Word, Number, Punctuation, Whitespace, NewLine }

public sealed record Token(TokenKind Kind, string Text, string Normalized, int Position)
{
    public bool IsWordLike => Kind is TokenKind.Word or TokenKind.Number;
}

/// <summary>
/// Tokenizador que preserva pontuação, aspas, espaços e quebras de linha.
/// Apóstrofos e hífens entre letras fazem parte da palavra (ex.: ra'ri, ido's, mor'hk'ta).
/// </summary>
public static class Tokenizer
{
    private static readonly Regex Pattern = new(
        @"(?<nl>\r\n|\r|\n)" +
        @"|(?<ws>[^\S\r\n]+)" +
        @"|(?<num>\d+(?:[.,]\d+)*)" +
        @"|(?<word>[\p{L}\p{M}][\p{L}\p{M}\p{N}]*(?:['\u2019\u2018\u02BC`\u00B4\-][\p{L}\p{M}\p{N}]+)*)" +
        @"|(?<other>.)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static IReadOnlyList<Token> Tokenize(string? text)
    {
        var list = new List<Token>();
        if (string.IsNullOrEmpty(text)) return list;

        foreach (Match m in Pattern.Matches(text))
        {
            var kind =
                m.Groups["nl"].Success ? TokenKind.NewLine :
                m.Groups["ws"].Success ? TokenKind.Whitespace :
                m.Groups["num"].Success ? TokenKind.Number :
                m.Groups["word"].Success ? TokenKind.Word :
                TokenKind.Punctuation;

            var norm = kind is TokenKind.Word or TokenKind.Number ? TextNormalizer.Normalize(m.Value) : m.Value;
            list.Add(new Token(kind, m.Value, norm, m.Index));
        }
        return list;
    }

    /// <summary>Tokeniza e devolve apenas as formas normalizadas das palavras/números.</summary>
    public static IReadOnlyList<string> WordsOf(string? text) =>
        Tokenize(text).Where(t => t.IsWordLike).Select(t => t.Normalized).ToList();
}
