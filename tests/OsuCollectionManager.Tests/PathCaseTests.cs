using OsuCollectionManager.Services;
using Xunit;

namespace OsuCollectionManager.Tests;

public class PathCaseTests
{
    [Fact]
    public void Finds_files_and_folders_whatever_their_case()
    {
        using var temp = new TempFolder();
        temp.File("OSU!.DB");
        temp.Folder("songs");

        Assert.NotNull(PathCase.FindFile(temp.Root, "osu!.db"));
        Assert.NotNull(PathCase.FindDirectory(temp.Root, "Songs"));
        Assert.Null(PathCase.FindFile(temp.Root, "collection.db"));
    }

    [Fact]
    public void Relative_paths_are_resolved_part_by_part_with_either_slash()
    {
        using var temp = new TempFolder();
        string real = temp.Folder("Games", "OSU!", "Songs");

        // Windows file systems ignore case already, so there the path simply keeps the case it was written in.
        bool ignoreCase = OperatingSystem.IsWindows();
        Assert.Equal(real, PathCase.ResolveRelative(temp.Root, @"games\osu!\songs"), ignoreCase);
        Assert.Equal(real, PathCase.ResolveRelative(temp.Root, "GAMES/osu!/SONGS"), ignoreCase);
    }

    [Fact]
    public void Missing_parts_are_kept_as_written()
    {
        using var temp = new TempFolder();
        Assert.Equal(Path.Combine(temp.Root, "New", "Folder"), PathCase.ResolveRelative(temp.Root, "New/Folder"));
    }

    [Fact]
    public void Osu_folder_is_recognised_from_the_folder_the_exe_or_the_songs_folder()
    {
        using var temp = new TempFolder();
        string osu = temp.Folder("osu!");
        temp.File(Path.Combine("osu!", "osu!.db"));
        temp.File(Path.Combine("osu!", "osu!.exe"));
        string songs = temp.Folder("osu!", "Songs", "123 Some Set");

        Assert.Equal(osu, OsuLocator.Normalize(osu));
        Assert.Equal(osu, OsuLocator.Normalize(Path.Combine(osu, "osu!.exe")));
        Assert.Equal(osu, OsuLocator.Normalize(songs));
        Assert.Null(OsuLocator.Normalize(temp.Root));
    }
}
