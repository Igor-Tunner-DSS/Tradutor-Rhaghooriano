using Rhaghoorian.Presentation.ViewModels;

namespace Rhaghoorian.App.Views;

public partial class TranslatorPage : ContentPage
{
    public TranslatorPage(TranslatorViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }
}
