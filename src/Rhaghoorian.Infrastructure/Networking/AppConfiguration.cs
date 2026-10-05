using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rhaghoorian.Infrastructure.Networking;

/// <summary>
/// Configuração centralizada. Camadas (a última vence): appsettings.json → appsettings.{Ambiente}.json → appsettings.local.json
/// (config/, fora do git) → appsettings.local.json no diretório de dados do usuário. NUNCA contém segredos:
/// a chave do OpenRouter só existe no backend (Secret Manager do Firebase).
/// </summary>
public sealed class AppConfiguration
{
    public string Environment { get; set; } = "Production";
    public FirebaseOptions Firebase { get; set; } = new();
    public AiOptions Ai { get; set; } = new();

    public static AppConfiguration Build(params string?[] jsonLayers)
    {
        JsonNode merged = new JsonObject();
        foreach (var layer in jsonLayers)
        {
            if (string.IsNullOrWhiteSpace(layer)) continue;
            var node = JsonNode.Parse(layer, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (node is JsonObject obj) Merge((JsonObject)merged, obj);
        }

        var config = merged.Deserialize<AppConfiguration>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new AppConfiguration();
        config.ApplyEmulatorDefaults();
        return config;
    }

    private static void Merge(JsonObject target, JsonObject source)
    {
        foreach (var (key, value) in source.ToList())
        {
            if (value is JsonObject child && target[key] is JsonObject existing) Merge(existing, child);
            else target[key] = value?.DeepClone();
        }
    }

    private void ApplyEmulatorDefaults()
    {
        if (!Firebase.UseEmulators) return;
        Firebase.FirestoreBaseUrl ??= "http://127.0.0.1:8080";
        Firebase.AuthBaseUrl ??= "http://127.0.0.1:9099/identitytoolkit.googleapis.com";
        Firebase.TokenBaseUrl ??= "http://127.0.0.1:9099/securetoken.googleapis.com";
        if (!Ai.IsConfigured) Ai.Endpoint = $"http://127.0.0.1:5001/{Firebase.ProjectId}/us-central1/ai";
    }
}
