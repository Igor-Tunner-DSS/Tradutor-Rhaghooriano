using System.Globalization;
using System.Text;

namespace Rhaghoorian.Core.Text;

/// <summary>
/// Normalização determinística usada em pesquisa, tradução, sugestões e comparação.
/// Nunca altera o valor exibido ao usuário: o resultado serve apenas como chave de comparação.
/// Regras: minúsculas, remoção de diacríticos, apóstrofos tipográficos -> ', aspas tipográficas -> ",
/// espaços colapsados e aparados.
/// </summary>
public static class TextNormalizer
{
    public static bool IsApostropheLike(char c) =>
        c is '\'' or '\u2019' or '\u2018' or '\u02BC' or '`' or '\u00B4' or '\u2032';

    public static bool IsDoubleQuoteLike(char c) =>
        c is '"' or '\u201C' or '\u201D' or '\u201E' or '\u00AB' or '\u00BB';

    public static string Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        var decomposed = input.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        var pendingSpace = false;

        foreach (var raw in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(raw) == UnicodeCategory.NonSpacingMark) continue;

            var c = raw;
            if (IsApostropheLike(c)) c = '\'';
            else if (IsDoubleQuoteLike(c)) c = '"';

            if (char.IsWhiteSpace(c))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }

            if (pendingSpace) { sb.Append(' '); pendingSpace = false; }
            sb.Append(char.ToLowerInvariant(c));
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>Quantidade de letras (ignora acentuação, apóstrofos, espaços e pontuação).</summary>
    public static int CountLetters(string? input)
    {
        var n = Normalize(input);
        var count = 0;
        foreach (var c in n) if (char.IsLetter(c)) count++;
        return count;
    }

    /// <summary>Converte apóstrofos tipográficos em ' sem tocar no restante (usado só em comparações de sufixo).</summary>
    public static string UnifyApostrophes(string input)
    {
        var sb = new StringBuilder(input.Length);
        foreach (var c in input) sb.Append(IsApostropheLike(c) ? '\'' : c);
        return sb.ToString();
    }

    public static bool HasDiacritics(string input)
    {
        foreach (var c in input.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) return true;
        return false;
    }
}
