using System.IO;

namespace KikuCaption.App.Services.Python;

/// <summary>
/// The single set of path + version rules for the managed Python environment (R7C), shared in spirit
/// with scripts/setup-python.ps1 so the UI installer and the fallback script never diverge:
/// <list type="bullet">
/// <item>managed venv → <c>%LOCALAPPDATA%\KikuCaption\python\venv</c> (user-writable, survives app updates);</item>
/// <item>staging / backup live beside it as <c>.venv-staging-&lt;guid&gt;</c> / <c>.venv-backup-&lt;guid&gt;</c>;</item>
/// <item>supported interpreter range: 64-bit CPython 3.12–3.13 (evidence: the pinned deps in
/// requirements-lock.txt and the known-good 3.13 venv).</item>
/// </list>
/// </summary>
public static class PythonPaths
{
    // Supported CPython minor range (inclusive), x64 only.
    public const int MinMinor = 12;
    public const int MaxMinor = 13;
    public const int RequiredMajor = 3;

    public static string PythonRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KikuCaption", "python");

    /// <summary>The official managed venv directory.</summary>
    public static string ManagedVenvDir => Path.Combine(PythonRoot, "venv");

    /// <summary>The venv's python.exe.</summary>
    public static string VenvPython(string venvDir) => Path.Combine(venvDir, "Scripts", "python.exe");

    public static string NewStagingDir() => Path.Combine(PythonRoot, $".venv-staging-{Guid.NewGuid():N}");
    public static string NewBackupDir() => Path.Combine(PythonRoot, $".venv-backup-{Guid.NewGuid():N}");

    /// <summary>True if a python.exe exists inside the venv (a minimal "the venv exists" check).</summary>
    public static bool VenvExists(string? venvDir)
        => !string.IsNullOrWhiteSpace(venvDir) && File.Exists(VenvPython(venvDir!));

    /// <summary>Is the given (major, minor) a supported interpreter?</summary>
    public static bool IsSupportedVersion(int major, int minor)
        => major == RequiredMajor && minor >= MinMinor && minor <= MaxMinor;

    public static string SupportedRangeText => $"CPython {RequiredMajor}.{MinMinor}–{RequiredMajor}.{MaxMinor} (64-bit)";
}
