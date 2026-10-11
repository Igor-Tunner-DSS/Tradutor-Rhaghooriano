using Rhaghoorian.Core.Abstractions;
using Rhaghoorian.Presentation.Mvvm;
using Rhaghoorian.Presentation.Services;

namespace Rhaghoorian.Presentation.ViewModels;

/// <summary>Barra de título: sessão, tema, sincronização e estado da conexão.</summary>
public sealed class ShellViewModel : ObservableObject
{
    private readonly IAuthService _auth;
    private readonly IThemeService _theme;
    private readonly IStartupCoordinator _startup;
    private readonly INavigationService _nav;
    private readonly IDialogService _dialogs;
    private readonly IUiDispatcher _ui;

    public ShellViewModel(IAuthService auth, IThemeService theme, IStartupCoordinator startup, INavigationService nav,
        IDialogService dialogs, IUiDispatcher ui, IErrorHandler errors)
    {
        _auth = auth; _theme = theme; _startup = startup; _nav = nav; _dialogs = dialogs; _ui = ui;

        ToggleThemeCommand = AsyncCommand.Create(() => _theme.ToggleAsync(), errors);
        SyncNowCommand = AsyncCommand.Create(async () => { await _startup.SyncNowAsync(); }, errors);
        AuthCommand = AsyncCommand.Create(AuthAsync, errors);
        OpenAiSettingsCommand = AsyncCommand.Create(() => _nav.GoToAsync(Routes.AiSettings), errors);

        _auth.AuthStateChanged += (_, _) => _ui.Post(RaiseAuth);
        _theme.Changed += (_, _) => _ui.Post(() => OnPropertyChanged(nameof(ThemeButtonText)));
        _startup.StatusChanged += (_, _) => _ui.Post(() => OnPropertyChanged(nameof(StatusText)));
    }

    public AsyncCommand ToggleThemeCommand { get; }
    public AsyncCommand SyncNowCommand { get; }
    public AsyncCommand AuthCommand { get; }
    public AsyncCommand OpenAiSettingsCommand { get; }

    public bool IsAuthenticated => _auth.IsAuthenticated;
    public string AuthButtonText => IsAuthenticated ? "Sair" : "Entrar";
    public string UserLabel => IsAuthenticated ? (_auth.Email ?? "Administrador") : "Visitante";
    public string ThemeButtonText => _theme.Current == ThemeMode.Dark ? "Tema claro" : "Tema escuro";
    public string StatusText => _startup.Status;

    private void RaiseAuth()
    {
        OnPropertyChanged(nameof(IsAuthenticated));
        OnPropertyChanged(nameof(AuthButtonText));
        OnPropertyChanged(nameof(UserLabel));
    }

    private async Task AuthAsync()
    {
        if (!IsAuthenticated) { await _nav.GoToAsync(Routes.Login); return; }
        if (await _dialogs.ConfirmAsync("Sair", "Deseja sair da conta? Você continuará podendo usar o Tradutor e consultar o Dicionário.", "Sair"))
            await _auth.SignOutAsync();
    }
}
