using Microsoft.Extensions.DependencyInjection;
using Rhaghoorian.Presentation.Services;
using Rhaghoorian.Presentation.ViewModels;

namespace Rhaghoorian.Presentation;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// ViewModels e coordenação de tela. O aplicativo precisa fornecer: IDialogService, INavigationService, IClipboardService,
    /// IUiDispatcher, IFilePickerService, IBundledDataService e IThemeService.
    /// </summary>
    public static IServiceCollection AddRhaghoPresentation(this IServiceCollection services)
    {
        services.AddSingleton<IErrorHandler, ErrorHandler>();
        services.AddSingleton<IStartupCoordinator, StartupCoordinator>();
        services.AddSingleton<TranslationReuseHub>();

        // Persistem durante a sessão (barra de título e módulo Modificação)
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<DictionaryViewModel>();
        services.AddSingleton<ImportViewModel>();
        services.AddSingleton<ModificationViewModel>();

        // Novas instâncias a cada abertura de tela
        services.AddTransient<TranslatorViewModel>();
        services.AddTransient<HistoryViewModel>();
        services.AddTransient<LoginViewModel>();
        services.AddTransient<WordEditViewModel>();
        return services;
    }
}
