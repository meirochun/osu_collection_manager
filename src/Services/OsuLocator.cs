using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace OsuCollectionManager.Services;

/// <summary>A sub-folder shown in the setup screen's folder browser.</summary>
public sealed record FolderEntry(string Name, string Path, bool HasOsuDb);

/// <summary>What is inside one folder. <paramref name="Parent"/> is "" for the drive list, null when already at that top level.</summary>
public sealed record FolderListing(string Path, string? Parent, bool HasOsuDb, List<FolderEntry> Folders);

/// <summary>An osu! install found on this PC. <paramref name="Confident"/> means it is safe to use without asking.</summary>
public sealed record OsuCandidate(string Path, string Source, bool Confident);

/// <summary>Finds the osu!stable folder (the one containing osu!.db) without the user having to type it.</summary>
public static partial class OsuLocator
{
    private const string DatabaseFileName = "osu!.db";

    public static bool IsValid(string? folder) =>
        !string.IsNullOrWhiteSpace(folder) && PathCase.FindFile(folder, DatabaseFileName) is not null;

    /// <summary>
    /// Turns whatever the user gave us into the osu! install folder, or null if it isn't one.
    /// Accepts the folder itself, osu!.exe / osu!.db, or the Songs folder (or anything just inside the install).
    /// </summary>
    public static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;

        try
        {
            string path = Path.GetFullPath(ExpandHome(input.Trim().Trim('"')));
            if (File.Exists(path)) path = Path.GetDirectoryName(path)!;

            // Walk up a few levels so ...\osu!\Songs and ...\osu!\Songs\123 abc also work.
            for (int level = 0; level < 3 && path is not null; level++)
            {
                if (IsValid(path)) return path;
                path = Path.GetDirectoryName(path)!;
            }
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            // Not a usable path: fall through to "not found".
        }
        return null;
    }

    private const int MaxFoldersListed = 500;

    /// <summary>
    /// Lists the sub-folders of <paramref name="path"/> for the folder browser, or the drives (plus a few shortcuts) when
    /// the path is empty. Folders are read one level at a time and only names are returned, never file contents.
    /// </summary>
    public static FolderListing ListFolders(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return ListDrives();

        string full = Path.GetFullPath(ExpandHome(path.Trim().Trim('"')));
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException("That folder does not exist.");

        var folders = new List<FolderEntry>();
        try
        {
            foreach (var dir in new DirectoryInfo(full).EnumerateDirectories().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
            {
                // Skip what Windows hides from people too ($RECYCLE.BIN, System Volume Information, ...). On Linux every
                // ".folder" counts as hidden, but that is exactly where Wine prefixes live (~/.local/share, ~/.wine), so show them.
                if (OperatingSystem.IsWindows() && (dir.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                folders.Add(new FolderEntry(dir.Name, dir.FullName, IsValid(dir.FullName)));
                if (folders.Count >= MaxFoldersListed) break;
            }
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            // Unreadable folder: show it as empty rather than failing the whole browser.
        }

        // Parent is "" (the top-level list) once we are at a drive root ("D:\" or "/").
        string trimmed = full.TrimEnd(Path.DirectorySeparatorChar);
        string root = (Path.GetPathRoot(full) ?? "").TrimEnd(Path.DirectorySeparatorChar);
        string parent = trimmed == root ? "" : Path.GetDirectoryName(trimmed) ?? "";
        return new FolderListing(full, parent, IsValid(full), folders);
    }

    private static FolderListing ListDrives()
    {
        if (!OperatingSystem.IsWindows()) return new FolderListing("", null, false, LinuxBrowseRoots());

        var entries = new List<FolderEntry>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.IsReady && drive.DriveType is DriveType.Fixed or DriveType.Removable or DriveType.Network)
                    entries.Add(new FolderEntry(drive.Name, drive.Name, IsValid(drive.Name)));
            }
            catch (IOException) { /* drive went away while listing */ }
        }
        return new FolderListing("", null, false, entries);
    }

    /// <summary>All installs we can find, most trustworthy first, without duplicates.</summary>
    public static List<OsuCandidate> FindCandidates()
    {
        var found = new List<OsuCandidate>();
        if (!OperatingSystem.IsWindows())
        {
            AddLinuxCandidates(found);
            return found;
        }

        void Add(string? path, string source, bool confident) => AddCandidate(found, path, source, confident);

        // Strong signals: the game is running right now, or it registered itself with Windows.
        Add(FromRunningProcess(), "osu! is running from here", confident: true);
        foreach (var registered in FromRegistry()) Add(registered, "found in the Windows registry", confident: true);

        // Weak signals: places osu! is usually installed. Fine to suggest, not fine to pick silently.
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Add(Path.Combine(local, "osu!"), "default install folder", confident: false);
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "osu!"), "Program Files", confident: false);
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "osu!"), "Program Files (x86)", confident: false);
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "osu!"), "your user folder", confident: false);

        foreach (var root in FixedDriveRoots())
        {
            Add(Path.Combine(root, "osu!"), $"found on {root}", confident: false);
            Add(Path.Combine(root, "Games", "osu!"), $"found on {root}", confident: false);
        }
        return found;
    }

    /// <summary>Adds an install to the list if the path really is one and it isn't there yet.</summary>
    private static void AddCandidate(List<OsuCandidate> found, string? path, string source, bool confident)
    {
        if (Normalize(path) is not { } folder) return;
        if (found.Any(c => string.Equals(c.Path, folder, StringComparison.OrdinalIgnoreCase))) return;
        found.Add(new OsuCandidate(folder, source, confident));
    }

    /// <summary>Lets people type "~/Games/osu!" in the path box.</summary>
    private static string ExpandHome(string path)
    {
        if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal))
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.TrimStart('~').TrimStart('/'));
        return path;
    }

    /// <summary>The candidate to use without asking: a strong signal, or the only install found.</summary>
    public static string? AutoPick(IReadOnlyList<OsuCandidate> candidates) =>
        candidates.FirstOrDefault(c => c.Confident)?.Path
        ?? (candidates.Count == 1 ? candidates[0].Path : null);

    private static string? FromRunningProcess()
    {
        foreach (var process in Process.GetProcessesByName("osu!"))
        {
            try
            {
                return process.MainModule?.FileName; // can throw "access denied" when osu! runs elevated
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // Try the next process, or fall back to the other detection methods.
            }
            finally
            {
                process.Dispose();
            }
        }
        return null;
    }

    /// <summary>osu! registers its exe for the osu:// protocol, e.g. "C:\...\osu!.exe" "%1".</summary>
    private static IEnumerable<string> FromRegistry()
    {
        if (!OperatingSystem.IsWindows()) yield break;

        foreach (var keyPath in new[] { @"osu\shell\open\command", @"osu!\shell\open\command" })
        {
            string? command = null;
            try
            {
                using var key = Registry.ClassesRoot.OpenSubKey(keyPath);
                command = key?.GetValue(null) as string;
            }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                // Registry not readable: just skip this key.
            }

            if (command is null) continue;
            var match = CommandExecutable().Match(command);
            if (match.Success) yield return match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
        }
    }

    // The program at the start of a command line: a quoted string, or the first word if it isn't quoted.
    [GeneratedRegex("^\\s*(?:\"([^\"]+)\"|(\\S+))")]
    private static partial Regex CommandExecutable();

    private static IEnumerable<string> FixedDriveRoots()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            bool usable;
            try { usable = drive.DriveType == DriveType.Fixed && drive.IsReady; }
            catch (IOException) { usable = false; }
            if (usable) yield return drive.Name;
        }
    }
}
