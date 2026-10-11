using System.Net.Http.Headers;
using System.Text.Json;
using Rhaghoorian.Core.Abstractions;
using Rhaghoorian.Core.Ai;
using Rhaghoorian.Core.Errors;

namespace Rhaghoorian.Infrastructure.Networking;

/// <summary>Guarda a chave pessoal no armazenamento protegido do Windows (DPAPI), nunca em arquivos de configuração nem no Firebase.</summary>
public sealed class SecureAiKeyStore : IAiKeyStore
{
    private const string StoreKey = "openrouter.apiKey";
    private readonly ISecureTokenStore _store;
    public SecureAiKeyStore(ISecureTokenStore store) => _store = store;

    public Task<string?> GetAsync(CancellationToken ct = default) => _store.GetAsync(StoreKey);
    public Task SetAsync(string key, CancellationToken ct = default) => _store.SetAsync(StoreKey, key);
    public Task ClearAsync(CancellationToken ct = default) { _store.Remove(StoreKey); return Task.CompletedTask; }
}

public sealed class OpenRouterKeyManager : IAiKeyManager
{
    private readonly HttpClient _http;
    private readonly AiOptions _options;
    private readonly IAiKeyStore _store;

    public OpenRouterKeyManager(HttpClient http, AiOptions options, IAiKeyStore store)
    {
        _http = http; _options = options; _store = store;
    }

    public async Task<AiKeyStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var key = await _store.GetAsync(ct);
        return key is null ? new AiKeyStatus(false, null) : new AiKeyStatus(true, AiKeyFormat.Mask(key));
    }

    /// <summary>
    /// Valida o formato e confere a chave com o OpenRouter (GET /key, que não gasta consulta nem crédito). Chave recusada (401)
    /// não é salva. Sem conexão a chave é salva sem confirmação — o erro, se houver, aparece no primeiro uso.
    /// </summary>
    public async Task<AiKeySaveResult> SaveAsync(string rawKey, CancellationToken ct = default)
    {
        var key = AiKeyFormat.Normalize(rawKey);

        bool verified = false;
        bool? freeTier = null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{_options.BaseUrl.TrimEnd('/')}/key");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            var reply = await HttpGateway.SendAsync(_http, req, TimeSpan.FromSeconds(15), ct);

            if ((int)reply.Status is 401 or 403) throw new AiKeyInvalidException();
            if (reply.IsSuccess)
            {
                verified = true;
                freeTier = ReadFreeTier(reply.Body);
            }
        }
        catch (Exception ex) when (ex is OfflineException or RemoteTimeoutException) { /* salva sem confirmar */ }

        await _store.SetAsync(key, ct);
        return new AiKeySaveResult(verified, freeTier);
    }

    public Task RemoveAsync(CancellationToken ct = default) => _store.ClearAsync(ct);

    private static bool? ReadFreeTier(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement.TryGetProperty("data", out var data) ? data : doc.RootElement;
            return root.TryGetProperty("is_free_tier", out var f) && f.ValueKind is JsonValueKind.True or JsonValueKind.False ? f.GetBoolean() : null;
        }
        catch (JsonException) { return null; }
    }
}
