using KikuCaption.ComponentManagement.Manifest;
using KikuCaption.ComponentManagement.Paths;
using KikuCaption.ComponentManagement.Security;
using Xunit;

namespace KikuCaption.ComponentManagement.Tests;

public class SecurityAndPathsTests
{
    [Fact] // R7A: a query-string token is never echoed into a log line
    public void UrlSanitizer_DropsQueryToken()
    {
        var s = UrlSanitizer.Sanitize("https://host.example/models/small.zip?sig=SECRET&token=abc123");
        Assert.Equal("https://host.example/models/small.zip", s);
        Assert.DoesNotContain("SECRET", s);
        Assert.DoesNotContain("token", s);
    }

    [Fact] // user-info credentials are not echoed
    public void UrlSanitizer_DropsUserInfo()
    {
        var s = UrlSanitizer.Sanitize("https://user:pass@host.example/a/b.zip?x=1");
        Assert.DoesNotContain("pass", s);
        Assert.DoesNotContain("x=1", s);
        Assert.StartsWith("https://host.example/", s);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not a url")]
    public void UrlSanitizer_HandlesNonUrls(string? input)
    {
        var s = UrlSanitizer.Sanitize(input);
        Assert.True(s is "(none)" or "(redacted)");
    }

    [Fact]
    public void IsContainedWithin_DetectsEscapeAndSiblingPrefix()
    {
        var root = Path.Combine(Path.GetTempPath(), "root");
        Assert.True(ResourceSecurity.IsContainedWithin(root, Path.Combine(root, "a", "b.txt")));
        Assert.True(ResourceSecurity.IsContainedWithin(root, root));
        Assert.False(ResourceSecurity.IsContainedWithin(root, Path.Combine(root, "..", "evil.txt")));
        Assert.False(ResourceSecurity.IsContainedWithin(root, root + "Evil"));
    }

    [Theory]
    [InlineData("models/whisper/small", true)]
    [InlineData("a/b/c", true)]
    [InlineData("../escape", false)]
    [InlineData("a/../b", false)]
    [InlineData("/abs", false)]
    [InlineData("C:/abs", false)]
    [InlineData("\\\\server\\share", false)]
    [InlineData("C:foo", false)]
    [InlineData("", false)]
    public void IsSafeRelativeInstallPath(string path, bool expected)
        => Assert.Equal(expected, ResourceSecurity.IsSafeRelativeInstallPath(path));

    [Fact]
    public void PathResolver_ResolvesContainedInstallDir()
    {
        var root = Path.Combine(Path.GetTempPath(), "comp_root_" + Guid.NewGuid().ToString("N"));
        var resolver = new ComponentPathResolver(root, Path.Combine(root, "downloads"));
        var component = new RemoteComponent("whisper-small", RemoteComponentType.WhisperModel, "1",
            "https://x/y.zip", new string('0', 64), 10, "models/whisper/small", Array.Empty<string>());

        var dir = resolver.ResolveInstallDirectory(component);

        Assert.True(ResourceSecurity.IsContainedWithin(root, dir));
        Assert.EndsWith(Path.Combine("models", "whisper", "small"), dir);
    }

    [Fact] // the cache filename comes from id+version, NOT the URL (so a query can't taint it)
    public void PathResolver_CacheNameFromIdVersion_NotUrl()
    {
        var root = Path.Combine(Path.GetTempPath(), "comp_root2");
        var resolver = new ComponentPathResolver(root, Path.Combine(root, "downloads"));
        var component = new RemoteComponent("whisper-small", RemoteComponentType.WhisperModel, "1",
            "https://x/y.zip?token=SECRET", new string('0', 64), 10, "models/whisper/small", Array.Empty<string>());

        var cached = resolver.GetCachedArchivePath(component);

        Assert.DoesNotContain("SECRET", cached);
        Assert.DoesNotContain("token", cached);
        Assert.EndsWith("whisper-small-1.zip", Path.GetFileName(cached));
        Assert.StartsWith(Path.GetFullPath(Path.Combine(root, "downloads")), cached);
    }

    [Fact] // the default resolver points the cache at %LOCALAPPDATA%\KikuCaption\downloads
    public void PathResolver_Default_UsesLocalAppDataDownloads()
    {
        var resolver = ComponentPathResolver.CreateDefault();
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KikuCaption", "downloads");
        Assert.Equal(Path.GetFullPath(expected), resolver.DownloadsCacheDirectory);
    }
}
