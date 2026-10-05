using Rhaghoorian.Core.Abstractions;
using Rhaghoorian.Core.Errors;
using Rhaghoorian.Core.Importing;

namespace Rhaghoorian.Core.Services;

public interface IDictionaryImportService
{
    /// <summary>Lê o arquivo, gera o relatório de validação e o guarda localmente para revisão posterior.</summary>
    Task<ImportReport> AnalyzeAsync(string fileContent, CancellationToken ct = default);
    /// <summary>Envia ao Firestore somente os registros que ainda não existem (nunca sobrescreve edições). Exige login.</summary>
    Task<int> PublishAsync(ImportReport report, IProgress<int>? progress = null, CancellationToken ct = default);
    /// <summary>Problemas da última análise, guardados localmente para revisão posterior.</summary>
    Task<IReadOnlyList<ImportIssueRecord>> GetStoredIssuesAsync(string? severity = null, CancellationToken ct = default);
}

public sealed class DictionaryImportService : IDictionaryImportService
{
    private readonly IDictionaryFileParser _parser;
    private readonly IImportIssueRepository _issues;
    private readonly IRemoteDictionaryStore _remote;
    private readonly IAuthService _auth;
    private readonly IConnectivityService _connectivity;

    public DictionaryImportService(IDictionaryFileParser parser, IImportIssueRepository issues,
        IRemoteDictionaryStore remote, IAuthService auth, IConnectivityService connectivity)
    {
        _parser = parser; _issues = issues; _remote = remote; _auth = auth; _connectivity = connectivity;
    }

    public async Task<ImportReport> AnalyzeAsync(string fileContent, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(fileContent)) throw new InvalidInputException("O arquivo está vazio.");
        var report = _parser.Parse(fileContent);
        await _issues.ReplaceAsync(report.Issues, ct);
        return report;
    }

    public Task<IReadOnlyList<ImportIssueRecord>> GetStoredIssuesAsync(string? severity = null, CancellationToken ct = default) =>
        _issues.ListAsync(severity, ct);

    public async Task<int> PublishAsync(ImportReport report, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        if (!_auth.IsAuthenticated) throw new NotAuthenticatedException();
        if (!_connectivity.IsOnline) throw new OfflineException();
        if (report.Entries.Count == 0) throw new InvalidInputException("Não há entradas para importar.");

        var user = _auth.UserId;
        var now = DateTimeOffset.UtcNow;
        foreach (var e in report.Entries)
        {
            e.CreatedAt = e.UpdatedAt = now;
            e.CreatedBy = e.UpdatedBy = user;
        }
        return await _remote.CreateMissingAsync(report.Entries, progress, ct);
    }
}
