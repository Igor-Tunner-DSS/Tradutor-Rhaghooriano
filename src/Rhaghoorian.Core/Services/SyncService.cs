using Rhaghoorian.Core.Abstractions;
using Rhaghoorian.Core.Errors;
using Rhaghoorian.Core.Models;

namespace Rhaghoorian.Core.Services;

/// <summary>
/// Firestore -> SQLite. Lê apenas o documento de revisão (1 leitura); só baixa o dicionário inteiro quando a revisão remota
/// difere da local. Se o servidor ainda não foi inicializado, o cache local NUNCA é apagado.
/// </summary>
public sealed class SyncService : ISyncService
{
    private readonly IDictionaryRepository _local;
    private readonly IRemoteDictionaryStore _remote;
    private readonly IConnectivityService _connectivity;

    public SyncService(IDictionaryRepository local, IRemoteDictionaryStore remote, IConnectivityService connectivity)
    {
        _local = local; _remote = remote; _connectivity = connectivity;
    }

    public async Task<SyncResult> SyncAsync(CancellationToken ct = default)
    {
        var state = await _local.GetSyncStateAsync(ct);
        if (!_connectivity.IsOnline)
            return new SyncResult(SyncOutcome.Offline, state.LocalCount, "Sem conexão: usando o dicionário salvo neste computador.");

        try
        {
            var remoteRevision = await _remote.GetRevisionAsync(ct);
            if (remoteRevision is null)
                return new SyncResult(SyncOutcome.RemoteNotInitialized, state.LocalCount,
                    "O dicionário online ainda não foi inicializado. Entre com uma conta e use Modificação > Importar.");

            if (state.Revision == remoteRevision && state.LocalCount > 0)
                return new SyncResult(SyncOutcome.UpToDate, state.LocalCount);

            var entries = await _remote.ListAllAsync(ct);
            await _local.ReplaceAllAsync(entries, remoteRevision.Value, ct);
            return new SyncResult(SyncOutcome.Updated, entries.Count);
        }
        catch (OperationCanceledException) { throw; }
        catch (AppException ex) when (ex is OfflineException or RemoteTimeoutException or RemoteStoreException)
        {
            return new SyncResult(SyncOutcome.Failed, state.LocalCount, ex.Message);
        }
    }
}
