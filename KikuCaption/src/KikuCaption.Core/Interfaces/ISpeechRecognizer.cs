using KikuCaption.Core.Models;

namespace KikuCaption.Core.Interfaces;

/// <summary>
/// Streaming speech recognizer (PROJECT.md 8.3). Backed by a resident Python faster-whisper
/// worker: the model is loaded once in <see cref="InitializeAsync"/> and reused for the whole
/// session. <see cref="RecognizeAsync"/> consumes normalized 16 kHz/mono/int16 audio and yields
/// timestamped partial / final-candidate updates.
/// </summary>
public interface ISpeechRecognizer : IAsyncDisposable
{
    Task InitializeAsync(SpeechOptions options, CancellationToken cancellationToken);

    IAsyncEnumerable<TranscriptUpdate> RecognizeAsync(
        IAsyncEnumerable<AudioChunk> audio,
        CancellationToken cancellationToken);
}
