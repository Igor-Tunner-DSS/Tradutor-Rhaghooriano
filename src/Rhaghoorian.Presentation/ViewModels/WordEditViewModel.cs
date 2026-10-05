using System.Collections.ObjectModel;
using Rhaghoorian.Core.Abstractions;
using Rhaghoorian.Core.Errors;
using Rhaghoorian.Core.Linguistics;
using Rhaghoorian.Core.Models;
using Rhaghoorian.Core.Services;
using Rhaghoorian.Core.Text;
using Rhaghoorian.Presentation.Mvvm;
using Rhaghoorian.Presentation.Services;

namespace Rhaghoorian.Presentation.ViewModels;

public sealed class EditableText : ObservableObject
{
    private string _text;
    public EditableText(string text = "") => _text = text;
    public string Text { get => _text; set => SetProperty(ref _text, value); }
    /// <summary>Remove este item da lista (definido pelo formulário que o criou).</summary>
    public AsyncCommand? RemoveCommand { get; set; }
}

public sealed class EditableSynonym : ObservableObject
{
    private string _text;
    public EditableSynonym(string text = "", bool fromAi = false) { _text = text; AcceptedFromAi = fromAi; }
    public string Text { get => _text; set => SetProperty(ref _text, value); }
    public bool AcceptedFromAi { get; }
    public AsyncCommand? RemoveCommand { get; set; }
}

public sealed class SynonymSuggestionViewModel
{
    public SynonymSuggestionViewModel(string text, AsyncCommand accept) { Text = text; AcceptCommand = accept; }
    public string Text { get; }
    public AsyncCommand AcceptCommand { get; }
}

/// <summary>Formulário de criação/edição (CREATE/UPDATE). Salvar exige login; sugestões de IA só viram dado quando a pessoa salva.</summary>
public sealed class WordEditViewModel : ObservableObject
{
    public static readonly string[] DefaultClasses =
        { "Substantivo", "Verbo", "Pronome", "Artigo", "Adjetivo", "Advérbio", "Preposição", "Outros" };

    private readonly IDictionaryService _dictionary;
    private readonly IWordAiAssistant _ai;
    private readonly IAuthService _auth;
    private readonly INavigationService _nav;
    private readonly IDialogService _dialogs;
    private readonly IRhaghoLinguisticService _ling;
    private readonly IErrorHandler _errors;

    private WordEntry? _original;
    private string _rhagho = string.Empty, _portuguese = string.Empty, _partOfSpeech = string.Empty, _newPos = string.Empty;
    private string _etymology = string.Empty, _example = string.Empty;
    private string _aiMessage = string.Empty, _conjugationCheck = string.Empty;
    private bool _busy;

    public WordEditViewModel(IDictionaryService dictionary, IWordAiAssistant ai, IAuthService auth, INavigationService nav,
        IDialogService dialogs, IRhaghoLinguisticService ling, IErrorHandler errors)
    {
        _dictionary = dictionary; _ai = ai; _auth = auth; _nav = nav; _dialogs = dialogs; _ling = ling; _errors = errors;

        SaveCommand = AsyncCommand.Create(SaveAsync, errors);
        CancelCommand = AsyncCommand.Create(() => _nav.GoBackAsync(), errors);
        AddAlternativeCommand = AsyncCommand.Create(() => { AddAlternative(""); return Task.CompletedTask; }, errors);
        AddAbbreviationCommand = AsyncCommand.Create(() => { AddAbbreviation(""); return Task.CompletedTask; }, errors);
        AddSynonymCommand = AsyncCommand.Create(() => { AddSynonym("", false); return Task.CompletedTask; }, errors);
        SuggestSynonymsCommand = AsyncCommand.Create(SuggestSynonymsAsync, errors);
        CheckConjugationCommand = AsyncCommand.Create(CheckConjugationAsync, errors);

        foreach (var c in DefaultClasses) PartsOfSpeech.Add(c);
    }

    public ObservableCollection<string> PartsOfSpeech { get; } = new();
    public ObservableCollection<EditableText> AlternativeForms { get; } = new();
    public ObservableCollection<EditableText> Abbreviations { get; } = new();
    public ObservableCollection<EditableSynonym> Synonyms { get; } = new();
    public ObservableCollection<SynonymSuggestionViewModel> AiSuggestions { get; } = new();

    public AsyncCommand SaveCommand { get; }
    public AsyncCommand CancelCommand { get; }
    public AsyncCommand AddAlternativeCommand { get; }
    public AsyncCommand AddAbbreviationCommand { get; }
    public AsyncCommand AddSynonymCommand { get; }
    public AsyncCommand SuggestSynonymsCommand { get; }
    public AsyncCommand CheckConjugationCommand { get; }

    public bool IsEditing => _original is not null;
    public string Title => IsEditing ? "Editar palavra" : "Nova palavra";
    public bool IsAuthenticated => _auth.IsAuthenticated;
    public bool IsBusy { get => _busy; private set => SetProperty(ref _busy, value); }

    public string Rhagho { get => _rhagho; set { if (SetProperty(ref _rhagho, value)) RefreshVerbPreview(); } }
    public string Portuguese { get => _portuguese; set => SetProperty(ref _portuguese, value); }
    public string PartOfSpeech { get => _partOfSpeech; set { if (SetProperty(ref _partOfSpeech, value)) RefreshVerbPreview(); } }
    /// <summary>Permite cadastrar uma classe que ainda não existe; quando preenchida, vale no lugar da lista.</summary>
    public string NewPartOfSpeech { get => _newPos; set { if (SetProperty(ref _newPos, value)) RefreshVerbPreview(); } }
    public string Etymology { get => _etymology; set => SetProperty(ref _etymology, value); }
    public string Example { get => _example; set => SetProperty(ref _example, value); }

    public string EffectivePartOfSpeech => string.IsNullOrWhiteSpace(NewPartOfSpeech) ? (PartOfSpeech ?? string.Empty).Trim() : NewPartOfSpeech.Trim();
    public bool IsVerb => TextNormalizer.Normalize(EffectivePartOfSpeech) == "verbo";

    public string VerbPreviewText { get; private set; } = string.Empty;
    public bool HasVerbPreview => IsVerb && VerbPreviewText.Length > 0;
    public string AiMessage { get => _aiMessage; private set { SetProperty(ref _aiMessage, value); OnPropertyChanged(nameof(HasAiMessage)); } }
    public bool HasAiMessage => _aiMessage.Length > 0;
    public string ConjugationCheckText { get => _conjugationCheck; private set { SetProperty(ref _conjugationCheck, value); OnPropertyChanged(nameof(HasConjugationCheck)); } }
    public bool HasConjugationCheck => _conjugationCheck.Length > 0;

    private void AddAlternative(string text)
    {
        var item = new EditableText(text);
        item.RemoveCommand = AsyncCommand.Create(() => { AlternativeForms.Remove(item); return Task.CompletedTask; }, _errors);
        AlternativeForms.Add(item);
    }

    private void AddAbbreviation(string text)
    {
        var item = new EditableText(text);
        item.RemoveCommand = AsyncCommand.Create(() => { Abbreviations.Remove(item); return Task.CompletedTask; }, _errors);
        Abbreviations.Add(item);
    }

    private void AddSynonym(string text, bool fromAi)
    {
        var item = new EditableSynonym(text, fromAi);
        item.RemoveCommand = AsyncCommand.Create(() => { Synonyms.Remove(item); return Task.CompletedTask; }, _errors);
        Synonyms.Add(item);
    }

    // ------------------------------------------------------------------ entrada

    /// <summary>Parâmetros de navegação: id (editar) ou word + field ("rhagho"/"portuguese") para criar a partir de uma palavra desconhecida.</summary>
    public void ApplyQuery(IDictionary<string, object> query)
    {
        PendingId = query.TryGetValue("id", out var id) ? Uri.UnescapeDataString(id?.ToString() ?? "") : null;
        PendingWord = query.TryGetValue("word", out var w) ? Uri.UnescapeDataString(w?.ToString() ?? "") : null;
        PendingField = query.TryGetValue("field", out var f) ? f?.ToString() : null;
    }

    public string? PendingId { get; private set; }
    public string? PendingWord { get; private set; }
    public string? PendingField { get; private set; }

    public async Task LoadAsync()
    {
        try
        {
            ResetForm();
            var classes = await _dictionary.GetPartsOfSpeechAsync();
            foreach (var c in classes)
                if (!PartsOfSpeech.Any(p => TextNormalizer.Normalize(p) == TextNormalizer.Normalize(c))) PartsOfSpeech.Add(c);

            if (!string.IsNullOrEmpty(PendingId))
            {
                var entry = await _dictionary.GetAsync(PendingId) ?? throw new InvalidInputException("Esta palavra não existe mais. Atualize a lista.");
                Fill(entry);
            }
            else if (!string.IsNullOrWhiteSpace(PendingWord))
            {
                if (PendingField == "portuguese") Portuguese = PendingWord!;
                else Rhagho = PendingWord!;
            }
            OnPropertyChanged(nameof(IsEditing));
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(IsAuthenticated));
        }
        catch (Exception ex) { await _errors.HandleAsync(ex); }
    }

    private void ResetForm()
    {
        _original = null;
        Rhagho = Portuguese = Etymology = Example = NewPartOfSpeech = string.Empty;
        PartOfSpeech = string.Empty;
        AlternativeForms.Clear(); Abbreviations.Clear(); Synonyms.Clear(); AiSuggestions.Clear();
        AiMessage = ConjugationCheckText = string.Empty;
    }

    private void Fill(WordEntry e)
    {
        _original = e;
        Rhagho = e.Rhagho;
        Portuguese = e.Portuguese;
        if (!PartsOfSpeech.Any(p => TextNormalizer.Normalize(p) == TextNormalizer.Normalize(e.PartOfSpeech))) PartsOfSpeech.Add(e.PartOfSpeech);
        PartOfSpeech = PartsOfSpeech.First(p => TextNormalizer.Normalize(p) == TextNormalizer.Normalize(e.PartOfSpeech));
        Etymology = e.Etymology ?? string.Empty;
        Example = e.Example ?? string.Empty;
        AlternativeForms.Clear(); foreach (var a in e.AlternativeForms) AddAlternative(a.Text);
        Abbreviations.Clear(); foreach (var a in e.Abbreviations) AddAbbreviation(a);
        Synonyms.Clear(); foreach (var s in e.Synonyms) AddSynonym(s.Text, s.AcceptedFromAi);
        OnPropertyChanged(nameof(IsEditing));
        OnPropertyChanged(nameof(Title));
    }

    // ------------------------------------------------------------------ verbos

    private void RefreshVerbPreview()
    {
        OnPropertyChanged(nameof(EffectivePartOfSpeech));
        OnPropertyChanged(nameof(IsVerb));
        var text = string.Empty;
        if (IsVerb && !string.IsNullOrWhiteSpace(Rhagho))
        {
            var b = Rhagho.Trim();
            if (_ling.IsBaseForm(b))
            {
                var c = _ling.Conjugate(b);
                text = $"Presente: {c.Present}\nPassado: {c.Past}\nFuturo: {c.Future}\nImperativo: {c.Imperative}";
            }
            else text = "A forma base não pode terminar em 'ta, 'vil ou 'nux: esses sufixos são gerados automaticamente.";
        }
        VerbPreviewText = text;
        OnPropertyChanged(nameof(VerbPreviewText));
        OnPropertyChanged(nameof(HasVerbPreview));
    }

    private async Task CheckConjugationAsync()
    {
        var check = await _ai.CheckConjugationAsync(Portuguese, Rhagho);
        ConjugationCheckText = check.AiAgrees
            ? "A IA concorda com as regras oficiais."
            : "A IA divergiu das regras oficiais. As formas oficiais acima prevalecem e serão usadas.";
    }

    // ------------------------------------------------------------------ sinônimos

    private async Task SuggestSynonymsAsync()
    {
        var suggestions = await _ai.SuggestSynonymsAsync(BuildEntry());
        AiSuggestions.Clear();
        foreach (var s in suggestions)
        {
            SynonymSuggestionViewModel? vm = null;
            vm = new SynonymSuggestionViewModel(s, AsyncCommand.Create(() => AcceptSuggestion(vm!), _errors));
            AiSuggestions.Add(vm);
        }
        AiMessage = suggestions.Count == 0
            ? "A IA não trouxe sinônimos novos."
            : "Sugestões da IA — nada foi salvo. Toque em uma sugestão para adicioná-la ao formulário; ela só é gravada quando você salvar.";
    }

    private Task AcceptSuggestion(SynonymSuggestionViewModel s)
    {
        if (!Synonyms.Any(x => TextNormalizer.Normalize(x.Text) == TextNormalizer.Normalize(s.Text)))
            AddSynonym(s.Text, fromAi: true);
        AiSuggestions.Remove(s);
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------ salvar

    private WordEntry BuildEntry()
    {
        var entry = _original?.Clone() ?? new WordEntry();
        entry.Rhagho = Rhagho.Trim();
        entry.Portuguese = Portuguese.Trim();
        entry.PartOfSpeech = EffectivePartOfSpeech;
        entry.Etymology = string.IsNullOrWhiteSpace(Etymology) ? null : Etymology.Trim();
        entry.Example = string.IsNullOrWhiteSpace(Example) ? null : Example.Trim();
        entry.AlternativeForms = AlternativeForms.Where(a => !string.IsNullOrWhiteSpace(a.Text)).Select(a => new AlternativeForm { Text = a.Text.Trim() }).ToList();
        entry.Abbreviations = Abbreviations.Where(a => !string.IsNullOrWhiteSpace(a.Text)).Select(a => a.Text.Trim()).ToList();
        entry.Synonyms = Synonyms.Where(s => !string.IsNullOrWhiteSpace(s.Text)).Select(s => new Synonym { Text = s.Text.Trim(), AcceptedFromAi = s.AcceptedFromAi }).ToList();
        return entry;
    }

    private async Task SaveAsync()
    {
        if (!_auth.IsAuthenticated)
        {
            if (await _dialogs.ConfirmAsync("Entrar", "Para salvar é preciso entrar com sua conta. Deseja entrar agora?", "Entrar"))
                await _nav.GoToAsync(Routes.Login);
            return;
        }

        IsBusy = true;
        try
        {
            var entry = BuildEntry();
            if (IsEditing) await _dictionary.UpdateAsync(entry);
            else await _dictionary.CreateAsync(entry);
            await _nav.GoBackAsync();
        }
        catch (ConflictException ex)
        {
            if (IsEditing && await _dialogs.ConfirmAsync("Conflito de edição",
                    ex.Message + "\n\nRecarregar a versão atual? As alterações feitas nesta tela serão descartadas.", "Recarregar"))
            {
                Fill(await _dictionary.ReloadFromRemoteAsync(_original!.Id));
            }
        }
        finally { IsBusy = false; }
    }
}
