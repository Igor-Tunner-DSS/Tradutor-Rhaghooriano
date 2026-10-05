using Rhaghoorian.Core.Models;

namespace Rhaghoorian.Core.Importing;

public enum IssueSeverity { Info = 0, Warning = 1, Error = 2 }

public sealed record ImportIssue(IssueSeverity Severity, string Code, int? Line, string? Key, string Message);

public sealed class ImportReport
{
    public List<WordEntry> Entries { get; } = new();
    public List<ImportIssue> Issues { get; } = new();
    public int TotalLines { get; set; }

    public IReadOnlyDictionary<string, int> EntriesByClass =>
        Entries.GroupBy(e => e.PartOfSpeech).ToDictionary(g => g.Key, g => g.Count());

    public IReadOnlyDictionary<string, int> IssuesByCode =>
        Issues.GroupBy(i => i.Code).OrderBy(g => g.Key).ToDictionary(g => g.Key, g => g.Count());

    public int Count(IssueSeverity severity) => Issues.Count(i => i.Severity == severity);
}
