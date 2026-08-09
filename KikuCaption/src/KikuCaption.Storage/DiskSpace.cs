namespace KikuCaption.Storage;

/// <summary>Free-disk-space checks for the output volume.</summary>
public static class DiskSpace
{
    public static long GetFreeBytes(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root))
        {
            return long.MaxValue;
        }

        try
        {
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch
        {
            // If the drive can't be queried, don't block the user.
            return long.MaxValue;
        }
    }

    public static bool HasAtLeastGb(string path, double gb)
        => GetFreeBytes(path) >= (long)(gb * 1024 * 1024 * 1024);

    public static double GetFreeGb(string path) => GetFreeBytes(path) / (1024d * 1024 * 1024);
}
