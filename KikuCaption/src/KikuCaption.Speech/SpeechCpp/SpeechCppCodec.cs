using System.Text.Json;
using System.Text.Encodings.Web;
using KikuCaption.Speech.Protocol;

namespace KikuCaption.Speech.SpeechCpp;

/// <summary>
/// Serializes/parses one speech.cpp JSON-Lines message. Writes omit null members and never escape
/// non-ASCII (so Japanese keywords stay compact, valid UTF-8); the worker reads UTF-8 JSON.
/// </summary>
public static class SpeechCppCodec
{
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = false
    };

    /// <summary>Serializes a message to one JSON line (no trailing newline).</summary>
    public static string Serialize(SpeechCppMessage message) => JsonSerializer.Serialize(message, Options);

    /// <summary>Parses one stdout line. Throws <see cref="ProtocolException"/> on a non-object/invalid line.</summary>
    public static SpeechCppMessage Parse(string line)
    {
        try
        {
            var message = JsonSerializer.Deserialize<SpeechCppMessage>(line, Options);
            if (message is null || string.IsNullOrEmpty(message.Type))
            {
                throw new ProtocolException("invalid_message", "speech.cpp 输出缺少 type。");
            }

            return message;
        }
        catch (JsonException ex)
        {
            throw new ProtocolException("invalid_json", $"无法解析 speech.cpp 输出：{ex.Message}");
        }
    }
}
