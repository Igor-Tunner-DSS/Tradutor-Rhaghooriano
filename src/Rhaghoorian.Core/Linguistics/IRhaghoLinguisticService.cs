using Rhaghoorian.Core.Models;

namespace Rhaghoorian.Core.Linguistics;

/// <summary>Resultado de remover um sufixo verbal Rhagho'oriano conhecido.</summary>
public sealed record RhaghoVerbAnalysis(string Base, VerbTense Tense);

/// <summary>Hipótese de leitura de uma forma verbal portuguesa regular.</summary>
public sealed record PortugueseVerbCandidate(string Infinitive, VerbTense Tense, int? PersonSlot);

/// <summary>
/// Regras linguísticas determinísticas e oficiais do Rhagho'oriano (flexão verbal) mais um
/// analisador/gerador de verbos portugueses regulares. Nenhuma regra aqui depende de IA.
/// </summary>
public interface IRhaghoLinguisticService
{
    string PastSuffix { get; }
    string FutureSuffix { get; }
    string ImperativeSuffix { get; }

    /// <summary>presente = base; passado = base+'ta; futuro = base+'vil; imperativo = base+'nux.</summary>
    string Inflect(string baseForm, VerbTense tense);
    VerbConjugation Conjugate(string baseForm);

    /// <summary>Verdadeiro quando a forma não termina com nenhum sufixo flexional conhecido (pode ser usada como forma base).</summary>
    bool IsBaseForm(string form);

    /// <summary>Remove 'ta/'vil/'nux. Nulo quando a forma não possui sufixo flexional conhecido.</summary>
    RhaghoVerbAnalysis? StripInflectionSuffix(string form);

    /// <summary>Hipóteses de leitura: sufixo conhecido (se houver) e, sempre, a própria forma como presente.</summary>
    IReadOnlyList<RhaghoVerbAnalysis> AnalyzeRhaghoForm(string form);

    bool LooksLikePortugueseInfinitive(string word);
    IReadOnlyList<PortugueseVerbCandidate> AnalyzePortugueseForm(string surface);
    /// <summary>Conjuga verbo português REGULAR. Nulo quando o infinitivo não é regular/suportado.</summary>
    string? ConjugatePortuguese(string infinitive, VerbTense tense, int? personSlot);
}
