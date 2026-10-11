using Rhaghoorian.Core.Abstractions;
using Rhaghoorian.Core.Errors;
using Rhaghoorian.Core.Models;

namespace Rhaghoorian.Core.Services;

/// <summary>
/// Firestore → SQLite pensada para o plano gratuito (Spark: 50.000 leituras/dia).
/// 1) Lê só o documento de revisão (1 leitura). Igual ao local → nada mais é lido.
/// 2) Diferente → sincronização INCREMENTAL: só os documentos alterados desde o cursor (+ exclusões).
/// 3) Carga completa apenas na primeira vez, com cache vazio, ou como rede de segurança a cada <see cref="FullSyncInterval"/>.
/// Se o servidor ainda não foi inicializado, o cache local NUNCA é apagado.
/// </summary>
public sealed class SyncService : ISyncService
{
    public static readonly TimeSpan FullSyncInterval = TimeSpan.FromDays(7);
    /// <summary>Margem para não perder commits concorrentes cujo carimbo foi atribuído um pouco antes de ficarem visíveis (reaplicar é idempotente; custa só uma pequena releitura).</summary>
    public static readonly TimeSpan CursorOverlap = TimeSpan.FromSeconds(15);

    private readonly IDictionaryRepository _local;
    private readonly IRemoteDictionaryStore _remote;
    private readonly IConnectivityService _connectivity;
    private readonly Func<DateTimeOffset> _now;

    public SyncService(IDictionaryRepository local, IRemoteDictionaryStore remote, IConnectivityService connectivity, Func<DateTimeOffset>? now = null)
    {
        _local = local; _remote = remote; _connectivity = connectivity; _now = now ?? (() => DateTimeOffset.UtcNow);
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

            var full = state.Cursor is null || state.LocalCount == 0
                       || state.LastFullSyncAt is null || _now() - state.LastFullSyncAt.Value > FullSyncInterval;

            var since = full ? (DateTimeOffset?)null : state.Cursor!.Value - CursorOverlap;
            var changes = await _remote.ListChangesAsync(since, ct);
            await _local.ApplyChangesAsync(changes, remoteRevision.Value, ct);

            var total = (await _local.GetSyncStateAsync(ct)).LocalCount;
            return new SyncResult(SyncOutcome.Updated, total);
        }
        catch (OperationCanceledException) { throw; }
        catch (AppException ex) when (ex is OfflineException or RemoteTimeoutException or RemoteStoreException or RemoteQuotaExceededException)
        {
            return new SyncResult(SyncOutcome.Failed, state.LocalCount, ex.Message);
        }
    }
}
