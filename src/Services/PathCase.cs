namespace OsuCollectionManager.Services;

/// <summary>
/// Windows programs (and Wine) don't care about upper/lower case in file names, but Linux file systems do.
/// These helpers find "osu!.db", "Songs", ... whatever case the game happened to create them in.
/// </summary>
public static class PathCase
{
    private static readonly bool CaseSensitiveFileSystem = !OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS();

    /// <summary>The existing file called <paramref name="name"/> inside <paramref name="folder"/> (ignoring case), or null.</summary>
    public static string? FindFile(string folder, string name) => Find(folder, name, wantDirectory: false);

    /// <summary>The existing sub-folder called <paramref name="name"/> inside <paramref name="folder"/> (ignoring case), or null.</summary>
    public static string? FindDirectory(string folder, string name) => Find(folder, name, wantDirectory: true);

    private static string? Find(string folder, string name, bool wantDirectory)
    {
        string exact = Path.Combine(folder, name);
        if (wantDirectory ? Directory.Exists(exact) : File.Exists(exact)) return exact;
        if (!CaseSensitiveFileSystem || !Directory.Exists(folder)) return null;

        try
        {
            var entries = wantDirectory ? Directory.EnumerateDirectories(folder) : Directory.EnumerateFiles(folder);
            return entries.FirstOrDefault(entry => string.Equals(Path.GetFileName(entry), name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Follows a relative path (either slash style) below <paramref name="root"/>, matching every part ignoring case.
    /// Parts that don't exist are kept as written, so the result is still a sensible path to create.
    /// </summary>
    public static string ResolveRelative(string root, string relative)
    {
        string current = root;
        foreach (var part in relative.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..")
            {
                current = Path.GetDirectoryName(current) ?? current;
                continue;
            }

            string next = Path.Combine(current, part);
            if (!Path.Exists(next)) next = FindAny(current, part) ?? next;
            current = next;
        }
        return current;
    }

    private static string? FindAny(string folder, string name)
    {
        if (!CaseSensitiveFileSystem || !Directory.Exists(folder)) return null;
        try
        {
            return Directory.EnumerateFileSystemEntries(folder)
                .FirstOrDefault(entry => string.Equals(Path.GetFileName(entry), name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
