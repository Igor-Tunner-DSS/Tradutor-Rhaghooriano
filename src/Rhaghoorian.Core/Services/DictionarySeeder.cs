using Rhaghoorian.Core.Abstractions;
using Rhaghoorian.Core.Importing;

namespace Rhaghoorian.Core.Services;

public interface IDictionarySeeder
{
    /// <summary>
    /// Primeira execução: se o cache local está vazio, carrega o dicionário incluído no aplicativo (revisão 0) para que a
    /// pesquisa e a tradução funcionem mesmo offline. A primeira sincronização com o Firestore substitui esses dados.
    /// Devolve verdadeiro quando houve carga.
    /// </summary>
    Task<bool> SeedIfEmptyAsync(string bundledDictionaryText, CancellationToken ct = default);
}

public sealed class DictionarySeeder : IDictionarySeeder
{
    private readonly IDictionaryFileParser _parser;
    private readonly IDictionaryRepository _local;
    private readonly IImportIssueRepository _issues;

    public DictionarySeeder(IDictionaryFileParser parser, IDictionaryRepository local, IImportIssueRepository issues)
    {
        _parser = parser; _local = local; _issues = issues;
    }

    public async Task<bool> SeedIfEmptyAsync(string bundledDictionaryText, CancellationToken ct = default)
    {
        var state = await _local.GetSyncStateAsync(ct);
        if (state.LocalCount > 0 || string.IsNullOrWhiteSpace(bundledDictionaryText)) return false;

        var report = _parser.Parse(bundledDictionaryText);
        if (report.Entries.Count == 0) return false;

        await _local.ReplaceAllAsync(report.Entries, 0, ct);
        await _issues.ReplaceAsync(report.Issues, ct);
        return true;
    }
}
