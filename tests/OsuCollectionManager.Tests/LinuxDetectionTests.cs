using System.Diagnostics;
using OsuCollectionManager.Services;
using Xunit;

namespace OsuCollectionManager.Tests;

/// <summary>
/// Detection on Linux. These change the HOME variable and start processes, so they share one collection and never run in
/// parallel with each other. They are skipped on Windows and run on the Ubuntu CI runner.
/// </summary>
[Collection("Linux environment")]
public class LinuxDetectionTests
{
    [SkippableFact]
    public void Finds_osu_inside_a_default_wine_prefix()
    {
        Skip.IfNot(OperatingSystem.IsLinux());
        using var temp = new TempFolder();
        var (_, osu, _) = Wine.MakePrefix(temp, ".wine");

        var candidates = WithHome(temp.Root, OsuLocator.FindCandidates);

        var found = Assert.Single(candidates, c => c.Path == osu);
        Assert.False(found.Confident); // a guess from a known location must never be picked silently among several
    }

    [SkippableFact]
    public void Finds_the_osu_winello_and_bottles_locations()
    {
        Skip.IfNot(OperatingSystem.IsLinux());
        using var temp = new TempFolder();
        string winello = temp.Folder(".local", "share", "osu-wine", "osu!");
        temp.File(Path.Combine(".local", "share", "osu-wine", "osu!", "osu!.db"));
        var (_, bottles, _) = Wine.MakePrefix(temp, ".local/share/bottles/bottles/osu");

        var paths = WithHome(temp.Root, OsuLocator.FindCandidates).Select(c => c.Path).ToList();

        Assert.Contains(winello, paths);
        Assert.Contains(bottles, paths);
    }

    [SkippableFact]
    public void Folder_browser_shows_dot_folders_and_the_root_has_no_parent()
    {
        Skip.IfNot(OperatingSystem.IsLinux());
        using var temp = new TempFolder();
        temp.Folder(".local");
        temp.Folder("Games");

        var listing = OsuLocator.ListFolders(temp.Root);
        var root = OsuLocator.ListFolders("/");

        Assert.Contains(listing.Folders, f => f.Name == ".local"); // Wine prefixes live in dot-folders
        Assert.Contains(listing.Folders, f => f.Name == "Games");
        Assert.Equal("", root.Parent);
        Assert.Equal("/", OsuLocator.ListFolders("/home").Parent);
    }

    [SkippableFact]
    public void Browser_start_page_lists_places_instead_of_drive_letters()
    {
        Skip.IfNot(OperatingSystem.IsLinux());

        var start = OsuLocator.ListFolders("");

        Assert.Null(start.Parent);
        Assert.Contains(start.Folders, f => f.Path == "/");
    }

    [SkippableFact]
    public void A_running_wine_osu_is_detected_and_its_folder_is_a_confident_candidate()
    {
        Skip.IfNot(OperatingSystem.IsLinux());
        using var temp = new TempFolder();
        var (_, osu, _) = Wine.MakePrefix(temp);

        using var fakeGame = StartFakeOsu(temp, workingDirectory: osu);

        Assert.True(WaitFor(() => OsuInstall.IsGameRunning), "the process named osu!.exe should count as the game running");
        var candidate = Assert.Single(OsuLocator.FindCandidates(), c => c.Path == osu);
        Assert.True(candidate.Confident);
        Assert.Equal(osu, OsuLocator.AutoPick([candidate]));

        fakeGame.Kill(entireProcessTree: true);
        fakeGame.WaitForExit();
        Assert.True(WaitFor(() => !OsuInstall.IsGameRunning), "once it exits the guard must let go");
    }

    // ---- helpers ----

    /// <summary>Copies "sleep" to a file called osu!.exe: Linux then reports the process name Wine would (osu!.exe).</summary>
    private static Process StartFakeOsu(TempFolder temp, string workingDirectory)
    {
        string sleep = File.Exists("/usr/bin/sleep") ? "/usr/bin/sleep" : "/bin/sleep";
        string exe = Path.Combine(temp.Root, "osu!.exe");
        File.Copy(sleep, exe);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        return Process.Start(new ProcessStartInfo(exe, "60") { WorkingDirectory = workingDirectory, UseShellExecute = false })
               ?? throw new InvalidOperationException("could not start the fake game");
    }

    private static bool WaitFor(Func<bool> condition)
    {
        for (int i = 0; i < 40; i++)
        {
            if (condition()) return true;
            Thread.Sleep(100);
        }
        return condition();
    }

    private static T WithHome<T>(string home, Func<T> action)
    {
        string? original = Environment.GetEnvironmentVariable("HOME");
        Environment.SetEnvironmentVariable("HOME", home);
        try { return action(); }
        finally { Environment.SetEnvironmentVariable("HOME", original); }
    }
}
