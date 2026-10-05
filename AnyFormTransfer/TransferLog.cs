using System.Text;

namespace AnyFormTransfer;

public sealed class TransferLog : IDisposable
{
    private readonly StreamWriter? _writer;
    private readonly object _gate = new();

    public TransferLog(string? logFilePath)
    {
        if (string.IsNullOrWhiteSpace(logFilePath))
        {
            return;
        }

        var full = Path.GetFullPath(logFilePath);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _writer = new StreamWriter(
            new FileStream(full, FileMode.Append, FileAccess.Write, FileShare.Read),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true))
        {
            AutoFlush = true
        };
    }

    public void Info(string message) => Write("INFO", message);

    public void Warn(string message) => Write("WARN", message);

    public void Error(string message) => Write("ERROR", message);

    private void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        lock (_gate)
        {
            Console.WriteLine(line);
            _writer?.WriteLine(line);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
        }
    }
}
