using Rhaghoorian.App.Views;
using Rhaghoorian.Presentation.Services;
using Rhaghoorian.Presentation.ViewModels;

namespace Rhaghoorian.App;

public partial class AppShell : Shell
{
    public AppShell(ShellViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;

        Routing.RegisterRoute(Routes.Login, typeof(LoginPage));
        Routing.RegisterRoute(Routes.History, typeof(HistoryPage));
        Routing.RegisterRoute(Routes.WordEdit, typeof(WordEditPage));
        Routing.RegisterRoute(Routes.AiSettings, typeof(AiSettingsPage));
    }
}
