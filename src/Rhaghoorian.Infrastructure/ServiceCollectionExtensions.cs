using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Rhaghoorian.Core.Abstractions;
using Rhaghoorian.Core.Ai;
using Rhaghoorian.Core.Importing;
using Rhaghoorian.Core.Linguistics;
using Rhaghoorian.Core.Services;
using Rhaghoorian.Core.Translation;
using Rhaghoorian.Infrastructure.Firebase;
using Rhaghoorian.Infrastructure.Networking;
using Rhaghoorian.Infrastructure.SQLite;

namespace Rhaghoorian.Infrastructure;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Núcleo linguístico + SQLite + Firebase (Auth/Firestore) + cliente de IA.
    /// O chamador (aplicativo) precisa fornecer os serviços de plataforma: <see cref="ISecureTokenStore"/> e <see cref="IConnectivityService"/>.
    /// </summary>
    public static IServiceCollection AddRhaghoInfrastructure(this IServiceCollection services, AppConfiguration config, string databasePath)
    {
        services.AddSingleton(config);
        services.AddSingleton(config.Firebase);
        services.AddSingleton(config.Ai);
        // Tempos limite são controlados por HttpGateway (por requisição), não pelo HttpClient.
        services.AddSingleton(new HttpClient { Timeout = Timeout.InfiniteTimeSpan });

        // Núcleo (regras determinísticas)
        services.AddSingleton<Rhaghoorian.Core.Text.IRhaghoScript, Rhaghoorian.Core.Text.LatinRhaghoScript>();
        services.AddSingleton<IRhaghoLinguisticService, RhaghoLinguisticService>();
        services.AddSingleton<IDictionaryFileParser, DictionaryFileParser>();
        services.AddSingleton<ISuggestionService, SuggestionService>();
        services.AddSingleton<ITranslationService, TranslationService>();
        services.AddSingleton<AiContextBuilder>();
        services.AddSingleton<ITranslationWorkflow, TranslationWorkflow>();
        services.AddSingleton<IDictionaryService, DictionaryService>();
        services.AddSingleton<ISyncService, SyncService>();
        services.AddSingleton<IDictionaryImportService, DictionaryImportService>();
        services.AddSingleton<IDictionarySeeder, DictionarySeeder>();
        services.AddSingleton<IWordAiAssistant, WordAiAssistant>();

        // SQLite local
        services.AddSingleton(sp => new SqliteDatabase(databasePath, sp.GetRequiredService<ILogger<SqliteDatabase>>()));
        services.AddSingleton<SqliteDictionaryRepository>();
        services.AddSingleton<IDictionaryRepository>(sp => sp.GetRequiredService<SqliteDictionaryRepository>());
        services.AddSingleton<IDictionaryLookup>(sp => sp.GetRequiredService<SqliteDictionaryRepository>());
        services.AddSingleton<ITranslationHistoryRepository, SqliteTranslationHistoryRepository>();
        services.AddSingleton<ISettingsStore, SqliteSettingsStore>();
        services.AddSingleton<IImportIssueRepository, SqliteImportIssueRepository>();

        // Firebase (plano Spark: somente Authentication + Firestore)
        services.AddSingleton<FirebaseAuthService>();
        services.AddSingleton<IAuthService>(sp => sp.GetRequiredService<FirebaseAuthService>());
        services.AddSingleton<IRemoteDictionaryStore, FirestoreDictionaryStore>();

        // IA: OpenRouter direto, com a chave pessoal de cada usuário (protegida por DPAPI) e limite diário local
        services.AddSingleton<IAiKeyStore, SecureAiKeyStore>();
        services.AddSingleton<IAiUsageLimiter>(sp => new SettingsAiUsageLimiter(sp.GetRequiredService<ISettingsStore>(), config.Ai.DailyLimit));
        services.AddSingleton<IAiKeyManager, OpenRouterKeyManager>();
        services.AddSingleton<IAiClient, OpenRouterAiClient>();
        return services;
    }
}
