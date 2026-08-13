using Microsoft.Extensions.Options;

namespace KikuCaption.Infrastructure.Configuration;

public sealed class KikuCaptionOptionsValidator : IValidateOptions<KikuCaptionOptions>
{
    public ValidateOptionsResult Validate(string? name, KikuCaptionOptions options)
    {
        var errors = new List<string>();
        if (options.Speech.BeamSize < 1) errors.Add("Speech.BeamSize must be at least 1.");
        if (options.Recording.FrameRate is < 1 or > 60) errors.Add("Recording.FrameRate must be between 1 and 60.");
        if (options.Recording.AudioSampleRate <= 0) errors.Add("Recording.AudioSampleRate must be positive.");
        if (options.Subtitle.FontSize <= 0) errors.Add("Subtitle.FontSize must be positive.");
        if (options.Subtitle.Opacity is < 0 or > 1) errors.Add("Subtitle.Opacity must be between 0 and 1.");
        if (options.Subtitle.MaxLines < 1) errors.Add("Subtitle.MaxLines must be at least 1.");
        if (options.Storage.MinimumFreeSpaceGb < 0) errors.Add("Storage.MinimumFreeSpaceGb cannot be negative.");
        if (options.Storage.LogRetentionDays < 1) errors.Add("Storage.LogRetentionDays must be at least 1.");
        if (string.IsNullOrWhiteSpace(options.Storage.OutputDirectory)) errors.Add("Storage.OutputDirectory is required.");
        if (options.Translation.TimeoutSeconds <= 0) errors.Add("Translation.TimeoutSeconds must be positive.");
        if (options.Translation.MaxRetries < 0) errors.Add("Translation.MaxRetries cannot be negative.");
        if (options.Translation.MaxQueueLength < 1) errors.Add("Translation.MaxQueueLength must be at least 1.");
        if (options.Translation.Enabled && string.IsNullOrWhiteSpace(options.Translation.Endpoint))
            errors.Add("Translation.Endpoint is required when translation is enabled.");
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
