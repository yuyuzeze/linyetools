using Microsoft.Extensions.Options;

namespace KikuCaption.Infrastructure.Configuration;

/// <summary>
/// Validates configuration at startup (PROJECT.md 11: "配置启动时必须验证").
/// Runs on the first access of <see cref="IOptions{TOptions}.Value"/>, which the App
/// forces during startup so bad configuration fails fast with a clear message.
/// </summary>
public sealed class KikuCaptionOptionsValidator : IValidateOptions<KikuCaptionOptions>
{
    public ValidateOptionsResult Validate(string? name, KikuCaptionOptions options)
    {
        var errors = new List<string>();

        // Speech
        if (options.Speech.WindowSeconds <= 0)
            errors.Add("Speech.WindowSeconds 必须大于 0。");
        if (options.Speech.OverlapSeconds < 0 || options.Speech.OverlapSeconds >= options.Speech.WindowSeconds)
            errors.Add("Speech.OverlapSeconds 必须 >= 0 且小于 Speech.WindowSeconds。");
        if (options.Speech.BeamSize < 1)
            errors.Add("Speech.BeamSize 必须 >= 1。");

        // Recording
        if (options.Recording.FrameRate is < 1 or > 60)
            errors.Add("Recording.FrameRate 必须在 1..60 之间。");
        if (options.Recording.AudioSampleRate <= 0)
            errors.Add("Recording.AudioSampleRate 必须大于 0。");

        // Subtitle
        if (options.Subtitle.FontSize <= 0)
            errors.Add("Subtitle.FontSize 必须大于 0。");
        if (options.Subtitle.Opacity is < 0 or > 1)
            errors.Add("Subtitle.Opacity 必须在 0..1 之间。");
        if (options.Subtitle.MaxLines < 1)
            errors.Add("Subtitle.MaxLines 必须 >= 1。");

        // Storage
        if (options.Storage.MinimumFreeSpaceGb < 0)
            errors.Add("Storage.MinimumFreeSpaceGb 不能为负数。");
        if (options.Storage.LogRetentionDays < 1)
            errors.Add("Storage.LogRetentionDays 必须 >= 1。");
        if (string.IsNullOrWhiteSpace(options.Storage.OutputDirectory))
            errors.Add("Storage.OutputDirectory 不能为空。");

        // Translation
        if (options.Translation.TimeoutSeconds <= 0)
            errors.Add("Translation.TimeoutSeconds 必须大于 0。");
        if (options.Translation.MaxRetries < 0)
            errors.Add("Translation.MaxRetries 不能为负数。");
        if (options.Translation.MaxQueueLength < 1)
            errors.Add("Translation.MaxQueueLength 必须 >= 1。");
        if (options.Translation.Enabled && string.IsNullOrWhiteSpace(options.Translation.Endpoint))
            errors.Add("启用翻译（Translation.Enabled = true）时必须配置 Translation.Endpoint。");

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }
}
