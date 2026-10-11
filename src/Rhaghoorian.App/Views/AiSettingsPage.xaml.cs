using Rhaghoorian.Presentation.ViewModels;

namespace Rhaghoorian.App.Views;

public partial class AiSettingsPage : ContentPage
{
    private readonly AiSettingsViewModel _viewModel;

    public AiSettingsPage(AiSettingsViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _ = _viewModel.OnAppearingAsync();
    }
}
