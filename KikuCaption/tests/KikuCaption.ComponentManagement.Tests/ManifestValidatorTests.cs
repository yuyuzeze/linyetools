using KikuCaption.ComponentManagement.Manifest;
using Xunit;

namespace KikuCaption.ComponentManagement.Tests;

public class ManifestValidatorTests
{
    private const string Sha = "0000000000000000000000000000000000000000000000000000000000000000";

    private static RemoteComponent Component(string id, string installDir, string version = "1",
        params string[] requiredFiles)
        => new(id, RemoteComponentType.WhisperModel, version, "https://x/y.zip", Sha, 10, installDir, requiredFiles);

    private static RemoteManifest Manifest(string product = "KikuMemo", string channel = "stable")
        => new(1, product, channel, null, Array.Empty<RemoteComponent>());

    // ---- requiredFiles -------------------------------------------------------------------------

    [Theory] // R7A.1: absolute, drive, UNC, "..", and empty requiredFiles entries are rejected
    [InlineData("/etc/passwd")]
    [InlineData("C:/x")]
    [InlineData("C:x")]
    [InlineData("\\\\server\\share\\f")]
    [InlineData("../escape")]
    [InlineData("a/../../b")]
    [InlineData("")]
    public void RequiredFiles_UnsafeEntry_Rejected(string file)
    {
        var ex = Assert.Throws<ManifestException>(
            () => ManifestValidator.ValidateRequiredFiles("c", "models/small", new[] { file }));
        Assert.Equal("required-file", ex.Code);
    }

    [Fact] // duplicates after normalization (case / separators) are rejected
    public void RequiredFiles_NormalizedDuplicate_Rejected()
    {
        var ex = Assert.Throws<ManifestException>(
            () => ManifestValidator.ValidateRequiredFiles("c", "models/small", new[] { "sub/model.bin", "SUB\\Model.bin" }));
        Assert.Equal("required-file", ex.Code);
    }

    [Fact] // a valid, distinct set is accepted
    public void RequiredFiles_Valid_Accepted()
        => ManifestValidator.ValidateRequiredFiles("c", "models/small", new[] { "config.json", "sub/model.bin" });

    // ---- component collection ------------------------------------------------------------------

    [Fact] // duplicate ids differing only in case are rejected
    public void DuplicateId_CaseInsensitive_Rejected()
    {
        var ex = Assert.Throws<ManifestException>(() => ManifestValidator.ValidateComponentCollection(new[]
        {
            Component("whisper-small", "models/a"),
            Component("Whisper-Small", "models/b")
        }));
        Assert.Equal("duplicate-id", ex.Code);
    }

    [Fact] // identical install directories are rejected
    public void DuplicateInstallDir_Rejected()
    {
        var ex = Assert.Throws<ManifestException>(() => ManifestValidator.ValidateComponentCollection(new[]
        {
            Component("a", "models/whisper/small"),
            Component("b", "models/whisper/Small") // case-insensitive match
        }));
        Assert.Equal("duplicate-dir", ex.Code);
    }

    [Fact] // one install directory nested inside another is rejected
    public void NestedInstallDir_Rejected()
    {
        var ex = Assert.Throws<ManifestException>(() => ManifestValidator.ValidateComponentCollection(new[]
        {
            Component("a", "models/whisper"),
            Component("b", "models/whisper/small")
        }));
        Assert.Equal("nested-dir", ex.Code);
    }

    [Fact] // a sibling with a shared prefix ("small" vs "small-v2") is NOT considered nested
    public void SiblingPrefixDirs_Allowed()
        => ManifestValidator.ValidateComponentCollection(new[]
        {
            Component("a", "models/small"),
            Component("b", "models/small-v2")
        });

    [Fact] // two components that map to the same cache file are rejected
    public void CacheNameCollision_Rejected()
    {
        // "a.b" and "a/b" (invalid chars sanitized to '_') would both become "a_b-1.zip".
        var ex = Assert.Throws<ManifestException>(() => ManifestValidator.ValidateComponentCollection(new[]
        {
            Component("a.b", "models/x"),
            Component("a_b", "models/y")
        }));
        Assert.Equal("cache-collision", ex.Code);
    }

    [Fact] // a clean, distinct set is accepted
    public void DistinctComponents_Accepted()
        => ManifestValidator.ValidateComponentCollection(new[]
        {
            Component("whisper-small", "models/whisper/small"),
            Component("whisper-medium", "models/whisper/medium")
        });

    // ---- identity ------------------------------------------------------------------------------

    [Fact] // matching product + channel passes (case-insensitive)
    public void Identity_Match_Passes()
        => ManifestValidator.ValidateIdentity(Manifest("kikumemo", "STABLE"), "KikuMemo", "stable");

    [Fact] // a different product is rejected, with a stable code
    public void Identity_WrongProduct_Rejected()
    {
        var ex = Assert.Throws<ManifestException>(
            () => ManifestValidator.ValidateIdentity(Manifest(product: "OtherApp"), "KikuMemo", "stable"));
        Assert.Equal("product", ex.Code);
    }

    [Fact] // a different channel is rejected
    public void Identity_WrongChannel_Rejected()
    {
        var ex = Assert.Throws<ManifestException>(
            () => ManifestValidator.ValidateIdentity(Manifest(channel: "beta"), "KikuMemo", "stable"));
        Assert.Equal("channel", ex.Code);
    }
}
