namespace KikuCaption.Core.Enums;

/// <summary>
/// Which recognition backend serves a session. <see cref="Whisper"/> is the resident Python
/// faster-whisper worker (the project default). <see cref="SpeechCpp"/> is the native
/// <c>speech.exe</c> worker (FastConformer/CTC, non-autoregressive) — lower per-utterance latency
/// for real-time captions. Selected in settings; a session's engine is fixed at start.
/// </summary>
public enum SpeechEngine
{
    Whisper = 0,
    SpeechCpp = 1
}
