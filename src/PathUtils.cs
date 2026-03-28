namespace TOITool;

public static class PathUtils
{
    public static string TrimEndPathSeparator(string path)
        => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    public static string? GetParent(string path)
        => Path.GetDirectoryName(TrimEndPathSeparator(path));

    public static string WithBaseName(string path, string basename)
        => Path.Combine(GetParent(path) ?? "", basename);

    public static string GetBaseName(string path)
    {
        if (string.IsNullOrEmpty(path))
            return path;
        var trimmed = TrimEndPathSeparator(path);
        return string.IsNullOrEmpty(trimmed) ? path : Path.GetFileName(trimmed);
    }
}