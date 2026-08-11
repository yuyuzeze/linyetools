using KikuCaption.Infrastructure.Configuration;
using KikuCaption.Infrastructure.Diagnostics;
using KikuCaption.Infrastructure.Logging;
using Xunit;

namespace KikuCaption.Infrastructure.Tests;

public class SensitiveInfoScannerTests
{
    [Theory] // 21: real secret VALUES are flagged
    [InlineData("Authorization: Bearer FAKEKEY1234567890abcdef")]
    [InlineData("\"api-key\": \"ABCDEFGHIJKLMNOP1234\"")]
    [InlineData("token = sk-ABCDEFGHIJKLMNOP1234")]
    public void Flags_PlaintextSecrets(string line)
    {
        Assert.NotEmpty(SensitiveInfoScanner.ScanText("f.cs", line));
    }

    [Theory] // header NAMES / mode strings are NOT flagged (avoid false positives on our own code)
    [InlineData("\"AuthenticationMode\": \"Bearer\"")]
    [InlineData("\"HeaderName\": \"Authorization\"")]
    [InlineData("public const string HttpClientName = \"translation\";")]
    [InlineData("request.Headers.TryAddWithoutValidation(header, ReadSecret());")]
    [InlineData("new AuthenticationHeaderValue(\"Bearer\", ReadSecret())")]
    public void DoesNotFlag_HeaderNamesOrCode(string line)
    {
        Assert.Empty(SensitiveInfoScanner.ScanText("f.cs", line));
    }

    [Fact] // the actual repo source + config contains no plaintext secrets (tests/bin/obj excluded)
    public void RepoSourceAndConfig_HaveNoSecrets()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);

        var exclude = new[] { "bin", "obj", ".venv", "tests", ".git", "node_modules", "publish" };
        var findings = new List<ScanFinding>();
        findings.AddRange(SensitiveInfoScanner.ScanDirectory(Path.Combine(root!, "src"),
            new[] { ".cs", ".json", ".xaml" }, exclude));
        findings.AddRange(SensitiveInfoScanner.ScanDirectory(Path.Combine(root!, "docs"),
            new[] { ".md" }, exclude));

        Assert.True(findings.Count == 0, "Unexpected secrets: " + string.Join("; ", findings.Select(f => $"{f.File}:{f.Line} {f.Pattern}")));
    }

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "KikuCaption.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName;
    }
}

public class LogRetentionTests
{
    [Fact] // 20: deletes over-retention dated logs, keeps recent, ignores others
    public void CleanupOldLogs_DeletesExpiredOnly()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kiku_logret", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var now = new DateTime(2026, 8, 9, 0, 0, 0, DateTimeKind.Utc);
        File.WriteAllText(Path.Combine(dir, "app-20260709.log"), "old");   // 31 days → delete
        File.WriteAllText(Path.Combine(dir, "app-20260808.log"), "recent"); // 1 day → keep
        File.WriteAllText(Path.Combine(dir, "app-.log"), "current");         // non-dated → keep
        File.WriteAllText(Path.Combine(dir, "meeting.mp4"), "user data");     // never touched

        int deleted = LogRetention.CleanupOldLogs(dir, retentionDays: 14, nowUtc: now);

        Assert.Equal(1, deleted);
        Assert.False(File.Exists(Path.Combine(dir, "app-20260709.log")));
        Assert.True(File.Exists(Path.Combine(dir, "app-20260808.log")));
        Assert.True(File.Exists(Path.Combine(dir, "meeting.mp4")));

        Directory.Delete(dir, true);
    }
}

public class UserSettingsStoreTests
{
    [Fact] // save/load roundtrip
    public void SaveThenLoad_Roundtrips()
    {
        var dir = TempDir();
        var store = new UserSettingsStore(dir);
        store.Save(new UserSettings { RecognitionLanguage = "zh", FrameRate = 20, TranslationEnabled = true, TimelineWidth = 500 });

        var (loaded, reset) = store.Load();
        Assert.False(reset);
        Assert.Equal("zh", loaded.RecognitionLanguage);
        Assert.Equal(20, loaded.FrameRate);
        Assert.True(loaded.TranslationEnabled);
        Assert.Equal(500, loaded.TimelineWidth);

        Directory.Delete(dir, true);
    }

    [Fact] // 10: corrupt settings → safe defaults + backup, not a silent overwrite
    public void Corrupt_ReturnsDefaults_AndBacksUp()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{ this is not valid json ");
        var store = new UserSettingsStore(dir);

        var (loaded, reset) = store.Load();
        Assert.True(reset);
        Assert.Equal("ja", loaded.RecognitionLanguage); // default
        Assert.Contains(Directory.GetFiles(dir), f => f.Contains(".corrupt-"));

        Directory.Delete(dir, true);
    }

    [Fact] // 11: settings never contain the API key or the (credential-bearing) endpoint
    public void Serialized_HasNoApiKeyOrEndpoint()
    {
        var dir = TempDir();
        var store = new UserSettingsStore(dir);
        store.Save(new UserSettings { TranslationEnabled = true, TranslationModel = "gpt-4.1", TranslationProxy = "http://proxy:8080" });

        var json = File.ReadAllText(store.FilePath);
        Assert.DoesNotContain("ApiKey", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
        // The endpoint (may embed a credential) is never persisted here — it is DPAPI-encrypted.
        Assert.DoesNotContain("Endpoint", json, StringComparison.OrdinalIgnoreCase);

        Directory.Delete(dir, true);
    }

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kiku_settings", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
