using System.Globalization;
using System.Text;
using System.Text.Json;
using Rhaghoorian.Core.Abstractions;
using Rhaghoorian.Core.Errors;
using Rhaghoorian.Infrastructure.Networking;

namespace Rhaghoorian.Infrastructure.Firebase;

/// <summary>
/// Firebase Authentication (e-mail/senha) via Identity Toolkit REST. Não existe cadastro no aplicativo.
/// A senha só trafega no momento do login (TLS) e nunca é guardada; apenas o refresh token vai para o armazenamento seguro.
/// </summary>
public sealed class FirebaseAuthService : IAuthService
{
    private const string RefreshTokenKey = "firebase.refreshToken";

    private readonly HttpClient _http;
    private readonly FirebaseOptions _options;
    private readonly ISecureTokenStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string? _idToken;
    private string? _refreshToken;
    private DateTimeOffset _expiresAt;

    public FirebaseAuthService(HttpClient http, FirebaseOptions options, ISecureTokenStore store)
    {
        _http = http; _options = options; _store = store;
    }

    public bool IsAuthenticated => _idToken is not null;
    public string? UserId { get; private set; }
    public string? Email { get; private set; }
    public event EventHandler? AuthStateChanged;

    private string AuthBase => (_options.AuthBaseUrl ?? "https://identitytoolkit.googleapis.com").TrimEnd('/');
    private string TokenBase => (_options.TokenBaseUrl ?? "https://securetoken.googleapis.com").TrimEnd('/');
    private TimeSpan Timeout => TimeSpan.FromSeconds(_options.TimeoutSeconds);

    public async Task SignInAsync(string email, string password, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password))
            throw new InvalidInputException("Informe e-mail e senha.");
        if (!_options.IsConfigured) throw new RemoteStoreException("O Firebase ainda não foi configurado. Veja o README.");

        var body = JsonSerializer.Serialize(new { email = email.Trim(), password, returnSecureToken = true });
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{AuthBase}/v1/accounts:signInWithPassword?key={Uri.EscapeDataString(_options.ApiKey)}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        var reply = await HttpGateway.SendAsync(_http, req, Timeout, ct);
        if (!reply.IsSuccess) throw MapSignInError(reply);

        using var doc = JsonDocument.Parse(reply.Body);
        var root = doc.RootElement;
        await ApplySessionAsync(
            root.GetProperty("idToken").GetString()!,
            root.GetProperty("refreshToken").GetString()!,
            root.GetProperty("expiresIn").GetString()!,
            root.TryGetProperty("localId", out var lid) ? lid.GetString() : null,
            root.TryGetProperty("email", out var em) ? em.GetString() : email.Trim());
    }

    public async Task<bool> TryRestoreSessionAsync(CancellationToken ct = default)
    {
        var refresh = await _store.GetAsync(RefreshTokenKey);
        if (string.IsNullOrEmpty(refresh) || !_options.IsConfigured) return false;
        _refreshToken = refresh;
        try
        {
            await RefreshAsync(ct);
            return IsAuthenticated;
        }
        catch (Exception ex) when (ex is OfflineException or RemoteTimeoutException)
        {
            return false;   // sem internet: mantém o refresh token e segue como visitante
        }
    }

    public async Task<string?> GetIdTokenAsync(CancellationToken ct = default)
    {
        if (_idToken is null) return null;
        if (DateTimeOffset.UtcNow < _expiresAt - TimeSpan.FromSeconds(60)) return _idToken;
        await RefreshAsync(ct);
        return _idToken;
    }

    public async Task SignOutAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { ClearSession(); }
        finally { _gate.Release(); }
        AuthStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_idToken is not null && DateTimeOffset.UtcNow < _expiresAt - TimeSpan.FromSeconds(60)) return;
            if (string.IsNullOrEmpty(_refreshToken)) { ClearSession(); throw new NotAuthenticatedException("Sua sessão expirou. Entre novamente."); }

            var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["refresh_token"] = _refreshToken });
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{TokenBase}/v1/token?key={Uri.EscapeDataString(_options.ApiKey)}") { Content = form };
            var reply = await HttpGateway.SendAsync(_http, req, Timeout, ct);

            if (!reply.IsSuccess)
            {
                if ((int)reply.Status is 400 or 401 or 403)
                {
                    ClearSession();
                    AuthStateChanged?.Invoke(this, EventArgs.Empty);
                    throw new NotAuthenticatedException("Sua sessão expirou. Entre novamente.");
                }
                throw new RemoteStoreException();
            }

            using var doc = JsonDocument.Parse(reply.Body);
            var root = doc.RootElement;
            var uid = root.TryGetProperty("user_id", out var u) ? u.GetString() : UserId;
            await ApplySessionAsync(root.GetProperty("id_token").GetString()!, root.GetProperty("refresh_token").GetString()!,
                root.GetProperty("expires_in").GetString()!, uid, Email, notify: _idToken is null);
        }
        finally { _gate.Release(); }
    }

    private async Task ApplySessionAsync(string idToken, string refreshToken, string expiresInSeconds, string? uid, string? email, bool notify = true)
    {
        _idToken = idToken;
        _refreshToken = refreshToken;
        _expiresAt = DateTimeOffset.UtcNow.AddSeconds(double.Parse(expiresInSeconds, CultureInfo.InvariantCulture));
        UserId = uid ?? UserId;
        Email = email ?? Email;
        await _store.SetAsync(RefreshTokenKey, refreshToken);
        if (notify) AuthStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ClearSession()
    {
        _idToken = null; _refreshToken = null; UserId = null; Email = null; _expiresAt = default;
        _store.Remove(RefreshTokenKey);
    }

    private static AppException MapSignInError(HttpGateway.Reply reply)
    {
        var code = reply.ErrorMessage ?? string.Empty;
        if (code.StartsWith("TOO_MANY_ATTEMPTS", StringComparison.Ordinal)) return new InvalidCredentialsException("Muitas tentativas. Aguarde alguns minutos e tente de novo.");
        if (code.StartsWith("USER_DISABLED", StringComparison.Ordinal)) return new InvalidCredentialsException("Esta conta foi desativada.");
        if ((int)reply.Status == 400) return new InvalidCredentialsException();   // INVALID_LOGIN_CREDENTIALS, EMAIL_NOT_FOUND, INVALID_PASSWORD, INVALID_EMAIL...
        return new RemoteStoreException("Não foi possível entrar agora. Tente novamente em instantes.");
    }
}
