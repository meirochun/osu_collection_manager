namespace OsuCollectionManager.Services;

/// <summary>
/// Linux side of the osu! folder detection. osu!stable runs through Wine there (osu-winello, Lutris, Bottles, Proton, ...),
/// so "the osu! folder" is a normal Linux folder that usually lives inside a Wine prefix.
/// </summary>
public static partial class OsuLocator
{
    private const int MaxFoldersPerWildcard = 60;

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    // ---- Is the game running? ----------------------------------------------------------------------------------

    /// <summary>
    /// Ids of running osu! processes. Wine names the process after the program ("osu!.exe", which /proc/&lt;pid&gt;/comm
    /// keeps), so look there first and then at the command line.
    /// </summary>
    public static IEnumerable<int> LinuxOsuProcessIds()
    {
        if (!Directory.Exists("/proc")) yield break;

        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out int pid)) continue;
            if (IsOsuProcess(dir)) yield return pid;
        }
    }

    private static bool IsOsuProcess(string procDir)
    {
        try
        {
            if (File.ReadAllText(Path.Combine(procDir, "comm")).Trim().Equals("osu!.exe", StringComparison.OrdinalIgnoreCase)) return true;
            return ReadCmdline(procDir).Any(arg => FileNameOf(arg).Equals("osu!.exe", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false; // the process ended, or belongs to someone else
        }
    }

    private static string FileNameOf(string path) => Path.GetFileName(path.Replace('\\', '/'));

    private static string[] ReadCmdline(string procDir) => File.ReadAllText(Path.Combine(procDir, "cmdline")).Split('\0', StringSplitOptions.RemoveEmptyEntries);

    // ---- Where is osu!? ----------------------------------------------------------------------------------------

    private static void AddLinuxCandidates(List<OsuCandidate> found)
    {
        // Strong signal: the game is running right now.
        AddCandidate(found, RunningOsuFolderOnLinux(), "osu! is running from here", confident: true);

        // Weak signals: where the common Wine setups put it. Fine to suggest, not fine to pick silently.
        string home = Home;
        AddCandidate(found, Path.Combine(home, ".local/share/osu-wine/osu!"), "osu-winello install", confident: false);

        // Every Wine prefix has a fake C: drive; osu! is installed somewhere inside it.
        string[] insideDriveC = ["drive_c/users/*/AppData/Local/osu!", "drive_c/Program Files/osu!", "drive_c/Program Files (x86)/osu!", "drive_c/osu!"];
        string[] prefixes =
        [
            ".wine",
            "Games/*",                                                                 // Lutris
            ".local/share/wineprefixes/*",
            ".local/share/bottles/bottles/*",                                          // Bottles
            ".var/app/com.usebottles.bottles/data/bottles/bottles/*",                  // Bottles (Flatpak)
            ".steam/steam/steamapps/compatdata/*/pfx",                                 // Proton
            ".local/share/Steam/steamapps/compatdata/*/pfx",
        ];
        foreach (var prefix in prefixes)
            foreach (var inside in insideDriveC)
                foreach (var folder in ExpandWildcards(Path.Combine(home, prefix, inside)))
                    AddCandidate(found, folder, "found in a Wine prefix", confident: false);

        AddCandidate(found, Path.Combine(home, "osu!"), "your home folder", confident: false);
        foreach (var root in new[] { "/mnt/*/osu!", $"/run/media/{Environment.UserName}/*/osu!", $"/media/{Environment.UserName}/*/osu!" })
            foreach (var folder in ExpandWildcards(root))
                AddCandidate(found, folder, "found on a mounted drive", confident: false);
    }

    /// <summary>The folder of a running osu!. Wine keeps the game folder as the process's working directory.</summary>
    private static string? RunningOsuFolderOnLinux()
    {
        foreach (int pid in LinuxOsuProcessIds())
        {
            string procDir = $"/proc/{pid}";
            try
            {
                if (File.ResolveLinkTarget(Path.Combine(procDir, "cwd"), returnFinalTarget: true) is { } cwd && Normalize(cwd.FullName) is { } folder)
                    return folder;

                // Otherwise translate the Windows path of osu!.exe from the command line, using the process's own Wine prefix.
                string prefix = ReadEnvironment(procDir, "WINEPREFIX") ?? Path.Combine(Home, ".wine");
                foreach (var arg in ReadCmdline(procDir))
                {
                    if (!WinePrefix.LooksLikeWindowsPath(arg) || !FileNameOf(arg).Equals("osu!.exe", StringComparison.OrdinalIgnoreCase)) continue;
                    if (WinePrefix.ToLinuxPath(arg, prefix) is { } linux && Normalize(linux) is { } fromCommandLine) return fromCommandLine;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Can't look at this process; try the next one.
            }
        }
        return null;
    }

    private static string? ReadEnvironment(string procDir, string name)
    {
        try
        {
            string prefix = name + "=";
            return File.ReadAllText(Path.Combine(procDir, "environ")).Split('\0')
                .FirstOrDefault(entry => entry.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Expands "*" path parts against the folders that exist, e.g. ".../bottles/*/drive_c". Only the wildcard levels
    /// are listed (never a whole-disk search), so this stays fast.
    /// </summary>
    private static IEnumerable<string> ExpandWildcards(string pattern)
    {
        var current = new List<string> { "/" };
        foreach (var part in pattern.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var next = new List<string>();
            foreach (var basePath in current)
            {
                if (part == "*")
                {
                    try { next.AddRange(Directory.EnumerateDirectories(basePath).Take(MaxFoldersPerWildcard)); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* not readable: skip */ }
                }
                else
                {
                    string candidate = Path.Combine(basePath, part);
                    if (Directory.Exists(candidate)) next.Add(candidate);
                }
            }
            current = next;
            if (current.Count == 0) break;
        }
        return current;
    }

    // ---- Folder browser start page -----------------------------------------------------------------------------

    /// <summary>Starting points for the in-page folder browser (Linux has no drive letters).</summary>
    private static List<FolderEntry> LinuxBrowseRoots()
    {
        string user = Environment.UserName;
        var places = new[]
        {
            Home, Path.Combine(Home, ".local/share"), Path.Combine(Home, "Games"), Path.Combine(Home, ".wine"),
            "/mnt", $"/run/media/{user}", $"/media/{user}", "/",
        };
        return places.Where(Directory.Exists).Select(place => new FolderEntry(place, place, IsValid(place))).ToList();
    }
}
