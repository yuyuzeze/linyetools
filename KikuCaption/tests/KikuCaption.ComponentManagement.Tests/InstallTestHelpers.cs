using System.IO.Compression;
using System.Security.Cryptography;
using KikuCaption.ComponentManagement.Progress;

namespace KikuCaption.ComponentManagement.Tests;

/// <summary>Builds deterministic in-memory ZIPs (with SHA-256) for install tests — no real network.</summary>
internal static class ZipFactory
{
    public static (byte[] Bytes, string Sha256) Build(params (string Name, byte[] Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = zip.CreateEntry(name);
                using var s = entry.Open();
                s.Write(content, 0, content.Length);
            }
        }

        var bytes = ms.ToArray();
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return (bytes, sha);
    }

    /// <summary>A minimal faster-whisper-shaped model archive (config/model/tokenizer/vocabulary).</summary>
    public static (byte[] Bytes, string Sha256) Model(int modelBinBytes = 2048)
        => Build(
            ("config.json", "{}"u8.ToArray()),
            ("model.bin", new byte[modelBinBytes]),
            ("tokenizer.json", "{}"u8.ToArray()),
            ("vocabulary.txt", "a\nb\n"u8.ToArray()));
}

/// <summary>An IProgress that records synchronously so tests can assert the phase sequence.</summary>
internal sealed class RecordingProgress : IProgress<ComponentInstallProgress>
{
    public List<ComponentInstallProgress> Reports { get; } = new();
    public List<string> Phases => Reports.Select(r => r.PhaseKey).ToList();
    public void Report(ComponentInstallProgress value) => Reports.Add(value);
}
