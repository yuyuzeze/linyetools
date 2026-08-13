using KikuCaption.Core.Interfaces;
using KikuCaption.Core.Models;
using Microsoft.Extensions.Logging;

namespace KikuCaption.Speech.Worker;

/// <summary>Keeps at most one initialized recognizer for transfer to the next meeting.</summary>
public sealed class SpeechRecognizerPrewarmer : IAsyncDisposable
{
    private readonly Func<ISpeechRecognizer> _factory;
    private readonly ILogger<SpeechRecognizerPrewarmer> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ISpeechRecognizer? _cached;
    private SpeechOptions? _cachedOptions;
    private bool _disposed;

    public SpeechRecognizerPrewarmer(Func<ISpeechRecognizer> factory, ILogger<SpeechRecognizerPrewarmer> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task PrewarmAsync(SpeechOptions options, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_cached is not null && Equivalent(_cachedOptions, options)) return;
            await DisposeCachedAsync().ConfigureAwait(false);
            var recognizer = _factory();
            try
            {
                await recognizer.InitializeAsync(options, cancellationToken).ConfigureAwait(false);
                _cached = recognizer;
                _cachedOptions = options;
                _logger.LogInformation("Whisper recognizer prewarmed for language {Language}.", options.Language);
            }
            catch { await recognizer.DisposeAsync().ConfigureAwait(false); throw; }
        }
        finally { _gate.Release(); }
    }

    public async Task<ISpeechRecognizer> AcquireAsync(SpeechOptions options, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_cached is not null && Equivalent(_cachedOptions, options))
            {
                var ready = _cached;
                _cached = null;
                _cachedOptions = null;
                return ready;
            }
            await DisposeCachedAsync().ConfigureAwait(false);
            var recognizer = _factory();
            try { await recognizer.InitializeAsync(options, cancellationToken).ConfigureAwait(false); }
            catch { await recognizer.DisposeAsync().ConfigureAwait(false); throw; }
            return recognizer;
        }
        finally { _gate.Release(); }
    }

    public async Task ClearAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await DisposeCachedAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task DisposeCachedAsync()
    {
        var value = _cached;
        _cached = null;
        _cachedOptions = null;
        if (value is not null) await value.DisposeAsync().ConfigureAwait(false);
    }

    private static bool Equivalent(SpeechOptions? a, SpeechOptions b)
        => a is not null && a.Model == b.Model && a.Device == b.Device && a.ComputeType == b.ComputeType
           && a.Language == b.Language && a.BeamSize == b.BeamSize && a.ModelCacheDirectory == b.ModelCacheDirectory
           && a.InitialPrompt == b.InitialPrompt && (a.Hotwords ?? []).SequenceEqual(b.Hotwords ?? []);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            await DisposeCachedAsync().ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
}
