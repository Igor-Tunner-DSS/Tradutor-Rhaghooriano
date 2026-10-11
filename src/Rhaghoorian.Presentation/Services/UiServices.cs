using Microsoft.Extensions.Logging;
using Rhaghoorian.Core.Errors;
using Rhaghoorian.Core.Models;

namespace Rhaghoorian.Presentation.Services;

public interface IDialogService
{
    Task AlertAsync(string title, string message);
    Task<bool> ConfirmAsync(string title, string message, string accept = "Sim", string cancel = "Cancelar");
}

public interface INavigationService
{
    Task GoToAsync(string route, IDictionary<string, object>? parameters = null);
    Task GoBackAsync();
}

public interface IClipboardService
{
    Task SetTextAsync(string text);
}

public enum ThemeMode { Light, Dark }

public interface IThemeService
{
    ThemeMode Current { get; }
    event EventHandler? Changed;
    /// <summary>Lê a preferência salva localmente (ou o tema do sistema na primeira vez) e a aplica.</summary>
    Task InitializeAsync(CancellationToken ct = default);
    Task ToggleAsync(CancellationToken ct = default);
}

public interface IFilePickerService
{
    /// <summary>Abre o seletor de arquivos e devolve o conteúdo de texto (UTF-8), ou nulo se o usuário cancelar.</summary>
    Task<string?> PickTextFileAsync(string title, CancellationToken ct = default);
}

public interface IUrlLauncher
{
    /// <summary>Abre um endereço no navegador padrão (ex.: página de chaves do OpenRouter).</summary>
    Task OpenAsync(string url);
}

public interface IBundledDataService
{
    /// <summary>Dicionário inicial incluído no aplicativo ("Dicionário rhagho'oriano atual").</summary>
    string GetInitialDictionaryText();
}

/// <summary>Tratamento centralizado de erros: mensagem clara ao usuário, detalhes técnicos só no log (nunca segredos).</summary>
public interface IErrorHandler
{
    Task HandleAsync(Exception ex);
}

public sealed class ErrorHandler : IErrorHandler
{
    private readonly IDialogService _dialogs;
    private readonly ILogger<ErrorHandler> _logger;

    public ErrorHandler(IDialogService dialogs, ILogger<ErrorHandler> logger)
    {
        _dialogs = dialogs; _logger = logger;
    }

    public async Task HandleAsync(Exception ex)
    {
        if (ex is OperationCanceledException) return;
        if (ex is AppException) _logger.LogWarning(ex, "Falha esperada: {Type}", ex.GetType().Name);
        else _logger.LogError(ex, "Erro inesperado");

        var title = ex switch
        {
            NotAuthenticatedException or InvalidCredentialsException => "Acesso",
            AiKeyMissingException or AiKeyInvalidException or AiRateLimitException or AiUnavailableException or InvalidAiResponseException => "IA",
            RemoteQuotaExceededException => "Cota do Firebase",
            OfflineException or RemoteTimeoutException => "Conexão",
            ConflictException => "Conflito de edição",
            InvalidInputException => "Confira os dados",
            _ => "Ops"
        };
        await _dialogs.AlertAsync(title, UserMessages.For(ex));
    }
}

/// <summary>Leva um item do histórico de volta ao Tradutor sem acoplar as duas telas.</summary>
public sealed class TranslationReuseHub
{
    public event Action<TranslationHistoryItem>? Requested;
    public void Publish(TranslationHistoryItem item) => Requested?.Invoke(item);
}

public static class Routes
{
    public const string Login = "login";
    public const string History = "history";
    public const string WordEdit = "wordedit";
    public const string AiSettings = "aisettings";
}

/// <summary>Garante que mudanças de estado vindas de threads de fundo (ex.: renovação de token) cheguem à UI na thread correta.</summary>
public interface IUiDispatcher
{
    void Post(Action action);
}


/// <summary>Quando a causa é a chave do OpenRouter (ausente ou recusada), oferece abrir as Configurações de IA em vez de só avisar.</summary>
public static class AiKeyPrompt
{
    public static async Task<bool> TryHandleAsync(Exception ex, IDialogService dialogs, INavigationService nav)
    {
        if (ex is not (AiKeyMissingException or AiKeyInvalidException)) return false;
        if (await dialogs.ConfirmAsync("Chave de IA", ex.Message + "\n\nAbrir as Configurações de IA agora?", "Abrir"))
            await nav.GoToAsync(Routes.AiSettings);
        return true;
    }
}
