using System.Text.Json;
using System.Text.Json.Serialization;

namespace KikuCaption.Speech.Protocol;

/// <summary>
/// Serializes/deserializes and validates <see cref="ProtocolMessage"/> as single-line JSON.
/// Untrusted input is validated (version, required fields, PCM size) with structured errors.
/// </summary>
public static class JsonLinesCodec
{
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Serialize(ProtocolMessage message)
        => JsonSerializer.Serialize(message, Options);

    public static ProtocolMessage Parse(string line)
    {
        ProtocolMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<ProtocolMessage>(line, Options);
        }
        catch (JsonException ex)
        {
            throw new ProtocolException("invalid_json", $"无法解析 JSON: {ex.Message}");
        }

        if (message is null)
        {
            throw new ProtocolException("invalid_message", "空消息。");
        }

        if (message.V != ProtocolConstants.Version)
        {
            throw new ProtocolException("version_mismatch", $"协议版本不匹配: {message.V}");
        }

        if (string.IsNullOrEmpty(message.Type))
        {
            throw new ProtocolException("missing_field", "缺少 type。");
        }

        if (string.IsNullOrEmpty(message.SessionId))
        {
            throw new ProtocolException("missing_field", "缺少 sessionId。");
        }

        return message;
    }

    /// <summary>
    /// Builds a validated audio message from raw 16 kHz/mono/int16 PCM. Rejects non-int16
    /// lengths and oversized payloads before they are sent to the worker.
    /// </summary>
    public static ProtocolMessage CreateAudio(string sessionId, long seq, ReadOnlySpan<byte> pcm)
    {
        if (pcm.Length == 0 || pcm.Length % 2 != 0)
        {
            throw new ProtocolException("invalid_pcm", "PCM 长度非法（必须为 int16 的整数倍且非空）。");
        }

        if (pcm.Length > ProtocolConstants.MaxAudioBytes)
        {
            throw new ProtocolException("message_too_large",
                $"音频消息过大: {pcm.Length} > {ProtocolConstants.MaxAudioBytes}");
        }

        return new ProtocolMessage
        {
            Type = ProtocolConstants.Types.Audio,
            SessionId = sessionId,
            Seq = seq,
            Pcm = Convert.ToBase64String(pcm),
            Frames = pcm.Length / 2
        };
    }
}
