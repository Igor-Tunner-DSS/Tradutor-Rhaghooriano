using System.Collections.ObjectModel;
using Rhaghoorian.Core.Models;
using Rhaghoorian.Core.Services;
using Rhaghoorian.Presentation.Mvvm;
using Rhaghoorian.Presentation.Services;

namespace Rhaghoorian.Presentation.ViewModels;

public sealed class HistoryItemViewModel
{
    public HistoryItemViewModel(TranslationHistoryItem model) => Model = model;
    public TranslationHistoryItem Model { get; }
    public long Id => Model.Id;
    public string Source => Model.SourceText;
    public string Result => Model.ResultText;
    public string DirectionLabel => Model.Direction == TranslationDirection.PortugueseToRhagho ? "Português → Rhagho'oriano" : "Rhagho'oriano → Português";
    public string When => Model.CreatedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
    public AsyncCommand? ReuseCommand { get; set; }
    public AsyncCommand? DeleteCommand { get; set; }
}

/// <summary>Histórico somente local (SQLite): nunca vai ao Firestore.</summary>
public sealed class HistoryViewModel : ObservableObject
{
    private readonly ITranslationWorkflow _workflow;
    private readonly TranslationReuseHub _hub;
    private readonly INavigationService _nav;
    private readonly IDialogService _dialogs;
    private readonly IErrorHandler _errors;

    public HistoryViewModel(ITranslationWorkflow workflow, TranslationReuseHub hub, INavigationService nav, IDialogService dialogs, IErrorHandler errors)
    {
        _workflow = workflow; _hub = hub; _nav = nav; _dialogs = dialogs; _errors = errors;
        LoadCommand = AsyncCommand.Create(LoadAsync, errors);
        ReuseCommand = AsyncCommand.Create<HistoryItemViewModel>(ReuseAsync, errors);
        DeleteCommand = AsyncCommand.Create<HistoryItemViewModel>(DeleteAsync, errors);
        ClearCommand = AsyncCommand.Create(ClearAsync, errors);
    }

    public ObservableCollection<HistoryItemViewModel> Items { get; } = new();
    public bool IsEmpty => Items.Count == 0;

    public AsyncCommand LoadCommand { get; }
    public AsyncCommand ReuseCommand { get; }
    public AsyncCommand DeleteCommand { get; }
    public AsyncCommand ClearCommand { get; }

    public Task OnAppearingAsync() => LoadCommand.ExecuteAsync();

    private async Task LoadAsync()
    {
        var items = await _workflow.GetHistoryAsync();
        Items.Clear();
        foreach (var i in items)
        {
            var vm = new HistoryItemViewModel(i);
            vm.ReuseCommand = AsyncCommand.Create(() => ReuseAsync(vm), _errors);
            vm.DeleteCommand = AsyncCommand.Create(() => DeleteAsync(vm), _errors);
            Items.Add(vm);
        }
        OnPropertyChanged(nameof(IsEmpty));
    }

    private async Task ReuseAsync(HistoryItemViewModel item)
    {
        _hub.Publish(item.Model);
        await _nav.GoBackAsync();
    }

    private async Task DeleteAsync(HistoryItemViewModel item)
    {
        await _workflow.DeleteHistoryItemAsync(item.Id);
        Items.Remove(item);
        OnPropertyChanged(nameof(IsEmpty));
    }

    private async Task ClearAsync()
    {
        if (Items.Count == 0) return;
        if (!await _dialogs.ConfirmAsync("Limpar histórico", "Apagar todo o histórico de traduções deste computador?", "Apagar")) return;
        await _workflow.ClearHistoryAsync();
        Items.Clear();
        OnPropertyChanged(nameof(IsEmpty));
    }
}
