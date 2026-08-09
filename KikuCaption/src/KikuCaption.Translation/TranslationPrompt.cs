namespace KikuCaption.Translation;

/// <summary>
/// The fixed, testable system instruction for JA→ZH meeting translation (M6 §3, PROJECT.md 8.5).
/// The original text is NEVER concatenated into this prompt — it is sent as a separate user message.
/// </summary>
public static class TranslationPrompt
{
    public const string System =
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
}
