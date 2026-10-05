using Rhaghoorian.Core.Abstractions;
using Rhaghoorian.Core.Errors;
using Rhaghoorian.Presentation.Mvvm;
using Rhaghoorian.Presentation.Services;

namespace Rhaghoorian.Presentation.ViewModels;

/// <summary>Somente login e senha, via Firebase Authentication. Não existe cadastro no aplicativo.</summary>
public sealed class LoginViewModel : ObservableObject
{
    private readonly IAuthService _auth;
    private readonly INavigationService _nav;
    private string _email = string.Empty;
    private string _password = string.Empty;
    private string? _error;
    private bool _busy;

    public LoginViewModel(IAuthService auth, INavigationService nav, IErrorHandler errors)
    {
        _auth = auth; _nav = nav;
        SignInCommand = AsyncCommand.Create(SignInAsync, errors);
        CancelCommand = AsyncCommand.Create(() => _nav.GoBackAsync(), errors);
    }

    public string Email { get => _email; set => SetProperty(ref _email, value); }
    /// <summary>Mantida apenas em memória enquanto a pessoa digita; apagada logo após o envio. Nunca é gravada.</summary>
    public string Password { get => _password; set => SetProperty(ref _password, value); }
    public string? ErrorMessage { get => _error; private set { SetProperty(ref _error, value); OnPropertyChanged(nameof(HasError)); } }
    public bool HasError => !string.IsNullOrEmpty(_error);
    public bool IsBusy { get => _busy; private set => SetProperty(ref _busy, value); }

    public AsyncCommand SignInCommand { get; }
    public AsyncCommand CancelCommand { get; }

    private async Task SignInAsync()
    {
        ErrorMessage = null;
        IsBusy = true;
        try
        {
            await _auth.SignInAsync(Email, Password);
            Password = string.Empty;
            await _nav.GoBackAsync();
        }
        catch (AppException ex)
        {
            ErrorMessage = ex.Message;      // mensagens de login são exibidas no próprio formulário
        }
        finally
        {
            Password = ErrorMessage is null ? string.Empty : Password;
            IsBusy = false;
        }
    }
}
