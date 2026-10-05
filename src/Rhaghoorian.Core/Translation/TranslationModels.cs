using Rhaghoorian.Core.Models;

namespace Rhaghoorian.Core.Translation;

public enum SegmentKind { Text, Word, Expression, Number, Unknown }

public sealed record WordSuggestion(string Text, string Meaning, string EntryId, double Score);

public sealed record TranslationSegment(
    string Source,
    string Output,
    SegmentKind Kind,
    string? EntryId = null,
    IReadOnlyList<string>? Alternatives = null)
{
    public bool IsAmbiguous => Alternatives is { Count: > 0 };
}

public sealed record UnknownWord(string Word, int Position, IReadOnlyList<WordSuggestion> Suggestions);

public sealed class TranslationResult
{
    public TranslationDirection Direction { get; init; }
    public string SourceText { get; init; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public IReadOnlyList<TranslationSegment> Segments { get; init; } = Array.Empty<TranslationSegment>();
    public IReadOnlyList<UnknownWord> UnknownWords { get; init; } = Array.Empty<UnknownWord>();
    public bool UsedAi { get; set; }

    public bool HasUnknownWords => UnknownWords.Count > 0;
    /// <summary>
    /// Ambiguidade real só existe em Português → Rhagho'oriano (ex.: "amamos" = presente ou passado). Em Rhagho'oriano → Português
    /// as outras leituras são apenas exibidas como dica: a língua tem formas únicas por design e isso não justifica chamar a IA.
    /// </summary>
    public bool HasAmbiguities => Direction == TranslationDirection.PortugueseToRhagho && Segments.Any(s => s.IsAmbiguous);
    /// <summary>A IA só é oferecida quando o motor local deixou lacunas ou escolhas ambíguas.</summary>
    public bool CanBenefitFromAi => !UsedAi && (HasUnknownWords || HasAmbiguities);
}
