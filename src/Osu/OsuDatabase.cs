namespace OsuCollectionManager.Osu;

public enum GameMode : byte { Osu = 0, Taiko = 1, Catch = 2, Mania = 3 }

public enum RankedStatus : byte
{
    Unknown = 0, Unsubmitted = 1, Pending = 2, Unused = 3, Ranked = 4, Approved = 5, Qualified = 6, Loved = 7
}

public sealed record LocalBeatmap
{
    public required string Md5 { get; init; }
    public int BeatmapId { get; init; }
    public int SetId { get; init; }
    public string Artist { get; init; } = "";
    public string Title { get; init; } = "";
    public string Creator { get; init; } = "";
    public string Version { get; init; } = "";
    public string Folder { get; init; } = "";
    public string FileName { get; init; } = "";
    public GameMode Mode { get; init; }
    public RankedStatus Status { get; init; }
    public double StarRating { get; init; }
    public float Ar { get; init; }
    public float Cs { get; init; }
    public float Hp { get; init; }
    public float Od { get; init; }
    public double MinBpm { get; init; }
    public double MaxBpm { get; init; }
    public int DrainSeconds { get; init; }
    public int Circles { get; init; }
    public int Sliders { get; init; }
    public DateTime? LastPlayed { get; init; }
}

public sealed record OsuDatabase(int Version, string? PlayerName, IReadOnlyList<LocalBeatmap> Beatmaps)
{
    // From this version on star ratings are stored as float32 instead of float64.
    private const int FloatStarRatingVersion = 20250107;

    public static OsuDatabase Read(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var r = new OsuReader(new BufferedStream(fs, 1 << 16));

        int version = r.ReadInt32();
        r.ReadInt32(); // folder count
        r.ReadBoolean(); // account unlocked
        r.ReadInt64(); // unlock date
        string? player = r.ReadOsuString();
        int count = r.ReadInt32();

        var maps = new List<LocalBeatmap>(count);
        for (int i = 0; i < count; i++)
            maps.Add(ReadBeatmap(r, version));

        return new OsuDatabase(version, player, maps);
    }

    private static LocalBeatmap ReadBeatmap(OsuReader r, int version)
    {
        if (version < 20191106) r.ReadInt32(); // entry size

        string artist = r.ReadOsuString() ?? "";
        r.ReadOsuString(); // artist unicode
        string title = r.ReadOsuString() ?? "";
        r.ReadOsuString(); // title unicode
        string creator = r.ReadOsuString() ?? "";
        string diff = r.ReadOsuString() ?? "";
        r.ReadOsuString(); // audio file
        string md5 = r.ReadOsuString() ?? "";
        string file = r.ReadOsuString() ?? "";
        var status = (RankedStatus)r.ReadByte();
        short circles = r.ReadInt16();
        short sliders = r.ReadInt16();
        r.ReadInt16(); // spinners
        r.ReadInt64(); // last modification
        float ar = r.ReadSingle(), cs = r.ReadSingle(), hp = r.ReadSingle(), od = r.ReadSingle();
        r.ReadDouble(); // slider velocity

        var nomodStars = new double[4];
        for (int mode = 0; mode < 4; mode++)
        {
            int pairs = r.ReadInt32();
            for (int p = 0; p < pairs; p++)
            {
                r.ReadByte();
                int mods = r.ReadInt32();
                r.ReadByte();
                double sr = version >= FloatStarRatingVersion ? r.ReadSingle() : r.ReadDouble();
                if (mods == 0) nomodStars[mode] = sr;
            }
        }

        int drain = r.ReadInt32();
        r.ReadInt32(); // total time (ms)
        r.ReadInt32(); // preview time

        int timingPoints = r.ReadInt32();
        double minBpm = double.MaxValue, maxBpm = 0;
        for (int t = 0; t < timingPoints; t++)
        {
            double msPerBeat = r.ReadDouble();
            r.ReadDouble(); // offset
            bool uninherited = r.ReadBoolean();
            if (uninherited && msPerBeat > 0)
            {
                double bpm = 60000 / msPerBeat;
                minBpm = Math.Min(minBpm, bpm);
                maxBpm = Math.Max(maxBpm, bpm);
            }
        }
        if (maxBpm == 0) minBpm = 0;

        int beatmapId = r.ReadInt32();
        int setId = r.ReadInt32();
        r.ReadInt32(); // thread id
        r.Skip(4); // grades per mode
        r.ReadInt16(); // local offset
        r.ReadSingle(); // stack leniency
        var gameMode = (GameMode)r.ReadByte();
        r.ReadOsuString(); // source
        r.ReadOsuString(); // tags
        r.ReadInt16(); // online offset
        r.ReadOsuString(); // title font
        bool unplayed = r.ReadBoolean();
        long lastPlayed = r.ReadInt64();
        r.ReadBoolean(); // osz2
        string folder = r.ReadOsuString() ?? "";
        r.ReadInt64(); // last checked against repository
        r.Skip(5); // ignore sound/skin, disable storyboard/video, visual override
        if (version < 20140609) r.ReadInt16();
        r.ReadInt32(); // last modification
        r.ReadByte(); // mania scroll speed

        return new LocalBeatmap
        {
            Md5 = md5, BeatmapId = beatmapId, SetId = setId,
            Artist = artist, Title = title, Creator = creator, Version = diff,
            Folder = folder, FileName = file, Mode = gameMode, Status = status,
            StarRating = Math.Round(nomodStars[(int)gameMode], 2),
            Ar = ar, Cs = cs, Hp = hp, Od = od,
            MinBpm = Math.Round(minBpm), MaxBpm = Math.Round(maxBpm),
            DrainSeconds = drain, Circles = circles, Sliders = sliders,
            LastPlayed = unplayed || lastPlayed <= 0 ? null : new DateTime(lastPlayed, DateTimeKind.Utc),
        };
    }
}
