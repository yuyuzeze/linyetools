using System.Text.Json.Serialization;

namespace KikuCaption.Speech.Protocol;

/// <summary>
/// One protocol message. A single flat record covers every message type; unused fields are
/// null and omitted from the wire form. Property names match the Python worker exactly.
/// </summary>
public sealed record ProtocolMessage
{
    [JsonPropertyName("v")] public int V { get; init; } = ProtocolConstants.Version;
    [JsonPropertyName("type")] public string Type { get; init; } = string.Empty;
    [JsonPropertyName("sessionId")] public string SessionId { get; init; } = string.Empty;
    [JsonPropertyName("seq")] public long Seq { get; init; }

    // initialize
    [JsonPropertyName("model")] public string? Model { get; init; }
    [JsonPropertyName("device")] public string? Device { get; init; }
    [JsonPropertyName("computeType")] public string? ComputeType { get; init; }
    [JsonPropertyName("beamSize")] public int? BeamSize { get; init; }
    [JsonPropertyName("language")] public string? Language { get; init; }
    [JsonPropertyName("modelCacheDir")] public string? ModelCacheDir { get; init; }

    // ready
    [JsonPropertyName("modelLoadMs")] public double? ModelLoadMs { get; init; }

    // audio
    [JsonPropertyName("pcm")] public string? Pcm { get; init; }
    [JsonPropertyName("frames")] public int? Frames { get; init; }

    // partial / final_candidate
    [JsonPropertyName("start")] public double? Start { get; init; }
    [JsonPropertyName("end")] public double? End { get; init; }
    [JsonPropertyName("text")] public string? Text { get; init; }
    [JsonPropertyName("confidence")] public double? Confidence { get; init; }

    // error
    [JsonPropertyName("code")] public string? Code { get; init; }
    [JsonPropertyName("message")] public string? Message { get; init; }

    // flushed
    [JsonPropertyName("count")] public int? Count { get; init; }
}
