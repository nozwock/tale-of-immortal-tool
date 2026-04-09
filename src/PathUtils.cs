namespace TaleOfImmortalTool;

public static class PathUtils
{
    public static string NormalizeSeparator(string path) => path.Replace("\\", "/");

    public static bool Equals(string path, string other)
        => string.Equals(
            Path.GetFullPath(path),
            Path.GetFullPath(other),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    public static string TrimEndPathSeparator(string path)
        => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    public static string[] Split(string path)
        => path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

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