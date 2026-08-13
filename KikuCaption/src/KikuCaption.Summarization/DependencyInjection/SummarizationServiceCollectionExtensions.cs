using Microsoft.Extensions.DependencyInjection;

namespace KikuCaption.Summarization.DependencyInjection;

/// <summary>
/// Registers the meeting-summary module (UI-R5C). Reuses the Translation registrations (the company
/// API transport options, the DPAPI secret store, and the shared HttpClient) — the caller must have
/// already called <c>AddKikuCaptionTranslation</c>.
/// </summary>
public static class SummarizationServiceCollectionExtensions
{
    public static IServiceCollection AddKikuCaptionSummarization(this IServiceCollection services, MeetingSummaryOptions? options = null)
    {
        services.AddSingleton(options ?? new MeetingSummaryOptions());
        services.AddSingleton<IMeetingSummaryChunker, MeetingSummaryChunker>();
        services.AddSingleton<IMeetingSummaryExporter, MarkdownMeetingSummaryExporter>();
        services.AddSingleton<IMeetingSummaryClient, OpenAiCompatibleSummaryClient>();
        services.AddSingleton<IMeetingSummaryService, MeetingSummaryService>();
        return services;
    }
}
