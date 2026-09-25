using Xunit;

namespace OsuCollectionManager.Tests;

/// <summary>A scratch folder that is deleted when the test ends.</summary>
internal sealed class TempFolder : IDisposable
{
    public string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ocm-tests-" + Guid.NewGuid().ToString("n")[..8]);

    public TempFolder() => Directory.CreateDirectory(Root);

    /// <summary>Creates (if needed) and returns a sub-folder.</summary>
    public string Folder(params string[] parts)
    {
        string path = System.IO.Path.Combine([Root, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    public string File(string relativePath, string content = "")
    {
        string path = System.IO.Path.Combine(Root, relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); }
        catch (IOException) { /* still in use; the OS temp cleaner will get it */ }
        catch (UnauthorizedAccessException) { }
    }
}

internal static class Wine
{
    /// <summary>
    /// Builds a fake Wine prefix like a real one: drive_c with osu! inside AppData/Local, plus dosdevices with
    /// c: -> drive_c, z: -> / and d: -> a separate "disk" folder. Linux only (Windows can't have "z:" as a file name).
    /// </summary>
    public static (string Prefix, string OsuFolder, string DiskD) MakePrefix(TempFolder temp, string prefixName = ".wine")
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "Wine prefixes only exist on Linux");

        string prefix = temp.Folder(prefixName);
        string osu = temp.Folder(prefixName, "drive_c", "users", "tester", "AppData", "Local", "osu!");
        temp.File(System.IO.Path.Combine(prefixName, "drive_c", "users", "tester", "AppData", "Local", "osu!", "osu!.db"));
        string diskD = temp.Folder("disk-d");

        string dosdevices = temp.Folder(prefixName, "dosdevices");
        Directory.CreateSymbolicLink(System.IO.Path.Combine(dosdevices, "c:"), "../drive_c");
        Directory.CreateSymbolicLink(System.IO.Path.Combine(dosdevices, "z:"), "/");
        Directory.CreateSymbolicLink(System.IO.Path.Combine(dosdevices, "d:"), diskD);
        return (prefix, osu, diskD);
    }
}
