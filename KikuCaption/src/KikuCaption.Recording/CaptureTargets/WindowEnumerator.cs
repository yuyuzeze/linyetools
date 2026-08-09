using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace KikuCaption.Recording.CaptureTargets;

/// <summary>
/// Enumerates visible, titled top-level windows for the recorder's target picker. Excludes
/// invisible/untitled/system shell windows. Does not hard-code any application (Teams is just a
/// normal titled window here).
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowEnumerator
{
    private static readonly HashSet<string> ExcludedTitles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Program Manager", "Default IME", "MSCTFIME UI", "Windows Input Experience"
    };

    public static IReadOnlyList<CaptureTarget> EnumerateWindows()
    {
        var results = new List<CaptureTarget>();

        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd))
            {
                return true;
            }

            int length = GetWindowTextLength(hWnd);
            if (length <= 0)
            {
                return true;
            }

            var buffer = new StringBuilder(length + 1);
            GetWindowText(hWnd, buffer, buffer.Capacity);
            var title = buffer.ToString();

            if (string.IsNullOrWhiteSpace(title) || ExcludedTitles.Contains(title))
            {
                return true;
            }

            results.Add(new CaptureTarget(hWnd, title));
            return true;
        }, IntPtr.Zero);

        return results
            .GroupBy(t => t.Title, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>True if a visible window with exactly this title currently exists.</summary>
    public static bool WindowExists(string title)
        => !string.IsNullOrWhiteSpace(title) &&
           EnumerateWindows().Any(t => string.Equals(t.Title, title, StringComparison.Ordinal));

    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hWnd, StringBuilder lpString, int nMaxCount);
}
