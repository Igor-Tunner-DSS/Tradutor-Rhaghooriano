using Rhaghoorian.Core.Abstractions;
using Rhaghoorian.Core.Errors;
using Rhaghoorian.Core.Models;
using Rhaghoorian.Core.Services;

namespace Rhaghoorian.Presentation.Services;

public interface IStartupCoordinator
{
    string Status { get; }
    bool IsReady { get; }
    event EventHandler? StatusChanged;
    /// <summary>Executa a inicialização uma única vez; chamadas seguintes aguardam a mesma tarefa.</summary>
    Task EnsureReadyAsync(CancellationToken ct = default);
    /// <summary>Sincronização manual (botão "Sincronizar").</summary>
    Task<SyncResult> SyncNowAsync(CancellationToken ct = default);
}

/// <summary>
/// Abertura do aplicativo: 1) inicia o SQLite; 2) verifica os dados locais (carga inicial se vazio); 3) verifica a conexão;
/// 4) restaura a sessão; 5) sincroniza Firestore → SQLite; 6) libera o sistema. Offline não impede o uso.
/// </summary>
public sealed class StartupCoordinator : IStartupCoordinator
{
    private readonly IDictionaryRepository _local;
    private readonly IDictionarySeeder _seeder;
    private readonly IBundledDataService _bundled;
    private readonly IConnectivityService _net;
    private readonly IAuthService _auth;
    private readonly ISyncService _sync;
    private readonly IThemeService _theme;
    private readonly IErrorHandler _errors;
    private readonly object _gate = new();
    private Task? _startup;

    public StartupCoordinator(IDictionaryRepository local, IDictionarySeeder seeder, IBundledDataService bundled,
        IConnectivityService net, IAuthService auth, ISyncService sync, IThemeService theme, IErrorHandler errors)
    {
        _local = local; _seeder = seeder; _bundled = bundled; _net = net; _auth = auth; _sync = sync; _theme = theme; _errors = errors;
    }

    public string Status { get; private set; } = "Iniciando…";
    public bool IsReady { get; private set; }
    public event EventHandler? StatusChanged;

    public Task EnsureReadyAsync(CancellationToken ct = default)
    {
        lock (_gate) _startup ??= RunAsync();
        return _startup;
    }

    private void Set(string status)
    {
        Status = status;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task RunAsync()
    {
        try
        {
            await _theme.InitializeAsync();

            Set("Abrindo o dicionário local…");
            await _seeder.SeedIfEmptyAsync(_bundled.GetInitialDictionaryText());   // inicia o SQLite e verifica os dados locais
            var count = (await _local.GetSyncStateAsync()).LocalCount;
            IsReady = true;                                                         // consulta e tradução já funcionam com o cache

            Set(_net.IsOnline ? "Conectando…" : "Offline — usando o dicionário salvo");
            if (_net.IsOnline)
            {
                try { await _auth.TryRestoreSessionAsync(); }
                catch (AppException) { /* segue como visitante */ }
            }

            Set("Sincronizando…");
            Set(Describe(await _sync.SyncAsync(), count));
        }
        catch (LocalDatabaseException ex)
        {
            Set("Banco local indisponível");
            IsReady = true;
            await _errors.HandleAsync(ex);
        }
        catch (Exception ex)
        {
            Set("Falha ao iniciar");
            IsReady = true;
            await _errors.HandleAsync(ex);
        }
    }

    public async Task<SyncResult> SyncNowAsync(CancellationToken ct = default)
    {
        await EnsureReadyAsync(ct);
        Set("Sincronizando…");
        var result = await _sync.SyncAsync(ct);
        Set(Describe(result, result.Count));
        return result;
    }

    private static string Describe(SyncResult r, int count) => r.Outcome switch
    {
        SyncOutcome.Updated => $"Sincronizado — {r.Count} registros",
        SyncOutcome.UpToDate => $"Atualizado — {r.Count} registros",
        SyncOutcome.Offline => $"Offline — {count} registros salvos",
        SyncOutcome.RemoteNotInitialized => "Dicionário online ainda não inicializado",
        _ => r.Message ?? "Falha na sincronização"
    };
}
