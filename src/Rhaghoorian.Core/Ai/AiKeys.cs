using Rhaghoorian.Core.Abstractions;
using Rhaghoorian.Core.Errors;

namespace Rhaghoorian.Core.Ai;

/// <summary>Armazena a chave do OpenRouter DESTA pessoa, protegida pelo sistema (DPAPI). Nunca vai a arquivos, Firebase ou logs.</summary>
public interface IAiKeyStore
{
    Task<string?> GetAsync(CancellationToken ct = default);
    Task SetAsync(string key, CancellationToken ct = default);
    Task ClearAsync(CancellationToken ct = default);
}

public sealed record AiKeyStatus(bool HasKey, string? MaskedKey);
public sealed record AiKeySaveResult(bool Verified, bool? IsFreeTier);

/// <summary>Cadastro da chave pessoal: valida o formato, confere com o OpenRouter (se houver conexão) e só então guarda.</summary>
public interface IAiKeyManager
{
    Task<AiKeyStatus> GetStatusAsync(CancellationToken ct = default);
    Task<AiKeySaveResult> SaveAsync(string key, CancellationToken ct = default);
    Task RemoveAsync(CancellationToken ct = default);
}

public static class AiKeyFormat
{
    /// <summary>Normaliza e valida o formato (as chaves do OpenRouter começam com "sk-or-").</summary>
    public static string Normalize(string? raw)
    {
        var key = (raw ?? string.Empty).Trim().Trim('"', '\'');
        if (key.Length == 0) throw new InvalidInputException("Cole a sua chave do OpenRouter.");
        if (key.Any(char.IsWhiteSpace)) throw new InvalidInputException("A chave não pode ter espaços ou quebras de linha.");
        if (!key.StartsWith("sk-or-", StringComparison.Ordinal))
            throw new InvalidInputException("Isso não parece uma chave do OpenRouter: ela começa com \"sk-or-\".");
        if (key.Length is < 20 or > 300) throw new InvalidInputException("A chave parece incompleta ou longa demais. Copie-a novamente.");
        return key;
    }

    /// <summary>"sk-or-…a1b2": suficiente para a pessoa reconhecer a chave sem expô-la na tela.</summary>
    public static string Mask(string key) => key.Length <= 10 ? "sk-or-…" : $"sk-or-…{key[^4..]}";
}

/// <summary>Uso diário da IA neste computador (dia UTC, como o OpenRouter conta).</summary>
public sealed record AiUsage(int UsedToday, int DailyLimit)
{
    public int Remaining => Math.Max(0, DailyLimit - UsedToday);
}

public interface IAiUsageLimiter
{
    Task<AiUsage> GetUsageAsync(CancellationToken ct = default);
    /// <summary>Reserva uma consulta; falso quando o limite diário local foi atingido.</summary>
    Task<bool> TryAcquireAsync(CancellationToken ct = default);
}

/// <summary>
/// Limite diário local (padrão 20, conforme o uso esperado). É um freio de conveniência: a chave é da pessoa e o limite real
/// é o do OpenRouter (modelos :free: 20 req/min e 50/dia, ou 1.000/dia após comprar US$ 10 em créditos — confira na documentação).
/// </summary>
public sealed class SettingsAiUsageLimiter : IAiUsageLimiter
{
    private const string Key = "ai.usage";
    private readonly ISettingsStore _settings;
    private readonly int _limit;
    private readonly Func<DateTimeOffset> _now;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SettingsAiUsageLimiter(ISettingsStore settings, int dailyLimit, Func<DateTimeOffset>? now = null)
    {
        _settings = settings; _limit = Math.Max(1, dailyLimit); _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    private string Today => _now().UtcDateTime.ToString("yyyyMMdd");

    private async Task<int> ReadAsync(CancellationToken ct)
    {
        var raw = await _settings.GetAsync(Key, ct);
        var parts = raw?.Split(':');
        return parts is { Length: 2 } && parts[0] == Today && int.TryParse(parts[1], out var n) ? n : 0;
    }

    public async Task<AiUsage> GetUsageAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { return new AiUsage(await ReadAsync(ct), _limit); }
        finally { _gate.Release(); }
    }

    public async Task<bool> TryAcquireAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var used = await ReadAsync(ct);
            if (used >= _limit) return false;
            await _settings.SetAsync(Key, $"{Today}:{used + 1}", ct);
            return true;
        }
        finally { _gate.Release(); }
    }
}
