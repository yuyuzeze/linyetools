namespace KikuCaption.Translation;

/// <summary>
/// Builds the system instruction for meeting translation, dispatched by prompt version (UI-R4A fix).
/// A job records the version it was created with, and the queue passes it here — so a job always
/// produces the same prompt regardless of the current UI language or settings, and an unknown version
/// is rejected rather than silently upgraded. The original transcript is NEVER concatenated in; it is
/// always a separate user message.
/// </summary>
public static class TranslationPrompt
{
    /// <summary>Prompt version stamped onto NEW jobs (the current generic prompt).</summary>
    public const int Version = 2;

    /// <summary>True when <paramref name="version"/> has a real prompt implementation.</summary>
    public static bool IsSupported(int version) => version is 1 or 2;

    /// <summary>
    /// Builds the system message for the given version and direction. Throws
    /// <see cref="System.ArgumentOutOfRangeException"/> for an unsupported version — the adapter turns
    /// that into an invalid-configuration failure BEFORE any HTTP call (never a silent latest-version
    /// fallback).
    /// </summary>
    public static string BuildSystem(int version, string sourceCode, string targetCode) => version switch
    {
        1 => BuildV1(),
        2 => BuildV2(sourceCode, targetCode),
        _ => throw new System.ArgumentOutOfRangeException(nameof(version), version, "Unsupported translation prompt version.")
    };

    // v1 (legacy M6): the original fixed JA→ZH meeting prompt. Legacy jobs are always ja→zh, so this
    // reproduces their original behaviour verbatim (direction args are ignored, as in v1).
    private static string BuildV1() =>
        "你是日中会议实时翻译助手。\n\n" +
        "将用户提供的日语会议字幕翻译成自然、简洁、准确的中文。\n\n" +
        "规则：\n" +
        "1. 不总结。\n" +
        "2. 不解释。\n" +
        "3. 不扩写。\n" +
        "4. 不回答原文中的问题。\n" +
        "5. 保留人名、产品名和技术术语。\n" +
        "6. Azure、API、Sprint、Release、Teams等英文技术词可以保留。\n" +
        "7. 只输出中文翻译结果。\n" +
        "8. 不添加“翻译：”、引号、Markdown或其他前缀。";

    // v2 (UI-R4A generic): fixed English instruction parameterized by source/target.
    private static string BuildV2(string sourceCode, string targetCode)
    {
        var source = LanguageName(sourceCode);
        var target = LanguageName(targetCode);
        return
            "You are a professional real-time meeting translator.\n\n" +
            $"Translate the provided meeting transcript from {source} ({sourceCode}) into {target} ({targetCode}).\n\n" +
            "Rules:\n" +
            "1. Preserve the original meaning.\n" +
            "2. Do not summarize.\n" +
            "3. Do not explain.\n" +
            "4. Do not add information.\n" +
            "5. Preserve product names, API names, code, identifiers and technical terms when appropriate.\n" +
            "6. Return only the translated text.";
    }

    /// <summary>
    /// English display name for a language code used inside the (English) prompt. <c>zh</c> is
    /// explicitly Simplified Chinese so the target is unambiguous (UI-R4A fix).
    /// </summary>
    public static string LanguageName(string? code) => (code ?? string.Empty).ToLowerInvariant() switch
    {
        "ja" => "Japanese",
        "zh" => "Simplified Chinese",
        "en" => "English",
        _ => code ?? "the source language"
    };
}
