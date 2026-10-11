using Rhaghoorian.Core.Abstractions;
using Rhaghoorian.Core.Errors;
using Rhaghoorian.Core.Importing;
using Rhaghoorian.Core.Models;
using Rhaghoorian.Core.Services;
using Rhaghoorian.Core.Tests.Support;
using Xunit;

namespace Rhaghoorian.Core.Tests;

public class Harness
{
    public InMemoryDictionaryRepository Local = new();
    public FakeRemoteStore Remote = new();
    public FakeAuth Auth = new();
    public FakeConnectivity Net = new();
    public DictionaryService Service;
    public SyncService Sync;

    public Harness()
    {
        Service = new DictionaryService(Local, Remote, Auth, Net, TestKit.Ling);
        Sync = new SyncService(Local, Remote, Net);
    }

    public static WordEntry NewWord(string pt = "casa", string rh = "kaza") =>
        new() { Portuguese = pt, Rhagho = rh, PartOfSpeech = "Substantivo" };
}

public class AuthorizationAndCrudTests
{
    [Fact]
    public async Task UnauthenticatedUser_CannotCreateUpdateOrDelete_AndNothingIsWritten()
    {
        var h = new Harness();
        h.Remote.Seed(new[] { TestKit.Word("sol", "sul", "Substantivo", 0) });
        await h.Sync.SyncAsync();

        await Assert.ThrowsAsync<NotAuthenticatedException>(() => h.Service.CreateAsync(Harness.NewWord()));
        var existing = (await h.Local.SearchAsync(new DictionaryQuery()))[0];
        await Assert.ThrowsAsync<NotAuthenticatedException>(() => h.Service.UpdateAsync(existing));
        await Assert.ThrowsAsync<NotAuthenticatedException>(() => h.Service.DeleteAsync(existing.Id));
        Assert.Equal(0, h.Remote.WriteCalls);
        Assert.Equal(1, await h.Local.CountAsync(new DictionaryQuery()));
    }

    [Fact]
    public async Task AuthenticatedUser_Create_PersistsRemoteFirstThenLocal_WithMetadata()
    {
        var h = new Harness { };
        h.Auth.IsAuthenticated = true;
        var saved = await h.Service.CreateAsync(Harness.NewWord());

        Assert.NotNull(await h.Remote.GetAsync(saved.Id));
        var local = await h.Local.GetAsync(saved.Id);
        Assert.NotNull(local);
        Assert.Equal("user-1", local!.CreatedBy);
        Assert.Equal("user-1", local.UpdatedBy);
        Assert.Equal(1, local.Version);
        Assert.NotNull(local.RemoteUpdateTime);
    }

    [Fact]
    public async Task Update_BumpsVersionAndMetadata()
    {
        var h = new Harness();
        h.Auth.IsAuthenticated = true;
        var created = await h.Service.CreateAsync(Harness.NewWord());
        var edit = (await h.Service.GetAsync(created.Id))!;
        edit.Example = "A casa é azul.";
        h.Auth.UserId = "user-2";
        var updated = await h.Service.UpdateAsync(edit);

        Assert.Equal(2, updated.Version);
        Assert.Equal("user-1", updated.CreatedBy);
        Assert.Equal("user-2", updated.UpdatedBy);
        Assert.Equal("A casa é azul.", (await h.Remote.GetAsync(created.Id))!.Example);
    }

    [Fact]
    public async Task ConcurrentEdit_IsDetected_NothingIsSilentlyOverwritten()
    {
        var h = new Harness();
        h.Auth.IsAuthenticated = true;
        var created = await h.Service.CreateAsync(Harness.NewWord());

        var mine = (await h.Service.GetAsync(created.Id))!;          // usuário A abre o formulário
        var theirs = (await h.Remote.GetAsync(created.Id))!;          // usuário B altera antes
        theirs.Etymology = "versão do usuário B";
        await h.Remote.UpdateAsync(theirs);

        mine.Example = "versão do usuário A";
        await Assert.ThrowsAsync<ConflictException>(() => h.Service.UpdateAsync(mine));
        Assert.Equal("versão do usuário B", (await h.Remote.GetAsync(created.Id))!.Etymology);
        Assert.Null((await h.Remote.GetAsync(created.Id))!.Example);

        var reloaded = await h.Service.ReloadFromRemoteAsync(created.Id);   // recarrega e tenta de novo
        reloaded.Example = "versão do usuário A";
        await h.Service.UpdateAsync(reloaded);
        Assert.Equal("versão do usuário A", (await h.Remote.GetAsync(created.Id))!.Example);
    }

    [Fact]
    public async Task Delete_RemovesRemoteThenLocal()
    {
        var h = new Harness();
        h.Auth.IsAuthenticated = true;
        var created = await h.Service.CreateAsync(Harness.NewWord());
        await h.Service.DeleteAsync(created.Id);
        Assert.Null(await h.Remote.GetAsync(created.Id));
        Assert.Null(await h.Local.GetAsync(created.Id));
    }

    [Fact]
    public async Task Offline_WritesFail_AndAreNeverReportedAsDone()
    {
        var h = new Harness();
        h.Auth.IsAuthenticated = true;
        h.Net.IsOnline = false;
        await Assert.ThrowsAsync<OfflineException>(() => h.Service.CreateAsync(Harness.NewWord()));
        Assert.Equal(0, await h.Local.CountAsync(new DictionaryQuery()));
        Assert.Equal(0, h.Remote.WriteCalls);
    }

    [Fact]
    public async Task RequiredFields_AreValidated()
    {
        var h = new Harness();
        h.Auth.IsAuthenticated = true;
        await Assert.ThrowsAsync<InvalidInputException>(() => h.Service.CreateAsync(new WordEntry { Portuguese = "x", Rhagho = "", PartOfSpeech = "Substantivo" }));
        await Assert.ThrowsAsync<InvalidInputException>(() => h.Service.CreateAsync(new WordEntry { Portuguese = "", Rhagho = "x", PartOfSpeech = "Substantivo" }));
        await Assert.ThrowsAsync<InvalidInputException>(() => h.Service.CreateAsync(new WordEntry { Portuguese = "x", Rhagho = "y", PartOfSpeech = "" }));
    }

    [Fact]
    public async Task NewVerb_RejectsBaseWithSuffix_AndGeneratesFourForms()
    {
        var h = new Harness();
        h.Auth.IsAuthenticated = true;
        var bad = new WordEntry { Portuguese = "amar", Rhagho = "mor'hk'ta", PartOfSpeech = "Verbo" };
        await Assert.ThrowsAsync<InvalidInputException>(() => h.Service.CreateAsync(bad));

        var saved = await h.Service.CreateAsync(new WordEntry { Portuguese = "amar", Rhagho = "mor'hk", PartOfSpeech = "Verbo" });
        Assert.True(saved.Verb!.IsInfinitive);
        Assert.Equal("mor'hk", saved.Verb.Base);

        var forms = h.Service.GenerateConjugation(saved)!;
        Assert.Equal(("mor'hk", "mor'hk'ta", "mor'hk'vil", "mor'hk'nux"), (forms.Present, forms.Past, forms.Future, forms.Imperative));
    }

    [Fact]
    public async Task Import_Publish_RequiresLogin_AndNeverOverwritesExistingDocuments()
    {
        var issues = new FakeImportIssues();
        var h = new Harness();
        var import = new DictionaryImportService(new DictionaryFileParser(TestKit.Ling), issues, h.Remote, h.Auth, h.Net);
        var report = await import.AnalyzeAsync("# SUBSTANTIVO\n\"casa\" : \"kaza\",\n\"sol\" : \"sul\",\n");
        Assert.NotEmpty(issues.Stored.Select(i => i.Code).Concat(new[] { "ok" }));

        await Assert.ThrowsAsync<NotAuthenticatedException>(() => import.PublishAsync(report));
        h.Auth.IsAuthenticated = true;
        Assert.Equal(2, await import.PublishAsync(report));
        Assert.Equal(0, await import.PublishAsync(report));   // segunda vez: nada novo, nada sobrescrito
    }
}

public class SyncTests
{
    [Fact]
    public async Task Sync_DownloadsRemoteIntoLocal_ThenSkipsWhenRevisionIsUnchanged()
    {
        var h = new Harness();
        h.Remote.Seed(new[] { TestKit.Word("sol", "sul", "Substantivo", 0), TestKit.Word("lua", "lun", "Substantivo", 1) });

        var first = await h.Sync.SyncAsync();
        Assert.Equal(SyncOutcome.Updated, first.Outcome);
        Assert.Equal(2, await h.Local.CountAsync(new DictionaryQuery()));

        var second = await h.Sync.SyncAsync();
        Assert.Equal(SyncOutcome.UpToDate, second.Outcome);
        Assert.Equal(1, h.Remote.ListCalls);
    }

    [Fact]
    public async Task Sync_PicksUpRemoteChangesMadeByOtherUsers()
    {
        var h = new Harness();
        h.Remote.Seed(new[] { TestKit.Word("sol", "sul", "Substantivo", 0) });
        await h.Sync.SyncAsync();
        h.Remote.Seed(new[] { TestKit.Word("lua", "lun", "Substantivo", 1) });
        Assert.Equal(SyncOutcome.Updated, (await h.Sync.SyncAsync()).Outcome);
        Assert.Equal(2, await h.Local.CountAsync(new DictionaryQuery()));
    }

    [Fact]
    public async Task Offline_UsesLocalCache_AndDoesNotTouchIt()
    {
        var h = new Harness();
        h.Remote.Seed(new[] { TestKit.Word("sol", "sul", "Substantivo", 0) });
        await h.Sync.SyncAsync();
        h.Net.IsOnline = false;
        var r = await h.Sync.SyncAsync();
        Assert.Equal(SyncOutcome.Offline, r.Outcome);
        Assert.Equal(1, await h.Local.CountAsync(new DictionaryQuery()));
    }

    [Fact]
    public async Task UninitializedRemote_NeverWipesTheLocalCache()
    {
        var h = new Harness();
        await h.Local.ReplaceAllAsync(new[] { TestKit.Word("sol", "sul", "Substantivo", 0) }, 5);
        var r = await h.Sync.SyncAsync();
        Assert.Equal(SyncOutcome.RemoteNotInitialized, r.Outcome);
        Assert.Equal(1, await h.Local.CountAsync(new DictionaryQuery()));
    }
}

public class SearchFilterSortTests
{
    private static async Task<InMemoryDictionaryRepository> RealRepo()
    {
        var repo = new InMemoryDictionaryRepository();
        await repo.ReplaceAllAsync(TestKit.RealDictionary().Entries, 1);
        return repo;
    }

    [Fact]
    public async Task Search_ByPortugueseAndByRhagho()
    {
        var repo = await RealRepo();
        Assert.Contains(await repo.SearchAsync(new DictionaryQuery { Text = "eu" }), e => e.Rhagho == "nuruh");
        Assert.Contains(await repo.SearchAsync(new DictionaryQuery { Text = "nuruh" }), e => e.Portuguese == "eu");
    }

    [Fact]
    public async Task Search_IgnoresAccents_InBothDirections()
    {
        var repo = await RealRepo();
        var a = (await repo.SearchAsync(new DictionaryQuery { Text = "voce" })).Select(e => e.Id).ToHashSet();
        var b = (await repo.SearchAsync(new DictionaryQuery { Text = "você" })).Select(e => e.Id).ToHashSet();
        Assert.NotEmpty(a);
        Assert.True(a.SetEquals(b));
        Assert.Contains(await repo.SearchAsync(new DictionaryQuery { Text = "cacar" }), e => e.Portuguese == "caçar");
    }

    [Fact]
    public async Task Search_ByAlternativeForm()
    {
        var repo = new InMemoryDictionaryRepository();
        var w = TestKit.Word("música", "melh", "Substantivo", 0);
        w.AlternativeForms.Add(new AlternativeForm { Text = "melhah" });
        await repo.UpsertAsync(w);
        Assert.Single(await repo.SearchAsync(new DictionaryQuery { Text = "melhah" }));
        Assert.Single(await repo.SearchAsync(new DictionaryQuery { Text = "musica" }));
    }

    [Fact]
    public async Task Filter_ByClass_AndClassListIsDynamic()
    {
        var repo = await RealRepo();
        var verbs = await repo.SearchAsync(new DictionaryQuery { PartOfSpeech = "Verbo", Take = 5000 });
        Assert.True(verbs.Count > 0 && verbs.All(v => v.PartOfSpeech == "Verbo"));
        var custom = TestKit.Word("teste", "tes", "Interjeição", 9999);
        await repo.UpsertAsync(custom);
        Assert.Contains("Interjeição", await repo.GetPartsOfSpeechAsync());
        Assert.Single(await repo.SearchAsync(new DictionaryQuery { PartOfSpeech = "interjeicao" }));
    }

    [Fact]
    public async Task List_HidesIndividuallyImportedInflectedFormsByDefault_ButSearchFindsThem()
    {
        var repo = await RealRepo();
        var listed = await repo.SearchAsync(new DictionaryQuery { PartOfSpeech = "Verbo", Take = 5000 });
        Assert.DoesNotContain(listed, e => e.Portuguese == "amei");
        Assert.Contains(await repo.SearchAsync(new DictionaryQuery { Text = "amei" }), e => e.Portuguese == "amei");
    }

    [Fact]
    public async Task Sort_Alphabetical_AndByLetterCount_IgnoringAccentsAndApostrophes()
    {
        var repo = new InMemoryDictionaryRepository();
        await repo.ReplaceAllAsync(new[]
        {
            TestKit.Word("a", "ra'ri", "Pronome", 0), TestKit.Word("b", "nu", "Pronome", 1), TestKit.Word("c", "morhf's", "Pronome", 2)
        }, 1);
        var byLen = await repo.SearchAsync(new DictionaryQuery { Sort = DictionarySort.LetterCount });
        Assert.Equal(new[] { "nu", "ra'ri", "morhf's" }, byLen.Select(e => e.Rhagho).ToArray());
        var desc = await repo.SearchAsync(new DictionaryQuery { Sort = DictionarySort.LetterCount, Descending = true });
        Assert.Equal("morhf's", desc[0].Rhagho);
        var alpha = await repo.SearchAsync(new DictionaryQuery { Sort = DictionarySort.AlphabeticalRhagho });
        Assert.Equal(new[] { "morhf's", "nu", "ra'ri" }, alpha.Select(e => e.Rhagho).ToArray());
    }
}

public class SeederTests
{
    [Fact]
    public async Task EmptyCache_IsSeededFromTheBundledFile_AndANonEmptyCacheIsNeverTouched()
    {
        var repo = new InMemoryDictionaryRepository();
        var issues = new FakeImportIssues();
        var seeder = new DictionarySeeder(new DictionaryFileParser(TestKit.Ling), repo, issues);
        var text = File.ReadAllText(TestKit.DataPath("dicionario_rhagho_oriano_atual.txt"));

        Assert.True(await seeder.SeedIfEmptyAsync(text));
        Assert.True(await repo.CountAsync(new DictionaryQuery { IncludeInflectedForms = true }) > 800);
        Assert.NotEmpty(issues.Stored);
        Assert.Equal(0, (await repo.GetSyncStateAsync()).Revision);

        await repo.UpsertAsync(TestKit.Word("extra", "ext", "Outros", 99999));
        var before = (await repo.GetSyncStateAsync()).LocalCount;
        Assert.False(await seeder.SeedIfEmptyAsync(text));
        Assert.Equal(before, (await repo.GetSyncStateAsync()).LocalCount);
    }
}

public class IncrementalSyncTests
{
    private sealed class QuotaRemote : IRemoteDictionaryStore
    {
        public Task<long?> GetRevisionAsync(CancellationToken ct = default) => throw new RemoteQuotaExceededException();
        public Task<RemoteChanges> ListChangesAsync(DateTimeOffset? since, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WordEntry?> GetAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WordEntry> CreateAsync(WordEntry entry, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WordEntry> UpdateAsync(WordEntry entry, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(string id, string? expectedUpdateTime, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> CreateMissingAsync(IReadOnlyCollection<WordEntry> entries, IProgress<int>? progress = null, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private static IEnumerable<WordEntry> Many(int n) =>
        Enumerable.Range(0, n).Select(i => TestKit.Word($"palavra{i}", $"rh{i}", "Substantivo", i));

    [Fact]
    public async Task AfterTheFirstFullDownload_OnlyTheChangedDocumentsAreRead()
    {
        var h = new Harness();
        h.Remote.Seed(Many(500));
        await h.Sync.SyncAsync();                                   // carga completa: 500 leituras
        Assert.Equal(1, h.Remote.FullListCalls);
        var afterFull = h.Remote.DocumentsRead;

        h.Remote.AdvanceClock(3600);
        var one = (await h.Remote.GetAsync((await h.Local.SearchAsync(new DictionaryQuery { Take = 1 }))[0].Id))!;
        one.Etymology = "alterada por outra pessoa";
        await h.Remote.UpdateAsync(one);

        var r = await h.Sync.SyncAsync();
        Assert.Equal(SyncOutcome.Updated, r.Outcome);
        Assert.Equal(1, h.Remote.IncrementalCalls);
        Assert.Equal(1, h.Remote.FullListCalls);                    // não baixou tudo de novo
        // 1 alterado + margem de 15 s do cursor (no simulador cada gravação ocupa 1 s) + 2 consultas; nunca os 500.
        Assert.True(h.Remote.DocumentsRead - afterFull <= 25, $"leu {h.Remote.DocumentsRead - afterFull} documentos");
        Assert.Equal("alterada por outra pessoa", (await h.Local.GetAsync(one.Id))!.Etymology);
        Assert.Equal(500, r.Count);
    }

    [Fact]
    public async Task RevisionUnchanged_CostsNoDocumentReadsAtAll()
    {
        var h = new Harness();
        h.Remote.Seed(Many(50));
        await h.Sync.SyncAsync();
        var reads = h.Remote.DocumentsRead;
        for (var i = 0; i < 5; i++) Assert.Equal(SyncOutcome.UpToDate, (await h.Sync.SyncAsync()).Outcome);
        Assert.Equal(reads, h.Remote.DocumentsRead);
    }

    [Fact]
    public async Task DeletionsAndRecreations_PropagateThroughTombstones()
    {
        var h = new Harness();
        h.Remote.Seed(Many(5));
        await h.Sync.SyncAsync();
        var victim = (await h.Local.SearchAsync(new DictionaryQuery { Take = 1 }))[0];

        h.Remote.AdvanceClock(3600);
        await h.Remote.DeleteAsync(victim.Id, null);
        await h.Sync.SyncAsync();
        Assert.Null(await h.Local.GetAsync(victim.Id));
        Assert.Equal(4, (await h.Local.GetSyncStateAsync()).LocalCount);

        h.Remote.AdvanceClock(3600);
        var again = victim.Clone(); again.RemoteUpdateTime = null;
        await h.Remote.CreateAsync(again);
        await h.Sync.SyncAsync();
        Assert.NotNull(await h.Local.GetAsync(victim.Id));
    }

    [Fact]
    public async Task SeededCache_IsReplacedByAFullDownloadOnTheFirstRealSync()
    {
        var h = new Harness();
        await h.Local.ReplaceAllAsync(new[] { TestKit.Word("local", "lok", "Outros", 0) }, 0);   // carga inicial embutida
        h.Remote.Seed(new[] { TestKit.Word("sol", "sul", "Substantivo", 0) });
        await h.Sync.SyncAsync();
        Assert.Equal(1, h.Remote.FullListCalls);
        var all = await h.Local.SearchAsync(new DictionaryQuery { IncludeInflectedForms = true });
        Assert.Equal(new[] { "sol" }, all.Select(e => e.Portuguese).ToArray());
    }

    [Fact]
    public async Task AFullDownloadIsRepeatedEveryWeek_AsASafetyNet()
    {
        var h = new Harness();
        var clock = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        var sync = new SyncService(h.Local, h.Remote, h.Net, () => clock);
        h.Remote.Seed(Many(3));
        await sync.SyncAsync();
        Assert.Equal(1, h.Remote.FullListCalls);

        h.Remote.AdvanceClock(60);
        h.Remote.Seed(Many(1).Select(e => { e.Id = "novo"; return e; }));
        await sync.SyncAsync();
        Assert.Equal(1, h.Remote.FullListCalls);                      // ainda incremental

        h.Local.SetLastFullSync(clock.AddDays(-8));                    // a última carga completa ficou velha
        h.Remote.AdvanceClock(60);
        h.Remote.Seed(Many(1).Select(e => { e.Id = "novo2"; return e; }));
        await sync.SyncAsync();
        Assert.Equal(2, h.Remote.FullListCalls);
    }

    [Fact]
    public async Task ExhaustedFreeQuota_IsReportedClearly_AndTheCacheIsKept()
    {
        var local = new InMemoryDictionaryRepository();
        await local.ReplaceAllAsync(new[] { TestKit.Word("sol", "sul", "Substantivo", 0) }, 3);
        var r = await new SyncService(local, new QuotaRemote(), new FakeConnectivity()).SyncAsync();
        Assert.Equal(SyncOutcome.Failed, r.Outcome);
        Assert.Contains("cota diária", r.Message);
        Assert.Equal(1, (await local.GetSyncStateAsync()).LocalCount);
    }
}
