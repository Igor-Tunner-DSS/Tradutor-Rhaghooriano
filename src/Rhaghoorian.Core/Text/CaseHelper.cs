namespace Rhaghoorian.Core.Text;

public static class CaseHelper
{
    /// <summary>Aplica ao <paramref name="target"/> o padrão de caixa observado em <paramref name="source"/>.</summary>
    public static string MatchCase(string source, string target)
    {
        if (string.IsNullOrEmpty(target)) return target;
        var letters = source.Where(char.IsLetter).ToArray();
        if (letters.Length == 0) return LowerIfOnlyInitialCapital(target);

        if (letters.Length > 1 && letters.All(char.IsUpper)) return target.ToUpperInvariant();
        if (char.IsUpper(letters[0]) && letters.Skip(1).All(char.IsLower)) return Capitalize(target);
        if (letters.All(char.IsLower)) return LowerIfOnlyInitialCapital(target);
        return target;
    }

    public static string Capitalize(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            if (!char.IsLetter(s[i])) continue;
            return s[..i] + char.ToUpperInvariant(s[i]) + s[(i + 1)..];
        }
        return s;
    }

    /// <summary>Palavras cadastradas como "Ton" (só a inicial maiúscula) são exibidas em minúsculas quando a origem não tem letras de caixa.</summary>
    private static string LowerIfOnlyInitialCapital(string s)
    {
        var letters = s.Where(char.IsLetter).ToArray();
        if (letters.Length > 1 && char.IsUpper(letters[0]) && letters.Skip(1).All(char.IsLower)) return s.ToLowerInvariant();
        return s;
    }

    /// <summary>Maiúscula no início do texto e após ., ! ou ? seguidos de espaço.</summary>
    public static string SentenceCase(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var chars = text.ToCharArray();
        var capitalizeNext = true;
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (capitalizeNext && char.IsLetter(c))
            {
                chars[i] = char.ToUpperInvariant(c);
                capitalizeNext = false;
            }
            else if (char.IsLetterOrDigit(c))
            {
                capitalizeNext = false;
            }
            else if (c is '.' or '!' or '?' or '\n' or '\r')
            {
                capitalizeNext = true;
            }
        }
        return new string(chars);
    }
}
