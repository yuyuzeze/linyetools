using KikuCaption.Infrastructure.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace KikuCaption.Infrastructure.Tests;

public class KikuCaptionOptionsValidatorTests
{
    private readonly KikuCaptionOptionsValidator _validator = new();

    [Fact]
    public void DefaultOptions_AreValid()
    {
        var result = _validator.Validate(null, new KikuCaptionOptions());

        Assert.True(result.Succeeded, string.Join("; ", result.Failures ?? Array.Empty<string>()));
    }

    [Fact]
    public void InvalidOpacity_Fails()
    {
        var options = new KikuCaptionOptions();
        options.Subtitle.Opacity = 2.0;

        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
    }

    [Fact]
    public void OverlapNotLessThanWindow_Fails()
    {
        var options = new KikuCaptionOptions();
        options.Speech.WindowSeconds = 3;
        options.Speech.OverlapSeconds = 3;

        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
    }

    [Fact]
    public void TranslationEnabledWithoutEndpoint_Fails()
    {
        var options = new KikuCaptionOptions();
        options.Translation.Enabled = true;
        options.Translation.Endpoint = "";

        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
    }
}
