using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Rhaghoorian.Core.Abstractions;
using Rhaghoorian.Core.Importing;
using Rhaghoorian.Core.Linguistics;
using Rhaghoorian.Core.Models;
using Rhaghoorian.Infrastructure.SQLite;

namespace Rhaghoorian.Infrastructure.Tests;

public sealed record Sent(HttpMethod Method, string Url, string Body, string? Authorization);

/// <summary>HttpClient falso: grava as requisições e responde com o que o teste definir.</summary>
public sealed class FakeHandler : HttpMessageHandler
{
    public List<Sent> Requests { get; } = new();
    public Func<Sent, (HttpStatusCode Status, string Body)> Responder { get; set; } = _ => (HttpStatusCode.OK, "{}");
    public Exception? Throw { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        var sent = new Sent(request.Method, request.RequestUri!.ToString(), body, request.Headers.Authorization?.ToString());
        Requests.Add(sent);
        if (Throw is not null) throw Throw;
        var (status, text) = Responder(sent);
        return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    }
}

public sealed class StubAuth : IAuthService
{
    public bool IsAuthenticated { get; set; } = true;
    public string? UserId { get; set; } = "uid-1";
    public string? Email { get; set; } = "a@b.c";
    public event EventHandler? AuthStateChanged { add { } remove { } }
    public Task<bool> TryRestoreSessionAsync(CancellationToken ct = default) => Task.FromResult(IsAuthenticated);
    public Task SignInAsync(string email, string password, CancellationToken ct = default) => Task.CompletedTask;
    public Task SignOutAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task<string?> GetIdTokenAsync(CancellationToken ct = default) => Task.FromResult<string?>(IsAuthenticated ? "id-token-123" : null);
}

public sealed class MemoryTokenStore : ISecureTokenStore
{
    public Dictionary<string, string> Data { get; } = new();
    public Task<string?> GetAsync(string key) => Task.FromResult(Data.TryGetValue(key, out var v) ? v : null);
    public Task SetAsync(string key, string value) { Data[key] = value; return Task.CompletedTask; }
    public void Remove(string key) => Data.Remove(key);
}

public sealed class TempDb : IDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "rhagho-tests-" + Guid.NewGuid().ToString("N"));
    public string FilePath => Path.Combine(Dir, "rhagho.db");
    public TempDb() => Directory.CreateDirectory(Dir);
    public SqliteDatabase Open() => new(FilePath, NullLogger<SqliteDatabase>.Instance);
    public void Dispose() { try { Directory.Delete(Dir, true); } catch { } }
}

public static class Data
{
    public static readonly IRhaghoLinguisticService Ling = new RhaghoLinguisticService();
    public static ImportReport RealDictionary() =>
        new DictionaryFileParser(Ling).Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "dicionario_rhagho_oriano_atual.txt")));
}
