namespace OsuCollectionManager.Services;

public sealed record OnlineBeatmap(
    int BeatmapId, int SetId, string Version, double Stars, double Bpm, int Length,
    int Circles, int Sliders, string Checksum, double Cs, double Ar);

public sealed record OnlineSet(
    int Id, string Artist, string Title, string Creator, string Status,
    int FavouriteCount, int PlayCount, List<OnlineBeatmap> Beatmaps)
{
    public string CoverUrl => $"https://assets.ppy.sh/beatmaps/{Id}/covers/list.jpg";
    public string PreviewUrl => $"https://b.ppy.sh/preview/{Id}.mp3";
}

public sealed record TopPlay(
    int BeatmapId, int SetId, string Artist, string Title, string Version, string Creator,
    double Pp, double Accuracy, string Mods, double Stars, double Bpm, string Checksum);

public sealed record UserProfile(int Id, string Username, double Pp, int? GlobalRank, int? CountryRank, double Accuracy, List<TopPlay> TopPlays);

/// <summary>Community-voted tags (e.g. "skillset/jumps") per difficulty, with vote counts.</summary>
public sealed record SetTags(int SetId, string Status, int FavouriteCount, Dictionary<int, Dictionary<string, int>> ByBeatmap);

public sealed record DownloadResult(int SetId, bool Success, string? Mirror, long Bytes, string? FilePath, string? Error,
    Dictionary<int, string> HashesByBeatmapId);
