namespace KikuCaption.Speech.Protocol;

/// <summary>
/// Constants for the C# ↔ Python worker JSON Lines protocol (PROJECT.md 8.3).
/// Must stay in sync with <c>python/whisper_worker/protocol.py</c>.
/// </summary>
public static class ProtocolConstants
{
    public const int Version = 1;

    /// <summary>Max PCM bytes per audio message: 10 s of 16 kHz mono int16.</summary>
    public const int MaxAudioBytes = 16000 * 2 * 10;

    public static class Types
    {
        // Incoming to worker
        public const string Initialize = "initialize";
        public const string Audio = "audio";
        public const string Flush = "flush";
        public const string Shutdown = "shutdown";

        // Outgoing from worker
        public const string Ready = "ready";
        public const string Partial = "partial";
        public const string FinalCandidate = "final_candidate";
        public const string Flushed = "flushed";
        public const string Error = "error";
    }
}
