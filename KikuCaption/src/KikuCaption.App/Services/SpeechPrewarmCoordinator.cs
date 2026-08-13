using KikuCaption.Core.Interfaces;
using KikuCaption.Infrastructure.Configuration;
using KikuCaption.Speech.Worker;
using Microsoft.Extensions.Logging;

namespace KikuCaption.App.Services;

/// <summary>Applies the optional user preference for keeping one Whisper worker/model warm.</summary>
public sealed class SpeechPrewarmCoordinator
{
    private readonly SpeechRecognizerPrewarmer _prewarmer;
    private readonly ISpeechOptionsProvider _options;
    private readonly ILogger<SpeechPrewarmCoordinator> _logger;
    private CancellationTokenSource? _operation;

    public SpeechPrewarmCoordinator(SpeechRecognizerPrewarmer prewarmer, ISpeechOptionsProvider options,
        ILogger<SpeechPrewarmCoordinator> logger)
    {
        _prewarmer = prewarmer;
        _options = options;
        _logger = logger;
    }

    public async Task ApplyAsync(bool enabled, string language)
    {
        _operation?.Cancel();
        _operation?.Dispose();
        _operation = new CancellationTokenSource();
        try
        {
            if (enabled)
                await _prewarmer.PrewarmAsync(_options.ForLanguage(language), _operation.Token);
            else
                await _prewarmer.ClearAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // Prewarming is only an optimization; never prevent normal on-demand recognition.
            _logger.LogWarning(ex, "Whisper background prewarm failed.");
        }
    }
}
