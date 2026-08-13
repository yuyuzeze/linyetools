using System.IO;
using System.Runtime.InteropServices;

namespace KikuCaption.App.Playback;

public sealed class PlaybackEngineUnavailableException : Exception
{
    public PlaybackEngineUnavailableException(string message) : base(message) { }
    public PlaybackEngineUnavailableException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Resolves the architecture-specific native LibVLC shipped by VideoLAN.LibVLC.Windows.</summary>
public static class LibVlcRuntimeLocator
{
    public static string Resolve(string? baseDirectory = null)
    {
        var architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "win-x64",
            Architecture.X86 => "win-x86",
            Architecture.Arm64 => "win-arm64",
            _ => throw new PlaybackEngineUnavailableException(
                $"Unsupported process architecture: {RuntimeInformation.ProcessArchitecture}.")
        };
        var root = Path.GetFullPath(baseDirectory ?? AppContext.BaseDirectory);
        var directory = Path.Combine(root, "libvlc", architecture);
        if (!File.Exists(Path.Combine(directory, "libvlc.dll")) ||
            !Directory.Exists(Path.Combine(directory, "plugins")))
        {
            throw new PlaybackEngineUnavailableException("The bundled LibVLC runtime is incomplete.");
        }
        return directory;
    }

    public static void Initialize(string? baseDirectory = null)
    {
        try { LibVLCSharp.Shared.Core.Initialize(Resolve(baseDirectory)); }
        catch (PlaybackEngineUnavailableException) { throw; }
        catch (Exception ex)
        {
            throw new PlaybackEngineUnavailableException("The bundled LibVLC runtime could not be loaded.", ex);
        }
    }
}
