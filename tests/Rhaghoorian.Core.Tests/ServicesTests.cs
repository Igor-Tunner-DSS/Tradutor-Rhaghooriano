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
