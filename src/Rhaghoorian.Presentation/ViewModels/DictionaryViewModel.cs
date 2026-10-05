using System.Collections.ObjectModel;
using Rhaghoorian.Core.Abstractions;
using Rhaghoorian.Core.Models;
using Rhaghoorian.Core.Services;
using Rhaghoorian.Presentation.Mvvm;
using Rhaghoorian.Presentation.Services;

namespace Rhaghoorian.Presentation.ViewModels;

public sealed record SortOption(string Label, DictionarySort Sort);

/// <summary>Um registro do dicionário pronto para exibição (campos já formatados).</summary>
public sealed class WordItemViewModel : ObservableObject
{
    private bool _expanded;

    public WordItemViewModel(WordEntry entry, VerbConjugation? generated)
    {
        Entry = entry;
        VerbText = BuildVerbText(entry, generated);
    }

    public WordEntry Entry { get; }
    public string Id => Entry.Id;
    public string Rhagho => Entry.Rhagho;
    public string Portuguese => Entry.Portuguese;
    public string PartOfSpeech => Entry.PartOfSpeech;
    public string? Example => Entry.Example;
    public string? Etymology => Entry.Etymology;
    public bool HasExample => !string.IsNullOrWhiteSpace(Entry.Example);
    public bool HasEtymology => !string.IsNullOrWhiteSpace(Entry.Etymology);
    public string SynonymsText => string.Join(", ", Entry.Synonyms.Select(s => s.Text));
    public bool HasSynonyms => Entry.Synonyms.Count > 0;
    public string AlternativeFormsText => string.Join(", ", Entry.AlternativeForms.Select(a => a.Text));
    public bool HasAlternativeForms => Entry.AlternativeForms.Count > 0;
    public string AbbreviationsText => string.Join(", ", Entry.Abbreviations);
    public bool HasAbbreviations => Entry.Abbreviations.Count > 0;
    public string VerbText { get; }
    public bool HasVerbInfo => VerbText.Length > 0;

    public bool IsExpanded { get => _expanded; set => SetProperty(ref _expanded, value); }
    public AsyncCommand? ToggleCommand { get; set; }
    public AsyncCommand? EditCommand { get; set; }
    public AsyncCommand? DeleteCommand { get; set; }

    private bool _canModify;
    /// <summary>Somente visibilidade dos botões; a autorização real é imposta no serviço e nas Security Rules.</summary>
    public bool CanModify { get => _canModify; set => SetProperty(ref _canModify, value); }

    private static string BuildVerbText(WordEntry e, VerbConjugation? generated)
    {
        if (generated is not null)
            return $"Presente: {generated.Present}  ·  Passado: {generated.Past}  ·  Futuro: {generated.Future}  ·  Imperativo: {generated.Imperative}";
        if (e.Verb is { IsInfinitive: false } v)
        {
            var tense = v.Tense switch { VerbTense.Past => "passado", VerbTense.Future => "futuro", VerbTense.Imperative => "imperativo", _ => "presente" };
            var of = string.IsNullOrWhiteSpace(v.Infinitive) ? "" : $" do verbo “{v.Infinitive}”";
            return $"Forma de {tense}{of}";
        }
        return string.Empty;
    }
}

public sealed class DictionaryViewModel : ObservableObject
{
    public const string AllClasses = "Todas";

    private readonly IDictionaryService _dictionary;
    private readonly IAuthService _auth;
    private readonly IStartupCoordinator _startup;
    private readonly INavigationService _nav;
    private readonly IDialogService _dialogs;
    private readonly IUiDispatcher _ui;
    private readonly IErrorHandler _errors;

    private string _search = string.Empty;
    private string _selectedClass = AllClasses;
    private SortOption _selectedSort;
    private bool _descending;
    private bool _busy;
    private string _count = string.Empty;
    private CancellationTokenSource? _debounce;
    private bool _loadingClasses;

    public DictionaryViewModel(IDictionaryService dictionary, IAuthService auth, IStartupCoordinator startup, INavigationService nav,
        IDialogService dialogs, IUiDispatcher ui, IErrorHandler errors)
    {
        _dictionary = dictionary; _auth = auth; _startup = startup; _nav = nav; _dialogs = dialogs; _ui = ui; _errors = errors;

        SortOptions = new[]
        {
            new SortOption("Alfabética (Português)", DictionarySort.AlphabeticalPortuguese),
            new SortOption("Alfabética (Rhagho'oriano)", DictionarySort.AlphabeticalRhagho),
            new SortOption("Quantidade de letras", DictionarySort.LetterCount)
        };
        _selectedSort = SortOptions[0];
        Classes.Add(AllClasses);

        RefreshCommand = AsyncCommand.Create(RefreshAsync, errors);
        NewWordCommand = AsyncCommand.Create(NewWordAsync, errors);
        EditCommand = AsyncCommand.Create<WordItemViewModel>(EditAsync, errors);
        DeleteCommand = AsyncCommand.Create<WordItemViewModel>(DeleteAsync, errors);

        _auth.AuthStateChanged += (_, _) => _ui.Post(() =>
        {
            OnPropertyChanged(nameof(IsAuthenticated));
            foreach (var item in Items) item.CanModify = _auth.IsAuthenticated;
        });
    }

    public IReadOnlyList<SortOption> SortOptions { get; }
    public ObservableCollection<string> Classes { get; } = new();
    public ObservableCollection<WordItemViewModel> Items { get; } = new();

    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand NewWordCommand { get; }
    public AsyncCommand EditCommand { get; }
    public AsyncCommand DeleteCommand { get; }

    /// <summary>Atraso antes de pesquisar ao digitar (ms). Zero nos testes.</summary>
    public int DebounceMilliseconds { get; set; } = 250;

    /// <summary>Controla somente a visibilidade dos botões; a autorização real é imposta no serviço e nas Security Rules.</summary>
    public bool IsAuthenticated => _auth.IsAuthenticated;

    public string SearchText { get => _search; set { if (SetProperty(ref _search, value)) ScheduleRefresh(); } }
    public string SelectedClass { get => _selectedClass; set { if (SetProperty(ref _selectedClass, value ?? AllClasses) && !_loadingClasses) ScheduleRefresh(); } }
    public SortOption SelectedSort { get => _selectedSort; set { if (value is not null && SetProperty(ref _selectedSort, value)) ScheduleRefresh(); } }
    public bool Descending { get => _descending; set { if (SetProperty(ref _descending, value)) ScheduleRefresh(); } }
    public bool IsBusy { get => _busy; private set => SetProperty(ref _busy, value); }
    public string CountText { get => _count; private set => SetProperty(ref _count, value); }
    public bool IsEmpty => Items.Count == 0;

    public Task OnAppearingAsync() => RefreshCommand.ExecuteAsync();

    private void ScheduleRefresh()
    {
        _debounce?.Cancel();
        var cts = _debounce = new CancellationTokenSource();
        _ = DebouncedAsync(cts.Token);
    }

    private async Task DebouncedAsync(CancellationToken ct)
    {
        try
        {
            if (DebounceMilliseconds > 0) await Task.Delay(DebounceMilliseconds, ct);
            if (!ct.IsCancellationRequested) await RefreshCommand.ExecuteAsync();
        }
        catch (OperationCanceledException) { }
    }

    private async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            await _startup.EnsureReadyAsync();
            await ReloadClassesAsync();

            var query = new DictionaryQuery
            {
                Text = SearchText,
                PartOfSpeech = SelectedClass == AllClasses ? null : SelectedClass,
                Sort = SelectedSort.Sort,
                Descending = Descending,
                Take = 1500
            };
            var entries = await _dictionary.SearchAsync(query);

            Items.Clear();
            foreach (var e in entries)
            {
                var vm = new WordItemViewModel(e, _dictionary.GenerateConjugation(e));
                vm.ToggleCommand = AsyncCommand.Create(() => { vm.IsExpanded = !vm.IsExpanded; return Task.CompletedTask; }, _errors);
                vm.EditCommand = AsyncCommand.Create(() => EditAsync(vm), _errors);
                vm.DeleteCommand = AsyncCommand.Create(() => DeleteAsync(vm), _errors);
                vm.CanModify = _auth.IsAuthenticated;
                Items.Add(vm);
            }
            CountText = entries.Count == 1 ? "1 registro" : $"{entries.Count} registros";
            OnPropertyChanged(nameof(IsEmpty));
        }
        finally { IsBusy = false; }
    }

    /// <summary>A lista de classes vem do próprio banco (nunca fixa), então novas classes aparecem sozinhas.</summary>
    private async Task ReloadClassesAsync()
    {
        var fresh = await _dictionary.GetPartsOfSpeechAsync();
        var wanted = new List<string> { AllClasses };
        wanted.AddRange(fresh);
        if (Classes.SequenceEqual(wanted)) return;

        _loadingClasses = true;
        try
        {
            var keep = SelectedClass;
            Classes.Clear();
            foreach (var c in wanted) Classes.Add(c);
            SelectedClass = wanted.Contains(keep) ? keep : AllClasses;
        }
        finally { _loadingClasses = false; }
    }

    private async Task NewWordAsync()
    {
        if (!await EnsureLoggedInAsync()) return;
        await _nav.GoToAsync(Routes.WordEdit);
    }

    private async Task EditAsync(WordItemViewModel item)
    {
        if (!await EnsureLoggedInAsync()) return;
        await _nav.GoToAsync(Routes.WordEdit, new Dictionary<string, object> { ["id"] = item.Id });
    }

    private async Task DeleteAsync(WordItemViewModel item)
    {
        if (!await EnsureLoggedInAsync()) return;
        if (!await _dialogs.ConfirmAsync("Excluir palavra", $"Excluir “{item.Portuguese}” ({item.Rhagho}) do dicionário compartilhado? Isso afeta todos os usuários.", "Excluir")) return;
        await _dictionary.DeleteAsync(item.Id);
        Items.Remove(item);
        CountText = Items.Count == 1 ? "1 registro" : $"{Items.Count} registros";
        OnPropertyChanged(nameof(IsEmpty));
    }

    private async Task<bool> EnsureLoggedInAsync()
    {
        if (_auth.IsAuthenticated) return true;
        if (await _dialogs.ConfirmAsync("Entrar", "Para alterar o dicionário é preciso entrar com sua conta. Deseja entrar agora?", "Entrar"))
            await _nav.GoToAsync(Routes.Login);
        return false;
    }
}
