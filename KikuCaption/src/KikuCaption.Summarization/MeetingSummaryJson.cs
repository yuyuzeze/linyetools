using System.Text.Json;

namespace KikuCaption.Summarization;

/// <summary>
/// Strict, bounded parsing/serialization of the AI's structured JSON (UI-R5C §9). Missing arrays
/// become empty; strings and array lengths are capped so a hostile/huge response can't exhaust memory;
/// model content is treated as plain text (never executed/rendered as HTML). Parsing failures are
/// signalled by returning false — the caller decides whether to attempt the single format-repair.
/// </summary>
public static class MeetingSummaryJson
{
    private const int MaxItems = 200;
    private const int MaxStringLength = 4000;

    /// <summary>Tries to parse one sections object. Tolerates a leading/trailing code fence defensively.</summary>
    public static bool TryParse(string json, out MeetingSummarySections sections)
    {
        sections = new MeetingSummarySections();
        var cleaned = StripFence(json);
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return false;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(cleaned);
        }
        catch (JsonException)
        {
            return false;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var root = doc.RootElement;
            sections = new MeetingSummarySections
            {
                Overview = Str(root, "overview"),
                Topics = Arr(root, "topics"),
                KeyPoints = Arr(root, "keyPoints"),
                Decisions = Arr(root, "decisions"),
                ActionItems = ActionItems(root),
                UnresolvedQuestions = Arr(root, "unresolvedQuestions"),
                Risks = Arr(root, "risks"),
                ProcessSteps = Arr(root, "processSteps"),
                Conclusions = Arr(root, "conclusions")
            };
            return true;
        }
    }

    /// <summary>Serializes intermediate sections as the Reduce input (a JSON array of objects).</summary>
    public static string SerializeParts(IReadOnlyList<MeetingSummarySections> parts)
    {
        var array = parts.Select(p => new
        {
            overview = p.Overview,
            topics = p.Topics,
            keyPoints = p.KeyPoints,
            decisions = p.Decisions,
            actionItems = p.ActionItems.Select(a => new { task = a.Task, owner = a.Owner, due = a.Due }),
            unresolvedQuestions = p.UnresolvedQuestions,
            risks = p.Risks,
            processSteps = p.ProcessSteps,
            conclusions = p.Conclusions
        });
        return JsonSerializer.Serialize(array);
    }

    /// <summary>Safe fallback for compatible gateways that return prose despite the JSON instruction.</summary>
    public static MeetingSummarySections FromPlainText(string content)
    {
        var text = StripFence(content).Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new MeetingSummaryException(
                KikuCaption.Core.Enums.TranslationErrorCode.InvalidResponse,
                "Summary response was empty.");
        }

        return new MeetingSummarySections { Overview = Cap(text) };
    }

    private static string StripFence(string s)
    {
        var t = s.Trim();
        if (t.StartsWith("```", StringComparison.Ordinal))
        {
            int firstNewline = t.IndexOf('\n');
            if (firstNewline >= 0)
            {
                t = t[(firstNewline + 1)..];
            }
            if (t.EndsWith("```", StringComparison.Ordinal))
            {
                t = t[..^3];
            }
        }
        return t.Trim();
    }

    private static string Str(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? Cap(v.GetString() ?? string.Empty)
            : string.Empty;

    private static IReadOnlyList<string> Arr(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var list = new List<string>();
        foreach (var item in v.EnumerateArray())
        {
            if (list.Count >= MaxItems)
            {
                break;
            }
            if (item.ValueKind == JsonValueKind.String)
            {
                var s = item.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                {
                    list.Add(Cap(s.Trim()));
                }
            }
        }
        return list;
    }

    private static IReadOnlyList<MeetingActionItem> ActionItems(JsonElement obj)
    {
        if (!obj.TryGetProperty("actionItems", out var v) || v.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<MeetingActionItem>();
        }

        var list = new List<MeetingActionItem>();
        foreach (var item in v.EnumerateArray())
        {
            if (list.Count >= MaxItems)
            {
                break;
            }
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var task = Str(item, "task");
            if (string.IsNullOrWhiteSpace(task))
            {
                continue;
            }
            list.Add(new MeetingActionItem(task, Str(item, "owner"), Str(item, "due")));
        }
        return list;
    }

    private static string Cap(string s) => s.Length <= MaxStringLength ? s : s[..MaxStringLength];
}
