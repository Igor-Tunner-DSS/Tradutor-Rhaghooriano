using Rhaghoorian.Presentation.ViewModels;

namespace Rhaghoorian.App.Views;

public partial class LoginPage : ContentPage
{
    public LoginPage(LoginViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }
}
