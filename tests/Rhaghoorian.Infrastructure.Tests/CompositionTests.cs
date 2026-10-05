using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Rhaghoorian.Core.Abstractions;
using Rhaghoorian.Core.Ai;
using Rhaghoorian.Core.Models;
using Rhaghoorian.Core.Services;
using Rhaghoorian.Infrastructure.Networking;
using Rhaghoorian.Infrastructure.SQLite;
using Rhaghoorian.Presentation;
using Rhaghoorian.Presentation.Services;
using Rhaghoorian.Presentation.ViewModels;
using Xunit;

namespace Rhaghoorian.Infrastructure.Tests;

/// <summary>
/// Monta o MESMO grafo de serviços do aplicativo (Infraestrutura + Apresentação), trocando apenas os serviços de plataforma
/// do MAUI por equivalentes simples. Garante que nada fique sem registro e roda a abertura do app com SQLite real.
/// </summary>
public class CompositionTests
{
    private sealed class Dialogs : IDialogService
    {
        public Task AlertAsync(string title, string message) => Task.CompletedTask;
        public Task<bool> ConfirmAsync(string title, string message, string accept = "Sim", string cancel = "Cancelar") => Task.FromResult(true);
    }
    private sealed class Nav : INavigationService
    {
        public Task GoToAsync(string route, IDictionary<string, object>? parameters = null) => Task.CompletedTask;
        public Task GoBackAsync() => Task.CompletedTask;
    }
    private sealed class Clip : IClipboardService { public Task SetTextAsync(string text) => Task.CompletedTask; }
    private sealed class Ui : IUiDispatcher { public void Post(Action action) => action(); }
    private sealed class Picker : IFilePickerService { public Task<string?> PickTextFileAsync(string title, CancellationToken ct = default) => Task.FromResult<string?>(null); }
    private sealed class Bundled : IBundledDataService
    {
        public string GetInitialDictionaryText() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "dicionario_rhagho_oriano_atual.txt"));
    }
    private sealed class Offline : IConnectivityService { public bool IsOnline => false; }
    private sealed class Theme : IThemeService
    {
        private readonly ISettingsStore _settings;
        public Theme(ISettingsStore settings) => _settings = settings;
        public ThemeMode Current { get; private set; }
        public event EventHandler? Changed;
        public async Task InitializeAsync(CancellationToken ct = default) => Current = await _settings.GetAsync("theme", ct) == "dark" ? ThemeMode.Dark : ThemeMode.Light;
        public async Task ToggleAsync(CancellationToken ct = default)
        {
            Current = Current == ThemeMode.Dark ? ThemeMode.Light : ThemeMode.Dark;
            await _settings.SetAsync("theme", Current == ThemeMode.Dark ? "dark" : "light", ct);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private static (IServiceProvider sp, TempDb tmp) Build(string? dbPath = null)
    {
        var tmp = new TempDb();
        var config = AppConfiguration.Build("{ \"Firebase\": { \"ProjectId\": \"demo\", \"ApiKey\": \"k\" }, \"Ai\": { \"Endpoint\": \"https://x.test/ai\" } }");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRhaghoInfrastructure(config, dbPath ?? tmp.FilePath);
        services.AddRhaghoPresentation();
        services.AddSingleton<ISecureTokenStore, MemoryTokenStore>();
        services.AddSingleton<IConnectivityService, Offline>();
        services.AddSingleton<IDialogService, Dialogs>();
        services.AddSingleton<INavigationService, Nav>();
        services.AddSingleton<IClipboardService, Clip>();
        services.AddSingleton<IUiDispatcher, Ui>();
        services.AddSingleton<IFilePickerService, Picker>();
        services.AddSingleton<IBundledDataService, Bundled>();
        services.AddSingleton<IThemeService, Theme>();
        return (services.BuildServiceProvider(), tmp);
    }

    [Fact]
    public void EveryViewModelAndServiceTheAppNeeds_CanBeResolved()
    {
        var (sp, tmp) = Build();
        using var _tmp = tmp;
        _ = sp.GetRequiredService<ShellViewModel>();
        _ = sp.GetRequiredService<TranslatorViewModel>();
        _ = sp.GetRequiredService<HistoryViewModel>();
        _ = sp.GetRequiredService<LoginViewModel>();
        _ = sp.GetRequiredService<ModificationViewModel>();
        _ = sp.GetRequiredService<WordEditViewModel>();
        _ = sp.GetRequiredService<IStartupCoordinator>();
        _ = sp.GetRequiredService<IAiClient>();
        _ = sp.GetRequiredService<IRemoteDictionaryStore>();
        Assert.Same(sp.GetRequiredService<ShellViewModel>(), sp.GetRequiredService<ShellViewModel>());
        Assert.NotSame(sp.GetRequiredService<TranslatorViewModel>(), sp.GetRequiredService<TranslatorViewModel>());
        Assert.Same(sp.GetRequiredService<IDictionaryRepository>(), sp.GetRequiredService<IDictionaryLookup>());
        Assert.Same(sp.GetRequiredService<IAuthService>(), sp.GetRequiredService<IAuthService>());
    }

    [Fact]
    public async Task FirstLaunchOffline_SeedsSqlite_AndTheRealTranslatorWorksThroughTheViewModel()
    {
        var (sp, tmp) = Build();
        using var _tmp = tmp;
        var startup = sp.GetRequiredService<IStartupCoordinator>();
        await startup.EnsureReadyAsync();
        Assert.True(startup.IsReady);
        Assert.StartsWith("Offline", startup.Status);

        var vm = sp.GetRequiredService<TranslatorViewModel>();
        vm.Direction = TranslationDirection.RhaghoToPortuguese;
        vm.InputText = "nuruh hoari'vil naurn ton ido's";
        await vm.TranslateCommand.ExecuteAsync();
        Assert.Equal("Eu irei caçar 5 vezes", vm.OutputText);

        vm.Direction = TranslationDirection.PortugueseToRhagho;
        vm.InputText = "amar amei amarei ame";
        await vm.TranslateCommand.ExecuteAsync();
        Assert.Equal("mor'hk mor'hk'ta mor'hk'vil mor'hk'nux", vm.OutputText);

        // O histórico foi gravado no SQLite local e sobrevive a "fechar e reabrir" (novo contêiner, mesmo arquivo).
        var (sp2, _) = Build(tmp.FilePath);
        var history = new HistoryViewModel(sp2.GetRequiredService<ITranslationWorkflow>(), sp2.GetRequiredService<TranslationReuseHub>(),
            sp2.GetRequiredService<INavigationService>(), sp2.GetRequiredService<IDialogService>(), sp2.GetRequiredService<IErrorHandler>());
        await history.OnAppearingAsync();
        Assert.Equal(2, history.Items.Count);
        Assert.Equal("Eu irei caçar 5 vezes", history.Items.Last().Result);
    }

    [Fact]
    public async Task ThemePreference_IsPersistedLocally_AndGuestCannotModifyAnything()
    {
        var (sp, tmp) = Build();
        using var _tmp = tmp;
        var shell = sp.GetRequiredService<ShellViewModel>();
        await shell.ToggleThemeCommand.ExecuteAsync();
        Assert.Equal("dark", await sp.GetRequiredService<ISettingsStore>().GetAsync("theme"));

        await sp.GetRequiredService<IStartupCoordinator>().EnsureReadyAsync();
        var dictionary = sp.GetRequiredService<IDictionaryService>();
        await Assert.ThrowsAsync<Rhaghoorian.Core.Errors.NotAuthenticatedException>(() =>
            dictionary.CreateAsync(new WordEntry { Rhagho = "a", Portuguese = "b", PartOfSpeech = "Outros" }));
        Assert.False(sp.GetRequiredService<IAuthService>().IsAuthenticated);
    }
}
