using Microsoft.Extensions.Logging;
using Rhaghoorian.App.Services;
using Rhaghoorian.App.Views;
using Rhaghoorian.Core.Abstractions;
using Rhaghoorian.Infrastructure;
using Rhaghoorian.Infrastructure.Networking;
using Rhaghoorian.Presentation;
using Rhaghoorian.Presentation.Services;

namespace Rhaghoorian.App;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();
#if DEBUG
        builder.Logging.AddDebug();
#endif

        var config = LoadConfiguration();
        var services = builder.Services;

        // Núcleo + SQLite + Firebase + IA (ver Rhaghoorian.Infrastructure) e ViewModels (ver Rhaghoorian.Presentation).
        services.AddRhaghoInfrastructure(config, Path.Combine(FileSystem.AppDataDirectory, "rhagho.db"));
        services.AddRhaghoPresentation();

        // Serviços de plataforma (Windows/MAUI)
        services.AddSingleton<ISecureTokenStore, DpapiTokenStore>();
        services.AddSingleton<IConnectivityService, MauiConnectivityService>();
        services.AddSingleton<IDialogService, MauiDialogService>();
        services.AddSingleton<INavigationService, MauiNavigationService>();
        services.AddSingleton<IClipboardService, MauiClipboardService>();
        services.AddSingleton<IUiDispatcher, MauiUiDispatcher>();
        services.AddSingleton<IFilePickerService, MauiFilePickerService>();
        services.AddSingleton<IUrlLauncher, MauiUrlLauncher>();
        services.AddSingleton<IBundledDataService, EmbeddedDataService>();
        services.AddSingleton<IThemeService, ThemeService>();

        // Shell e páginas
        services.AddSingleton<AppShell>();
        services.AddTransient<TranslatorPage>();
        services.AddTransient<HistoryPage>();
        services.AddTransient<LoginPage>();
        services.AddTransient<ModificationPage>();
        services.AddTransient<WordEditPage>();
        services.AddTransient<AiSettingsPage>();

        return builder.Build();
    }

    /// <summary>appsettings.json → appsettings.{Ambiente}.json → config/appsettings.local.json → %LOCALAPPDATA% (ver docs/configuracao.md).</summary>
    private static AppConfiguration LoadConfiguration()
    {
#if DEBUG
        const string defaultEnvironment = "Development";
#else
        const string defaultEnvironment = "Production";
#endif
        var environment = Environment.GetEnvironmentVariable("RHAGHO_ENV") ?? defaultEnvironment;
        var userOverride = Path.Combine(FileSystem.AppDataDirectory, "appsettings.local.json");

        return AppConfiguration.Build(
            EmbeddedResources.ReadTextOrNull("appsettings.json"),
            EmbeddedResources.ReadTextOrNull($"appsettings.{environment}.json"),
            EmbeddedResources.ReadTextOrNull("appsettings.local.json"),
            File.Exists(userOverride) ? File.ReadAllText(userOverride) : null);
    }
}
