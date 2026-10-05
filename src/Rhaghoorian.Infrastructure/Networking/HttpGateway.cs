using System.Net;
using System.Text.Json;
using Rhaghoorian.Core.Errors;

namespace Rhaghoorian.Infrastructure.Networking;

/// <summary>Envia requisições e traduz falhas de rede/HTTP em exceções de domínio com mensagens claras.</summary>
public static class HttpGateway
{
    public sealed record Reply(HttpStatusCode Status, string Body)
    {
        public bool IsSuccess => (int)Status is >= 200 and < 300;
        public string? ErrorStatus => TryReadErrorStatus(Body);
        public string? ErrorMessage => TryReadErrorMessage(Body);
    }

    public static async Task<Reply> SendAsync(HttpClient client, HttpRequestMessage request, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            using var response = await client.SendAsync(request, cts.Token);
            var body = await response.Content.ReadAsStringAsync(cts.Token);
            return new Reply(response.StatusCode, body);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new RemoteTimeoutException();
        }
        catch (HttpRequestException ex)
        {
            throw new OfflineException("Não foi possível se conectar ao servidor. Verifique sua internet.", ex);
        }
    }

    private static string? TryReadErrorStatus(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0) root = root[0];
            return root.TryGetProperty("error", out var e) && e.TryGetProperty("status", out var s) ? s.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    private static string? TryReadErrorMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0) root = root[0];
            return root.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var m) ? m.GetString() : null;
        }
        catch (JsonException) { return null; }
    }
}
