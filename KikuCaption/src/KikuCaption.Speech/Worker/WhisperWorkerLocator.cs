namespace KikuCaption.Speech.Worker;

/// <summary>
/// Discovers the worker's Python executable and script by walking up from a starting directory
/// to find <c>python/whisper_worker/main.py</c> (and the sibling venv). Used in development;
/// deployment can pass explicit paths via configuration instead.
/// </summary>
public static class WhisperWorkerLocator
{
    public static (string PythonExecutable, string WorkerScript)? TryLocate(string startDirectory)
    {
        var directory = new DirectoryInfo(startDirectory);
        while (directory is not null)
        {
            var script = Path.Combine(directory.FullName, "python", "whisper_worker", "main.py");
            if (File.Exists(script))
            {
                var python = Path.Combine(directory.FullName, "python", "whisper_worker", ".venv", "Scripts", "python.exe");
                return (python, script);
            }

            directory = directory.Parent;
        }

        return null;
    }
}
