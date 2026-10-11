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
        var changes = await store.ListChangesAsync(null);
        var all = changes.Upserts;
        Assert.True(changes.IsFull);
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
        Assert.Equal("syncStamp", writes[0].GetProperty("updateTransforms")[0].GetProperty("fieldPath").GetString());
        Assert.Equal("REQUEST_TIME", writes[0].GetProperty("updateTransforms")[0].GetProperty("setToServerValue").GetString());
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
    public async Task Delete_LeavesATombstoneInTheSameAtomicCommit()
    {
        var (store, h, _) = Build();
        h.Responder = _ => (HttpStatusCode.OK, "{\"writeResults\":[{},{\"updateTime\":\"T\"},{}]}");
        await store.DeleteAsync("w1", "T0");
        using var body = JsonDocument.Parse(h.Requests.Single().Body);
        var writes = body.RootElement.GetProperty("writes");
        Assert.Equal(3, writes.GetArrayLength());                                   // exclusão + marcador + revisão
        Assert.Contains("/dictionary/w1", writes[0].GetProperty("delete").GetString());
        Assert.Equal("T0", writes[0].GetProperty("currentDocument").GetProperty("updateTime").GetString());
        Assert.Contains("/deletions/w1", writes[1].GetProperty("update").GetProperty("name").GetString());
        Assert.Equal("syncStamp", writes[1].GetProperty("updateTransforms")[0].GetProperty("fieldPath").GetString());
    }

    [Fact]
    public async Task IncrementalSync_AsksOnlyForDocumentsStampedAfterTheCursor()
    {
        var (store, h, _) = Build();
        string Doc(string id, string stamp) => $"{{\"document\":{{\"name\":\"projects/demo-proj/databases/(default)/documents/dictionary/{id}\",\"fields\":{{\"rhagho\":{{\"stringValue\":\"r\"}},\"portuguese\":{{\"stringValue\":\"p\"}},\"partOfSpeech\":{{\"stringValue\":\"Outros\"}},\"syncStamp\":{{\"timestampValue\":\"{stamp}\"}}}}}},\"readTime\":\"x\"}}";
        h.Responder = s => s.Body.Contains("\"deletions\"")
            ? (HttpStatusCode.OK, "[{\"document\":{\"name\":\"projects/demo-proj/databases/(default)/documents/deletions/gone\",\"fields\":{\"syncStamp\":{\"timestampValue\":\"2026-10-05T10:00:09.000000Z\"}}}}]")
            : (HttpStatusCode.OK, $"[{Doc("a", "2026-10-05T10:00:05.250000Z")},{Doc("b", "2026-10-05T10:00:07.000000Z")}]");

        var since = new DateTimeOffset(2026, 10, 5, 10, 0, 0, TimeSpan.Zero);
        var changes = await store.ListChangesAsync(since);

        Assert.False(changes.IsFull);
        Assert.Equal(new[] { "a", "b" }, changes.Upserts.Select(e => e.Id).ToArray());
        Assert.Equal(new[] { "gone" }, changes.DeletedIds.ToArray());
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 10, 0, 9, TimeSpan.Zero), changes.Cursor);   // maior carimbo visto
        Assert.Equal(2, h.Requests.Count);                                                          // 1 consulta por coleção
        Assert.All(h.Requests, r => Assert.Null(r.Authorization));                                  // leituras públicas
        Assert.All(h.Requests, r => Assert.EndsWith(":runQuery", r.Url));
        using var q = JsonDocument.Parse(h.Requests[0].Body);
        var filter = q.RootElement.GetProperty("structuredQuery").GetProperty("where").GetProperty("fieldFilter");
        Assert.Equal("syncStamp", filter.GetProperty("field").GetProperty("fieldPath").GetString());
        Assert.Equal("GREATER_THAN", filter.GetProperty("op").GetString());
        Assert.Equal("2026-10-05T10:00:00.000000Z", filter.GetProperty("value").GetProperty("timestampValue").GetString());
    }

    [Fact]
    public async Task IncrementalSync_WithNothingNew_ReturnsAnEmptyDelta()
    {
        var (store, h, _) = Build();
        h.Responder = _ => (HttpStatusCode.OK, "[{\"readTime\":\"2026-10-05T10:00:00Z\"}]");   // resposta de consulta vazia
        var changes = await store.ListChangesAsync(DateTimeOffset.UtcNow);
        Assert.Empty(changes.Upserts);
        Assert.Empty(changes.DeletedIds);
        Assert.Null(changes.Cursor);
    }

    [Fact]
    public async Task ExhaustedDailyQuota_IsRecognizedAndExplained()
    {
        var (store, h, _) = Build();
        h.Responder = _ => (HttpStatusCode.TooManyRequests, "{\"error\":{\"code\":429,\"status\":\"RESOURCE_EXHAUSTED\",\"message\":\"Quota exceeded.\"}}");
        var ex = await Assert.ThrowsAsync<RemoteQuotaExceededException>(() => store.GetRevisionAsync());
        Assert.Contains("cota diária", ex.Message);
        h.Responder = _ => (HttpStatusCode.TooManyRequests, "{\"error\":{\"status\":\"UNAVAILABLE\"}}");
        await Assert.ThrowsAsync<RemoteStoreException>(() => store.GetRevisionAsync());
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

public class ConfigurationTests
{
    [Fact]
    public void Layers_OverrideEachOther_AndPlaceholdersAreReportedAsUnconfigured()
    {
        const string baseJson = "{ \"Firebase\": { \"ProjectId\": \"REPLACE_ME\", \"ApiKey\": \"REPLACE_ME\" }, \"Ai\": { \"DailyLimit\": 20 } }";
        var placeholders = AppConfiguration.Build(baseJson);
        Assert.False(placeholders.Firebase.IsConfigured);
        Assert.Equal("openrouter/free", placeholders.Ai.Model);
        Assert.Equal(20, placeholders.Ai.DailyLimit);

        var real = AppConfiguration.Build(baseJson,
            "{ \"Environment\": \"Development\", \"Firebase\": { \"ProjectId\": \"meu-proj\" } }",
            null, "",
            "{ \"Firebase\": { \"ApiKey\": \"AIza-xyz\" }, \"Ai\": { \"DailyLimit\": 10 } }");
        Assert.Equal("Development", real.Environment);
        Assert.Equal("meu-proj", real.Firebase.ProjectId);
        Assert.True(real.Firebase.IsConfigured);
        Assert.Equal(10, real.Ai.DailyLimit);
        Assert.Equal(20, real.Firebase.TimeoutSeconds);          // padrão preservado
    }

    [Fact]
    public void Emulators_GetLocalUrlsWithoutOverridingExplicitOnes()
    {
        var c = AppConfiguration.Build("{ \"Firebase\": { \"ProjectId\": \"demo\", \"ApiKey\": \"k\", \"UseEmulators\": true, \"FirestoreBaseUrl\": \"http://localhost:9999\" } }");
        Assert.Equal("http://localhost:9999", c.Firebase.FirestoreBaseUrl);
        Assert.Equal("http://127.0.0.1:9099/identitytoolkit.googleapis.com", c.Firebase.AuthBaseUrl);
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
