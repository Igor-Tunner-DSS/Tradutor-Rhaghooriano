using Rhaghoorian.Core.Abstractions;
using Rhaghoorian.Presentation.Mvvm;
using Rhaghoorian.Presentation.Services;

namespace Rhaghoorian.Presentation.ViewModels;

/// <summary>Módulo Modificação: subseção Dicionário (consulta para todos; alteração só autenticado) e Importação.</summary>
public sealed class ModificationViewModel : ObservableObject
{
    private readonly IAuthService _auth;
    private int _section;

    public ModificationViewModel(DictionaryViewModel dictionary, ImportViewModel import, IAuthService auth, IUiDispatcher ui, IErrorHandler errors)
    {
        Dictionary = dictionary; Import = import; _auth = auth;
        ShowDictionaryCommand = AsyncCommand.Create(() => SetSectionAsync(0), errors);
        ShowImportCommand = AsyncCommand.Create(() => SetSectionAsync(1), errors);
        _auth.AuthStateChanged += (_, _) => ui.Post(() => OnPropertyChanged(nameof(IsAuthenticated)));
    }

    public DictionaryViewModel Dictionary { get; }
    public ImportViewModel Import { get; }
    public AsyncCommand ShowDictionaryCommand { get; }
    public AsyncCommand ShowImportCommand { get; }

    public bool IsAuthenticated => _auth.IsAuthenticated;
    public bool ShowDictionary => _section == 0;
    public bool ShowImport => _section == 1;

    public Task OnAppearingAsync() => _section == 0 ? Dictionary.OnAppearingAsync() : Import.OnAppearingAsync();

    private async Task SetSectionAsync(int section)
    {
        if (_section == section) return;
        _section = section;
        OnPropertyChanged(nameof(ShowDictionary));
        OnPropertyChanged(nameof(ShowImport));
        await OnAppearingAsync();
    }
}
