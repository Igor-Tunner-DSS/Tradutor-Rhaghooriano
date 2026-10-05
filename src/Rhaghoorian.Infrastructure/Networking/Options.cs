namespace Rhaghoorian.Infrastructure.Networking;

public sealed class FirebaseOptions
{
    public string ProjectId { get; set; } = string.Empty;
    /// <summary>"Web API Key" do projeto Firebase. É um identificador público (não é segredo); a segurança vem das Security Rules.</summary>
    public string ApiKey { get; set; } = string.Empty;
    public string DictionaryCollection { get; set; } = "dictionary";
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

public sealed class AiOptions
{
    /// <summary>URL base da função HTTPS (ex.: https://us-central1-SEU_PROJETO.cloudfunctions.net/ai). Nenhuma chave aqui.</summary>
    public string Endpoint { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; } = 60;
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Endpoint) && !Endpoint.Contains("REPLACE", StringComparison.OrdinalIgnoreCase);
}
