using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Rhaghoorian.Core.Abstractions;
using Rhaghoorian.Core.Ai;
using Rhaghoorian.Core.Errors;
using Rhaghoorian.Core.Models;

namespace Rhaghoorian.Infrastructure.Networking;

/// <summary>
/// Cliente da camada de IA (Firebase Cloud Function). O aplicativo nunca conhece a chave do OpenRouter: ele só envia o contexto
/// compacto ao backend e recebe de volta um texto simples, que é sanitizado e validado antes de qualquer uso.
/// </summary>
public sealed class BackendAiClient : IAiClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private readonly HttpClient _http;
    private readonly AiOptions _options;
    private readonly IAuthService _auth;

    public BackendAiClient(HttpClient http, AiOptions options, IAuthService auth)
    {
        _http = http; _options = options; _auth = auth;
    }

    public async Task<string> TranslateAsync(AiTranslationRequest request, CancellationToken ct = default)
    {
        var payload = new
        {
            text = request.Text,
            direction = request.Direction == TranslationDirection.PortugueseToRhagho ? "pt-rh" : "rh-pt",
            context = new
            {
                entries = request.Context.Entries.Select(e => new { rhagho = e.Rhagho, portuguese = e.Portuguese, partOfSpeech = e.PartOfSpeech, tense = e.Tense }),
                unknownWords = request.Context.UnknownWords
            },
            localDraft = request.LocalDraft
        };
        return AiResponseSanitizer.CleanTranslation(await PostAsync("translate", payload, ct));
    }

    public async Task<VerbConjugation> ConjugateAsync(AiConjugationRequest request, CancellationToken ct = default) =>
        AiResponseSanitizer.ParseConjugation(await PostAsync("conjugate", new { infinitive = request.Infinitive, baseForm = request.BaseForm }, ct));

    public async Task<IReadOnlyList<string>> SuggestSynonymsAsync(AiSynonymRequest request, CancellationToken ct = default) =>
        AiResponseSanitizer.ParseSynonyms(await PostAsync("synonyms",
            new { portuguese = request.Portuguese, rhagho = request.Rhagho, partOfSpeech = request.PartOfSpeech, existing = request.Existing }, ct));

    private async Task<string> PostAsync(string route, object payload, CancellationToken ct)
    {
        if (!_options.IsConfigured) throw new AiUnavailableException("O endereço da IA ainda não foi configurado. Veja o README.");

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_options.Endpoint.TrimEnd('/')}/{route}")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, Json), Encoding.UTF8, "application/json")
        };
        // Identidade opcional: com login o backend limita por usuário; sem login, por origem.
        try
        {
            var token = await _auth.GetIdTokenAsync(ct);
            if (token is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        catch (NotAuthenticatedException) { /* segue como visitante */ }

        HttpGateway.Reply reply;
        try { reply = await HttpGateway.SendAsync(_http, req, TimeSpan.FromSeconds(_options.TimeoutSeconds), ct); }
        catch (RemoteTimeoutException ex) { throw new AiUnavailableException("A IA demorou demais para responder. Tente novamente.", ex); }
        catch (OfflineException ex) { throw new AiUnavailableException("Sem conexão para usar a IA.", ex); }

        if (reply.Status == HttpStatusCode.TooManyRequests) throw new AiRateLimitException();
        if (!reply.IsSuccess) throw new AiUnavailableException();

        try
        {
            using var doc = JsonDocument.Parse(reply.Body);
            var text = doc.RootElement.TryGetProperty("text", out var t) ? t.GetString() : null;
            return string.IsNullOrWhiteSpace(text) ? throw new InvalidAiResponseException() : text;
        }
        catch (JsonException ex) { throw new InvalidAiResponseException(inner: ex); }
    }
}
