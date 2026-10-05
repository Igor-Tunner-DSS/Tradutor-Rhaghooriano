using System.Collections.ObjectModel;
using Rhaghoorian.Core.Abstractions;
using Rhaghoorian.Core.Importing;
using Rhaghoorian.Core.Services;
using Rhaghoorian.Presentation.Mvvm;
using Rhaghoorian.Presentation.Services;

namespace Rhaghoorian.Presentation.ViewModels;

public sealed record IssueItem(string Severity, string Code, string Where, string Message);

/// <summary>Importação do dicionário inicial e revisão do relatório de validação (duplicatas, aspas, formas verbais, conflitos...).</summary>
public sealed class ImportViewModel : ObservableObject
{
    public static readonly string[] SeverityFilters = { "Todos", "Erros", "Avisos", "Informações" };

    /// <summary>Opções do filtro (instância, para o binding do XAML).</summary>
    public IReadOnlyList<string> SeverityOptions => SeverityFilters;

    private static string? ToStoredSeverity(string label) => label switch { "Erros" => "Error", "Avisos" => "Warning", "Informações" => "Info", _ => null };
    private static string ToLabel(string stored) => stored switch { "Error" => "Erro", "Warning" => "Aviso", _ => "Info" };

    private readonly IDictionaryImportService _import;
    private readonly IFilePickerService _picker;
    private readonly IBundledDataService _bundled;
    private readonly IAuthService _auth;
    private readonly IStartupCoordinator _startup;
    private readonly IDialogService _dialogs;
    private readonly INavigationService _nav;

    private ImportReport? _report;
    private string _summary = "Nenhuma análise feita ainda.";
    private string _filter = "Todos";
    private bool _busy;

    public ImportViewModel(IDictionaryImportService import, IFilePickerService picker, IBundledDataService bundled, IAuthService auth,
        IStartupCoordinator startup, IDialogService dialogs, INavigationService nav, IErrorHandler errors)
    {
        _import = import; _picker = picker; _bundled = bundled; _auth = auth; _startup = startup; _dialogs = dialogs; _nav = nav;

        AnalyzeBundledCommand = AsyncCommand.Create(() => AnalyzeAsync(_bundled.GetInitialDictionaryText()), errors);
        PickFileCommand = AsyncCommand.Create(PickAsync, errors);
        PublishCommand = AsyncCommand.Create(PublishAsync, errors);
        ReloadIssuesCommand = AsyncCommand.Create(LoadIssuesAsync, errors);
    }

    public ObservableCollection<IssueItem> Issues { get; } = new();
    public AsyncCommand AnalyzeBundledCommand { get; }
    public AsyncCommand PickFileCommand { get; }
    public AsyncCommand PublishCommand { get; }
    public AsyncCommand ReloadIssuesCommand { get; }

    public bool IsAuthenticated => _auth.IsAuthenticated;
    public bool HasReport => _report is not null;
    public bool IsBusy { get => _busy; private set => SetProperty(ref _busy, value); }
    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }
    public string SeverityFilter { get => _filter; set { if (SetProperty(ref _filter, value)) _ = ReloadIssuesCommand.ExecuteAsync(); } }

    public Task OnAppearingAsync()
    {
        OnPropertyChanged(nameof(IsAuthenticated));
        return LoadIssuesAsync();
    }

    private async Task PickAsync()
    {
        var text = await _picker.PickTextFileAsync("Escolha o arquivo do dicionário (.txt)");
        if (text is not null) await AnalyzeAsync(text);
    }

    private async Task AnalyzeAsync(string text)
    {
        IsBusy = true;
        try
        {
            _report = await _import.AnalyzeAsync(text);
            OnPropertyChanged(nameof(HasReport));
            var classes = string.Join(", ", _report.EntriesByClass.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}"));
            Summary = $"{_report.Entries.Count} entradas lidas ({classes}).\n" +
                      $"Problemas: {_report.Count(IssueSeverity.Error)} erros · {_report.Count(IssueSeverity.Warning)} avisos · {_report.Count(IssueSeverity.Info)} informações.\n" +
                      "Nada foi descartado nem corrigido: os valores originais foram preservados.";
            await LoadIssuesAsync();
        }
        finally { IsBusy = false; }
    }

    private async Task LoadIssuesAsync()
    {
        var records = await _import.GetStoredIssuesAsync(ToStoredSeverity(SeverityFilter));
        Issues.Clear();
        foreach (var r in records.Take(1500))
            Issues.Add(new IssueItem(ToLabel(r.Severity), r.Code, r.Line is { } l ? $"linha {l}" : "geral", r.Message));
    }

    private async Task PublishAsync()
    {
        if (_report is null) return;
        if (!_auth.IsAuthenticated)
        {
            if (await _dialogs.ConfirmAsync("Entrar", "Para publicar no Firebase é preciso entrar com sua conta. Deseja entrar agora?", "Entrar"))
                await _nav.GoToAsync(Routes.Login);
            return;
        }
        if (!await _dialogs.ConfirmAsync("Publicar dicionário",
                $"Enviar {_report.Entries.Count} registros ao Firebase? Registros que já existem lá NÃO serão sobrescritos.", "Publicar")) return;

        IsBusy = true;
        try
        {
            var created = await _import.PublishAsync(_report, new Progress<int>(n => Summary = $"Publicando… {n} registros enviados."));
            await _startup.SyncNowAsync();
            Summary = created == 0
                ? "Nada novo para publicar: todos os registros já existem no Firebase."
                : $"{created} registros publicados e sincronizados.";
        }
        finally { IsBusy = false; }
    }
}
