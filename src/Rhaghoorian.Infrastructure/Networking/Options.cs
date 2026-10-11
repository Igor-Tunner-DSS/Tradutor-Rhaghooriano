namespace Rhaghoorian.Infrastructure.Networking;

public sealed class FirebaseOptions
{
    public string ProjectId { get; set; } = string.Empty;
    /// <summary>"Web API Key" do projeto Firebase. É um identificador público (não é segredo); a segurança vem das Security Rules.</summary>
    public string ApiKey { get; set; } = string.Empty;
    public string DictionaryCollection { get; set; } = "dictionary";
    /// <summary>Marcadores de exclusão (um por palavra apagada) que permitem sincronizar exclusões sem reler tudo.</summary>
    public string DeletionsCollection { get; set; } = "deletions";
    public string MetaCollection { get; set; } = "meta";
    public string MetaDocument { get; set; } = "dictionary";
    /// <summary>Permite apontar para o emulador local (ex.: http://127.0.0.1:8080). Vazio em produção.</summary>
    public string? FirestoreBaseUrl { get; set; }
    public string? AuthBaseUrl { get; set; }
    public string? TokenBaseUrl { get; set; }
    public int TimeoutSeconds { get; set; } = 20;
    /// <summary>Desenvolvimento: usa o Firebase Emulator Suite local (Auth 9099, Firestore 8080, Functions 5001).</summary>
    public bool UseEmulators { get; set; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ProjectId) && !ProjectId.StartsWith("REPLACE", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(ApiKey) && !ApiKey.StartsWith("REPLACE", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// IA: chamada DIRETA ao OpenRouter com a chave pessoal de cada usuário (não há backend: Cloud Functions exigem o plano Blaze).
/// Nenhuma chave existe nestas opções nem em arquivos de configuração.
/// </summary>
public sealed class AiOptions
{
    public string BaseUrl { get; set; } = "https://openrouter.ai/api/v1";
    public string Model { get; set; } = "openrouter/free";
    public int TimeoutSeconds { get; set; } = 45;
    public int MaxRetries { get; set; } = 2;
    /// <summary>Limite diário local de consultas (uso esperado ≈ 20).</summary>
    public int DailyLimit { get; set; } = 20;
}
