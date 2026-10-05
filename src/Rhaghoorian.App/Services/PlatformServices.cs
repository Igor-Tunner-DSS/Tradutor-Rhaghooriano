using System.Security.Cryptography;
using System.Text;
using Rhaghoorian.Core.Abstractions;
using Rhaghoorian.Presentation.Services;

namespace Rhaghoorian.App.Services;

/// <summary>Leitura dos recursos embutidos (configuração central e dicionário inicial).</summary>
public static class EmbeddedResources
{
    public static string? ReadTextOrNull(string logicalName)
    {
        using var stream = typeof(EmbeddedResources).Assembly.GetManifestResourceStream(logicalName);
        if (stream is null) return null;
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}

public sealed class EmbeddedDataService : IBundledDataService
{
    public string GetInitialDictionaryText() => EmbeddedResources.ReadTextOrNull("dicionario_inicial.txt") ?? string.Empty;
}

public sealed class MauiDialogService : IDialogService
{
    private static Page? CurrentPage => Shell.Current?.CurrentPage ?? Application.Current?.Windows.FirstOrDefault()?.Page;

    public Task AlertAsync(string title, string message) =>
        MainThread.InvokeOnMainThreadAsync(async () =>
        {
            if (CurrentPage is { } page) await page.DisplayAlert(title, message, "OK");
        });

    public Task<bool> ConfirmAsync(string title, string message, string accept = "Sim", string cancel = "Cancelar") =>
        MainThread.InvokeOnMainThreadAsync(async () =>
            CurrentPage is { } page && await page.DisplayAlert(title, message, accept, cancel));
}

public sealed class MauiNavigationService : INavigationService
{
    public Task GoToAsync(string route, IDictionary<string, object>? parameters = null) =>
        MainThread.InvokeOnMainThreadAsync(() => parameters is null
            ? Shell.Current.GoToAsync(route)
            : Shell.Current.GoToAsync(route, parameters));

    public Task GoBackAsync() => MainThread.InvokeOnMainThreadAsync(() => Shell.Current.GoToAsync(".."));
}

public sealed class MauiClipboardService : IClipboardService
{
    public Task SetTextAsync(string text) => MainThread.InvokeOnMainThreadAsync(() => Clipboard.Default.SetTextAsync(text));
}

public sealed class MauiUiDispatcher : IUiDispatcher
{
    public void Post(Action action)
    {
        if (MainThread.IsMainThread) action();
        else MainThread.BeginInvokeOnMainThread(action);
    }
}

public sealed class MauiConnectivityService : IConnectivityService
{
    public bool IsOnline => Connectivity.Current.NetworkAccess == NetworkAccess.Internet;
}

public sealed class MauiFilePickerService : IFilePickerService
{
    public async Task<string?> PickTextFileAsync(string title, CancellationToken ct = default)
    {
        var options = new PickOptions
        {
            PickerTitle = title,
            FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
            {
                [DevicePlatform.WinUI] = new[] { ".txt" }
            })
        };
        var file = await MainThread.InvokeOnMainThreadAsync(() => FilePicker.Default.PickAsync(options));
        if (file is null) return null;

        await using var stream = await file.OpenReadAsync();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(ct);
    }
}

/// <summary>
/// Guarda o refresh token da sessão protegido pelo DPAPI do Windows (vinculado ao usuário do Windows).
/// Nunca guarda senhas. Usa DPAPI direto porque o SecureStorage do MAUI depende de identidade de pacote (MSIX).
/// </summary>
public sealed class DpapiTokenStore : ISecureTokenStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Rhaghoorian.TokenStore.v1");
    private readonly string _directory = Path.Combine(FileSystem.AppDataDirectory, "secure");

    private string PathFor(string key) =>
        Path.Combine(_directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..24] + ".bin");

    public async Task<string?> GetAsync(string key)
    {
        var path = PathFor(key);
        if (!File.Exists(path)) return null;
        try
        {
            var protectedBytes = await File.ReadAllBytesAsync(path);
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser));
        }
        catch (CryptographicException)
        {
            File.Delete(path);      // arquivo de outro usuário/máquina ou corrompido: descarta
            return null;
        }
    }

    public async Task SetAsync(string key, string value)
    {
        Directory.CreateDirectory(_directory);
        var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser);
        await File.WriteAllBytesAsync(PathFor(key), data);
    }

    public void Remove(string key)
    {
        var path = PathFor(key);
        if (File.Exists(path)) File.Delete(path);
    }
}

public sealed class ThemeService : IThemeService
{
    private const string SettingKey = "theme";
    private readonly ISettingsStore _settings;

    public ThemeService(ISettingsStore settings) => _settings = settings;

    public ThemeMode Current { get; private set; } = ThemeMode.Light;
    public event EventHandler? Changed;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var saved = await _settings.GetAsync(SettingKey, ct);
        Current = saved switch
        {
            "dark" => ThemeMode.Dark,
            "light" => ThemeMode.Light,
            _ => Application.Current?.RequestedTheme == AppTheme.Dark ? ThemeMode.Dark : ThemeMode.Light
        };
        Apply();
    }

    public async Task ToggleAsync(CancellationToken ct = default)
    {
        Current = Current == ThemeMode.Dark ? ThemeMode.Light : ThemeMode.Dark;
        Apply();
        await _settings.SetAsync(SettingKey, Current == ThemeMode.Dark ? "dark" : "light", ct);   // preferência persistida localmente
    }

    private void Apply() =>
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (Application.Current is { } app) app.UserAppTheme = Current == ThemeMode.Dark ? AppTheme.Dark : AppTheme.Light;
            Changed?.Invoke(this, EventArgs.Empty);
        });
}
