using Rhaghoorian.Presentation.ViewModels;

namespace Rhaghoorian.App.Views;

public partial class ModificationPage : ContentPage
{
    private readonly ModificationViewModel _viewModel;

    public ModificationPage(ModificationViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _ = _viewModel.OnAppearingAsync();   // recarrega a lista ao voltar do formulário
    }
}
