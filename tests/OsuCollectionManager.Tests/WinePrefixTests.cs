using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OsuCollectionManager.Services;
using Xunit;

namespace OsuCollectionManager.Tests;

public class WinePrefixTests
{
    [Theory]
    [InlineData(@"D:\Games\osu!", true)]
    [InlineData("z:/home/me/osu!", true)]
    [InlineData(@"Songs\Extra", true)]
    [InlineData("/home/me/osu!", false)]
    [InlineData("Songs", false)]
    public void Recognises_windows_style_paths(string path, bool expected) =>
        Assert.Equal(expected, WinePrefix.LooksLikeWindowsPath(path));

    [Fact]
    public void Drive_C_maps_to_drive_c_folder_even_without_dosdevices()
    {
        using var temp = new TempFolder();
        string prefix = temp.Folder("prefix");
        temp.Folder("prefix", "drive_c", "osu!", "Songs");

        string? result = WinePrefix.ToLinuxPath(@"C:\osu!\Songs", prefix);

        Assert.Equal(Path.Combine(prefix, "drive_c", "osu!", "Songs"), result);
    }

    [Fact]
    public void An_unmapped_drive_letter_gives_null()
    {
        using var temp = new TempFolder();
        string prefix = temp.Folder("prefix");
        Assert.Null(WinePrefix.ToLinuxPath(@"Q:\anything", prefix));
    }

    [SkippableFact]
    public void Drive_letters_follow_the_dosdevices_links_and_ignore_case()
    {
        using var temp = new TempFolder();
        var (prefix, _, diskD) = Wine.MakePrefix(temp);
        string songs = Path.Combine(diskD, "Games", "osu!", "SONGS");
        Directory.CreateDirectory(songs); // real folder is upper case, the Windows path is not

        Assert.Equal(songs, WinePrefix.ToLinuxPath(@"D:\games\OSU!\songs", prefix));
        Assert.Equal("/home", WinePrefix.ToLinuxPath(@"Z:\home", prefix));
    }

    [SkippableFact]
    public void Songs_folder_from_a_windows_path_is_translated_on_linux()
    {
        Skip.If(OperatingSystem.IsWindows(), "Windows uses the setting as written");
        using var temp = new TempFolder();
        var (_, osu, diskD) = Wine.MakePrefix(temp);
        string realSongs = Path.Combine(diskD, "osu!", "Songs");
        Directory.CreateDirectory(realSongs);
        File.WriteAllText(Path.Combine(osu, "osu!.tester.cfg"), "Foo = 1\nBeatmapDirectory = D:\\osu!\\Songs\n");

        Assert.Equal(realSongs, NewInstall(temp, osu).SongsPath);
    }

    [SkippableFact]
    public void Songs_folder_falls_back_to_the_osu_folder_when_the_drive_is_not_mapped()
    {
        Skip.If(OperatingSystem.IsWindows(), "Windows uses the setting as written");
        using var temp = new TempFolder();
        var (_, osu, _) = Wine.MakePrefix(temp);
        Directory.CreateDirectory(Path.Combine(osu, "Songs"));
        File.WriteAllText(Path.Combine(osu, "osu!.tester.cfg"), "BeatmapDirectory = Q:\\nowhere\\Songs\n");

        Assert.Equal(Path.Combine(osu, "Songs"), NewInstall(temp, osu).SongsPath);
    }

    private static OsuInstall NewInstall(TempFolder temp, string osuFolder)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OsuPath"] = osuFolder,
            ["SettingsFile"] = Path.Combine(temp.Root, "settings.json"),
            ["AutoDetect"] = "false",
        }).Build();
        return new OsuInstall(config, new UserSettingsStore(config, NullLogger<UserSettingsStore>.Instance), NullLogger<OsuInstall>.Instance);
    }
}
