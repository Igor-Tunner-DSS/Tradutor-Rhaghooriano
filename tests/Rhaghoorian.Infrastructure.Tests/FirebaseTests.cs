using System.Net;
using System.Text.Json;
using Rhaghoorian.Core.Ai;
using Rhaghoorian.Core.Errors;
using Rhaghoorian.Core.Models;
using Rhaghoorian.Infrastructure.Firebase;
using Rhaghoorian.Infrastructure.Networking;
using Xunit;

namespace Rhaghoorian.Infrastructure.Tests;

public class FirestoreMapperTests
{
    [Fact]
    public void EveryEntryOfTheRealDictionary_RoundTripsThroughFirestoreDocuments()
    {
        foreach (var e in Data.RealDictionary().Entries)
        {
            var doc = new System.Text.Json.Nodes.JsonObject
            {
                ["name"] = $"projects/p/databases/(default)/documents/dictionary/{e.Id}",
                ["fields"] = FirestoreMapper.ToFields(e),
                ["updateTime"] = "2026-02-03T04:05:06.123456Z"
            };
            using var parsed = JsonDocument.Parse(doc.ToJsonString());
            var back = FirestoreMapper.FromDocument(parsed.RootElement);

            Assert.Equal(e.Id, back.Id);
            Assert.Equal(e.Rhagho, back.Rhagho);
            Assert.Equal(e.Portuguese, back.Portuguese);
            Assert.Equal(e.PartOfSpeech, back.PartOfSpeech);
            Assert.Equal(e.SortOrder, back.SortOrder);
            Assert.Equal(e.SourceLine, back.SourceLine);
            Assert.Equal(e.Verb?.Infinitive, back.Verb?.Infinitive);
            Assert.Equal(e.Verb?.Tense, back.Verb?.Tense);
            Assert.Equal(e.Verb?.PersonSlot, back.Verb?.PersonSlot);
            Assert.Equal(e.Verb?.IsInfinitive, back.Verb?.IsInfinitive);
            Assert.Equal("2026-02-03T04:05:06.123456Z", back.RemoteUpdateTime);
        }
    }

    [Fact]
    public void OptionalAndNestedFields_RoundTrip()
    {
        var e = new WordEntry
        {
            Id = "a1", Rhagho = "r", Portuguese = "p", PartOfSpeech = "Substantivo", Etymology = "et", Example = "ex",
            AlternativeForms = { new AlternativeForm { Text = "r2", Note = "n" }, new AlternativeForm { Text = "r3" } },
            Abbreviations = { "ab" }, Synonyms = { new Synonym { Text = "s", AcceptedFromAi = true } },
            CreatedBy = "u1", UpdatedBy = "u2", Version = 9
        };
        var doc = new System.Text.Json.Nodes.JsonObject { ["name"] = "x/a1", ["fields"] = FirestoreMapper.ToFields(e) };
        using var parsed = JsonDocument.Parse(doc.ToJsonString());
        var back = FirestoreMapper.FromDocument(parsed.RootElement);
        Assert.Equal("et", back.Etymology);
        Assert.Equal(2, back.AlternativeForms.Count);
        Assert.Null(back.AlternativeForms[1].Note);
        Assert.True(back.Synonyms[0].AcceptedFromAi);
        Assert.Equal(9, back.Version);
        Assert.Equal("u2", back.UpdatedBy);
        Assert.Null(back.Verb);
    }
}

public class FirestoreStoreTests
{
    private static readonly FirebaseOptions Opts = new() { ProjectId = "demo-proj", ApiKey = "key" };

    private static (FirestoreDictionaryStore store, FakeHandler handler, StubAuth auth) Build()
    {
        var h = new FakeHandler();
        var auth = new StubAuth();
        return (new FirestoreDictionaryStore(new HttpClient(h), Opts, auth), h, auth);
    }

    private static WordEntry Word(string id = "w1") => new() { Id = id, Rhagho = "kaza", Portuguese = "casa", PartOfSpeech = "Substantivo" };

    [Fact]
    public async Task Revision_IsNullWhenMetaDocumentDoesNotExist_AndReadsNeedNoToken()
    {
        var (store, h, _) = Build();
        h.Responder = _ => (HttpStatusCode.NotFound, "{\"error\":{\"status\":\"NOT_FOUND\"}}");
        Assert.Null(await store.GetRevisionAsync());
        Assert.Null(h.Requests[0].Authorization);

        h.Responder = _ => (HttpStatusCode.OK, "{\"fields\":{\"revision\":{\"integerValue\":\"42\"}}}");
        Assert.Equal(42, await store.GetRevisionAsync());
        Assert.Contains("/documents/meta/dictionary", h.Requests[1].Url);
    }

    [Fact]
    public async Task ListAll_FollowsPagination()
    {
        var (store, h, _) = Build();
        string Doc(string id) => $"{{\"name\":\"projects/demo-proj/databases/(default)/documents/dictionary/{id}\",\"fields\":{{\"rhagho\":{{\"stringValue\":\"r{id}\"}},\"portuguese\":{{\"stringValue\":\"p{id}\"}},\"partOfSpeech\":{{\"stringValue\":\"Outros\"}}}},\"updateTime\":\"t{id}\"}}";
        h.Responder = s => s.Url.Contains("pageToken=tok2")
            ? (HttpStatusCode.OK, $"{{\"documents\":[{Doc("c")}]}}")
            : (HttpStatusCode.OK, $"{{\"documents\":[{Doc("a")},{Doc("b")}],\"nextPageToken\":\"tok2\"}}");
        var all = await store.ListAllAsync();
        Assert.Equal(new[] { "a", "b", "c" }, all.Select(e => e.Id).ToArray());
        Assert.Equal("ta", all[0].RemoteUpdateTime);
        Assert.Equal(2, h.Requests.Count);
    }

    [Fact]
    public async Task Create_IsAnAtomicCommitWithExistsFalseAndRevisionIncrement()
    {
        var (store, h, _) = Build();
        h.Responder = _ => (HttpStatusCode.OK, "{\"writeResults\":[{\"updateTime\":\"T1\"},{\"updateTime\":\"T1\"}]}");
        var saved = await store.CreateAsync(Word());

        Assert.Equal("T1", saved.RemoteUpdateTime);
        var req = h.Requests.Single();
        Assert.Equal("Bearer id-token-123", req.Authorization);
        Assert.EndsWith("documents:commit", req.Url);
        using var body = JsonDocument.Parse(req.Body);
        var writes = body.RootElement.GetProperty("writes");
        Assert.Equal(2, writes.GetArrayLength());
        Assert.False(writes[0].GetProperty("currentDocument").GetProperty("exists").GetBoolean());
        Assert.Equal("revision", writes[1].GetProperty("updateTransforms")[0].GetProperty("fieldPath").GetString());
        Assert.Equal("1", writes[1].GetProperty("updateTransforms")[0].GetProperty("increment").GetProperty("integerValue").GetString());
    }

    [Fact]
    public async Task Update_UsesUpdateTimePrecondition_AndMapsPreconditionFailureToConflict()
    {
        var (store, h, _) = Build();
        var e = Word(); e.RemoteUpdateTime = "T0";
        h.Responder = _ => (HttpStatusCode.OK, "{\"writeResults\":[{\"updateTime\":\"T2\"},{}]}");
        Assert.Equal("T2", (await store.UpdateAsync(e)).RemoteUpdateTime);
        using (var body = JsonDocument.Parse(h.Requests[0].Body))
            Assert.Equal("T0", body.RootElement.GetProperty("writes")[0].GetProperty("currentDocument").GetProperty("updateTime").GetString());

        h.Responder = _ => (HttpStatusCode.BadRequest, "{\"error\":{\"code\":400,\"status\":\"FAILED_PRECONDITION\",\"message\":\"x\"}}");
        await Assert.ThrowsAsync<ConflictException>(() => store.UpdateAsync(e));

        e.RemoteUpdateTime = null;
        await Assert.ThrowsAsync<ConflictException>(() => store.UpdateAsync(e));
    }

    [Fact]
    public async Task Writes_WithoutLogin_FailBeforeAnyHttpRequest()
    {
        var (store, h, auth) = Build();
        auth.IsAuthenticated = false;
        await Assert.ThrowsAsync<NotAuthenticatedException>(() => store.CreateAsync(Word()));
        await Assert.ThrowsAsync<NotAuthenticatedException>(() => store.DeleteAsync("w1", "T"));
        Assert.Empty(h.Requests);
    }

    [Fact]
    public async Task ServerRefusals_AreMappedToClearErrors()
    {
        var (store, h, _) = Build();
        h.Responder = _ => (HttpStatusCode.Forbidden, "{\"error\":{\"status\":\"PERMISSION_DENIED\"}}");
        await Assert.ThrowsAsync<NotAuthenticatedException>(() => store.CreateAsync(Word()));
        h.Responder = _ => (HttpStatusCode.Conflict, "{\"error\":{\"status\":\"ALREADY_EXISTS\"}}");
        await Assert.ThrowsAsync<ConflictException>(() => store.CreateAsync(Word()));
        h.Responder = _ => (HttpStatusCode.ServiceUnavailable, "oops");
        await Assert.ThrowsAsync<RemoteStoreException>(() => store.CreateAsync(Word()));
    }

    [Fact]
    public async Task NetworkFailures_BecomeOfflineAndTimeoutErrors()
    {
        var (store, h, _) = Build();
        h.Throw = new HttpRequestException("dns");
        await Assert.ThrowsAsync<OfflineException>(() => store.GetRevisionAsync());
        h.Throw = new TaskCanceledException();
        await Assert.ThrowsAsync<RemoteTimeoutException>(() => store.GetRevisionAsync());
    }

    [Fact]
    public async Task CreateMissing_OnlyWritesDocumentsThatDoNotExistYet()
    {
        var (store, h, _) = Build();
        h.Responder = s => s.Method == HttpMethod.Get
            ? (HttpStatusCode.OK, "{\"documents\":[{\"name\":\"projects/demo-proj/databases/(default)/documents/dictionary/w1\"}]}")
            : (HttpStatusCode.OK, "{\"writeResults\":[{\"updateTime\":\"T\"},{\"updateTime\":\"T\"}]}");
        var created = await store.CreateMissingAsync(new[] { Word("w1"), Word("w2") });
        Assert.Equal(1, created);
        var commit = h.Requests.Single(r => r.Method == HttpMethod.Post);
        Assert.Contains("/dictionary/w2", commit.Body);
        Assert.DoesNotContain("/dictionary/w1", commit.Body);
    }
}

public class FirebaseAuthTests
{
    private static readonly FirebaseOptions Opts = new() { ProjectId = "demo-proj", ApiKey = "api-key-xyz" };

    private static (FirebaseAuthService svc, FakeHandler h, MemoryTokenStore store) Build()
    {
        var h = new FakeHandler(); var store = new MemoryTokenStore();
        return (new FirebaseAuthService(new HttpClient(h), Opts, store), h, store);
    }

    private const string SignInOk = "{\"idToken\":\"ID1\",\"refreshToken\":\"REF1\",\"expiresIn\":\"3600\",\"localId\":\"uid-9\",\"email\":\"admin@x.com\"}";

    [Fact]
    public async Task SignIn_Succeeds_StoresOnlyTheRefreshToken_NeverThePassword()
    {
        var (svc, h, store) = Build();
        h.Responder = _ => (HttpStatusCode.OK, SignInOk);
        await svc.SignInAsync("admin@x.com", "S3nha-Secreta!");

        Assert.True(svc.IsAuthenticated);
        Assert.Equal("uid-9", svc.UserId);
        Assert.Equal("ID1", await svc.GetIdTokenAsync());
        Assert.Contains("signInWithPassword", h.Requests[0].Url);
        Assert.Contains("api-key-xyz", h.Requests[0].Url);
        Assert.Equal(new[] { "REF1" }, store.Data.Values.ToArray());
        Assert.DoesNotContain(store.Data.Values, v => v.Contains("S3nha"));
    }

    [Fact]
    public async Task WrongPassword_GivesAFriendlyMessage_AndStaysSignedOut()
    {
        var (svc, h, store) = Build();
        h.Responder = _ => (HttpStatusCode.BadRequest, "{\"error\":{\"code\":400,\"message\":\"INVALID_LOGIN_CREDENTIALS\"}}");
        var ex = await Assert.ThrowsAsync<InvalidCredentialsException>(() => svc.SignInAsync("a@b.c", "x"));
        Assert.Equal("E-mail ou senha incorretos.", ex.Message);
        Assert.False(svc.IsAuthenticated);
        Assert.Empty(store.Data);
    }

    [Fact]
    public async Task EmptyCredentials_AreRejectedLocally()
    {
        var (svc, h, _) = Build();
        await Assert.ThrowsAsync<InvalidInputException>(() => svc.SignInAsync("", ""));
        Assert.Empty(h.Requests);
    }

    [Fact]
    public async Task RestoreSession_UsesTheStoredRefreshToken()
    {
        var (svc, h, store) = Build();
        store.Data["firebase.refreshToken"] = "REF-OLD";
        h.Responder = _ => (HttpStatusCode.OK, "{\"id_token\":\"ID2\",\"refresh_token\":\"REF2\",\"expires_in\":\"3600\",\"user_id\":\"uid-9\"}");
        Assert.True(await svc.TryRestoreSessionAsync());
        Assert.Equal("ID2", await svc.GetIdTokenAsync());
        Assert.Contains("securetoken", h.Requests[0].Url);
        Assert.Contains("refresh_token=REF-OLD", h.Requests[0].Body);
        Assert.Equal("REF2", store.Data["firebase.refreshToken"]);
    }

    [Fact]
    public async Task RestoreSession_WhenOffline_KeepsTheTokenAndStartsAsGuest()
    {
        var (svc, h, store) = Build();
        store.Data["firebase.refreshToken"] = "REF-OLD";
        h.Throw = new HttpRequestException("offline");
        Assert.False(await svc.TryRestoreSessionAsync());
        Assert.Equal("REF-OLD", store.Data["firebase.refreshToken"]);
    }

    [Fact]
    public async Task RevokedRefreshToken_ClearsTheSession()
    {
        var (svc, h, store) = Build();
        store.Data["firebase.refreshToken"] = "REF-OLD";
        h.Responder = _ => (HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"TOKEN_EXPIRED\"}}");
        await Assert.ThrowsAsync<NotAuthenticatedException>(() => svc.TryRestoreSessionAsync());
        Assert.Empty(store.Data);
    }

    [Fact]
    public async Task SignOut_ClearsEverything_AndRaisesTheEvent()
    {
        var (svc, h, store) = Build();
        h.Responder = _ => (HttpStatusCode.OK, SignInOk);
        await svc.SignInAsync("admin@x.com", "pw");
        var raised = 0; svc.AuthStateChanged += (_, _) => raised++;
        await svc.SignOutAsync();
        Assert.False(svc.IsAuthenticated);
        Assert.Null(await svc.GetIdTokenAsync());
        Assert.Empty(store.Data);
        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task ExpiredIdToken_IsRefreshedTransparently()
    {
        var (svc, h, _) = Build();
        h.Responder = s => s.Url.Contains("securetoken")
            ? (HttpStatusCode.OK, "{\"id_token\":\"ID-NEW\",\"refresh_token\":\"REF-NEW\",\"expires_in\":\"3600\",\"user_id\":\"uid-9\"}")
            : (HttpStatusCode.OK, SignInOk.Replace("3600", "30"));   // expira em 30 s => já dentro da margem de renovação
        await svc.SignInAsync("admin@x.com", "pw");
        Assert.Equal("ID-NEW", await svc.GetIdTokenAsync());
    }
}

public class BackendAiClientTests
{
    private static (BackendAiClient client, FakeHandler h) Build(bool signedIn = true)
    {
        var h = new FakeHandler();
        return (new BackendAiClient(new HttpClient(h), new AiOptions { Endpoint = "https://example.test/ai/" }, new StubAuth { IsAuthenticated = signedIn }), h);
    }

    private static AiTranslationRequest Req(TranslationDirection d = TranslationDirection.RhaghoToPortuguese) =>
        new("nuruh zzzx", d, new AiContext(new[] { new AiContextEntry("nuruh", "eu", "Pronome", null) }, new[] { "zzzx" }), "Eu zzzx");

    [Fact]
    public async Task Translate_PostsOnlyCompactContext_NoSecrets_AndCleansTheAnswer()
    {
        var (client, h) = Build();
        h.Responder = _ => (HttpStatusCode.OK, "{\"text\":\"Tradução: Eu zzzx\"}");
        Assert.Equal("Eu zzzx", await client.TranslateAsync(Req()));

        var sent = h.Requests.Single();
        Assert.Equal("https://example.test/ai/translate", sent.Url);
        Assert.Equal("Bearer id-token-123", sent.Authorization);
        using var body = JsonDocument.Parse(sent.Body);
        Assert.Equal("rh-pt", body.RootElement.GetProperty("direction").GetString());
        Assert.Equal(1, body.RootElement.GetProperty("context").GetProperty("entries").GetArrayLength());
        Assert.DoesNotContain("openrouter", sent.Body.ToLowerInvariant());
    }

    [Fact]
    public async Task Guests_CanTranslate_WithoutAnAuthorizationHeader()
    {
        var (client, h) = Build(signedIn: false);
        h.Responder = _ => (HttpStatusCode.OK, "{\"text\":\"ok\"}");
        await client.TranslateAsync(Req());
        Assert.Null(h.Requests[0].Authorization);
    }

    [Fact]
    public async Task RateLimitAndOutages_AreMappedToUserFriendlyErrors()
    {
        var (client, h) = Build();
        h.Responder = _ => (HttpStatusCode.TooManyRequests, "{}");
        await Assert.ThrowsAsync<AiRateLimitException>(() => client.TranslateAsync(Req()));
        h.Responder = _ => (HttpStatusCode.BadGateway, "{}");
        await Assert.ThrowsAsync<AiUnavailableException>(() => client.TranslateAsync(Req()));
        h.Responder = _ => (HttpStatusCode.OK, "not json");
        await Assert.ThrowsAsync<InvalidAiResponseException>(() => client.TranslateAsync(Req()));
        h.Responder = _ => (HttpStatusCode.OK, "{\"text\":\"  \"}");
        await Assert.ThrowsAsync<InvalidAiResponseException>(() => client.TranslateAsync(Req()));
        h.Throw = new TaskCanceledException();
        await Assert.ThrowsAsync<AiUnavailableException>(() => client.TranslateAsync(Req()));
    }

    [Fact]
    public async Task Conjugate_AndSynonyms_ParseOnlyTheRequestedShape()
    {
        var (client, h) = Build();
        h.Responder = _ => (HttpStatusCode.OK, "{\"text\":\"mor'hk\\nmor'hk'ta\\nmor'hk'vil\\nmor'hk'nux\"}");
        Assert.Equal("mor'hk'vil", (await client.ConjugateAsync(new AiConjugationRequest("amar", "mor'hk"))).Future);
        h.Responder = _ => (HttpStatusCode.OK, "{\"text\":\"lar\\nmoradia\"}");
        Assert.Equal(2, (await client.SuggestSynonymsAsync(new AiSynonymRequest("casa", null, "Substantivo", Array.Empty<string>()))).Count);
        h.Responder = _ => (HttpStatusCode.OK, "{\"text\":\"Aqui vai uma longa explicação sobre verbos\"}");
        await Assert.ThrowsAsync<InvalidAiResponseException>(() => client.ConjugateAsync(new AiConjugationRequest("amar", "mor'hk")));
    }

    [Fact]
    public async Task Unconfigured_FailsWithAClearMessage()
    {
        var client = new BackendAiClient(new HttpClient(new FakeHandler()), new AiOptions(), new StubAuth());
        var ex = await Assert.ThrowsAsync<AiUnavailableException>(() => client.TranslateAsync(Req()));
        Assert.Contains("configurado", ex.Message);
    }
}

public class ConfigurationTests
{
    [Fact]
    public void Layers_OverrideEachOther_AndPlaceholdersAreReportedAsUnconfigured()
    {
        const string baseJson = "{ \"Firebase\": { \"ProjectId\": \"REPLACE_ME\", \"ApiKey\": \"REPLACE_ME\" }, \"Ai\": { \"Endpoint\": \"REPLACE_ME\" } }";
        var placeholders = AppConfiguration.Build(baseJson);
        Assert.False(placeholders.Firebase.IsConfigured);
        Assert.False(placeholders.Ai.IsConfigured);

        var real = AppConfiguration.Build(baseJson,
            "{ \"Environment\": \"Development\", \"Firebase\": { \"ProjectId\": \"meu-proj\" } }",
            null, "",
            "{ \"Firebase\": { \"ApiKey\": \"AIza-xyz\" }, \"Ai\": { \"Endpoint\": \"https://x.test/ai\" } }");
        Assert.Equal("Development", real.Environment);
        Assert.Equal("meu-proj", real.Firebase.ProjectId);
        Assert.True(real.Firebase.IsConfigured);
        Assert.True(real.Ai.IsConfigured);
        Assert.Equal(20, real.Firebase.TimeoutSeconds);          // padrão preservado
    }

    [Fact]
    public void Emulators_GetLocalUrlsWithoutOverridingExplicitOnes()
    {
        var c = AppConfiguration.Build("{ \"Firebase\": { \"ProjectId\": \"demo\", \"ApiKey\": \"k\", \"UseEmulators\": true, \"FirestoreBaseUrl\": \"http://localhost:9999\" } }");
        Assert.Equal("http://localhost:9999", c.Firebase.FirestoreBaseUrl);
        Assert.Equal("http://127.0.0.1:9099/identitytoolkit.googleapis.com", c.Firebase.AuthBaseUrl);
        Assert.Equal("http://127.0.0.1:5001/demo/us-central1/ai", c.Ai.Endpoint);
    }

    [Fact]
    public void NoSecretFieldExistsInTheConfigurationModel()
    {
        var names = typeof(AppConfiguration).Assembly.GetTypes()
            .Where(t => t.Namespace == typeof(AppConfiguration).Namespace)
            .SelectMany(t => t.GetProperties()).Select(p => p.Name.ToLowerInvariant());
        Assert.DoesNotContain("openrouterkey", names);
        Assert.DoesNotContain("openrouterapikey", names);
        Assert.DoesNotContain("password", names);
    }
}
