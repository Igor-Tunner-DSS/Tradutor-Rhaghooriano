using Rhaghoorian.Core.Errors;
using Rhaghoorian.Core.Models;
using Rhaghoorian.Core.Services;
using Rhaghoorian.Core.Importing;
using Rhaghoorian.Core.Tests.Support;
using Rhaghoorian.Presentation.Mvvm;
using Rhaghoorian.Presentation.Services;
using Rhaghoorian.Presentation.ViewModels;
using Xunit;

namespace Rhaghoorian.Presentation.Tests;

public class TranslatorViewModelTests
{
    [Fact]
    public async Task RequiredSentence_IsTranslated_SavedToHistory_WithoutAi()
    {
        var fx = new Fx();
        var vm = fx.Translator();
        vm.Direction = TranslationDirection.RhaghoToPortuguese;
        vm.InputText = "nuruh hoari'vil naurn ton ido's";
        await vm.TranslateCommand.ExecuteAsync();

        Assert.Equal("Eu irei caçar 5 vezes", vm.OutputText);
        Assert.Equal("Rhagho'oriano → Português", vm.DirectionTitle);
        Assert.Single(fx.History.Items);
        Assert.Equal(0, fx.Ai.Calls);
        Assert.False(vm.CanRefineWithAi);
    }

    [Fact]
    public async Task EmptyInput_ShowsAHintInsteadOfTranslating()
    {
        var fx = new Fx();
        var vm = fx.Translator();
        await vm.TranslateCommand.ExecuteAsync();
        Assert.Equal("Digite um texto para traduzir.", vm.StatusMessage);
        Assert.Empty(fx.History.Items);
    }

    [Fact]
    public async Task SwapDirection_CarriesTheResultAsNewInput_AndClearCopyWork()
    {
        var fx = new Fx();
        var vm = fx.Translator();
        vm.InputText = "eu";
        await vm.TranslateCommand.ExecuteAsync();
        Assert.Equal("nuruh", vm.OutputText);
        await vm.CopyCommand.ExecuteAsync();
        Assert.Equal("nuruh", fx.Clipboard.Text);

        await vm.SwapDirectionCommand.ExecuteAsync();
        Assert.Equal(TranslationDirection.RhaghoToPortuguese, vm.Direction);
        Assert.Equal("nuruh", vm.InputText);
        Assert.Equal("", vm.OutputText);

        await vm.ClearCommand.ExecuteAsync();
        Assert.Equal("", vm.InputText);
    }

    [Fact]
    public async Task UnknownWord_ShowsClickableSuggestions_AndApplyingOneRetranslates()
    {
        var fx = new Fx();
        var vm = fx.Translator();
        vm.Direction = TranslationDirection.RhaghoToPortuguese;
        vm.InputText = "nuruhh mor'hk";
        await vm.TranslateCommand.ExecuteAsync();

        Assert.True(vm.HasUnknownWords);
        Assert.Equal("1 palavra não está no dicionário.", vm.StatusMessage);
        var unknown = Assert.Single(vm.UnknownWords);
        var suggestion = Assert.Single(unknown.Suggestions.Where(s => s.Text == "nuruh"));

        await suggestion.ApplyCommand.ExecuteAsync();
        Assert.Equal("nuruh mor'hk", vm.InputText);
        Assert.Equal("Eu amo", vm.OutputText);
        Assert.False(vm.HasUnknownWords);
    }

    [Fact]
    public async Task AddToDictionary_AsGuest_AsksToLogIn_AndDoesNotOpenTheForm()
    {
        var fx = new Fx();
        var vm = fx.Translator();
        vm.Direction = TranslationDirection.RhaghoToPortuguese;
        vm.InputText = "zzzx";
        await vm.TranslateCommand.ExecuteAsync();

        await vm.UnknownWords[0].AddCommand.ExecuteAsync();
        Assert.Contains("Entrar", fx.Dialogs.Confirms);
        Assert.Equal(Routes.Login, fx.Nav.Visits.Single().Route);
    }

    [Fact]
    public async Task AddToDictionary_Authenticated_OpensTheCreateFormWithTheWordFilled()
    {
        var fx = new Fx();
        fx.Auth.IsAuthenticated = true;
        var vm = fx.Translator();
        vm.InputText = "xilofone";                      // Português → Rhagho'oriano
        await vm.TranslateCommand.ExecuteAsync();
        await vm.UnknownWords[0].AddCommand.ExecuteAsync();

        var (route, args) = fx.Nav.Visits.Single();
        Assert.Equal(Routes.WordEdit, route);
        Assert.Equal("xilofone", args!["word"]);
        Assert.Equal("portuguese", args["field"]);

        fx.Nav.Visits.Clear();
        vm.Direction = TranslationDirection.RhaghoToPortuguese;
        vm.InputText = "zzzx";
        await vm.TranslateCommand.ExecuteAsync();
        await vm.UnknownWords[0].AddCommand.ExecuteAsync();
        Assert.Equal("rhagho", fx.Nav.Visits.Single().Args!["field"]);
    }

    [Fact]
    public async Task Ai_IsOfferedOnlyWhenNeeded_AndItsAnswerIsShown()
    {
        var fx = new Fx();
        fx.Ai.TranslateResponse = "Tradução: Eu amo zzzx";
        var vm = fx.Translator();
        vm.Direction = TranslationDirection.RhaghoToPortuguese;
        vm.InputText = "nuruh mor'hk zzzx";
        await vm.TranslateCommand.ExecuteAsync();
        Assert.True(vm.CanRefineWithAi);
        Assert.Equal(0, fx.Ai.Calls);

        await vm.RefineWithAiCommand.ExecuteAsync();
        Assert.Equal("Eu amo zzzx", vm.OutputText);
        Assert.True(vm.UsedAi);
        Assert.Equal(1, fx.Ai.Calls);
    }

    [Fact]
    public async Task AmbiguousForms_ProduceReadableNotes()
    {
        var fx = new Fx();
        var vm = fx.Translator();
        vm.InputText = "amamos";
        await vm.TranslateCommand.ExecuteAsync();
        Assert.Contains(vm.Notes, n => n.Contains("mor'hk'ta"));
        Assert.True(vm.CanRefineWithAi);
    }

    [Fact]
    public async Task History_IsReusedThroughTheHub()
    {
        var fx = new Fx();
        var vm = fx.Translator();
        fx.Hub.Publish(new TranslationHistoryItem { SourceText = "nuruh", Direction = TranslationDirection.RhaghoToPortuguese, ResultText = "Eu" });
        Assert.Equal("nuruh", vm.InputText);
        Assert.Equal("Eu", vm.OutputText);
        Assert.Equal(TranslationDirection.RhaghoToPortuguese, vm.Direction);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task HistoryViewModel_ListsReusesDeletesAndClears()
    {
        var fx = new Fx();
        await fx.Workflow.TranslateAsync("eu", TranslationDirection.PortugueseToRhagho);
        await fx.Workflow.TranslateAsync("nuruh", TranslationDirection.RhaghoToPortuguese);
        var vm = new HistoryViewModel(fx.Workflow, fx.Hub, fx.Nav, fx.Dialogs, fx.Errors);
        await vm.OnAppearingAsync();
        Assert.Equal(2, vm.Items.Count);

        var reused = new List<TranslationHistoryItem>();
        fx.Hub.Requested += reused.Add;
        await vm.ReuseCommand.ExecuteAsync(vm.Items[0]);
        Assert.Single(reused);
        Assert.Equal(1, fx.Nav.Backs);

        await vm.DeleteCommand.ExecuteAsync(vm.Items[0]);
        Assert.Single(vm.Items);
        await vm.ClearCommand.ExecuteAsync();
        Assert.True(vm.IsEmpty);
        Assert.Contains("Limpar histórico", fx.Dialogs.Confirms);
    }
}

public class DictionaryViewModelTests
{
    [Fact]
    public async Task Search_Filter_Sort_AndDynamicClasses()
    {
        var fx = new Fx();
        var vm = fx.DictionaryVm();
        await vm.OnAppearingAsync();
        Assert.True(vm.Items.Count > 100);
        Assert.Contains("Verbo", vm.Classes);
        Assert.Equal("Todas", vm.Classes[0]);

        vm.SearchText = "voce";
        await Task.Delay(30);
        var voce = vm.Items.Count;
        Assert.True(voce > 0);
        vm.SearchText = "você";
        await Task.Delay(30);
        Assert.Equal(voce, vm.Items.Count);

        vm.SearchText = "";
        vm.SelectedClass = "Pronome";
        await Task.Delay(30);
        Assert.All(vm.Items, i => Assert.Equal("Pronome", i.PartOfSpeech));

        vm.SelectedSort = vm.SortOptions.Single(s => s.Sort == DictionarySort.LetterCount);
        await Task.Delay(30);
        var lens = vm.Items.Select(i => Rhaghoorian.Core.Text.TextNormalizer.CountLetters(i.Rhagho)).ToList();
        Assert.Equal(lens.OrderBy(x => x).ToList(), lens);

        // Nova classe vinda do banco aparece sem alterar código
        await fx.Local.UpsertAsync(TestKit.Word("oba", "obh", "Interjeição", 90000));
        await vm.RefreshCommand.ExecuteAsync();
        Assert.Contains("Interjeição", vm.Classes);
    }

    [Fact]
    public async Task VerbItems_ShowGeneratedForms()
    {
        var fx = new Fx();
        var vm = fx.DictionaryVm();
        vm.SearchText = "amar";
        await Task.Delay(30);
        var amar = vm.Items.First(i => i.Portuguese == "amar");
        Assert.True(amar.HasVerbInfo);
        Assert.Contains("Passado: mor'hk'ta", amar.VerbText);
        Assert.Contains("Imperativo: mor'hk'nux", amar.VerbText);
    }

    [Fact]
    public async Task Guest_CannotStartCreateEditOrDelete_AndIsOfferedLogin()
    {
        var fx = new Fx();
        var vm = fx.DictionaryVm();
        await vm.OnAppearingAsync();
        Assert.False(vm.IsAuthenticated);
        var item = vm.Items[0];

        await vm.NewWordCommand.ExecuteAsync();
        await vm.EditCommand.ExecuteAsync(item);
        await vm.DeleteCommand.ExecuteAsync(item);
        Assert.All(fx.Nav.Visits, v => Assert.Equal(Routes.Login, v.Route));
        Assert.Equal(3, fx.Nav.Visits.Count);
        Assert.Equal(0, fx.Remote.WriteCalls);
    }

    [Fact]
    public async Task Authenticated_CanCreateEditAndDelete()
    {
        var fx = new Fx();
        fx.Auth.IsAuthenticated = true;
        fx.Remote.Seed(TestKit.RealDictionary().Entries);
        await new SyncService(fx.Local, fx.Remote, fx.Net).SyncAsync();
        var vm = fx.DictionaryVm();
        await vm.OnAppearingAsync();
        var item = vm.Items.First(i => i.Portuguese == "eu");

        await vm.NewWordCommand.ExecuteAsync();
        await vm.EditCommand.ExecuteAsync(item);
        Assert.Equal(Routes.WordEdit, fx.Nav.Visits[0].Route);
        Assert.Equal(item.Id, fx.Nav.Visits[1].Args!["id"]);

        var before = vm.Items.Count;
        Assert.True(item.CanModify);
        await item.DeleteCommand!.ExecuteAsync();                  // botão do próprio item
        Assert.Equal(before - 1, vm.Items.Count);
        Assert.Null(await fx.Remote.GetAsync(item.Id));
    }
}

public class WordEditViewModelTests
{
    private static async Task<(Fx fx, WordEditViewModel vm)> Open(bool auth = true, IDictionary<string, object>? query = null)
    {
        var fx = new Fx();
        fx.Auth.IsAuthenticated = auth;
        var vm = fx.Edit();
        vm.ApplyQuery(query ?? new Dictionary<string, object>());
        await vm.LoadAsync();
        return (fx, vm);
    }

    [Fact]
    public async Task UnknownWordFlow_OpensTheFormPrefilled()
    {
        var (_, vm) = await Open(query: new Dictionary<string, object> { ["word"] = "xilofone", ["field"] = "portuguese" });
        Assert.Equal("xilofone", vm.Portuguese);
        Assert.Equal("", vm.Rhagho);
        Assert.False(vm.IsEditing);
        Assert.Equal("Nova palavra", vm.Title);

        var (_, vm2) = await Open(query: new Dictionary<string, object> { ["word"] = "zzzx", ["field"] = "rhagho" });
        Assert.Equal("zzzx", vm2.Rhagho);
    }

    [Fact]
    public async Task Create_PersistsAndReturns_AndGuestIsSentToLogin()
    {
        var (fx, vm) = await Open();
        vm.Portuguese = "xilofone"; vm.Rhagho = "xilo"; vm.PartOfSpeech = "Substantivo";
        vm.Etymology = "do grego"; vm.Example = "toco xilo";
        await vm.AddAlternativeCommand.ExecuteAsync();
        vm.AlternativeForms[0].Text = "xilon";
        await vm.AddAbbreviationCommand.ExecuteAsync();
        vm.Abbreviations[0].Text = "xil.";
        await vm.AddSynonymCommand.ExecuteAsync();
        vm.Synonyms[0].Text = "marimba";
        await vm.AddSynonymCommand.ExecuteAsync();                 // linha em branco: ignorada ao salvar
        await vm.Synonyms[1].RemoveCommand!.ExecuteAsync();         // remover pelo próprio item
        Assert.Single(vm.Synonyms);
        await vm.SaveCommand.ExecuteAsync();

        var saved = (await fx.Local.SearchAsync(new DictionaryQuery { Text = "xilon" })).Single();
        Assert.Equal("do grego", saved.Etymology);
        Assert.Equal("xilon", saved.AlternativeForms[0].Text);
        Assert.Equal("marimba", saved.Synonyms.Single().Text);
        Assert.Equal(1, fx.Nav.Backs);

        var (fx2, guest) = await Open(auth: false);
        guest.Portuguese = "a"; guest.Rhagho = "b"; guest.PartOfSpeech = "Outros";
        await guest.SaveCommand.ExecuteAsync();
        Assert.Equal(Routes.Login, fx2.Nav.Visits.Single().Route);
        Assert.Equal(0, fx2.Remote.WriteCalls);
    }

    [Fact]
    public async Task ValidationProblems_AreShownAsFriendlyMessages()
    {
        var (fx, vm) = await Open();
        await vm.SaveCommand.ExecuteAsync();
        Assert.Single(fx.Dialogs.Alerts);
        Assert.Contains("Rhagho'oriano", fx.Dialogs.Alerts[0].Message);
        Assert.Equal(0, fx.Nav.Backs);
    }

    [Fact]
    public async Task NewVerb_ShowsGeneratedFormsLive_AndWarnsAboutSuffixesInTheBase()
    {
        var (_, vm) = await Open();
        vm.PartOfSpeech = "Verbo";
        vm.Rhagho = "mor'hk";
        Assert.True(vm.HasVerbPreview);
        Assert.Equal("Presente: mor'hk\nPassado: mor'hk'ta\nFuturo: mor'hk'vil\nImperativo: mor'hk'nux", vm.VerbPreviewText);

        vm.Rhagho = "mor'hk'ta";
        Assert.Contains("não pode terminar", vm.VerbPreviewText);
        vm.PartOfSpeech = "Substantivo";
        Assert.False(vm.HasVerbPreview);
    }

    [Fact]
    public async Task AiSuggestions_AreOnlySuggestions_UntilTheAuthenticatedUserSaves()
    {
        var (fx, vm) = await Open();
        vm.Portuguese = "casa"; vm.Rhagho = "kaza"; vm.PartOfSpeech = "Substantivo";
        await vm.SuggestSynonymsCommand.ExecuteAsync();          // FakeAi devolve ["x"]
        var chip = Assert.Single(vm.AiSuggestions);
        Assert.Empty(vm.Synonyms);
        Assert.Equal(0, fx.Remote.WriteCalls);                    // nada persistido

        await chip.AcceptCommand.ExecuteAsync();
        Assert.Single(vm.Synonyms);
        Assert.Empty(vm.AiSuggestions);
        Assert.Equal(0, fx.Remote.WriteCalls);                    // continua só no formulário

        await vm.SaveCommand.ExecuteAsync();
        var saved = (await fx.Local.SearchAsync(new DictionaryQuery { Text = "kaza" })).Single();
        Assert.True(saved.Synonyms.Single().AcceptedFromAi);
    }

    [Fact]
    public async Task AiConjugationCheck_NeverReplacesTheOfficialForms()
    {
        var (fx, vm) = await Open();
        vm.Portuguese = "amar"; vm.Rhagho = "mor'hk"; vm.PartOfSpeech = "Verbo";
        await vm.CheckConjugationCommand.ExecuteAsync();          // FakeAi devolve a,b,c,d
        Assert.Contains("divergiu", vm.ConjugationCheckText);
        Assert.Contains("mor'hk'vil", vm.VerbPreviewText);
        await vm.SaveCommand.ExecuteAsync();
        var saved = (await fx.Local.SearchAsync(new DictionaryQuery { Text = "mor'hk", Take = 2000 })).Single(e => e.CreatedBy == "user-1");
        Assert.Equal("mor'hk", saved.Verb!.Base);
        Assert.True(saved.Verb.IsInfinitive);
    }

    [Fact]
    public async Task ConflictingEdit_OffersReload_AndNothingIsOverwritten()
    {
        var fx = new Fx();
        fx.Auth.IsAuthenticated = true;
        fx.Remote.Seed(TestKit.RealDictionary().Entries);
        await new SyncService(fx.Local, fx.Remote, fx.Net).SyncAsync();
        var target = (await fx.Local.SearchAsync(new DictionaryQuery { Text = "eu" })).First(e => e.Portuguese == "eu");

        var vm = fx.Edit();
        vm.ApplyQuery(new Dictionary<string, object> { ["id"] = target.Id });
        await vm.LoadAsync();
        Assert.True(vm.IsEditing);

        var theirs = (await fx.Remote.GetAsync(target.Id))!;       // outra pessoa altera antes
        theirs.Etymology = "versão da outra pessoa";
        await fx.Remote.UpdateAsync(theirs);

        vm.Example = "minha versão";
        await vm.SaveCommand.ExecuteAsync();                       // confirm = true => recarrega
        Assert.Equal("versão da outra pessoa", (await fx.Remote.GetAsync(target.Id))!.Etymology);
        Assert.Null((await fx.Remote.GetAsync(target.Id))!.Example);
        Assert.Equal("versão da outra pessoa", vm.Etymology);      // formulário recarregado
        Assert.Equal("", vm.Example);
        Assert.Contains("Conflito de edição", fx.Dialogs.Confirms);

        vm.Example = "minha versão";
        await vm.SaveCommand.ExecuteAsync();                       // agora com a versão certa
        Assert.Equal("minha versão", (await fx.Remote.GetAsync(target.Id))!.Example);
    }
}

public class ShellLoginStartupImportTests
{
    private sealed class RejectingAuth : Rhaghoorian.Core.Abstractions.IAuthService
    {
        public bool IsAuthenticated => false;
        public string? UserId => null;
        public string? Email => null;
        public event EventHandler? AuthStateChanged { add { } remove { } }
        public Task<bool> TryRestoreSessionAsync(CancellationToken ct = default) => Task.FromResult(false);
        public Task SignInAsync(string email, string password, CancellationToken ct = default) => throw new InvalidCredentialsException();
        public Task SignOutAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetIdTokenAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
    }

    [Fact]
    public async Task Login_Success_ClearsThePasswordAndGoesBack_Failure_ShowsInlineMessage()
    {
        var fx = new Fx();
        var ok = new LoginViewModel(fx.Auth, fx.Nav, fx.Errors) { Email = "a@b.c", Password = "segredo" };
        await ok.SignInCommand.ExecuteAsync();
        Assert.True(fx.Auth.IsAuthenticated);
        Assert.Equal("", ok.Password);
        Assert.Equal(1, fx.Nav.Backs);

        var bad = new LoginViewModel(new RejectingAuth(), fx.Nav, fx.Errors) { Email = "a@b.c", Password = "errada" };
        await bad.SignInCommand.ExecuteAsync();
        Assert.True(bad.HasError);
        Assert.Equal("E-mail ou senha incorretos.", bad.ErrorMessage);
        Assert.Empty(fx.Dialogs.Alerts);
    }

    [Fact]
    public async Task Shell_ReflectsSessionAndThemeAndLogout()
    {
        var fx = new Fx();
        var vm = new ShellViewModel(fx.Auth, fx.Theme, fx.Startup, fx.Nav, fx.Dialogs, fx.Ui, fx.Errors);
        Assert.Equal("Entrar", vm.AuthButtonText);
        Assert.Equal("Visitante", vm.UserLabel);
        await vm.AuthCommand.ExecuteAsync();
        Assert.Equal(Routes.Login, fx.Nav.Visits.Single().Route);

        await fx.Auth.SignInAsync("x", "y");
        Assert.Equal("Sair", vm.AuthButtonText);
        await vm.AuthCommand.ExecuteAsync();
        Assert.False(fx.Auth.IsAuthenticated);

        Assert.Equal("Tema escuro", vm.ThemeButtonText);
        await vm.ToggleThemeCommand.ExecuteAsync();
        Assert.Equal("Tema claro", vm.ThemeButtonText);
        await vm.SyncNowCommand.ExecuteAsync();
        Assert.Equal(1, fx.Startup.SyncCalls);
    }

    [Fact]
    public async Task Startup_SeedsLocalCache_ThenSyncs_AndRunsOnlyOnce()
    {
        var fx = new Fx(seed: false);
        fx.Remote.Seed(new[] { TestKit.Word("sol", "sul", "Substantivo", 0) });
        var seeder = new DictionarySeeder(new DictionaryFileParser(TestKit.Ling), fx.Local, new FakeImportIssues());
        var sync = new CountingSync(new SyncService(fx.Local, fx.Remote, fx.Net));
        var startup = new StartupCoordinator(fx.Local, seeder, new FakeBundled(), fx.Net, fx.Auth, sync, fx.Theme, fx.Errors);

        await Task.WhenAll(startup.EnsureReadyAsync(), startup.EnsureReadyAsync());
        await startup.EnsureReadyAsync();
        Assert.True(startup.IsReady);
        Assert.Equal(1, sync.Calls);
        Assert.Equal(1, fx.Theme.Inits);
        Assert.Equal("Sincronizado — 1 registros", startup.Status);   // o remoto (1 item) substituiu a carga inicial
    }

    [Fact]
    public async Task Startup_Offline_UsesTheBundledCacheAndSaysSo()
    {
        var fx = new Fx(seed: false);
        fx.Net.IsOnline = false;
        var seeder = new DictionarySeeder(new DictionaryFileParser(TestKit.Ling), fx.Local, new FakeImportIssues());
        var startup = new StartupCoordinator(fx.Local, seeder, new FakeBundled(), fx.Net, fx.Auth, new SyncService(fx.Local, fx.Remote, fx.Net), fx.Theme, fx.Errors);
        await startup.EnsureReadyAsync();
        Assert.StartsWith("Offline", startup.Status);
        Assert.True((await fx.Local.GetSyncStateAsync()).LocalCount > 800);
        Assert.Empty(fx.Dialogs.Alerts);
    }

    [Fact]
    public async Task Startup_LocalDatabaseFailure_IsReportedClearly()
    {
        var fx = new Fx(seed: false);
        var broken = new ThrowingSeeder();
        var startup = new StartupCoordinator(fx.Local, broken, new FakeBundled(), fx.Net, fx.Auth, new SyncService(fx.Local, fx.Remote, fx.Net), fx.Theme, fx.Errors);
        await startup.EnsureReadyAsync();
        Assert.Equal("Banco local indisponível", startup.Status);
        Assert.Contains("banco de dados local", fx.Dialogs.Alerts.Single().Message);
    }

    private sealed class ThrowingSeeder : IDictionarySeeder
    {
        public Task<bool> SeedIfEmptyAsync(string text, CancellationToken ct = default) => throw new LocalDatabaseException();
    }

    private sealed class CountingSync : Rhaghoorian.Core.Abstractions.ISyncService
    {
        private readonly Rhaghoorian.Core.Abstractions.ISyncService _inner;
        public int Calls { get; private set; }
        public CountingSync(Rhaghoorian.Core.Abstractions.ISyncService inner) => _inner = inner;
        public Task<SyncResult> SyncAsync(CancellationToken ct = default) { Calls++; return _inner.SyncAsync(ct); }
    }

    [Fact]
    public async Task Import_AnalyzeShowsTheValidationReport_PublishNeedsLoginAndConfirmation()
    {
        var fx = new Fx(seed: false);
        var issues = new FakeImportIssues();
        var import = new DictionaryImportService(new DictionaryFileParser(TestKit.Ling), issues, fx.Remote, fx.Auth, fx.Net);
        var vm = new ImportViewModel(import, new FakePicker(), new FakeBundled(), fx.Auth, fx.Startup, fx.Dialogs, fx.Nav, fx.Errors);

        await vm.AnalyzeBundledCommand.ExecuteAsync();
        Assert.True(vm.HasReport);
        Assert.Contains("entradas lidas", vm.Summary);
        Assert.Contains("Nada foi descartado", vm.Summary);
        Assert.Contains(vm.Issues, i => i.Code == "DuplicataExata");
        vm.SeverityFilter = "Erros";
        await vm.ReloadIssuesCommand.ExecuteAsync();
        Assert.All(vm.Issues, i => Assert.Equal("Erro", i.Severity));
        vm.SeverityFilter = "Avisos";
        await vm.ReloadIssuesCommand.ExecuteAsync();
        Assert.True(vm.Issues.Count > 0);
        Assert.All(vm.Issues, i => Assert.Equal("Aviso", i.Severity));
        Assert.Contains("Avisos", vm.SeverityOptions);

        await vm.PublishCommand.ExecuteAsync();                    // visitante
        Assert.Equal(Routes.Login, fx.Nav.Visits.Single().Route);
        Assert.Equal(0, fx.Remote.WriteCalls);

        fx.Auth.IsAuthenticated = true;
        await vm.PublishCommand.ExecuteAsync();
        Assert.True(fx.Remote.WriteCalls > 0);
        Assert.Equal(1, fx.Startup.SyncCalls);
        Assert.Contains("publicados", vm.Summary);

        await vm.PublishCommand.ExecuteAsync();                    // segunda vez: nada novo
        Assert.Contains("Nada novo", vm.Summary);
    }

    [Fact]
    public async Task ErrorHandler_ShowsNonTechnicalMessages_AndSkipsCancellation()
    {
        var fx = new Fx();
        await fx.Errors.HandleAsync(new OfflineException());
        await fx.Errors.HandleAsync(new InvalidOperationException("NullReference at Foo.Bar() secret=abc"));
        await fx.Errors.HandleAsync(new OperationCanceledException());
        Assert.Equal(2, fx.Dialogs.Alerts.Count);
        Assert.Equal("Conexão", fx.Dialogs.Alerts[0].Title);
        Assert.DoesNotContain("secret", fx.Dialogs.Alerts[1].Message);
        Assert.Equal("Ocorreu um erro inesperado. Tente novamente.", fx.Dialogs.Alerts[1].Message);
    }

    [Fact]
    public async Task AsyncCommand_BlocksReentrancyAndRoutesErrors()
    {
        var fx = new Fx();
        var gate = new TaskCompletionSource();
        var runs = 0;
        var cmd = AsyncCommand.Create(async () => { runs++; await gate.Task; }, fx.Errors);
        var first = cmd.ExecuteAsync();
        await cmd.ExecuteAsync();                                   // ignorado enquanto roda
        Assert.False(cmd.CanExecute(null));
        gate.SetResult();
        await first;
        Assert.Equal(1, runs);
        Assert.True(cmd.CanExecute(null));

        var failing = AsyncCommand.Create(() => throw new ConflictException(), fx.Errors);
        await failing.ExecuteAsync();
        Assert.Equal("Conflito de edição", fx.Dialogs.Alerts.Last().Title);
    }
}


public class AiSettingsViewModelTests
{
    private const string Key = "sk-or-v1-0123456789abcdef0123456789abcdef";

    private static (AiSettingsViewModel vm, FakeKeyStore store, FakeKeyManager mgr, FakeLauncher launcher, Fx fx) Build()
    {
        var fx = new Fx();
        var store = new FakeKeyStore();
        var mgr = new FakeKeyManager(store);
        var launcher = new FakeLauncher();
        var usage = new Rhaghoorian.Core.Ai.SettingsAiUsageLimiter(new FakeSettings(), 20);
        return (new AiSettingsViewModel(mgr, usage, launcher, fx.Dialogs, fx.Errors), store, mgr, launcher, fx);
    }

    [Fact]
    public async Task WithoutKey_ShowsThatAiIsOff_AndUsage()
    {
        var (vm, _, _, _, _) = Build();
        await vm.OnAppearingAsync();
        Assert.False(vm.HasKey);
        Assert.Contains("Nenhuma chave", vm.StatusText);
        Assert.Contains("0 de 20", vm.UsageText);
        Assert.Contains("openrouter.ai/keys", vm.HelpText);
        Assert.Contains("50 por dia", vm.LimitsText);
    }

    [Fact]
    public async Task Save_StoresTheKey_ClearsTheInput_AndShowsOnlyAMaskedKey()
    {
        var (vm, store, mgr, _, _) = Build();
        mgr.FreeTier = true;
        vm.ApiKeyInput = Key;
        await vm.SaveCommand.ExecuteAsync();

        Assert.Equal(Key, store.Key);
        Assert.Equal("", vm.ApiKeyInput);
        Assert.True(vm.HasKey);
        Assert.Equal("Chave configurada: sk-or-…cdef", vm.StatusText);
        Assert.DoesNotContain("0123456789", vm.StatusText + vm.FeedbackMessage + vm.UsageText);
        Assert.True(vm.FeedbackIsSuccess);
        Assert.Contains("confirmada", vm.FeedbackMessage);
        Assert.Contains("50 por dia", vm.FeedbackMessage);
    }

    [Fact]
    public async Task BadKeys_AreExplainedInlineWithoutPopups_AndNothingIsSaved()
    {
        var (vm, store, mgr, _, fx) = Build();
        vm.ApiKeyInput = "AIzaSyFirebaseKey-0123456789abcdef";
        await vm.SaveCommand.ExecuteAsync();
        Assert.True(vm.FeedbackIsError);
        Assert.Contains("sk-or-", vm.FeedbackMessage);

        vm.ApiKeyInput = Key;
        mgr.RemoteFailure = new AiKeyInvalidException();
        await vm.SaveCommand.ExecuteAsync();
        Assert.True(vm.FeedbackIsError);
        Assert.Contains("recusada", vm.FeedbackMessage);
        Assert.Null(store.Key);
        Assert.Empty(fx.Dialogs.Alerts);
    }

    [Fact]
    public async Task Unverified_Remove_AndOpenKeysPage()
    {
        var (vm, store, mgr, launcher, fx) = Build();
        mgr.Verified = false;
        vm.ApiKeyInput = Key;
        await vm.SaveCommand.ExecuteAsync();
        Assert.Contains("não foi possível confirmá-la", vm.FeedbackMessage);

        await vm.RemoveCommand.ExecuteAsync();
        Assert.Contains("Remover chave", fx.Dialogs.Confirms);
        Assert.Null(store.Key);
        Assert.False(vm.HasKey);

        await vm.OpenKeysPageCommand.ExecuteAsync();
        Assert.Equal("https://openrouter.ai/keys", launcher.Opened.Single());
    }

    [Fact]
    public async Task RefineWithAi_WithoutAKey_OffersTheSettingsScreen()
    {
        var fx = new Fx();
        var throwing = new ThrowingAi(new AiKeyMissingException());
        var wf = new Rhaghoorian.Core.Services.TranslationWorkflow(
            new Rhaghoorian.Core.Translation.TranslationService(fx.Local, TestKit.Ling, new Rhaghoorian.Core.Translation.SuggestionService(fx.Local, TestKit.Ling)),
            fx.History, throwing, new Rhaghoorian.Core.Ai.AiContextBuilder(fx.Local, TestKit.Ling), fx.Local, TestKit.Ling, fx.Net);
        var vm = new TranslatorViewModel(wf, fx.Startup, fx.Clipboard, fx.Nav, fx.Auth, fx.Dialogs, fx.Hub, fx.Errors);
        vm.Direction = TranslationDirection.RhaghoToPortuguese;
        vm.InputText = "nuruh zzzx";
        await vm.TranslateCommand.ExecuteAsync();
        Assert.True(vm.CanRefineWithAi);

        await vm.RefineWithAiCommand.ExecuteAsync();
        Assert.Contains("Chave de IA", fx.Dialogs.Confirms);
        Assert.Equal(Routes.AiSettings, fx.Nav.Visits.Single().Route);
        Assert.Empty(fx.Dialogs.Alerts);
    }

    [Fact]
    public async Task ShellButton_OpensTheSettings()
    {
        var fx = new Fx();
        var shell = new ShellViewModel(fx.Auth, fx.Theme, fx.Startup, fx.Nav, fx.Dialogs, fx.Ui, fx.Errors);
        await shell.OpenAiSettingsCommand.ExecuteAsync();
        Assert.Equal(Routes.AiSettings, fx.Nav.Visits.Single().Route);
    }

    private sealed class ThrowingAi : Rhaghoorian.Core.Ai.IAiClient
    {
        private readonly Exception _ex;
        public ThrowingAi(Exception ex) => _ex = ex;
        public Task<string> TranslateAsync(Rhaghoorian.Core.Ai.AiTranslationRequest request, CancellationToken ct = default) => throw _ex;
        public Task<VerbConjugation> ConjugateAsync(Rhaghoorian.Core.Ai.AiConjugationRequest request, CancellationToken ct = default) => throw _ex;
        public Task<IReadOnlyList<string>> SuggestSynonymsAsync(Rhaghoorian.Core.Ai.AiSynonymRequest request, CancellationToken ct = default) => throw _ex;
    }
}
