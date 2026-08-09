using KikuCaption.Core.Exceptions;
using KikuCaption.Core.Models;

namespace KikuCaption.Storage;

/// <summary>Builds and validates session directories, guarding against path traversal.</summary>
public static class SessionPaths
{
    /// <summary>Directory name: <c>yyyy-MM-dd_HHmmss_&lt;session-id&gt;</c>. The id (a GUID "N") and
    /// timestamp are inherently safe; the session id disambiguates same-second starts.</summary>
    public static string BuildDirectoryName(MeetingSession session)
        => $"{session.StartedAt.LocalDateTime:yyyy-MM-dd_HHmmss}_{session.Id:N}";

    public static string BuildSessionDirectory(string outputRoot, MeetingSession session)
    {
        var root = Path.GetFullPath(outputRoot);
        var candidate = Path.GetFullPath(Path.Combine(root, BuildDirectoryName(session)));
        EnsureWithinRoot(root, candidate);
        return candidate;
    }

    /// <summary>Throws if <paramref name="candidate"/> is not inside <paramref name="root"/>.</summary>
    public static void EnsureWithinRoot(string root, string candidate)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullCandidate = Path.GetFullPath(candidate);

        if (!fullCandidate.Equals(fullRoot, StringComparison.OrdinalIgnoreCase) &&
            !fullCandidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new StorageException("path_traversal", "输出路径不在允许的输出根目录内。");
        }
    }
}
