using Rhaghoorian.Core.Ai;
using Rhaghoorian.Core.Errors;
using Rhaghoorian.Presentation.Mvvm;
using Rhaghoorian.Presentation.Services;

namespace Rhaghoorian.Presentation.ViewModels;

/// <summary>
/// Configurações de IA: cada pessoa usa a PRÓPRIA chave do OpenRouter. Ela fica só neste computador (protegida pelo Windows),
/// nunca vai ao Firebase, a arquivos de configuração nem a outros usuários. A tela só mostra a chave mascarada.
/// </summary>
public sealed class AiSettingsViewModel : ObservableObject
{
    public const string KeysPageUrl = "https://openrouter.ai/keys";

    private readonly IAiKeyManager _keys;
    private readonly IAiUsageLimiter _usage;
    private readonly IUrlLauncher _launcher;
    private readonly IDialogService _dialogs;

    private string _input = string.Empty;
    private string _status = string.Empty;
    private string _usageText = string.Empty;
    private string _feedback = string.Empty;
    private bool _feedbackIsError;
    private bool _hasKey;
    private bool _busy;

    public AiSettingsViewModel(IAiKeyManager keys, IAiUsageLimiter usage, IUrlLauncher launcher, IDialogService dialogs, IErrorHandler errors)
    {
        _keys = keys; _usage = usage; _launcher = launcher; _dialogs = dialogs;
        SaveCommand = AsyncCommand.Create(SaveAsync, errors);
        RemoveCommand = AsyncCommand.Create(RemoveAsync, errors);
        OpenKeysPageCommand = AsyncCommand.Create(() => _launcher.OpenAsync(KeysPageUrl), errors);
        RefreshCommand = AsyncCommand.Create(RefreshAsync, errors);
    }

    public AsyncCommand SaveCommand { get; }
    public AsyncCommand RemoveCommand { get; }
    public AsyncCommand OpenKeysPageCommand { get; }
    public AsyncCommand RefreshCommand { get; }

    /// <summary>Mantida só em memória enquanto a pessoa cola a chave; apagada ao salvar.</summary>
    public string ApiKeyInput { get => _input; set => SetProperty(ref _input, value); }
    public string StatusText { get => _status; private set => SetProperty(ref _status, value); }
    public string UsageText { get => _usageText; private set => SetProperty(ref _usageText, value); }
    public bool HasKey { get => _hasKey; private set => SetProperty(ref _hasKey, value); }
    public bool IsBusy { get => _busy; private set => SetProperty(ref _busy, value); }
    public string FeedbackMessage { get => _feedback; private set { SetProperty(ref _feedback, value); OnPropertyChanged(nameof(HasFeedback)); } }
    public bool FeedbackIsError { get => _feedbackIsError; private set { SetProperty(ref _feedbackIsError, value); OnPropertyChanged(nameof(FeedbackIsSuccess)); } }
    public bool FeedbackIsSuccess => HasFeedback && !_feedbackIsError;
    public bool HasFeedback => _feedback.Length > 0;

    public string HelpText =>
        "Cada pessoa usa a própria chave (gratuita) do OpenRouter:\n" +
        "1. Entre em openrouter.ai/keys (e-mail ou GitHub; não precisa de cartão).\n" +
        "2. Crie uma chave e copie-a (começa com \"sk-or-\").\n" +
        "3. Cole abaixo e toque em \"Testar e salvar\".\n\n" +
        "A chave fica só neste computador, protegida pelo Windows — não é enviada ao Firebase nem aparece para outros usuários. " +
        "O app usa o modelo openrouter/free e só chama a IA quando você pede.";

    public string LimitsText =>
        "Limites do OpenRouter para modelos gratuitos (segundo a documentação deles na data desta versão): 20 consultas por minuto e " +
        "50 por dia por conta, subindo para 1.000 por dia depois de uma compra única de US$ 10 em créditos. O limite é da sua conta. " +
        "O app ainda aplica um freio local diário para você não esgotá-lo sem perceber.";

    public Task OnAppearingAsync() => RefreshCommand.ExecuteAsync();

    private async Task RefreshAsync()
    {
        var status = await _keys.GetStatusAsync();
        HasKey = status.HasKey;
        StatusText = status.HasKey ? $"Chave configurada: {status.MaskedKey}" : "Nenhuma chave configurada — a IA está desativada (o tradutor local continua funcionando).";
        var u = await _usage.GetUsageAsync();
        UsageText = $"Hoje (dia UTC): {u.UsedToday} de {u.DailyLimit} consultas de IA usadas neste computador.";
    }

    private async Task SaveAsync()
    {
        FeedbackMessage = string.Empty;
        IsBusy = true;
        try
        {
            var result = await _keys.SaveAsync(ApiKeyInput);
            ApiKeyInput = string.Empty;
            FeedbackIsError = false;
            FeedbackMessage = result.Verified
                ? "Chave confirmada pelo OpenRouter e salva neste computador." +
                  (result.IsFreeTier == true ? " Sua conta ainda não comprou créditos: o teto diário dos modelos gratuitos é o menor (50 por dia)." : string.Empty)
                : "Chave salva, mas não foi possível confirmá-la agora (sem conexão). Ela será validada no primeiro uso.";
            await RefreshAsync();
        }
        catch (AppException ex)
        {
            FeedbackIsError = true;
            FeedbackMessage = ex.Message;        // erros de chave aparecem no próprio formulário
        }
        finally { IsBusy = false; }
    }

    private async Task RemoveAsync()
    {
        if (!HasKey) return;
        if (!await _dialogs.ConfirmAsync("Remover chave", "Remover a chave de IA deste computador? A IA ficará desativada até você configurar outra.", "Remover")) return;
        await _keys.RemoveAsync();
        FeedbackIsError = false;
        FeedbackMessage = "Chave removida deste computador.";
        await RefreshAsync();
    }
}
