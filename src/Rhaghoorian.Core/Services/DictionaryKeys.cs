using Rhaghoorian.Core.Models;
using Rhaghoorian.Core.Text;

namespace Rhaghoorian.Core.Services;

/// <summary>Chaves de pesquisa de um registro — compartilhadas por SQLite e pela implementação em memória.</summary>
public static class DictionaryKeys
{
    public static string Portuguese(WordEntry e) => TextNormalizer.Normalize(e.Portuguese);
    public static string Rhagho(WordEntry e) => TextNormalizer.Normalize(e.Rhagho);

    public static IEnumerable<string> RhaghoKeysWithAlternatives(WordEntry e)
    {
        yield return Rhagho(e);
        foreach (var a in e.AlternativeForms)
        {
            var k = TextNormalizer.Normalize(a.Text);
            if (k.Length > 0) yield return k;
        }
    }

    public static int WordCount(string normalizedKey) =>
        normalizedKey.Length == 0 ? 0 : normalizedKey.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
}
