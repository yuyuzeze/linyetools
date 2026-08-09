namespace KikuCaption.Storage;

/// <summary>Storage configuration (PROJECT.md 11, 12).</summary>
public sealed class StorageOptions
{
    /// <summary>Output root. Relative paths resolve against <see cref="BaseDirectory"/>.</summary>
    public string OutputDirectory { get; init; } = "Meetings";

    /// <summary>Base directory used to resolve a relative <see cref="OutputDirectory"/>.</summary>
    public string BaseDirectory { get; init; } = AppContext.BaseDirectory;

    /// <summary>Refuse to start a session with less than this much free disk (GB).</summary>
    public double MinimumFreeSpaceGb { get; init; } = 2;

    /// <summary>Max wait after a final before the readable files are re-exported (ms).</summary>
    public int ExportDebounceMs { get; init; } = 1000;

    /// <summary>Bounded persistence queue capacity (finals). Back-pressure, never silent drop.</summary>
    public int QueueCapacity { get; init; } = 256;

    public string ResolveOutputRoot()
        => Path.IsPathRooted(OutputDirectory)
            ? Path.GetFullPath(OutputDirectory)
            : Path.GetFullPath(Path.Combine(BaseDirectory, OutputDirectory));

    /// <summary>Single SQLite database for all sessions, under the output root.</summary>
    public string ResolveDatabasePath() => Path.Combine(ResolveOutputRoot(), "kikucaption.db");
}
