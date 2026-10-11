using System.Collections.ObjectModel;
using System.Text;
using Rhaghoorian.Core.Abstractions;
using Rhaghoorian.Core.Errors;
using Rhaghoorian.Core.Models;
using Rhaghoorian.Core.Services;
using Rhaghoorian.Core.Text;
using Rhaghoorian.Core.Translation;
using Rhaghoorian.Presentation.Mvvm;
using Rhaghoorian.Presentation.Services;

namespace Rhaghoorian.Presentation.ViewModels;

public sealed class SuggestionViewModel
{
    public SuggestionViewModel(WordSuggestion model, AsyncCommand apply) { Model = model; ApplyCommand = apply; }
    public WordSuggestion Model { get; }
    public string Text => Model.Text;
    public string Display => $"{Model.Text}  ·  {Model.Meaning}";
    public AsyncCommand ApplyCommand { get; }
}

public sealed class UnknownWordViewModel
{
    public UnknownWordViewModel(UnknownWord model, IReadOnlyList<SuggestionViewModel> suggestions, AsyncCommand add)
    {
        Model = model; Suggestions = suggestions; AddCommand = add;
    }
    public UnknownWord Model { get; }
    public string Word => Model.Word;
    public IReadOnlyList<SuggestionViewModel> Suggestions { get; }
    public bool HasSuggestions => Suggestions.Count > 0;
    public AsyncCommand AddCommand { get; }
}

public sealed class TranslatorViewModel : ObservableObject
{
    private readonly ITranslationWorkflow _workflow;
    private readonly IStartupCoordinator _startup;
    private readonly IClipboardService _clipboard;
    private readonly INavigationService _nav;
    private readonly IAuthService _auth;
    private readonly IDialogService _dialogs;
    private readonly IErrorHandler _errors;

    private TranslationDirection _direction = TranslationDirection.PortugueseToRhagho;
    private string _input = string.Empty;
    private string _output = string.Empty;
    private string _status = string.Empty;
    private bool _busy;
    private bool _usedAi;
    private TranslationResult? _result;

    public TranslatorViewModel(ITranslationWorkflow workflow, IStartupCoordinator startup, IClipboardService clipboard,
        INavigationService nav, IAuthService auth, IDialogService dialogs, TranslationReuseHub hub, IErrorHandler errors)
    {
        _workflow = workflow; _startup = startup; _clipboard = clipboard; _nav = nav; _auth = auth; _dialogs = dialogs; _errors = errors;

        TranslateCommand = AsyncCommand.Create(TranslateAsync, errors);
        ClearCommand = AsyncCommand.Create(() => { Clear(); return Task.CompletedTask; }, errors);
        CopyCommand = AsyncCommand.Create(CopyAsync, errors);
        SwapDirectionCommand = AsyncCommand.Create(() => { SwapDirection(); return Task.CompletedTask; }, errors);
        OpenHistoryCommand = AsyncCommand.Create(() => _nav.GoToAsync(Routes.History), errors);
        RefineWithAiCommand = AsyncCommand.Create(RefineAsync, errors);

        hub.Requested += item =>
        {
            Direction = item.Direction;
            InputText = item.SourceText;
            ClearDetails();
            OutputText = item.ResultText;
        };
    }

    public AsyncCommand TranslateCommand { get; }
    public AsyncCommand ClearCommand { get; }
    public AsyncCommand CopyCommand { get; }
    public AsyncCommand SwapDirectionCommand { get; }
    public AsyncCommand OpenHistoryCommand { get; }
    public AsyncCommand RefineWithAiCommand { get; }

    public ObservableCollection<UnknownWordViewModel> UnknownWords { get; } = new();
    public ObservableCollection<string> Notes { get; } = new();

    public TranslationDirection Direction
    {
        get => _direction;
        set
        {
            if (!SetProperty(ref _direction, value)) return;
            OnPropertyChanged(nameof(DirectionTitle));
            OnPropertyChanged(nameof(SourceLanguage));
            OnPropertyChanged(nameof(TargetLanguage));
        }
    }

    public string DirectionTitle => Direction == TranslationDirection.PortugueseToRhagho ? "Português → Rhagho'oriano" : "Rhagho'oriano → Português";
    public string SourceLanguage => Direction == TranslationDirection.PortugueseToRhagho ? "Português" : "Rhagho'oriano";
    public string TargetLanguage => Direction == TranslationDirection.PortugueseToRhagho ? "Rhagho'oriano" : "Português";

    public string InputText { get => _input; set => SetProperty(ref _input, value); }
    public string OutputText { get => _output; private set => SetProperty(ref _output, value); }
    public string StatusMessage { get => _status; private set { SetProperty(ref _status, value); OnPropertyChanged(nameof(HasStatus)); } }
    public bool HasStatus => !string.IsNullOrEmpty(_status);
    public bool IsBusy { get => _busy; private set => SetProperty(ref _busy, value); }
    public bool UsedAi { get => _usedAi; private set => SetProperty(ref _usedAi, value); }

    public bool HasUnknownWords => UnknownWords.Count > 0;
    public bool HasNotes => Notes.Count > 0;
    /// <summary>A IA é um recurso auxiliar: só é oferecida quando o motor local deixou palavras sem registro ou escolhas ambíguas.</summary>
    public bool CanRefineWithAi => _result?.CanBenefitFromAi == true;

    private void SwapDirection()
    {
        var carry = !string.IsNullOrWhiteSpace(OutputText) && UnknownWords.Count == 0 ? OutputText : InputText;
        Direction = Direction == TranslationDirection.PortugueseToRhagho ? TranslationDirection.RhaghoToPortuguese : TranslationDirection.PortugueseToRhagho;
        ClearDetails();
        InputText = carry;
        OutputText = string.Empty;
    }

    private void Clear()
    {
        InputText = string.Empty;
        OutputText = string.Empty;
        ClearDetails();
    }

    private void ClearDetails()
    {
        _result = null;
        UsedAi = false;
        UnknownWords.Clear();
        Notes.Clear();
        StatusMessage = string.Empty;
        RaiseDetails();
    }

    private void RaiseDetails()
    {
        OnPropertyChanged(nameof(HasUnknownWords));
        OnPropertyChanged(nameof(HasNotes));
        OnPropertyChanged(nameof(CanRefineWithAi));
    }

    private async Task TranslateAsync()
    {
        if (string.IsNullOrWhiteSpace(InputText)) { StatusMessage = "Digite um texto para traduzir."; return; }
        await RunTranslationAsync();
    }

    private async Task RunTranslationAsync()
    {
        IsBusy = true;
        try
        {
            await _startup.EnsureReadyAsync();
            Apply(await _workflow.TranslateAsync(InputText, Direction));
        }
        finally { IsBusy = false; }
    }

    private async Task RefineAsync()
    {
        if (_result is null || !_result.CanBenefitFromAi) return;
        IsBusy = true;
        try
        {
            var refined = await _workflow.RefineWithAiAsync(_result);
            Apply(refined);
            StatusMessage = refined.Direction == TranslationDirection.PortugueseToRhagho
                ? "Resposta da IA conferida: só contém palavras do dicionário."
                : "Resposta da IA (apoio). Palavras sem registro continuam sinalizadas.";
        }
        catch (Exception ex) when (ex is AiKeyMissingException or AiKeyInvalidException)
        {
            IsBusy = false;
            await AiKeyPrompt.TryHandleAsync(ex, _dialogs, _nav);   // oferece abrir as Configurações de IA
        }
        finally { IsBusy = false; }
    }

    private void Apply(TranslationResult r)
    {
        _result = r;
        OutputText = r.Text;
        UsedAi = r.UsedAi;

        UnknownWords.Clear();
        foreach (var u in r.UnknownWords)
        {
            var suggestions = u.Suggestions
                .Select(s => new SuggestionViewModel(s, AsyncCommand.Create(() => ApplySuggestionAsync(u.Word, s.Text), _errors)))
                .ToList();
            UnknownWords.Add(new UnknownWordViewModel(u, suggestions, AsyncCommand.Create(() => AddToDictionaryAsync(u.Word), _errors)));
        }

        Notes.Clear();
        var verb = r.Direction == TranslationDirection.PortugueseToRhagho ? "pode ser" : "também pode significar";
        foreach (var seg in r.Segments.Where(s => s.IsAmbiguous))
            Notes.Add($"“{seg.Source.Trim()}” {verb}: {string.Join(", ", seg.Alternatives!)}");

        StatusMessage = r.HasUnknownWords
            ? (r.UnknownWords.Count == 1 ? "1 palavra não está no dicionário." : $"{r.UnknownWords.Count} palavras não estão no dicionário.")
            : string.Empty;
        RaiseDetails();
    }

    private async Task ApplySuggestionAsync(string unknownWord, string replacement)
    {
        var key = TextNormalizer.Normalize(unknownWord);
        var sb = new StringBuilder();
        foreach (var t in Tokenizer.Tokenize(InputText))
            sb.Append(t.IsWordLike && t.Normalized == key ? replacement : t.Text);
        InputText = sb.ToString();
        await RunTranslationAsync();
    }

    private async Task AddToDictionaryAsync(string word)
    {
        if (!_auth.IsAuthenticated)
        {
            if (await _dialogs.ConfirmAsync("Entrar", "Para adicionar palavras ao dicionário é preciso entrar com sua conta. Deseja entrar agora?", "Entrar"))
                await _nav.GoToAsync(Routes.Login);
            return;
        }
        // Abre o formulário de criação já com a palavra preenchida no campo correto.
        await _nav.GoToAsync(Routes.WordEdit, new Dictionary<string, object>
        {
            ["word"] = word,
            ["field"] = Direction == TranslationDirection.PortugueseToRhagho ? "portuguese" : "rhagho"
        });
    }

    private async Task CopyAsync()
    {
        if (string.IsNullOrWhiteSpace(OutputText)) { StatusMessage = "Não há resultado para copiar."; return; }
        await _clipboard.SetTextAsync(OutputText);
        StatusMessage = "Resultado copiado.";
    }
}
