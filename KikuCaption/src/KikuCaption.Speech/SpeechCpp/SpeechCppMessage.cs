using System.Text.Json.Serialization;

namespace KikuCaption.Speech.SpeechCpp;

/// <summary>
/// One line of the speech.cpp worker JSON-Lines protocol (protocol 4, see
/// speech.cpp docs/worker.md). A single flat record covers every message this app sends or reads;
/// unused members are null and omitted on the wire. Property names are speech.cpp's snake_case.
/// Only the recognition subset is modelled (chunk / transcribe / cancel out; ready / end / error /
/// cancelled / progress / fatal in); unknown members on an incoming line are ignored.
/// </summary>
public sealed record SpeechCppMessage
{
    [JsonPropertyName("type")] public string Type { get; init; } = string.Empty;

    // Request/answer correlation (every recognition request has a non-empty id).
    [JsonPropertyName("id")] public string? Id { get; init; }

    // chunk (out): ordered audio for a request.
    [JsonPropertyName("seq")] public long? Seq { get; init; }
    [JsonPropertyName("pcm")] public string? Pcm { get; init; }

    // transcribe (out): completes a recognition request.
    [JsonPropertyName("sample_rate")] public int? SampleRate { get; init; }
    [JsonPropertyName("keywords")] public string? Keywords { get; init; }
    [JsonPropertyName("timestamps")] public bool? Timestamps { get; init; }

    // ready (in).
    [JsonPropertyName("protocol")] public int? Protocol { get; init; }
    [JsonPropertyName("version")] public string? Version { get; init; }

    // end (in).
    [JsonPropertyName("text")] public string? Text { get; init; }
    [JsonPropertyName("stop")] public string? Stop { get; init; }
    [JsonPropertyName("untimed")] public string? Untimed { get; init; }
    [JsonPropertyName("segments")] public IReadOnlyList<SpeechCppSegment>? Segments { get; init; }

    // progress (in).
    [JsonPropertyName("done")] public double? Done { get; init; }

    // error / fatal (in).
    [JsonPropertyName("error")] public SpeechCppError? Error { get; init; }
}

/// <summary>One timed segment of a recognition answer (present only when <c>timestamps</c> was set).</summary>
public sealed record SpeechCppSegment
{
    [JsonPropertyName("start")] public double Start { get; init; }
    [JsonPropertyName("end")] public double End { get; init; }
    [JsonPropertyName("text")] public string Text { get; init; } = string.Empty;
}

/// <summary>The <c>error</c> payload of an <c>error</c> or <c>fatal</c> message.</summary>
public sealed record SpeechCppError
{
    [JsonPropertyName("code")] public string? Code { get; init; }
    [JsonPropertyName("option")] public string? Option { get; init; }
    [JsonPropertyName("message")] public string? Message { get; init; }
}
