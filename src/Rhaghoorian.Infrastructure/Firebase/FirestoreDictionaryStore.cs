using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rhaghoorian.Core.Abstractions;
using Rhaghoorian.Core.Errors;
using Rhaghoorian.Core.Models;
using Rhaghoorian.Infrastructure.Networking;

namespace Rhaghoorian.Infrastructure.Firebase;

/// <summary>
/// Cloud Firestore via API REST (não existe SDK oficial do Firestore para .NET MAUI). Toda escrita é um
/// <c>commit</c> atômico que também incrementa meta/dictionary.revision; a revisão permite sincronizar com 1 leitura.
/// Concorrência otimista: update/delete usam a pré-condição currentDocument.updateTime.
/// </summary>
public sealed class FirestoreDictionaryStore : IRemoteDictionaryStore
{
    private const int CommitChunk = 400;
    private readonly HttpClient _http;
    private readonly FirebaseOptions _options;
    private readonly IAuthService _auth;

    public FirestoreDictionaryStore(HttpClient http, FirebaseOptions options, IAuthService auth)
    {
        _http = http;
        _options = options;
        _auth = auth;
    }

    private string BaseUrl => (_options.FirestoreBaseUrl ?? "https://firestore.googleapis.com").TrimEnd('/') + "/v1";
    private string DbPath => $"projects/{_options.ProjectId}/databases/(default)";
    private string DocumentsUrl => $"{BaseUrl}/{DbPath}/documents";
    private string DocName(string id) => $"{DbPath}/documents/{_options.DictionaryCollection}/{Uri.EscapeDataString(id)}";
    private string MetaName => $"{DbPath}/documents/{_options.MetaCollection}/{_options.MetaDocument}";
    private TimeSpan Timeout => TimeSpan.FromSeconds(_options.TimeoutSeconds);

    // ------------------------------------------------------------------ leitura (pública)

    public async Task<long?> GetRevisionAsync(CancellationToken ct = default)
    {
        var reply = await SendAsync(HttpMethod.Get, $"{DocumentsUrl}/{_options.MetaCollection}/{_options.MetaDocument}", null, requireAuth: false, ct);
        if (reply.Status == HttpStatusCode.NotFound) return null;
        EnsureOk(reply);
        using var doc = JsonDocument.Parse(reply.Body);
        if (doc.RootElement.TryGetProperty("fields", out var f) && f.TryGetProperty("revision", out var r) &&
            r.TryGetProperty("integerValue", out var n) && long.TryParse(n.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var rev))
            return rev;
        return 0;
    }

    public async Task<IReadOnlyList<WordEntry>> ListAllAsync(CancellationToken ct = default)
    {
        var result = new List<WordEntry>();
        string? pageToken = null;
        do
        {
            var url = $"{DocumentsUrl}/{_options.DictionaryCollection}?pageSize=300" + (pageToken is null ? "" : "&pageToken=" + Uri.EscapeDataString(pageToken));
            var reply = await SendAsync(HttpMethod.Get, url, null, requireAuth: false, ct);
            EnsureOk(reply);
            using var doc = JsonDocument.Parse(reply.Body);
            if (doc.RootElement.TryGetProperty("documents", out var docs))
                foreach (var d in docs.EnumerateArray()) result.Add(FirestoreMapper.FromDocument(d));
            pageToken = doc.RootElement.TryGetProperty("nextPageToken", out var t) ? t.GetString() : null;
        } while (!string.IsNullOrEmpty(pageToken));
        return result;
    }

    public async Task<WordEntry?> GetAsync(string id, CancellationToken ct = default)
    {
        var reply = await SendAsync(HttpMethod.Get, $"{DocumentsUrl}/{_options.DictionaryCollection}/{Uri.EscapeDataString(id)}", null, requireAuth: false, ct);
        if (reply.Status == HttpStatusCode.NotFound) return null;
        EnsureOk(reply);
        using var doc = JsonDocument.Parse(reply.Body);
        return FirestoreMapper.FromDocument(doc.RootElement);
    }

    // ------------------------------------------------------------------ escrita (autenticada)

    public async Task<WordEntry> CreateAsync(WordEntry entry, CancellationToken ct = default)
    {
        var write = UpdateWrite(entry);
        write["currentDocument"] = new JsonObject { ["exists"] = false };
        var times = await CommitAsync(new[] { write }, ct);
        var saved = entry.Clone();
        saved.RemoteUpdateTime = times[0];
        return saved;
    }

    public async Task<WordEntry> UpdateAsync(WordEntry entry, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(entry.RemoteUpdateTime))
            throw new ConflictException("Não foi possível confirmar a versão deste registro. Recarregue-o e tente de novo.");
        var write = UpdateWrite(entry);
        write["currentDocument"] = new JsonObject { ["updateTime"] = entry.RemoteUpdateTime };
        var times = await CommitAsync(new[] { write }, ct);
        var saved = entry.Clone();
        saved.RemoteUpdateTime = times[0];
        return saved;
    }

    public async Task DeleteAsync(string id, string? expectedUpdateTime, CancellationToken ct = default)
    {
        var write = new JsonObject { ["delete"] = DocName(id) };
        if (!string.IsNullOrEmpty(expectedUpdateTime)) write["currentDocument"] = new JsonObject { ["updateTime"] = expectedUpdateTime };
        await CommitAsync(new[] { write }, ct);
    }

    public async Task<int> CreateMissingAsync(IReadOnlyCollection<WordEntry> entries, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var existing = await ListExistingIdsAsync(ct);
        var missing = entries.Where(e => !existing.Contains(e.Id)).ToList();
        var created = 0;
        foreach (var chunk in missing.Chunk(CommitChunk))
        {
            var writes = chunk.Select(e =>
            {
                var w = UpdateWrite(e);
                w["currentDocument"] = new JsonObject { ["exists"] = false };
                return w;
            }).ToList();
            await CommitAsync(writes, ct);
            created += chunk.Length;
            progress?.Report(created);
        }
        return created;
    }

    private async Task<HashSet<string>> ListExistingIdsAsync(CancellationToken ct)
    {
        var ids = new HashSet<string>();
        string? pageToken = null;
        do
        {
            var url = $"{DocumentsUrl}/{_options.DictionaryCollection}?pageSize=300&mask.fieldPaths=version" +
                      (pageToken is null ? "" : "&pageToken=" + Uri.EscapeDataString(pageToken));
            var reply = await SendAsync(HttpMethod.Get, url, null, requireAuth: false, ct);
            EnsureOk(reply);
            using var doc = JsonDocument.Parse(reply.Body);
            if (doc.RootElement.TryGetProperty("documents", out var docs))
                foreach (var d in docs.EnumerateArray()) ids.Add(FirestoreMapper.IdFromName(d.GetProperty("name").GetString() ?? string.Empty));
            pageToken = doc.RootElement.TryGetProperty("nextPageToken", out var t) ? t.GetString() : null;
        } while (!string.IsNullOrEmpty(pageToken));
        return ids;
    }

    private JsonObject UpdateWrite(WordEntry e) => new()
    {
        ["update"] = new JsonObject { ["name"] = DocName(e.Id), ["fields"] = FirestoreMapper.ToFields(e) }
    };

    /// <summary>Commit atômico: escritas + incremento da revisão. Devolve o updateTime de cada escrita (na ordem).</summary>
    private async Task<IReadOnlyList<string>> CommitAsync(IReadOnlyList<JsonObject> writes, CancellationToken ct)
    {
        var all = new JsonArray();
        foreach (var w in writes) all.Add(w);
        all.Add(new JsonObject
        {
            ["update"] = new JsonObject { ["name"] = MetaName, ["fields"] = new JsonObject() },
            ["updateMask"] = new JsonObject { ["fieldPaths"] = new JsonArray() },
            ["updateTransforms"] = new JsonArray(new JsonObject
            {
                ["fieldPath"] = "revision",
                ["increment"] = new JsonObject { ["integerValue"] = "1" }
            })
        });

        var body = new JsonObject { ["writes"] = all }.ToJsonString();
        var reply = await SendAsync(HttpMethod.Post, $"{BaseUrl}/{DbPath}/documents:commit", body, requireAuth: true, ct);
        EnsureOk(reply);

        using var doc = JsonDocument.Parse(reply.Body);
        var times = new List<string>();
        if (doc.RootElement.TryGetProperty("writeResults", out var results))
            foreach (var r in results.EnumerateArray()) times.Add(r.TryGetProperty("updateTime", out var u) ? u.GetString() ?? "" : "");
        if (times.Count < writes.Count) throw new RemoteStoreException();
        return times;
    }

    // ------------------------------------------------------------------ HTTP

    private async Task<HttpGateway.Reply> SendAsync(HttpMethod method, string url, string? json, bool requireAuth, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        if (requireAuth)
        {
            var token = await _auth.GetIdTokenAsync(ct) ?? throw new NotAuthenticatedException();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return await HttpGateway.SendAsync(_http, request, Timeout, ct);
    }

    private static void EnsureOk(HttpGateway.Reply reply)
    {
        if (reply.IsSuccess) return;
        var status = reply.ErrorStatus;
        throw (int)reply.Status switch
        {
            401 => new NotAuthenticatedException("Sua sessão expirou. Entre novamente."),
            403 => new NotAuthenticatedException("Você não tem permissão para esta operação. Entre com uma conta autorizada."),
            404 => new ConflictException("Este registro foi excluído por outra pessoa. Atualize a lista."),
            409 or 400 when status is "ABORTED" or "ALREADY_EXISTS" or "FAILED_PRECONDITION" => new ConflictException(),
            409 => new ConflictException(),
            429 => new RemoteStoreException("O servidor está recebendo requisições demais. Aguarde um instante e tente de novo."),
            _ => new RemoteStoreException()
        };
    }
}
