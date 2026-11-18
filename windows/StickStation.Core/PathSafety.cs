namespace StickStation.Core;

public static class PathSafety
{
    /// <summary>Full path with every symbolic link or junction along it resolved to its final target.</summary>
    public static string Resolve(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? string.Empty;
        var current = root;
        foreach (var part in full[root.Length..].Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                var target = info.ResolveLinkTarget(returnFinalTarget: true);
                if (target is not null) current = Path.GetFullPath(target.FullName);
            }
        }
        return current.Length > root.Length ? current.TrimEnd('\\', '/') : current;
    }

    /// <summary>True when <paramref name="child"/> equals or lies inside <paramref name="parent"/> (case-insensitive).</summary>
    public static bool IsSameOrInside(string child, string parent)
    {
        var c = child.TrimEnd('\\', '/');
        var p = parent.TrimEnd('\\', '/');
        return c.Equals(p, StringComparison.OrdinalIgnoreCase) ||
               c.StartsWith(p + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
               c.StartsWith(p + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
