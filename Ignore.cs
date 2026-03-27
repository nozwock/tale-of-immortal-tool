using System.Runtime.InteropServices;

namespace TOITool;

public record class IgnoreWalk(
    IReadOnlyList<string> Paths,
    IReadOnlyList<string>? Extensions = null,
    IReadOnlyList<string>? Overrides = null,
    string? CurrentDir = null,
    bool CaseInsensitive = true,
    bool IgnoreHidden = true,
    bool UseIgnoreFiles = true,
    bool UseParentIgnoreFiles = true,
    IReadOnlyList<string>? CustomIgnoreFilenames = null)
{
    Ignore.Ignore? ignoreOverrides = null;

    public IEnumerable<string> Enumerate()
    {
        var cwd = CurrentDir ?? Directory.GetCurrentDirectory();

        if (Overrides != null)
        {
            ignoreOverrides = new();
            foreach (var pattern in Overrides)
            {
                AddPattern(ignoreOverrides, pattern);
            }
        }

        var ignore = BuildIgnore(cwd);

        foreach (var root in Paths)
        {
            var fullRoot = Path.GetFullPath(Path.Combine(cwd, root));

            foreach (var entry in Walk(fullRoot, fullRoot, ignore))
                yield return entry;
        }
    }

    private Ignore.Ignore BuildIgnore(string cwd)
    {
        var ignore = new Ignore.Ignore();

        if (UseParentIgnoreFiles)
        {
            var dir = new DirectoryInfo(cwd);

            while (dir != null)
            {
                LoadIgnoreFiles(dir.FullName, ignore);
                dir = dir.Parent;
            }
        }
        else
        {
            LoadIgnoreFiles(cwd, ignore);
        }

        return ignore;
    }

    private void LoadIgnoreFiles(string dir, Ignore.Ignore ignore)
    {
        var ignoreFilenames = new List<string>([
            ".gitignore",
            ".ignore"
        ]);

        if (CustomIgnoreFilenames != null)
            ignoreFilenames.AddRange(CustomIgnoreFilenames);

        foreach (var filepath in ignoreFilenames
            .Distinct()
            .Select(it => Path.Join(dir, it)))
        {
            LoadIgnoreFile(filepath, ignore);
        }
    }

    private void LoadIgnoreFile(string file, Ignore.Ignore ignore)
    {
        if (!File.Exists(file))
            return;

        foreach (var line in File.ReadLines(file))
        {
            AddPattern(ignore, line);
        }
    }

    private void AddPattern(Ignore.Ignore ignore, string line)
    {
        line = line.Trim().Replace('\\', '/');

        if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
            return;

        ignore.Add(CaseInsensitive ? line.ToLowerInvariant() : line);
    }

    private IEnumerable<string> Walk(string root, string current, Ignore.Ignore ignore)
    {
        var options = new EnumerationOptions()
        {
            RecurseSubdirectories = true,
            AttributesToSkip = 0,
            IgnoreInaccessible = false,
            MatchCasing = CaseInsensitive ? MatchCasing.CaseInsensitive : MatchCasing.CaseSensitive,
        };

        foreach (var entry in Directory.EnumerateFileSystemEntries(current, "*", options))
        {
            var attr = File.GetAttributes(entry);
            var isDir = attr.HasFlag(FileAttributes.Directory);

            if (IgnoreHidden && IsHidden(entry, attr))
                continue;

            var relForMatch = Path.GetRelativePath(root, entry);
            relForMatch = CaseInsensitive ? relForMatch.ToLowerInvariant() : relForMatch;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                // Normalizing since IsIgnored treats / and \ differently
                // https://github.com/jellyfin/jellyfin/issues/15484
                // https://github.com/goelhardik/ignore/issues/51
                relForMatch = relForMatch.Replace('\\', '/');
            if (isDir && !relForMatch.EndsWith('/'))
                relForMatch += "/"; // Required for IsIgnored to think of it as directory

            if (ignoreOverrides != null && ignoreOverrides.IsIgnored(relForMatch))
                continue;
            if (UseIgnoreFiles && ignore.IsIgnored(relForMatch))
                continue;

            if (isDir)
            {
                yield return entry;
            }
            else
            {
                if (!MatchExtension(entry))
                    continue;

                yield return entry;
            }
        }
    }

    private bool MatchExtension(string path)
    {
        if (Extensions == null || Extensions.Count == 0)
            return true;

        var ext = Path.GetExtension(path);

        return Extensions.Any(e =>
            string.Equals(
                e,
                ext,
                CaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
    }

    private static bool IsHidden(string path, FileAttributes attr)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return attr.HasFlag(FileAttributes.Hidden);
        }
        else
        {
            var name = Path.GetFileName(path);
            return name.StartsWith('.');
        }
    }
}