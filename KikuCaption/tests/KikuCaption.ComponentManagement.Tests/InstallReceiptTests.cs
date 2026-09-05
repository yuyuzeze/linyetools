using KikuCaption.ComponentManagement.Installing;
using KikuCaption.Core.Models;
using Xunit;

namespace KikuCaption.ComponentManagement.Tests;

public class InstallReceiptTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kiku_receipt", Guid.NewGuid().ToString("N"));

    private InstallReceipt Sample(ModelVerificationLevel level = ModelVerificationLevel.RuntimeVerified) => new(
        InstallReceiptStore.CurrentSchemaVersion, "whisper-small", "1",
        new string('a', 64), DateTime.UtcNow.ToString("O"), level);

    [Fact] // R7B.1: write + read round-trips version and verification level; no temp file remains
    public void Write_Read_RoundTrips_Atomic()
    {
        Directory.CreateDirectory(_dir);
        InstallReceiptStore.Write(_dir, Sample(ModelVerificationLevel.StructureVerified));

        var read = InstallReceiptStore.TryRead(_dir);
        Assert.NotNull(read);
        Assert.Equal("whisper-small", read!.ComponentId);
        Assert.Equal("1", read.Version);
        Assert.Equal(ModelVerificationLevel.StructureVerified, read.VerificationLevel);
        Assert.False(File.Exists(InstallReceiptStore.PathFor(_dir) + ".tmp")); // atomic: no leftover temp
    }

    [Fact] // a missing receipt reads back null (caller re-verifies)
    public void Missing_ReturnsNull()
    {
        Directory.CreateDirectory(_dir);
        Assert.Null(InstallReceiptStore.TryRead(_dir));
    }

    [Fact] // a malformed receipt reads back null, never throws
    public void Malformed_ReturnsNull()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(InstallReceiptStore.PathFor(_dir), "{ not json");
        Assert.Null(InstallReceiptStore.TryRead(_dir));
    }

    [Fact] // a receipt from an unknown schema version is rejected (re-verify)
    public void WrongSchema_ReturnsNull()
    {
        Directory.CreateDirectory(_dir);
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            SchemaVersion = 999, ComponentId = "whisper-small", Version = "1",
            Sha256 = new string('a', 64), InstalledAtUtc = "x", VerificationLevel = "RuntimeVerified"
        });
        File.WriteAllText(InstallReceiptStore.PathFor(_dir), json);
        Assert.Null(InstallReceiptStore.TryRead(_dir));
    }

    [Fact] // overwriting a receipt (re-install) replaces it cleanly
    public void Overwrite_Replaces()
    {
        Directory.CreateDirectory(_dir);
        InstallReceiptStore.Write(_dir, Sample(ModelVerificationLevel.StructureVerified));
        InstallReceiptStore.Write(_dir, Sample(ModelVerificationLevel.RuntimeVerified));
        Assert.Equal(ModelVerificationLevel.RuntimeVerified, InstallReceiptStore.TryRead(_dir)!.VerificationLevel);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }
}
