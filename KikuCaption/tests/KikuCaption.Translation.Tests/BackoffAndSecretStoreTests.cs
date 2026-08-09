using System.Security.Cryptography;
using KikuCaption.Translation.Security;
using Xunit;

namespace KikuCaption.Translation.Tests;

public sealed class BackoffTests
{
    [Fact] // queue 8: exponential growth
    public void Delay_GrowsExponentially()
    {
        var rng = new Random(1);
        var d1 = TranslationBackoff.ComputeDelay(1, null, rng, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60));
        var d2 = TranslationBackoff.ComputeDelay(2, null, rng, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60));
        var d3 = TranslationBackoff.ComputeDelay(3, null, rng, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60));

        // Base (before jitter) is 1, 2, 4s; even with up to +50% jitter the ranges are ordered.
        Assert.InRange(d1.TotalSeconds, 1.0, 1.5);
        Assert.InRange(d2.TotalSeconds, 2.0, 3.0);
        Assert.InRange(d3.TotalSeconds, 4.0, 6.0);
    }

    [Fact] // queue 9: jitter varies and stays within bounds
    public void Jitter_WithinBounds_AndVaries()
    {
        var rng = new Random(42);
        var values = Enumerable.Range(0, 20)
            .Select(_ => TranslationBackoff.ComputeDelay(3, null, rng, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60)).TotalSeconds)
            .ToList();

        Assert.All(values, v => Assert.InRange(v, 4.0, 6.0)); // 4s base + 0..50%
        Assert.True(values.Distinct().Count() > 1, "jitter should vary");
    }

    [Fact] // Retry-After honored
    public void RetryAfter_Honored_WhenLarger()
    {
        var rng = new Random(1);
        var delay = TranslationBackoff.ComputeDelay(1, TimeSpan.FromSeconds(20), rng, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60));
        Assert.Equal(20, delay.TotalSeconds, 0);
    }

    [Fact]
    public void MaxDelay_Caps()
    {
        var rng = new Random(1);
        var delay = TranslationBackoff.ComputeDelay(20, null, rng, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10));
        Assert.InRange(delay.TotalSeconds, 10.0, 15.0); // capped base 10 + jitter
    }
}

/// <summary>DPAPI secret store (M6 §8). Windows-only; uses a temp directory, never a real key.</summary>
public sealed class DpapiSecretStoreTests : IDisposable
{
    private readonly string _dir;

    public DpapiSecretStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "kiku_secret_tests", Guid.NewGuid().ToString("N"));
    }

    [Fact] // security 1: save + read roundtrip
    public void SaveThenRead_Roundtrips()
    {
        var store = new DpapiTranslationSecretStore(_dir);
        Assert.False(store.IsConfigured);

        store.Save("dummy-nonproduction-key-123");
        Assert.True(store.IsConfigured);
        Assert.Equal("dummy-nonproduction-key-123", store.Read());
    }

    [Fact] // security 2: replace
    public void Save_Replaces()
    {
        var store = new DpapiTranslationSecretStore(_dir);
        store.Save("first");
        store.Save("second");
        Assert.Equal("second", store.Read());
    }

    [Fact] // security 3: delete
    public void Delete_RemovesKey()
    {
        var store = new DpapiTranslationSecretStore(_dir);
        store.Save("to-remove");
        store.Delete();
        Assert.False(store.IsConfigured);
        Assert.Throws<InvalidOperationException>(() => store.Read());
    }

    [Fact] // security 4: corrupt ciphertext → throws, file NOT deleted
    public void CorruptCiphertext_Throws_AndKeepsFile()
    {
        var store = new DpapiTranslationSecretStore(_dir);
        store.Save("valid");
        var file = Path.Combine(_dir, "translation.key");
        File.WriteAllBytes(file, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }); // corrupt

        Assert.Throws<CryptographicException>(() => store.Read());
        Assert.True(File.Exists(file)); // ciphertext preserved for retry/re-enter
    }

    [Fact] // security 5/6/7: only ciphertext on disk — plaintext is not present
    public void OnDisk_IsCiphertext_NotPlaintext()
    {
        var store = new DpapiTranslationSecretStore(_dir);
        const string secret = "PLAINTEXT-SHOULD-NOT-APPEAR";
        store.Save(secret);

        var bytes = File.ReadAllBytes(Path.Combine(_dir, "translation.key"));
        var asText = System.Text.Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain(secret, asText);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}
