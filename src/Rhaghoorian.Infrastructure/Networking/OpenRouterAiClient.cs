using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Rhaghoorian.Core.Ai;
using Rhaghoorian.Core.Errors;
using Rhaghoorian.Core.Models;

namespace Rhaghoorian.Infrastructure.Networking;

/// <summary>
/// Chama o OpenRouter DIRETAMENTE com a chave pessoal de quem está usando o app (sem backend: Cloud Functions exigem o plano Blaze).
/// Respeita o limite diário local, faz retry controlado (408/429/5xx/rede/resposta vazia, com backoff), cancelamento e valida a resposta.
/// A chave só trafega no cabeçalho Authorization para openrouter.ai e nunca é registrada em log.
/// </summary>
public sealed class OpenRouterAiClient : IAiClient
{
    private static readonly Regex Think = new(@"<think>.*?</think>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private const int MaxOutputChars = 4000;

    private readonly HttpClient _http;
    private readonly AiOptions _options;
    private readonly IAiKeyStore _keys;
    private readonly IAiUsageLimiter _usage;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public OpenRouterAiClient(HttpClient http, AiOptions options, IAiKeyStore keys, IAiUsageLimiter usage,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _http = http; _options = options; _keys = keys; _usage = usage;
        _delay = delay ?? Task.Delay;
    }

    public async Task<string> TranslateAsync(AiTranslationRequest request, CancellationToken ct = default) =>
        AiResponseSanitizer.CleanTranslation(await CompleteAsync(AiPrompts.Translation(request), ct));

    public async Task<VerbConjugation> ConjugateAsync(AiConjugationRequest request, CancellationToken ct = default) =>
        AiResponseSanitizer.ParseConjugation(await CompleteAsync(AiPrompts.Conjugation(request), ct));

    public async Task<IReadOnlyList<string>> SuggestSynonymsAsync(AiSynonymRequest request, CancellationToken ct = default) =>
        AiResponseSanitizer.ParseSynonyms(await CompleteAsync(AiPrompts.Synonyms(request), ct));

    private async Task<string> CompleteAsync(IReadOnlyList<AiMessage> messages, CancellationToken ct)
    {
        var key = await _keys.GetAsync(ct) ?? throw new AiKeyMissingException();

        // Uma consulta lógica = 1 uso (tentativas repetidas não gastam o limite local).
        if (!await _usage.TryAcquireAsync(ct))
        {
            var u = await _usage.GetUsageAsync(ct);
            throw new AiRateLimitException($"Você já fez {u.UsedToday} consultas de IA hoje neste computador (limite {u.DailyLimit}). " +
                                           "Tente de novo amanhã — o dia vira à meia-noite UTC. A tradução local continua funcionando.");
        }

        var body = JsonSerializer.Serialize(new
        {
            model = _options.Model,
            messages = messages.Select(m => new { role = m.Role, content = m.Content }),
            temperature = 0.2,
            max_tokens = 700
        });

        AppException last = new AiUnavailableException();
        for (var attempt = 0; attempt <= _options.MaxRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_options.BaseUrl.TrimEnd('/')}/chat/completions")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            req.Headers.Add("X-Title", "Rhaghoorian");

            HttpGateway.Reply reply;
            try { reply = await HttpGateway.SendAsync(_http, req, TimeSpan.FromSeconds(_options.TimeoutSeconds), ct); }
            catch (RemoteTimeoutException ex) { last = new AiUnavailableException("A IA demorou demais para responder. Tente novamente.", ex); await BackoffAsync(attempt, ct); continue; }
            catch (OfflineException ex) { throw new AiUnavailableException("Sem conexão para usar a IA.", ex); }

            var status = (int)reply.Status;
            if (reply.IsSuccess)
            {
                var text = TryExtract(reply.Body);
                if (text is not null) return text;
                last = new InvalidAiResponseException();            // vazio/inesperado: tenta de novo
            }
            else if (status == 401 || status == 403)
            {
                throw new AiKeyInvalidException();
            }
            else if (status == 402)
            {
                throw new AiUnavailableException("A sua conta do OpenRouter está sem saldo (erro 402), mesmo para modelos gratuitos. Adicione créditos ou use outra conta.");
            }
            else if (status == 429)
            {
                last = new AiRateLimitException("O OpenRouter limitou as consultas da sua conta (modelos gratuitos: 20 por minuto e um teto diário). Aguarde um pouco e tente de novo.");
            }
            else if (status == 408 || status >= 500)
            {
                last = new AiUnavailableException();
            }
            else
            {
                throw new AiUnavailableException();
            }
            await BackoffAsync(attempt, ct);
        }
        throw last;
    }

    private Task BackoffAsync(int attempt, CancellationToken ct) =>
        attempt >= _options.MaxRetries ? Task.CompletedTask
            : _delay(TimeSpan.FromMilliseconds(Math.Min(400 * Math.Pow(2, attempt), 3000) + Random.Shared.Next(0, 150)), ct);

    /// <summary>Valida o formato OpenAI-compatível e devolve só o texto útil (ou nulo se vazio/inesperado).</summary>
    internal static string? TryExtract(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0) return null;
            if (!choices[0].TryGetProperty("message", out var message) || !message.TryGetProperty("content", out var content)) return null;

            var text = content.ValueKind switch
            {
                JsonValueKind.String => content.GetString(),
                JsonValueKind.Array => string.Concat(content.EnumerateArray().Select(p =>
                    p.ValueKind == JsonValueKind.String ? p.GetString() : p.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : "")),
                _ => null
            };
            if (text is null) return null;
            text = Think.Replace(text, string.Empty).Trim();
            if (text.Length == 0) return null;
            return text.Length > MaxOutputChars ? throw new InvalidAiResponseException() : text;
        }
        catch (JsonException) { return null; }
    }
}
