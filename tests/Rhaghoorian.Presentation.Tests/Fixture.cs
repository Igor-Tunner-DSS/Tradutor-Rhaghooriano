using Microsoft.Extensions.Logging.Abstractions;
using Rhaghoorian.Core.Abstractions;
using Rhaghoorian.Core.Ai;
using Rhaghoorian.Core.Importing;
using Rhaghoorian.Core.Models;
using Rhaghoorian.Core.Services;
using Rhaghoorian.Core.Tests.Support;
using Rhaghoorian.Core.Translation;
using Rhaghoorian.Presentation.Services;
using Rhaghoorian.Presentation.ViewModels;

namespace Rhaghoorian.Presentation.Tests;

public sealed class FakeDialogs : IDialogService
{
    public bool ConfirmAnswer { get; set; } = true;
    public List<(string Title, string Message)> Alerts { get; } = new();
    public List<string> Confirms { get; } = new();
    public Task AlertAsync(string title, string message) { Alerts.Add((title, message)); return Task.CompletedTask; }
    public Task<bool> ConfirmAsync(string title, string message, string accept = "Sim", string cancel = "Cancelar") { Confirms.Add(title); return Task.FromResult(ConfirmAnswer); }
}

public sealed class FakeNav : INavigationService
{
    public List<(string Route, IDictionary<string, object>? Args)> Visits { get; } = new();
    public int Backs { get; private set; }
    public Task GoToAsync(string route, IDictionary<string, object>? parameters = null) { Visits.Add((route, parameters)); return Task.CompletedTask; }
    public Task GoBackAsync() { Backs++; return Task.CompletedTask; }
}

public sealed class FakeClipboard : IClipboardService
{
    public string? Text { get; private set; }
    public Task SetTextAsync(string text) { Text = text; return Task.CompletedTask; }
}

public sealed class FakeTheme : IThemeService
{
    public ThemeMode Current { get; private set; } = ThemeMode.Light;
    public int Inits { get; private set; }
    public event EventHandler? Changed;
    public Task InitializeAsync(CancellationToken ct = default) { Inits++; return Task.CompletedTask; }
    public Task ToggleAsync(CancellationToken ct = default) { Current = Current == ThemeMode.Light ? ThemeMode.Dark : ThemeMode.Light; Changed?.Invoke(this, EventArgs.Empty); return Task.CompletedTask; }
}

public sealed class ImmediateUi : IUiDispatcher { public void Post(Action action) => action(); }

public sealed class FakeStartup : IStartupCoordinator
{
    public string Status => "ok";
    public bool IsReady => true;
    public int SyncCalls { get; private set; }
    public event EventHandler? StatusChanged { add { } remove { } }
    public Task EnsureReadyAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task<SyncResult> SyncNowAsync(CancellationToken ct = default) { SyncCalls++; return Task.FromResult(new SyncResult(SyncOutcome.UpToDate, 1)); }
}

public sealed class FakeBundled : IBundledDataService
{
    public string GetInitialDictionaryText() => File.ReadAllText(TestKit.DataPath("dicionario_rhagho_oriano_atual.txt"));
}

public sealed class FakePicker : IFilePickerService
{
    public string? Content { get; set; }
    public Task<string?> PickTextFileAsync(string title, CancellationToken ct = default) => Task.FromResult(Content);
}

/// <summary>Monta o grafo completo (serviços reais do núcleo sobre repositórios em memória + remoto simulado).</summary>
public sealed class Fx
{
    public readonly InMemoryDictionaryRepository Local = new();
    public readonly FakeRemoteStore Remote = new();
    public readonly FakeAuth Auth = new();
    public readonly FakeConnectivity Net = new();
    public readonly FakeAi Ai = new();
    public readonly FakeHistory History = new();
    public readonly FakeDialogs Dialogs = new();
    public readonly FakeNav Nav = new();
    public readonly FakeClipboard Clipboard = new();
    public readonly FakeTheme Theme = new();
    public readonly ImmediateUi Ui = new();
    public readonly FakeStartup Startup = new();
    public readonly TranslationReuseHub Hub = new();
    public readonly IErrorHandler Errors;
    public readonly DictionaryService Dictionary;
    public readonly TranslationWorkflow Workflow;
    public readonly WordAiAssistant AiAssistant;

    public Fx(bool seed = true)
    {
        Errors = new ErrorHandler(Dialogs, NullLogger<ErrorHandler>.Instance);
        if (seed) Local.ReplaceAllAsync(TestKit.RealDictionary().Entries, 1).GetAwaiter().GetResult();
        Dictionary = new DictionaryService(Local, Remote, Auth, Net, TestKit.Ling);
        var translator = new TranslationService(Local, TestKit.Ling, new SuggestionService(Local, TestKit.Ling));
        Workflow = new TranslationWorkflow(translator, History, Ai, new AiContextBuilder(Local, TestKit.Ling), Local, TestKit.Ling, Net);
        AiAssistant = new WordAiAssistant(Ai, Auth, Net, TestKit.Ling);
    }

    public TranslatorViewModel Translator() => new(Workflow, Startup, Clipboard, Nav, Auth, Dialogs, Hub, Errors);
    public DictionaryViewModel DictionaryVm() => new(Dictionary, Auth, Startup, Nav, Dialogs, Ui, Errors) { DebounceMilliseconds = 0 };
    public WordEditViewModel Edit() => new(Dictionary, AiAssistant, Auth, Nav, Dialogs, TestKit.Ling, Errors);
}
