using System.IO.Compression;
using KikuCaption.ComponentManagement.Archives;
using Xunit;

namespace KikuCaption.ComponentManagement.Tests;

public class SafeZipExtractorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kiku_zip", Guid.NewGuid().ToString("N"));

    private string MakeZip(string name, Action<ZipArchive> build)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, name);
        using var fs = new FileStream(path, FileMode.CreateNew);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        build(zip);
        return path;
    }

    private static void AddFile(ZipArchive zip, string entryName, byte[] content, int? externalAttributes = null)
    {
        var entry = zip.CreateEntry(entryName);
        if (externalAttributes is { } attr) entry.ExternalAttributes = attr;
        using var s = entry.Open();
        s.Write(content, 0, content.Length);
    }

    private string Dest() => Path.Combine(_dir, "out");
    private readonly SafeZipExtractor _extractor = new();

    [Fact] // R7A: a normal archive extracts into the destination
    public void NormalArchive_Extracts()
    {
        var zip = MakeZip("ok.zip", z =>
        {
            AddFile(z, "config.json", "{}"u8.ToArray());
            AddFile(z, "sub/model.bin", new byte[100]);
        });

        var result = _extractor.Extract(zip, Dest(), maxEntries: 100, maxTotalBytes: 1_000_000);

        Assert.Equal(2, result.FileCount);
        Assert.True(File.Exists(Path.Combine(Dest(), "config.json")));
        Assert.True(File.Exists(Path.Combine(Dest(), "sub", "model.bin")));
    }

    [Fact] // a "../" traversal entry is rejected (Zip-Slip)
    public void TraversalEntry_Rejected()
    {
        var zip = MakeZip("slip.zip", z => AddFile(z, "../evil.txt", new byte[10]));
        var ex = Assert.Throws<ArchiveValidationException>(() => _extractor.Extract(zip, Dest(), 100, 1_000_000));
        Assert.Equal("path", ex.Code);
        Assert.False(File.Exists(Path.Combine(_dir, "evil.txt")));
    }

    [Fact] // an absolute-path entry is rejected
    public void AbsoluteEntry_Rejected()
    {
        var zip = MakeZip("abs.zip", z => AddFile(z, "C:/evil.txt", new byte[10]));
        var ex = Assert.Throws<ArchiveValidationException>(() => _extractor.Extract(zip, Dest(), 100, 1_000_000));
        Assert.Equal("path", ex.Code);
    }

    [Fact] // exceeding the entry-count limit is rejected
    public void TooManyEntries_Rejected()
    {
        var zip = MakeZip("many.zip", z =>
        {
            for (int i = 0; i < 10; i++) AddFile(z, $"f{i}.txt", new byte[1]);
        });
        var ex = Assert.Throws<ArchiveValidationException>(() => _extractor.Extract(zip, Dest(), maxEntries: 5, maxTotalBytes: 1_000_000));
        Assert.Equal("entry-count", ex.Code);
    }

    [Fact] // exceeding the total uncompressed size is rejected (zip bomb)
    public void TotalSizeBomb_Rejected()
    {
        var zip = MakeZip("bomb.zip", z => AddFile(z, "big.bin", new byte[2000]));
        var ex = Assert.Throws<ArchiveValidationException>(() => _extractor.Extract(zip, Dest(), maxEntries: 100, maxTotalBytes: 100));
        Assert.Equal("bomb", ex.Code);
    }

    [Fact] // a symlink entry (unix symlink mode bits) is rejected
    public void SymlinkEntry_Rejected()
    {
        // 0xA1FF << 16: high 16 bits carry the unix mode; 0xA000 = S_IFLNK.
        int symlinkAttrs = unchecked((int)((uint)0xA1FF << 16));
        var zip = MakeZip("link.zip", z => AddFile(z, "link", "target/path"u8.ToArray(), symlinkAttrs));

        var ex = Assert.Throws<ArchiveValidationException>(() => _extractor.Extract(zip, Dest(), 100, 1_000_000));
        Assert.Equal("symlink", ex.Code);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }
}
