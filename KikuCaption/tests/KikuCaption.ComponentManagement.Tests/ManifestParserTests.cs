using KikuCaption.ComponentManagement.Manifest;
using Xunit;

namespace KikuCaption.ComponentManagement.Tests;

public class ManifestParserTests
{
    private const string Sha = "0000000000000000000000000000000000000000000000000000000000000000";

    private static string ValidManifest(string installDir = "models/whisper/small",
        string version = "0.1.1", string type = "WhisperModel", string url = "https://internal.example/m.zip") => $$"""
        {
          "schemaVersion": 1,
          "product": "KikuMemo",
          "channel": "stable",
          "application": {
            "version": "{{version}}",
            "packageUrl": "https://internal.example/app.zip",
            "sha256": "{{Sha}}",
            "sizeBytes": 123,
            "releaseNotesUrl": "https://internal.example/notes",
            "minimumSupportedVersion": "0.1.0"
          },
          "components": [
            {
              "id": "whisper-small",
              "type": "{{type}}",
              "version": "1",
              "url": "{{url}}",
              "sha256": "{{Sha}}",
              "sizeBytes": 486000000,
              "installDirectory": "{{installDir}}",
              "requiredFiles": ["config.json", "model.bin"]
            }
          ]
        }
        """;

    private readonly ManifestParser _parser = new();

    [Fact] // R7A: a well-formed manifest parses with all fields
    public void ValidManifest_Parses()
    {
        var m = _parser.Parse(ValidManifest(), allowInsecureHttp: false);
        Assert.Equal(1, m.SchemaVersion);
        Assert.Equal("KikuMemo", m.Product);
        Assert.Equal("0.1.1", m.Application!.Version);
        var c = Assert.Single(m.Components);
        Assert.Equal(RemoteComponentType.WhisperModel, c.Type);
        Assert.Equal("models/whisper/small", c.InstallDirectory);
        Assert.Equal(2, c.RequiredFiles.Count);
    }

    [Fact] // wrong schema version is rejected
    public void WrongSchema_Rejected()
    {
        var json = ValidManifest().Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2");
        var ex = Assert.Throws<ManifestException>(() => _parser.Parse(json, false));
        Assert.Equal("schema", ex.Code);
    }

    [Fact] // an unknown component type is rejected (whitelist only)
    public void UnknownComponentType_Rejected()
    {
        var ex = Assert.Throws<ManifestException>(() => _parser.Parse(ValidManifest(type: "ArbitraryDll"), false));
        Assert.Equal("component-type", ex.Code);
    }

    [Fact] // a plain-HTTP component URL is rejected by default
    public void NonHttpsUrl_RejectedByDefault()
    {
        var ex = Assert.Throws<ManifestException>(
            () => _parser.Parse(ValidManifest(url: "http://internal.example/m.zip"), allowInsecureHttp: false));
        Assert.Equal("url", ex.Code);
    }

    [Fact] // plain HTTP is accepted only when explicitly allowed
    public void NonHttpsUrl_AllowedWhenOptedIn()
    {
        var m = _parser.Parse(ValidManifest(url: "http://internal.example/m.zip"), allowInsecureHttp: true);
        Assert.Single(m.Components);
    }

    [Theory] // absolute or traversal install directories are rejected (UNC covered in SecurityAndPathsTests)
    [InlineData("C:/Windows/System32")]
    [InlineData("/etc/passwd")]
    [InlineData("../../escape")]
    [InlineData("models/../../x")]
    public void UnsafeInstallDirectory_Rejected(string dir)
    {
        var ex = Assert.Throws<ManifestException>(() => _parser.Parse(ValidManifest(installDir: dir), false));
        Assert.Equal("path", ex.Code);
    }

    [Fact] // an invalid version string is rejected (no lexicographic fallback)
    public void InvalidVersion_Rejected()
    {
        var ex = Assert.Throws<ManifestException>(() => _parser.Parse(ValidManifest(version: "1.0.x"), false));
        Assert.Equal("version", ex.Code);
    }

    [Fact] // a bad sha256 is rejected
    public void BadSha256_Rejected()
    {
        var json = ValidManifest().Replace(Sha, "not-a-hash");
        var ex = Assert.Throws<ManifestException>(() => _parser.Parse(json, false));
        Assert.Equal("sha256", ex.Code);
    }

    [Fact] // malformed JSON does not throw a raw JsonException to callers
    public void MalformedJson_ThrowsManifestException()
    {
        var ex = Assert.Throws<ManifestException>(() => _parser.Parse("{ not json", false));
        Assert.Equal("json", ex.Code);
    }

    [Fact] // a manifest with no components/application is still valid (empty list)
    public void MinimalManifest_Parses()
    {
        var json = """
            { "schemaVersion": 1, "product": "KikuMemo", "channel": "stable" }
            """;
        var m = _parser.Parse(json, false);
        Assert.Null(m.Application);
        Assert.Empty(m.Components);
    }
}
