using Rhaghoorian.Presentation.ViewModels;

namespace Rhaghoorian.App.Views;

/// <summary>Formulário de criação/edição. Recebe "id" (editar) ou "word"+"field" (criar a partir de palavra desconhecida).</summary>
public partial class WordEditPage : ContentPage, IQueryAttributable
{
    private readonly WordEditViewModel _viewModel;

    public WordEditPage(WordEditViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query) => _viewModel.ApplyQuery(query);

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _ = _viewModel.LoadAsync();
    }
}
