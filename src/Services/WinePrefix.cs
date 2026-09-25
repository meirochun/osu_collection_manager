using System.Text.RegularExpressions;

namespace OsuCollectionManager.Services;

/// <summary>
/// osu!stable on Linux runs inside a Wine "prefix": a folder with a fake C: drive (<c>drive_c</c>) and a <c>dosdevices</c>
/// folder that maps drive letters to real folders. osu! writes Windows-style paths (like D:\Games\osu!\Songs) into its
/// settings; this turns them back into real Linux paths.
/// </summary>
public static partial class WinePrefix
{
    [GeneratedRegex(@"^([A-Za-z]):[\\/]?(.*)$")]
    private static partial Regex DrivePath();

    /// <summary>True for "D:\Games\osu!" style paths (and anything using backslashes).</summary>
    public static bool LooksLikeWindowsPath(string path) => HasDriveLetter(path) || path.Contains('\\');

    /// <summary>True when the path starts with a drive letter ("D:\..." or "D:/...").</summary>
    public static bool HasDriveLetter(string path) => DrivePath().IsMatch(path.Trim());

    /// <summary>The Wine prefix that contains <paramref name="folder"/>: the closest parent that has a <c>drive_c</c> folder.</summary>
    public static string? FindPrefixRoot(string folder)
    {
        for (var dir = new DirectoryInfo(folder); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "drive_c"))) return dir.FullName;
        }
        return null;
    }

    /// <summary>
    /// Converts a Windows path to the real path on this machine using the prefix's drive letters,
    /// or returns null when the drive isn't mapped. Upper/lower case differences are tolerated.
    /// </summary>
    public static string? ToLinuxPath(string windowsPath, string? prefixRoot)
    {
        if (prefixRoot is null) return null;

        var match = DrivePath().Match(windowsPath.Trim());
        if (!match.Success) return null;

        string? driveRoot = ResolveDrive(prefixRoot, char.ToLowerInvariant(match.Groups[1].Value[0]));
        return driveRoot is null ? null : PathCase.ResolveRelative(driveRoot, match.Groups[2].Value);
    }

    /// <summary>Where a drive letter points: the symlink in dosdevices, or Wine's defaults (c: = drive_c, z: = /).</summary>
    private static string? ResolveDrive(string prefixRoot, char letter)
    {
        string link = Path.Combine(prefixRoot, "dosdevices", $"{letter}:");
        try
        {
            if (Directory.ResolveLinkTarget(link, returnFinalTarget: true) is { } target)
            {
                return Path.IsPathRooted(target.FullName)
                    ? target.FullName
                    : Path.GetFullPath(target.FullName, Path.GetDirectoryName(link)!);
            }
            if (Directory.Exists(link)) return link; // not a link but a real folder
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Fall through to the defaults below.
        }

        return letter switch
        {
            'c' => Path.Combine(prefixRoot, "drive_c"),
            'z' => "/",
            _ => null,
        };
    }
}
