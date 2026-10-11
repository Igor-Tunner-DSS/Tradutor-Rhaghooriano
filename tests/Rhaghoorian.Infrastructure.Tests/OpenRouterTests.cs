using System.Net;
using System.Text.Json;
using Rhaghoorian.Core.Ai;
using Rhaghoorian.Core.Errors;
using Rhaghoorian.Core.Models;
using Rhaghoorian.Core.Tests.Support;
using Rhaghoorian.Infrastructure.Networking;
using Xunit;

namespace Rhaghoorian.Infrastructure.Tests;

public class OpenRouterAiClientTests
{
    private const string Key = "sk-or-v1-0123456789abcdef0123456789abcdef";

    private sealed class Rig
    {
        public readonly FakeHandler Http = new();
        public readonly FakeKeyStore Keys = new() { Key = Key };
        public readonly FakeSettings Settings = new();
        public readonly SettingsAiUsageLimiter Usage;
        public readonly OpenRouterAiClient Client;
        public int Delays;

        public Rig(int dailyLimit = 20, int retries = 2)
        {
            Usage = new SettingsAiUsageLimiter(Settings, dailyLimit);
            Client = new OpenRouterAiClient(new HttpClient(Http), new AiOptions { MaxRetries = retries, DailyLimit = dailyLimit }, Keys, Usage,
                (_, _) => { Delays++; return Task.CompletedTask; });
        }
    }

    private static string Ok(string content) => JsonSerializer.Serialize(new { choices = new[] { new { message = new { content } } } });

    private static AiTranslationRequest Req() =>
        new("nuruh zzzx", TranslationDirection.RhaghoToPortuguese,
            new AiContext(new[] { new AiContextEntry("nuruh", "eu", "Pronome", null) }, new[] { "zzzx" }), "Eu zzzx");

    [Fact]
    public async Task Translate_CallsOpenRouterDirectly_WithThePersonalKeyOnlyInTheAuthorizationHeader()
    {
        var rig = new Rig();
        rig.Http.Responder = _ => (HttpStatusCode.OK, Ok("Tradução: Eu zzzx"));
        Assert.Equal("Eu zzzx", await rig.Client.TranslateAsync(Req()));

        var sent = rig.Http.Requests.Single();
        Assert.Equal("https://openrouter.ai/api/v1/chat/completions", sent.Url);
        Assert.Equal($"Bearer {Key}", sent.Authorization);
        Assert.DoesNotContain(Key, sent.Body);
        using var body = JsonDocument.Parse(sent.Body);
        Assert.Equal("openrouter/free", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("system", body.RootElement.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.Contains("<<<\nnuruh zzzx\n>>>", body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString());
        Assert.Equal(1, (await rig.Usage.GetUsageAsync()).UsedToday);
    }

    [Fact]
    public async Task WithoutAKey_NothingIsSent_AndNoUsageIsSpent()
    {
        var rig = new Rig();
        rig.Keys.Key = null;
        await Assert.ThrowsAsync<AiKeyMissingException>(() => rig.Client.TranslateAsync(Req()));
        Assert.Empty(rig.Http.Requests);
        Assert.Equal(0, (await rig.Usage.GetUsageAsync()).UsedToday);
    }

    [Fact]
    public async Task LocalDailyLimit_BlocksBeforeAnyHttpCall()
    {
        var rig = new Rig(dailyLimit: 2);
        rig.Http.Responder = _ => (HttpStatusCode.OK, Ok("ok"));
        await rig.Client.TranslateAsync(Req());
        await rig.Client.TranslateAsync(Req());
        var ex = await Assert.ThrowsAsync<AiRateLimitException>(() => rig.Client.TranslateAsync(Req()));
        Assert.Contains("limite 2", ex.Message);
        Assert.Equal(2, rig.Http.Requests.Count);
    }

    [Fact]
    public async Task TransientFailures_AreRetriedWithBackoff_AndCountAsASingleUse()
    {
        var rig = new Rig();
        var calls = 0;
        rig.Http.Responder = _ => ++calls switch
        {
            1 => (HttpStatusCode.ServiceUnavailable, "{}"),
            2 => (HttpStatusCode.OK, Ok("   ")),                 // resposta vazia
            _ => (HttpStatusCode.OK, Ok("<think>pensando</think>Eu irei"))
        };
        Assert.Equal("Eu irei", await rig.Client.TranslateAsync(Req()));
        Assert.Equal(3, calls);
        Assert.Equal(2, rig.Delays);
        Assert.Equal(1, (await rig.Usage.GetUsageAsync()).UsedToday);
    }

    [Fact]
    public async Task OpenRouterRateLimit_AfterRetries_BecomesAFriendlyMessage()
    {
        var rig = new Rig();
        rig.Http.Responder = _ => (HttpStatusCode.TooManyRequests, "{}");
        var ex = await Assert.ThrowsAsync<AiRateLimitException>(() => rig.Client.TranslateAsync(Req()));
        Assert.Equal(3, rig.Http.Requests.Count);
        Assert.Contains("OpenRouter", ex.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task RefusedKey_IsNotRetried(HttpStatusCode status)
    {
        var rig = new Rig();
        rig.Http.Responder = _ => (status, "{}");
        await Assert.ThrowsAsync<AiKeyInvalidException>(() => rig.Client.TranslateAsync(Req()));
        Assert.Single(rig.Http.Requests);
    }

    [Fact]
    public async Task NoBalance_NetworkLoss_AndTimeouts_AreExplainedWithoutRetryStorms()
    {
        var rig = new Rig();
        rig.Http.Responder = _ => (HttpStatusCode.PaymentRequired, "{}");
        var ex = await Assert.ThrowsAsync<AiUnavailableException>(() => rig.Client.TranslateAsync(Req()));
        Assert.Contains("sem saldo", ex.Message);
        Assert.Single(rig.Http.Requests);

        var offline = new Rig();
        offline.Http.Throw = new HttpRequestException("dns");
        await Assert.ThrowsAsync<AiUnavailableException>(() => offline.Client.TranslateAsync(Req()));
        Assert.Single(offline.Http.Requests);

        var slow = new Rig(retries: 1);
        slow.Http.Throw = new TaskCanceledException();
        await Assert.ThrowsAsync<AiUnavailableException>(() => slow.Client.TranslateAsync(Req()));
        Assert.Equal(2, slow.Http.Requests.Count);
    }

    [Fact]
    public async Task Conjugation_AndSynonyms_ParseOnlyTheRequestedShape()
    {
        var rig = new Rig();
        rig.Http.Responder = _ => (HttpStatusCode.OK, Ok("mor'hk\nmor'hk'ta\nmor'hk'vil\nmor'hk'nux"));
        Assert.Equal("mor'hk'vil", (await rig.Client.ConjugateAsync(new AiConjugationRequest("amar", "mor'hk"))).Future);
        rig.Http.Responder = _ => (HttpStatusCode.OK, Ok("lar\nmoradia"));
        Assert.Equal(2, (await rig.Client.SuggestSynonymsAsync(new AiSynonymRequest("casa", null, null, Array.Empty<string>()))).Count);
        rig.Http.Responder = _ => (HttpStatusCode.OK, Ok("Aqui vai uma longa explicação sobre verbos"));
        await Assert.ThrowsAsync<InvalidAiResponseException>(() => rig.Client.ConjugateAsync(new AiConjugationRequest("amar", "mor'hk")));
    }

    [Fact]
    public async Task Cancellation_StopsTheCall()
    {
        var rig = new Rig();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => rig.Client.TranslateAsync(Req(), cts.Token));
    }
}

public class KeyManagerTests
{
    private const string Key = "sk-or-v1-0123456789abcdef0123456789abcdef";

    private static (OpenRouterKeyManager mgr, FakeHandler http, FakeKeyStore store) Build()
    {
        var http = new FakeHandler(); var store = new FakeKeyStore();
        return (new OpenRouterKeyManager(new HttpClient(http), new AiOptions(), store), http, store);
    }

    [Fact]
    public async Task ValidKey_IsCheckedWithOpenRouterThenSaved_AndOnlyMaskedWhenShown()
    {
        var (mgr, http, store) = Build();
        http.Responder = _ => (HttpStatusCode.OK, "{\"data\":{\"label\":\"x\",\"is_free_tier\":true}}");
        var r = await mgr.SaveAsync($"  {Key}  ");
        Assert.True(r.Verified);
        Assert.True(r.IsFreeTier == true);
        Assert.Equal(Key, store.Key);
        Assert.Equal("https://openrouter.ai/api/v1/key", http.Requests[0].Url);
        Assert.Equal($"Bearer {Key}", http.Requests[0].Authorization);
        var status = await mgr.GetStatusAsync();
        Assert.True(status.HasKey);
        Assert.Equal("sk-or-…cdef", status.MaskedKey);
        Assert.DoesNotContain("0123456789", status.MaskedKey!);
    }

    [Fact]
    public async Task RefusedKey_IsNotSaved()
    {
        var (mgr, http, store) = Build();
        http.Responder = _ => (HttpStatusCode.Unauthorized, "{}");
        await Assert.ThrowsAsync<AiKeyInvalidException>(() => mgr.SaveAsync(Key));
        Assert.Null(store.Key);
    }

    [Fact]
    public async Task BadFormat_NeverReachesTheNetwork()
    {
        var (mgr, http, store) = Build();
        await Assert.ThrowsAsync<InvalidInputException>(() => mgr.SaveAsync("AIzaSyFirebaseKey-0123456789abcdef"));
        Assert.Empty(http.Requests);
        Assert.Null(store.Key);
    }

    [Fact]
    public async Task Offline_SavesWithoutConfirmation_AndRemoveClearsTheKey()
    {
        var (mgr, http, store) = Build();
        http.Throw = new HttpRequestException("offline");
        var r = await mgr.SaveAsync(Key);
        Assert.False(r.Verified);
        Assert.Equal(Key, store.Key);
        await mgr.RemoveAsync();
        Assert.False((await mgr.GetStatusAsync()).HasKey);
    }

    [Fact]
    public async Task SecureStore_KeepsTheKeyOnlyInTheProtectedTokenStore()
    {
        var tokens = new MemoryTokenStore();
        var store = new SecureAiKeyStore(tokens);
        await store.SetAsync(Key);
        Assert.Equal(Key, await store.GetAsync());
        Assert.Single(tokens.Data);
        await store.ClearAsync();
        Assert.Null(await store.GetAsync());
        Assert.Empty(tokens.Data);
    }
}
