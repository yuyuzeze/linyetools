using KikuCaption.Core.Interfaces;
using KikuCaption.Core.Models;
using KikuCaption.Speech.Worker;
using Microsoft.Extensions.Logging;

namespace KikuCaption.App.Services;

/// <summary>Applies the optional user preference for keeping one Whisper worker/model warm.</summary>
public sealed class SpeechPrewarmCoordinator
{
    private readonly SpeechRecognizerPrewarmer _prewarmer;
    private readonly ISpeechOptionsProvider _options;
    private readonly IModelUsageRegistry _usage;
    private readonly ILogger<SpeechPrewarmCoordinator> _logger;
    private CancellationTokenSource? _operation;
    private IModelUsageLease? _lease; // R7B.1: held while the small model is warm

    public SpeechPrewarmCoordinator(SpeechRecognizerPrewarmer prewarmer, ISpeechOptionsProvider options,
        IModelUsageRegistry usage, ILogger<SpeechPrewarmCoordinator> logger)
    {
        _prewarmer = prewarmer;
        _options = options;
        _usage = usage;
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
            {
                // Resolve the (possibly managed) small model through the shared locator.
                await _prewarmer.PrewarmAsync(_options.ForLanguage(language, WhisperModelPurpose.Prewarm), _operation.Token);
                _lease ??= _usage.Acquire(ModelCatalog.Small.ComponentId, WhisperModelPurpose.Prewarm);
            }
            else
            {
                await _prewarmer.ClearAsync();
                ReleaseLease();
            }
        }
        catch (OperationCanceledException) { ReleaseLease(); }
        catch (Exception ex)
        {
            // Prewarming is only an optimization; never prevent normal on-demand recognition.
            ReleaseLease();
            _logger.LogWarning(ex, "Whisper background prewarm failed.");
        }
    }

    private void ReleaseLease()
    {
        _lease?.Dispose();
        _lease = null;
    }
}
