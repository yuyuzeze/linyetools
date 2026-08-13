using KikuCaption.Core.Interfaces;
using KikuCaption.Storage.Export;
using KikuCaption.Storage.Recovery;
using KikuCaption.Storage.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KikuCaption.Storage.DependencyInjection;

public static class StorageServiceCollectionExtensions
{
    public static IServiceCollection AddKikuCaptionStorage(
        this IServiceCollection services,
        StorageOptions options,
        string appVersion)
    {
        services.AddSingleton(options);

        services.AddSingleton<SqliteTranscriptRepository>(sp =>
            new SqliteTranscriptRepository(options.ResolveDatabasePath(),
                sp.GetRequiredService<ILogger<SqliteTranscriptRepository>>()));
        services.AddSingleton<ITranscriptStore>(sp => sp.GetRequiredService<SqliteTranscriptRepository>());
        services.AddSingleton<ITranscriptRepository>(sp => sp.GetRequiredService<SqliteTranscriptRepository>());
        services.AddSingleton<ITranslationJobStore>(sp => sp.GetRequiredService<SqliteTranscriptRepository>());

        services.AddSingleton<ITranscriptExporter>(sp =>
            new TranscriptExporter(sp.GetRequiredService<ITranscriptStore>(), appVersion));

        services.AddSingleton<SessionRecorder>();
        services.AddSingleton<SessionRecoveryService>();
        services.AddSingleton<MeetingSessionDeletionService>();
        return services;
    }
}
